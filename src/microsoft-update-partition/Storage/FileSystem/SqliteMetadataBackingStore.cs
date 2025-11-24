// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.PackageGraph.MicrosoftUpdate;
using Microsoft.PackageGraph.MicrosoftUpdate.Index;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Prerequisites;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Partitions;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Stores metadata in a SQLite database.
    /// </summary>
    class SqliteMetadataBackingStore : DbContext, IMetadataBackingStore, IMetadataSink, IMetadataSource, IStoreBackedFilter
    {
        private const string dbName = "metadata.db";
        private static ReadOnlySpan<byte> SqliteHeader => "SQLite format 3\0"u8;

        private readonly string _dbPath;

        public bool SupportsParallelProcessing => false;

        public event EventHandler<PackageStoreEventArgs> MetadataCopyProgress;

#pragma warning disable 0067
        public event EventHandler<PackageStoreEventArgs> OpenProgress;
        public event EventHandler<PackageStoreEventArgs> PackagesAddProgress;
#pragma warning restore 0067

        public event EventHandler<PackageStoreEventArgs> PackageIndexingProgress;

        public List<IPackage> PendingPackages { get; } = [];

        public SqliteMetadataBackingStore(string path, FileMode mode)
        {
            var dbPath = Path.Combine(path, dbName);
            switch (mode)
            {
                case FileMode.CreateNew:
                case FileMode.Create:
                    if (File.Exists(dbPath))
                    {
                        File.Delete(dbPath);
                    }
                    break;
                case FileMode.Open:
                    if (!File.Exists(dbPath))
                    {
                        throw new FileNotFoundException($"Database not found: {dbPath}", dbPath);
                    }
                    break;
                case FileMode.OpenOrCreate:
                    break;
                default:
                    throw new NotSupportedException($"The file mode {mode} is not supported.");
            }

            _dbPath = dbPath;

            InitializeDatabase();

            using var connection = GetConnection();

            // Request optimization for all tables.
            using var optimizeCommand = connection.CreateCommand();
            optimizeCommand.CommandText = "PRAGMA optimize = 0x10002";
            optimizeCommand.ExecuteNonQuery();
        }

        public override SqliteConnection GetConnection()
        {
            SqliteConnection connection = new($"Data Source={_dbPath}");
            connection.Open();

            return connection;
        }

        protected override void InitializeDatabase()
        {
            using var connection = GetConnection();

            // Enable WAL(Write-Ahead Logging) for performance.
            using (var walCommand = connection.CreateCommand())
            {
                walCommand.CommandText = "PRAGMA journal_mode = 'WAL'";
                walCommand.ExecuteNonQuery();
            }

            using var createTableCommand = connection.CreateCommand();
            // TODO driver update support
            /* Updates: Contains the stored identities(category, update, etc..)
             *  id -> server specific update id(revision id)
             *  guid -> global update GUID
             *  revision -> global update revision number
             *  creation_date -> the date when the update is created
             *  is_expired -> true if the update is expired
             *  package_type -> the type of the package
             * Files: Contains file information used for updates
             *  file_digest -> primary file digest as hex string
             *  size -> the size of the file
             *  modified_date -> the date when the file was modified
             *  digests -> jsonb object containing array of content file digest
             *  urls -> jsonb object containing array of original download url
             *  patching_type -> the patching type of the file
             * Metadatas: Contains full metadata xml and file digests
             *  revision_id -> server specific update id
             *  metadata -> update metadata xml
             *  categories -> jsonb object containing list of categories of this update
             *  files -> jsonb object containing list of files primary hex digest
             *  prerequisites -> jsonb object containing data of prerequisites of this update
             * SoftwareInformation: Contains information specific for software updates
             *  revision_id -> server specific update id
             * Bundled: Contains updates bundled with the update
             *  revision_id -> server specific update id
             *  guid -> bundled update guid
             *  revision -> bundled update revision number
             * Superseded: Contains superseded update ids for updates
             *  revision_id -> server specific update id
             *  superseded_guid -> supseded update global GUID
             */
            createTableCommand.CommandText = """
            CREATE TABLE IF NOT EXISTS identities (
                id INTEGER PRIMARY KEY,
                guid TEXT NOT NULL COLLATE NOCASE,
                revision INTEGER NOT NULL,
                title TEXT NOT NULL,
                creation_date TEXT NOT NULL,
                is_expired INTEGER NOT NULL,
                package_type INTEGER NOT NULL DEFAULT(-1),
                UNIQUE(guid, revision)
            );
            CREATE TABLE IF NOT EXISTS files (
                file_digest TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                size INTEGER NOT NULL,
                modified_date TEXT NOT NULL,
                digests BLOB NOT NULL,
                urls BLOB NOT NULL,
                patching_type TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS metadatas (
                revision_id INTEGER PRIMARY KEY,
                metadata BLOB NOT NULL,
                categories BLOB,
                files BLOB,
                prerequisites BLOB,
                FOREIGN KEY (revision_id) REFERENCES identities(id)
            );
            CREATE TABLE IF NOT EXISTS software_informations (
                revision_id INTEGER PRIMARY KEY,
                kb_article_id TEXT,
                bundled BLOB,
                FOREIGN KEY (revision_id) REFERENCES identities(id)
            );
            CREATE TABLE IF NOT EXISTS bundled (
                revision_id INTEGER NOT NULL,
                guid TEXT NOT NULL COLLATE NOCASE,
                revision INTEGER NOT NULL,
                PRIMARY KEY (revision_id, guid, revision),
                FOREIGN KEY (revision_id) REFERENCES identities(id)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS superseded (
                revision_id INTEGER NOT NULL,
                superseded_guid TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (revision_id, superseded_guid),
                FOREIGN KEY (revision_id) REFERENCES identities(id)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS idx_bundled_guid_revision ON bundled(guid, revision);
            CREATE INDEX IF NOT EXISTS idx_superseded_guid ON superseded(superseded_guid);
            """;
            createTableCommand.ExecuteNonQuery();
        }

        public void Dispose()
        {
        }

        public static bool IsValid(string path)
        {
            var dbPath = Path.Combine(path, dbName);

            try
            {
                var sqlHeaderBuf = new byte[16];
                using (var dbStream = File.OpenRead(dbPath))
                {
                    dbStream.ReadExactly(sqlHeaderBuf, 0, 16);
                }

                if (!sqlHeaderBuf.AsSpan().SequenceEqual(SqliteHeader))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            return true;
        }

        private bool _isReindexingRequired;

        public bool IsReindexingRequired
        {
            get
            {
                if (!_isReindexingRequired)
                {
                    using var connection = GetConnection();
                    using var checkCommand = connection.CreateCommand();
                    checkCommand.CommandText = "PRAGMA quick_check";
                    var checkResult = checkCommand.ExecuteScalar() as string;

                    _isReindexingRequired = checkResult != "ok";
                }

                return _isReindexingRequired;
            }
            private set
            {
                _isReindexingRequired = value;
            }
        }

        public void ReIndex()
        {
            CheckIndex(true);
        }

        public void CheckIndex(bool forceReindex)
        {
            using var connection = GetConnection();

            // Optimize the database before checking.
            using (var optimizeCommand = connection.CreateCommand())
            {
                optimizeCommand.CommandText = "PRAGMA optimize";
                optimizeCommand.ExecuteNonQuery();
            }

            // Check if the database is currently consistent. 
            if (!forceReindex && !IsReindexingRequired)
            {
                return;
            }

            PackageStoreEventArgs progressEvent = new()
            {
                Total = PackageCount,
                Current = 0
            };

            // There is no way to track each package within SQLite.
            PackageIndexingProgress?.Invoke(this, progressEvent);

            // Reindex the database.
            using var reindexCommand = connection.CreateCommand();
            reindexCommand.CommandText = "REINDEX";
            reindexCommand.ExecuteNonQuery();

            // Set current to total to indicate completion.
            progressEvent.Current = progressEvent.Total;
            PackageIndexingProgress?.Invoke(this, progressEvent);

            IsReindexingRequired = false;
        }

        public int PackageCount
        {
            get
            {
                using var connection = GetConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM identities";
                return (int)(command.ExecuteScalar() as long? ?? 0);
            }
        }

        void IMetadataStoreOperations.AddPackages(IEnumerable<IPackage> packages)
        {
            AddPackages(packages);
        }

        public void AddPackages(IEnumerable<IPackage> packages)
        {
            using var connection = GetConnection();
            using var transaction = connection.BeginTransaction();

            using var insertIdentityCommand = connection.CreateCommand();
            insertIdentityCommand.Transaction = transaction;
            insertIdentityCommand.CommandText = """
            INSERT INTO identities (guid, revision, title, creation_date, is_expired)
                VALUES (@guid, @revision, @title, @creation_date, @is_expired);
            SELECT last_insert_rowid();
            """;
            insertIdentityCommand.Parameters.Add("@guid", SqliteType.Text);
            insertIdentityCommand.Parameters.Add("@revision", SqliteType.Integer);
            insertIdentityCommand.Parameters.Add("@title", SqliteType.Text);
            insertIdentityCommand.Parameters.Add("@creation_date", SqliteType.Text);
            insertIdentityCommand.Parameters.Add("@is_expired", SqliteType.Integer);

            using var insertMetadataCommand = connection.CreateCommand();
            insertMetadataCommand.Transaction = transaction;
            insertMetadataCommand.CommandText = """
            INSERT INTO metadatas (revision_id, metadata, categories, files, prerequisites)
                VALUES (@revision_id, @metadata, jsonb(@categories), jsonb(@files), jsonb(@prerequisites))
            """;
            insertMetadataCommand.Parameters.Add("@revision_id", SqliteType.Integer);
            insertMetadataCommand.Parameters.Add("@metadata", SqliteType.Blob);
            insertMetadataCommand.Parameters.Add("@categories", SqliteType.Blob);
            insertMetadataCommand.Parameters.Add("@files", SqliteType.Blob);
            insertMetadataCommand.Parameters.Add("@prerequisites", SqliteType.Blob);

            using var insertSoftwareCommand = connection.CreateCommand();
            insertSoftwareCommand.Transaction = transaction;
            insertSoftwareCommand.CommandText = """
            INSERT INTO software_informations (revision_id, kb_article_id, bundled)
                VALUES (@revision_id, @kb_article_id, jsonb(@bundled));
            """;
            insertSoftwareCommand.Parameters.Add("@revision_id", SqliteType.Integer);
            insertSoftwareCommand.Parameters.Add("@kb_article_id", SqliteType.Text);
            insertSoftwareCommand.Parameters.Add("@bundled", SqliteType.Blob);

            using var insertBundledCommand = connection.CreateCommand();
            insertBundledCommand.Transaction = transaction;
            insertBundledCommand.CommandText = """
            INSERT INTO bundled (revision_id, guid, revision)
                VALUES (@revision_id, @guid, @revision)
            """;
            var revisionIdBundledParam = insertBundledCommand.Parameters.Add("@revision_id", SqliteType.Integer);
            var guidBundledParam = insertBundledCommand.Parameters.Add("@guid", SqliteType.Text);
            var revisionBundledParam = insertBundledCommand.Parameters.Add("@revision", SqliteType.Integer);

            using var insertSupersededCommand = connection.CreateCommand();
            insertSupersededCommand.Transaction = transaction;
            insertSupersededCommand.CommandText = "INSERT INTO superseded (revision_id, superseded_guid) VALUES (@revision_id, @superseded_guid)";
            insertSupersededCommand.Parameters.Add("@revision_id", SqliteType.Integer);
            insertSupersededCommand.Parameters.Add("@superseded_guid", SqliteType.Text);

            using var addFileCommand = connection.CreateCommand();
            addFileCommand.Transaction = transaction;
            addFileCommand.CommandText = """
            INSERT INTO files (file_digest, name, size, modified_date, digests, urls, patching_type)
                VALUES (@file_digest, @name, @size, @modified_date, jsonb(@digests), jsonb(@urls), @patching_type)
                ON CONFLICT(file_digest) DO NOTHING
            """;
            addFileCommand.Parameters.Add("@file_digest", SqliteType.Text);
            addFileCommand.Parameters.Add("@name", SqliteType.Text);
            addFileCommand.Parameters.Add("@size", SqliteType.Integer);
            addFileCommand.Parameters.Add("@modified_date", SqliteType.Text);
            addFileCommand.Parameters.Add("@digests", SqliteType.Blob);
            addFileCommand.Parameters.Add("@urls", SqliteType.Blob);
            addFileCommand.Parameters.Add("@patching_type", SqliteType.Text);

            foreach (var package in packages)
            {
                AddPackage(package,
                    insertIdentityCommand,
                    insertMetadataCommand,
                    insertSoftwareCommand,
                    insertBundledCommand,
                    insertSupersededCommand,
                    addFileCommand);
            }

            transaction.Commit();
        }

        void IMetadataSink.AddPackage(IPackage package)
        {
            _ = AddPackage(package);
        }

        public int AddPackage(IPackage package)
        {
            AddPackages([package]);
            return GetPackageIndex(package.Id);
        }

        private int AddPackage(IPackage package,
            SqliteCommand insertIdentityCommand,
            SqliteCommand insertMetadataCommand,
            SqliteCommand insertSoftwareCommand,
            SqliteCommand insertBundledCommand,
            SqliteCommand insertSupersededCommand,
            SqliteCommand addFileCommand)
        {
            if (package is not MicrosoftUpdatePackage microsoftUpdate ||
                microsoftUpdate.Id is not MicrosoftUpdatePackageIdentity microsoftUpdatePackageIdentity)
            {
                return -1;
            }

            insertIdentityCommand.Parameters["@guid"].Value = microsoftUpdatePackageIdentity.ID;
            insertIdentityCommand.Parameters["@revision"].Value = microsoftUpdatePackageIdentity.Revision;
            insertIdentityCommand.Parameters["@title"].Value = package.Title;
            insertIdentityCommand.Parameters["@creation_date"].Value = microsoftUpdate.CreationDate.ToString("o", DateTimeFormatInfo.InvariantInfo);
            insertIdentityCommand.Parameters["@is_expired"].Value = microsoftUpdate.IsExpired;

            var identityId = (int)(long)insertIdentityCommand.ExecuteScalar();

            insertMetadataCommand.Parameters["@revision_id"].Value = identityId;

            using (var metadataStream = package.GetMetadataStream())
            {
                using var valueStream = new MemoryStream();
                using (var compressStream = new GZipStream(valueStream, CompressionLevel.Optimal, true))
                {
                    metadataStream.CopyTo(compressStream);
                }

                insertMetadataCommand.Parameters["@metadata"].Value = valueStream.ToArray();
            }

            List<Guid> categoryGuids = [];
            foreach (var prereq in microsoftUpdate.Prerequisites.OfType<AtLeastOne>().Where(p => p.IsCategory))
            {
                categoryGuids.AddRange(prereq.Simple.Select(s => s.UpdateId));
            }

            if (categoryGuids.Count > 0)
            {
                insertMetadataCommand.Parameters["@categories"].Value =
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(categoryGuids));
            }
            else
            {
                insertMetadataCommand.Parameters["@categories"].Value = DBNull.Value;
            }

            if (PartitionRegistration.TryGetPartitionFromPackage(package, out var partitionDefinition) &&
                partitionDefinition.HasExternalContentFileMetadata &&
                package.Files?.Any() == true)
            {
                var digests = JsonConvert.SerializeObject(package.Files.Select(f => f.Digest.DigestBase64));
                insertMetadataCommand.Parameters["@files"].Value = Encoding.UTF8.GetBytes(digests);

                AddFiles(package.Files, addFileCommand);
            }
            else
            {
                insertMetadataCommand.Parameters["@files"].Value = DBNull.Value;
            }

            if (microsoftUpdate.Prerequisites.Count > 0)
            {
                insertMetadataCommand.Parameters["@prerequisites"].Value =
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(microsoftUpdate.Prerequisites));
            }
            else
            {
                insertMetadataCommand.Parameters["@prerequisites"].Value = DBNull.Value;
            }

            insertMetadataCommand.ExecuteNonQuery();

            if (package is SoftwareUpdate softwareUpdate)
            {
                // Insert software update specific datas into database.
                insertSoftwareCommand.Parameters["@revision_id"].Value = identityId;
                insertSoftwareCommand.Parameters["@kb_article_id"].Value =
                    (object)softwareUpdate.KBArticleId ?? DBNull.Value;
                if (softwareUpdate.BundledUpdates.Count > 0)
                {
                    insertSoftwareCommand.Parameters["@bundled"].Value =
                        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(softwareUpdate.BundledUpdates));
                }
                else
                {
                    insertSoftwareCommand.Parameters["@bundled"].Value = DBNull.Value;
                }

                insertSoftwareCommand.ExecuteNonQuery();

                // Insert bundled updates relationship into database.
                foreach (var bundled in softwareUpdate.BundledUpdates)
                {
                    insertBundledCommand.Parameters["@revision_id"].Value = identityId;
                    insertBundledCommand.Parameters["@guid"].Value = bundled.ID;
                    insertBundledCommand.Parameters["@revision"].Value = bundled.Revision;

                    insertBundledCommand.ExecuteNonQuery();
                }

                // Insert superseded updates relationship into database.
                foreach (var superseded in softwareUpdate.SupersededUpdates)
                {
                    insertSupersededCommand.Parameters["@revision_id"].Value = identityId;
                    insertSupersededCommand.Parameters["@superseded_guid"].Value = superseded;

                    insertSupersededCommand.ExecuteNonQuery();
                }
            }

            PendingPackages.Add(package);

            return identityId;
        }

        public void AddPackageType(int packageIndex, int packageType)
        {
            using var connection = GetConnection();
            using var updateTypeCommand = connection.CreateCommand();
            updateTypeCommand.CommandText = "UPDATE identities SET package_type = @package_type WHERE id = @id";
            updateTypeCommand.Parameters.Add("@package_type", SqliteType.Integer).Value = packageType;
            updateTypeCommand.Parameters.Add("@id", SqliteType.Integer).Value = packageIndex;

            var affected = updateTypeCommand.ExecuteNonQuery();
            if (affected != 1)
            {
                throw new InvalidDataException($"Expected 1 row affected, instead got ${affected} row(s).");
            }
        }

        private static void AddFiles(IEnumerable<IContentFile> files, SqliteCommand addFileCommand)
        {
            // TODO encapsulate this for other types?
            foreach (var file in files)
            {
                addFileCommand.Parameters["@file_digest"].Value = file.Digest.DigestBase64;
                addFileCommand.Parameters["@name"].Value = file.FileName;
                addFileCommand.Parameters["@size"].Value = file.Size;
                if (file is UpdateFile updateFile)
                {
                    addFileCommand.Parameters["@modified_date"].Value =
                        updateFile.ModifiedDate.ToUniversalTime().ToString("o", DateTimeFormatInfo.InvariantInfo);
                    addFileCommand.Parameters["@digests"].Value =
                        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(updateFile.Digests));
                    addFileCommand.Parameters["@urls"].Value =
                        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(updateFile.Urls));
                    addFileCommand.Parameters["@patching_type"].Value = updateFile.PatchingType;
                }
                else
                {
                    addFileCommand.Parameters["@modified_date"].Value = "";
                    addFileCommand.Parameters["@digests"].Value = (byte[])[];
                    addFileCommand.Parameters["@urls"].Value = (byte[])[];
                    addFileCommand.Parameters["@patching_type"].Value = "";
                }

                addFileCommand.ExecuteNonQuery();
            }
        }

        public void Flush()
        {
            PendingPackages.Clear();
        }

        public IEnumerator<IPackage> GetEnumerator()
        {
            var identities = GetPackageIdentities();
            return identities.Select(GetPackage).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public IEnumerator<IPackage> GetEnumerator(IMetadataFilter filter)
        {
            var identities = GetPackageIdentities(filter);
            return identities.Select(GetPackage).GetEnumerator();
        }

        public IEnumerable<IPackageIdentity> GetIdentitiesFromStore(MetadataFilter filter)
        {
            return GetPackageIdentities(filter);
        }

        public IEnumerable<IPackage> FilterFromStore(MetadataFilter filter)
        {
            using var enumerator = GetEnumerator(filter);
            while (enumerator.MoveNext())
            {
                yield return enumerator.Current;
            }
        }

        public int CountFromStore(MetadataFilter filter)
        {
            using var connection = GetConnection();
            using var command = connection.CreateCommand();
            BuildFilterQuery(filter, command, true);
            return (int)(command.ExecuteScalar() as long? ?? 0);
        }

        public List<T> GetFiles<T>(IPackageIdentity packageIdentity)
        {
            var identityId = GetPackageIndex(packageIdentity);
            if (identityId == -1)
            {
                return [];
            }

            using var connection = GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
            SELECT f.name, f.size, f.modified_date, json(f.digests), json(f.urls), f.patching_type
            FROM metadatas as m INNER JOIN json_each(m.files) as d
                INNER JOIN files as f ON f.file_digest = d.value
            WHERE m.revision_id = @revision_id
            """;
            command.Parameters.Add("@revision_id", SqliteType.Integer).Value = identityId;

            List<UpdateFile> files = [];
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                files.Add(new UpdateFile()
                {
                    FileName = reader.GetString(0),
                    Size = (ulong)reader.GetInt64(1),
                    ModifiedDate = reader.GetDateTime(2),
                    Digests = JsonConvert.DeserializeObject<List<ContentFileDigest>>(reader.GetString(3)),
                    Urls = JsonConvert.DeserializeObject<List<UpdateFileUrl>>(reader.GetString(4)),
                    PatchingType = reader.GetString(5)
                });
            }

            return files.Count > 0 ? files.Cast<T>().ToList() : [];
        }

        public Stream GetMetadata(IPackageIdentity packageIdentity)
        {
            var identityId = GetPackageIndex(packageIdentity);
            if (identityId == -1)
            {
                return null;
            }

            var connection = GetConnection();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT metadata FROM metadatas WHERE revision_id = @revision_id";
            command.Parameters.Add("@revision_id", SqliteType.Integer).Value = identityId;

            var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                reader.Dispose();
                command.Dispose();
                connection.Dispose();
                return null;
            }

            var compressedStream = new BlobStream(connection, command, reader);
            var stream = new GZipStream(compressedStream, CompressionMode.Decompress);

            return stream;
        }

        public IPackage GetPackage(IPackageIdentity packageIdentity)
        {
            if (packageIdentity is MicrosoftUpdatePackageIdentity updatePackageIdentity)
            {
                var packageType = (StoredPackageType)GetPackageType(GetPackageIndex(packageIdentity));

                return MicrosoftUpdatePackage.FromTypeAndStore(packageType, updatePackageIdentity, this, this);
            }

            throw new InvalidDataException("Invalid update identity");
        }

        public IEnumerable<IPackageIdentity> GetPackageIdentities()
        {
            using var connection = GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT guid, revision FROM identities";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var guid = reader.GetGuid(0);
                var revision = reader.GetInt32(1);

                yield return new MicrosoftUpdatePackageIdentity(guid, revision);
            }
        }

        public int GetPackageIndex(IPackageIdentity packageIdentity)
        {
            if (packageIdentity is not MicrosoftUpdatePackageIdentity microsoftUpdatePackageIdentity)
            {
                return -1;
            }

            using var connection = GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM identities WHERE guid = @guid AND revision = @revision";
            command.Parameters.Add("@guid", SqliteType.Text).Value = microsoftUpdatePackageIdentity.ID;
            command.Parameters.Add("@revision", SqliteType.Integer).Value = microsoftUpdatePackageIdentity.Revision;

            return (int)(command.ExecuteScalar() as long? ?? -1);
        }

        public IPackageIdentity GetPackageIdentity(int packageIndex)
        {
            using var connection = GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT guid, revision FROM identities WHERE id = @id";
            command.Parameters.Add("@id", SqliteType.Integer).Value = packageIndex;

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                var guid = reader.GetGuid(0);
                var revision = reader.GetInt32(1);
                return new MicrosoftUpdatePackageIdentity(guid, revision);
            }

            return null;
        }

        public bool ContainsPackage(IPackageIdentity packageIdentity)
        {
            return GetPackageIndex(packageIdentity) != -1;
        }

        public bool ContainsMetadata(IPackageIdentity packageIdentity)
        {
            return ContainsPackage(packageIdentity);
        }

        public int GetPackageType(int packageIndex)
        {
            using var connection = GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT package_type FROM identities WHERE id = @id";
            command.Parameters.Add("@id", SqliteType.Integer).Value = packageIndex;

            return (int)(command.ExecuteScalar() as long? ?? -1);
        }

        public bool TrySimpleKeyLookup<T>(IPackageIdentity packageIdentity, string indexName, out T value)
        {
            var packageIndex = GetPackageIndex(packageIdentity);
            if (packageIndex == -1)
            {
                value = default;
                return false;
            }

            using var connection = GetConnection();

            using var command = connection.CreateCommand();
            command.Parameters.Add("@revision_id", SqliteType.Integer).Value = packageIndex;

            switch (indexName)
            {
                case Index.AvailableIndexes.TitlesIndexName:
                    command.CommandText = "SELECT title FROM identities WHERE id = @revision_id";
                    var title = command.ExecuteScalar() as string;
                    if (!string.IsNullOrEmpty(title))
                    {
                        value = (T)Convert.ChangeType(title, typeof(T));
                        return true;
                    }
                    break;

                case AvailableIndexes.KbArticleIndexName:
                    command.CommandText = "SELECT kb_article_id FROM software_informations WHERE revision_id = @revision_id";
                    var kbArticle = command.ExecuteScalar() as string;
                    if (!string.IsNullOrEmpty(kbArticle))
                    {
                        value = (T)Convert.ChangeType(kbArticle, typeof(T));
                        return true;
                    }
                    break;

                case AvailableIndexes.IsExpiredIndexName:
                    command.CommandText = "SELECT is_expired FROM identities WHERE id = @revision_id";
                    var isExpired = command.ExecuteScalar() as long?;
                    if (isExpired.HasValue)
                    {
                        value = (T)Convert.ChangeType(isExpired.Value != 0, typeof(T));
                        return true;
                    }

                    break;

                case AvailableIndexes.CategoriesIndexName:
                    command.CommandText = "SELECT json(categories) FROM metadatas WHERE revision_id = @revision_id";
                    var categories = command.ExecuteScalar() as string;
                    if (!string.IsNullOrEmpty(categories))
                    {
                        value = JsonConvert.DeserializeObject<T>(categories);
                        return true;
                    }
                    break;

                default:
                    throw new NotImplementedException($"Index '{indexName}' not implemented for simple key lookup.");
            }

            value = default;
            return false;
        }

        public bool TryListKeyLookup<T>(IPackageIdentity packageIdentity, string indexName, out List<T> value)
        {
            var packageIndex = GetPackageIndex(packageIdentity);
            if (packageIndex == -1)
            {
                value = null;
                return false;
            }

            using var connection = GetConnection();

            using var command = connection.CreateCommand();
            command.Parameters.Add("@revision_id", SqliteType.Integer).Value = packageIndex;

            switch (indexName)
            {
                case AvailableIndexes.PrerequisitesIndexName:
                    command.CommandText = """
                    SELECT p.value,
                    CASE
                        WHEN json_type(p.value, '$.Simple') IS NULL
                            THEN 1
                        ELSE 0
                    END AS is_simple
                    FROM metadatas AS m INNER JOIN json_each(m.prerequisites) AS p
                    WHERE m.revision_id = @revision_id
                    """;

                    List<IPrerequisite> prerequisites = [];

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            // Is Simple
                            if (reader.GetBoolean(1))
                            {
                                prerequisites.Add(JsonConvert.DeserializeObject<Simple>(reader.GetString(0)));
                            }
                            else
                            {
                                prerequisites.Add(JsonConvert.DeserializeObject<AtLeastOne>(reader.GetString(0)));
                            }
                        }
                    }
                    if (prerequisites.Count > 0)
                    {
                        value = prerequisites.Cast<T>().ToList();
                        return true;
                    }

                    break;

                case AvailableIndexes.FilesIndexName:
                    command.CommandText = """
                    SELECT f.name, f.size, f.modified_date, json(f.digests), json(f.urls), f.patching_type
                    FROM metadatas as m INNER JOIN json_each(m.files) as d
                        INNER JOIN files as f ON f.file_digest = d.value
                    WHERE m.revision_id = @revision_id
                    """;
                    List<IContentFile> files = [];
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            files.Add(new UpdateFile()
                            {
                                FileName = reader.GetString(0),
                                Size = (ulong)reader.GetInt64(1),
                                ModifiedDate = reader.GetDateTime(2),
                                Digests = JsonConvert.DeserializeObject<List<ContentFileDigest>>(reader.GetString(3)),
                                Urls = JsonConvert.DeserializeObject<List<UpdateFileUrl>>(reader.GetString(4)),
                                PatchingType = reader.GetString(5)
                            });
                        }
                    }
                    if (files.Count > 0)
                    {
                        value = files.Cast<T>().ToList();
                        return true;
                    }
                    break;

                case AvailableIndexes.IsSupersedingIndexName:
                    command.CommandText = "SELECT superseded_guid FROM superseded WHERE revision_id = @revision_id";
                    List<Guid> guids = [];
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            guids.Add(reader.GetGuid(0));
                        }
                    }
                    if (guids.Count > 0)
                    {
                        value = guids.Cast<T>().ToList();
                        return true;
                    }
                    break;

                case AvailableIndexes.IsBundleIndexName:
                    command.CommandText = "SELECT guid, revision FROM bundled WHERE revision_id = @revision_id";
                    List<MicrosoftUpdatePackageIdentity> identities = [];

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            identities.Add(new MicrosoftUpdatePackageIdentity(reader.GetGuid(0), reader.GetInt32(1)));
                        }
                    }
                    if (identities.Count > 0)
                    {
                        value = identities.Cast<T>().ToList();
                        return true;
                    }
                    break;

                case AvailableIndexes.DriverMetadataIndexName:
                    // TODO implement this
                    break;

                default:
                    throw new NotImplementedException($"Index '{indexName}' not implemented for list key lookup.");
            }

            value = null;
            return false;
        }

        public bool TryPackageLookupByCustomKey<T>(T key, string indexName, out IPackageIdentity value)
        {
            // Currently not used.

            value = null;
            return false;
        }

        public bool TryPackageListLookupByCustomKey<T>(T key, string indexName, out List<IPackageIdentity> value)
        {
            using var connection = GetConnection();
            using var command = connection.CreateCommand();

            switch (indexName)
            {
                case AvailableIndexes.BundledWithIndexName:
                    if (key is MicrosoftUpdatePackageIdentity packageIdentity)
                    {
                        command.CommandText = """
                        SELECT i.guid, i.revision
                            FROM bundled as b INNER JOIN identities as i
                            ON i.id = b.revision_id
                            WHERE b.guid = @guid AND b.revision = @revision
                        """;
                        command.Parameters.Add("@guid", SqliteType.Text).Value = packageIdentity.ID;
                        command.Parameters.Add("@revision", SqliteType.Integer).Value = packageIdentity.Revision;

                        value = [];

                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            value.Add(new MicrosoftUpdatePackageIdentity(reader.GetGuid(0), reader.GetInt32(1)));
                        }
                        if (value.Count > 0)
                        {
                            return true;
                        }
                    }

                    break;

                case AvailableIndexes.IsSupersededIndexName:
                    if (key is Guid supersededGuid)
                    {
                        command.CommandText = """
                        SELECT i.guid, i.revision
                            FROM superseded AS s INNER JOIN identities AS i
                            ON i.id = s.revision_id
                            WHERE s.superseded_guid = @superseded_guid
                        """;
                        command.Parameters.Add("@superseded_guid", SqliteType.Text).Value = supersededGuid;

                        value = [];

                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            value.Add(new MicrosoftUpdatePackageIdentity(reader.GetGuid(0), reader.GetInt32(1)));
                        }
                        if (value.Count > 0)
                        {
                            return true;
                        }
                    }

                    break;

                default:
                    throw new NotImplementedException($"Index '{indexName}' not implemented for package list lookup by custom key.");
            }

            value = null;
            return false;
        }

        /// <summary>
        /// Read-only stream used for reading SQLite BLOB data.
        /// </summary>
        private class BlobStream : Stream
        {
            private readonly SqliteConnection _connection;
            private readonly SqliteCommand _command;
            private readonly SqliteDataReader _reader;
            private readonly Stream _blobStream;

            public BlobStream(SqliteConnection connection, SqliteCommand command, SqliteDataReader reader)
            {
                _connection = connection;
                _command = command;
                _reader = reader;
                _blobStream = reader.GetStream(0);
            }

            public override bool CanRead => _blobStream.CanRead;
            public override bool CanSeek => _blobStream.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _blobStream.Length;
            public override long Position
            {
                get => _blobStream.Position;
                set => _blobStream.Position = value;
            }

            public override void Flush() => _blobStream.Flush();

            public override int Read(byte[] buffer, int offset, int count) => _blobStream.Read(buffer, offset, count);

            public override long Seek(long offset, SeekOrigin origin) => _blobStream.Seek(offset, origin);

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _blobStream.Dispose();
                    _reader.Dispose();
                    _command.Dispose();
                    _connection.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        public void CopyTo(IMetadataSink destination, CancellationToken cancelToken)
        {
            var packageEntries = GetPackageIdentities();
            var progressArgs = new PackageStoreEventArgs()
            {
                Total = packageEntries.Count(),
                Current = 0
            };

            MetadataCopyProgress?.Invoke(this, progressArgs);

            if (destination.SupportsParallelProcessing)
            {
                packageEntries.AsParallel().ForAll(packageEntry =>
                {
                    if (cancelToken.IsCancellationRequested)
                    {
                        return;
                    }

                    destination.AddPackage(GetPackage(packageEntry));

                    lock (progressArgs)
                    {
                        progressArgs.Current++;
                    }
                    if (progressArgs.Current % 100 == 0)
                    {
                        MetadataCopyProgress?.Invoke(this, progressArgs);
                    }
                });
            }
            else
            {
                foreach (var packageEntry in packageEntries)
                {
                    if (cancelToken.IsCancellationRequested)
                    {
                        break;
                    }

                    destination.AddPackage(GetPackage(packageEntry));

                    progressArgs.Current++;

                    if (progressArgs.Current % 100 == 0)
                    {
                        MetadataCopyProgress?.Invoke(this, progressArgs);
                    }
                }
            }
        }

        public void CopyTo(IMetadataSink destination, IMetadataFilter filter, CancellationToken cancelToken)
        {
            var packageEntries = GetPackageIdentities(filter);

            var progressArgs = new PackageStoreEventArgs()
            {
                Total = packageEntries.Count(),
                Current = 0
            };
            MetadataCopyProgress?.Invoke(this, progressArgs);

            if (destination.SupportsParallelProcessing)
            {
                packageEntries.AsParallel().ForAll(packageEntry =>
                {
                    if (cancelToken.IsCancellationRequested)
                    {
                        return;
                    }

                    destination.AddPackage(GetPackage(packageEntry));

                    lock (progressArgs)
                    {
                        progressArgs.Current++;
                    }
                    if (progressArgs.Current % 100 == 0)
                    {
                        MetadataCopyProgress?.Invoke(this, progressArgs);
                    }
                });
            }
            else
            {
                foreach (var packageEntry in packageEntries)
                {
                    if (cancelToken.IsCancellationRequested)
                    {
                        break;
                    }

                    destination.AddPackage(GetPackage(packageEntry));

                    progressArgs.Current++;

                    if (progressArgs.Current % 100 == 0)
                    {
                        MetadataCopyProgress?.Invoke(this, progressArgs);
                    }
                }
            }
        }

        private static string BuildSortOrder(MetadataSortOrder sortOrder)
        {
            (SortOrder Direction, string ColumnName)[] sortClauses = [
                (Direction: sortOrder.CreationDate, ColumnName: "i.creation_date"),
                (Direction: sortOrder.Id, ColumnName: "i.guid"),
                (Direction: sortOrder.KbArticle, ColumnName: "si.kb_article_id"),
                (Direction: sortOrder.Title, ColumnName: "i.title")
            ];

            var orders = sortClauses
                .Where(clause => clause.Direction != SortOrder.None)
                .Select(clause => $"{clause.ColumnName} {(clause.Direction == SortOrder.Ascending ? "ASC" : "DESC")}");

            return string.Join(", ", orders);
        }

        private static void BuildFilterQuery(MetadataFilter metadataFilter, SqliteCommand command, bool countOnly)
        {
            var queryBuilder = new StringBuilder();
            var tableBuilder = new StringBuilder("identities AS i");
            var whereBuilder = new StringBuilder("1=1");
            var groupByBuilder = new StringBuilder();

            var hasProductFilter = metadataFilter.ProductFilter is { Count: > 0 };
            var hasClassificationFilter = metadataFilter.ClassificationFilter is { Count: > 0 };
            var hasExcludedProductFilter = metadataFilter.ExcludedProductFilter is { Count: > 0 };
            var hasExcludedClassificationFilter = metadataFilter.ExcludedClassificationFilter is { Count: > 0 };
            var requiresCategoryFiltering = hasProductFilter || hasClassificationFilter || hasExcludedProductFilter || hasExcludedClassificationFilter;
            var hasIdFilter = metadataFilter.IdFilter is { Count: > 0 };
            var hasExcludedIdFilter = metadataFilter.ExcludedIdFilter is { Count: > 0 };
            var hasKbFilter = metadataFilter.KbArticleFilter is { Count: > 0 };
            var hasExcludedKbFilter = metadataFilter.ExcludedKbArticleFilter is { Count: > 0 };

            if (requiresCategoryFiltering || hasIdFilter || hasExcludedIdFilter || hasKbFilter || hasExcludedKbFilter)
            {
                PopulateFilterTable(
                    command.Connection,
                    metadataFilter,
                    hasProductFilter,
                    hasExcludedProductFilter,
                    hasClassificationFilter,
                    hasExcludedClassificationFilter,
                    hasIdFilter,
                    hasExcludedIdFilter,
                    hasKbFilter,
                    hasExcludedKbFilter);
            }

            if (requiresCategoryFiltering)
            {
                tableBuilder.Append("""

                INNER JOIN metadatas AS m ON i.id = m.revision_id
                INNER JOIN json_each(m.categories) AS c
                LEFT JOIN temp.filter_values AS fv ON fv.value = c.value
                """);

                if (countOnly)
                {
                    groupByBuilder.Append("i.id");
                }
                else
                {
                    groupByBuilder.Append("i.guid, i.revision");
                }

                groupByBuilder.Append("\nHAVING 1=1");

                if (hasProductFilter)
                {
                    groupByBuilder.Append("\nAND count(CASE WHEN fv.kind = 'product' THEN 1 END) > 0");
                }

                if (hasExcludedProductFilter)
                {
                    groupByBuilder.Append("\nAND count(CASE WHEN fv.kind = 'excluded_product' THEN 1 END) = 0");
                }

                if (hasClassificationFilter)
                {
                    groupByBuilder.Append("\nAND count(CASE WHEN fv.kind = 'classification' THEN 1 END) > 0");
                }

                if (hasExcludedClassificationFilter)
                {
                    groupByBuilder.Append("\nAND count(CASE WHEN fv.kind = 'excluded_classification' THEN 1 END) = 0");
                }
            }

            if (metadataFilter.PackageType != -1)
            {
                whereBuilder.Append("\nAND i.package_type = @package_type");
                command.Parameters.Add("@package_type", SqliteType.Integer).Value = metadataFilter.PackageType;
            }

            if (!string.IsNullOrEmpty(metadataFilter.TitleFilter))
            {
                whereBuilder.Append("\nAND i.title LIKE @title");
                command.Parameters.Add("@title", SqliteType.Text).Value = $"%{metadataFilter.TitleFilter}%";
            }

            if (!string.IsNullOrEmpty(metadataFilter.ExcludedTitleFilter))
            {
                whereBuilder.Append("\nAND i.title NOT LIKE @excluded_title");
                command.Parameters.Add("@excluded_title", SqliteType.Text).Value = $"%{metadataFilter.ExcludedTitleFilter}%";
            }

            if (!metadataFilter.IncludeExpired)
            {
                whereBuilder.Append("\nAND i.is_expired = 0");
            }

            if (metadataFilter.SkipSuperseded)
            {
                whereBuilder.Append("\nAND NOT EXISTS (SELECT 1 FROM superseded AS sup WHERE sup.superseded_guid = i.guid)");
            }

            if (hasIdFilter)
            {
                tableBuilder.Append("\nINNER JOIN temp.filter_values AS fv_id ON fv_id.kind = 'id' AND fv_id.value = i.guid");
            }
            if (hasExcludedIdFilter)
            {
                tableBuilder.Append("\nLEFT JOIN temp.filter_values AS fv_exid ON fv_exid.kind = 'excluded_id' AND fv_exid.value = i.guid");
                whereBuilder.Append("\nAND fv_exid.value IS NULL");
            }

            if (hasKbFilter ||
                hasExcludedKbFilter ||
                metadataFilter.SortOrder.KbArticle != SortOrder.None)
            {
                tableBuilder.Append("\nINNER JOIN software_informations AS si ON si.revision_id = i.id");

                if (hasKbFilter)
                {
                    tableBuilder.Append("\nINNER JOIN temp.filter_values AS fv_kb ON fv_kb.kind = 'kb' AND fv_kb.value = si.kb_article_id");
                }

                if (hasExcludedKbFilter)
                {
                    tableBuilder.Append("\nLEFT JOIN temp.filter_values AS fv_exkb ON fv_exkb.kind = 'excluded_kb' AND fv_exkb.value = si.kb_article_id");
                    whereBuilder.Append("\nAND fv_exkb.value IS NULL");
                }
            }

            switch (metadataFilter.BundleFilter)
            {
                case BundleType.NotBundled:
                    whereBuilder.Append("\nAND NOT EXISTS (SELECT 1 FROM bundled as b WHERE b.guid = i.guid AND b.revision = i.revision)");
                    break;
                case BundleType.IsBundled:
                    whereBuilder.Append("\nAND EXISTS (SELECT 1 FROM bundled as b WHERE b.guid = i.guid AND b.revision = i.revision)");
                    break;
            }

            if (countOnly)
            {
                queryBuilder.Append("SELECT COUNT(*)");
                if (groupByBuilder.Length > 0)
                {
                    queryBuilder.Append(" FROM (SELECT 1");
                }
            }
            else
            {
                queryBuilder.Append("SELECT i.guid, i.revision");
            }

            queryBuilder.Append($" FROM {tableBuilder}\nWHERE {whereBuilder}");

            if (groupByBuilder.Length > 0)
            {
                queryBuilder.Append($"\nGROUP BY {groupByBuilder}");
            }

            if (countOnly && groupByBuilder.Length > 0)
            {
                queryBuilder.Append(')');
            }

            if (!countOnly)
            {
                var requiresDriverFiltering = metadataFilter switch
                {
                    { HardwareIdFilter.Length: > 0 } => true,
                    { ExcludedHardwareIdFilter.Length: > 0 } => true,
                    { ComputerHardwareIdFilter: var id } when id != Guid.Empty => true,
                    { ExcludedComputerHardwareIdFilter: var id } when id != Guid.Empty => true,
                    _ => false
                };

                if (!requiresDriverFiltering)
                {
                    if (metadataFilter.SortOrder.NeedSort())
                    {
                        queryBuilder.Append($"\nORDER BY {BuildSortOrder(metadataFilter.SortOrder)}");
                    }
                    else
                    {
                        // sort by creation date and id with descending order by default 
                        queryBuilder.Append("\nORDER BY i.creation_date DESC, i.id DESC");
                    }

                    if (metadataFilter.FirstX > 0 || metadataFilter.AfterX > 0)
                    {
                        if (metadataFilter.FirstX > 0)
                        {
                            queryBuilder.Append("\nLIMIT @limit");
                            command.Parameters.Add("@limit", SqliteType.Integer).Value = metadataFilter.FirstX;

                            if (metadataFilter.AfterX > 0)
                            {
                                queryBuilder.Append(" OFFSET @offset");
                                command.Parameters.Add("@offset", SqliteType.Integer).Value = metadataFilter.AfterX;
                            }
                        }
                        else if (metadataFilter.AfterX > 0)
                        {
                            queryBuilder.Append("\nLIMIT -1 OFFSET @offset");
                            command.Parameters.Add("@offset", SqliteType.Integer).Value = metadataFilter.AfterX;
                        }
                    }
                }
            }

            command.CommandText = queryBuilder.ToString();
        }

        private static void PopulateFilterTable(
            SqliteConnection connection,
            MetadataFilter metadataFilter,
            bool hasProductFilter,
            bool hasExcludedProductFilter,
            bool hasClassificationFilter,
            bool hasExcludedClassificationFilter,
            bool hasIdFilter,
            bool hasExcludedIdFilter,
            bool hasKbFilter,
            bool hasExcludedKbFilter)
        {
            ArgumentNullException.ThrowIfNull(connection);

            using (var tempCommand = connection.CreateCommand())
            {
                tempCommand.CommandText = """
                DROP TABLE IF EXISTS temp.filter_values;
                CREATE TEMP TABLE filter_values(
                    kind TEXT,
                    value TEXT COLLATE NOCASE,
                    PRIMARY KEY(kind, value)
                ) WITHOUT ROWID;
                """;
                tempCommand.ExecuteNonQuery();
            }

            using var transaction = connection.BeginTransaction();
            using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = "INSERT OR IGNORE INTO temp.filter_values(kind, value) VALUES (@kind, @value)";
            var kindParam = insertCmd.Parameters.Add("@kind", SqliteType.Text);
            var valueParam = insertCmd.Parameters.Add("@value", SqliteType.Text);

            void AddValues<T>(string kind, IEnumerable<T> values)
            {
                kindParam.Value = kind;
                foreach (var value in values)
                {
                    valueParam.Value = value;
                    insertCmd.ExecuteNonQuery();
                }
            }

            if (hasProductFilter)
            {
                AddValues("product", metadataFilter.ProductFilter);
            }

            if (hasExcludedProductFilter)
            {
                AddValues("excluded_product", metadataFilter.ExcludedProductFilter);
            }

            if (hasClassificationFilter)
            {
                AddValues("classification", metadataFilter.ClassificationFilter);
            }

            if (hasExcludedClassificationFilter)
            {
                AddValues("excluded_classification", metadataFilter.ExcludedClassificationFilter);
            }

            if (hasIdFilter)
            {
                AddValues("id", metadataFilter.IdFilter);
            }

            if (hasExcludedIdFilter)
            {
                AddValues("excluded_id", metadataFilter.ExcludedIdFilter);
            }

            if (hasKbFilter)
            {
                AddValues("kb", metadataFilter.KbArticleFilter);
            }

            if (hasExcludedKbFilter)
            {
                AddValues("excluded_kb", metadataFilter.ExcludedKbArticleFilter);
            }

            transaction.Commit();
        }

        private IEnumerable<IPackageIdentity> GetPackageIdentities(IMetadataFilter filter)
        {
            if (filter is not MetadataFilter metadataFilter)
            {
                foreach (var identity in GetPackageIdentities())
                {
                    yield return identity;
                }

                yield break;
            }

            List<IPackageIdentity> identities = [];
            Dictionary<MicrosoftUpdatePackageIdentity, MicrosoftUpdatePackage> packageCache = null;

            MicrosoftUpdatePackage GetCachedPackage(IPackageIdentity identity)
            {
                if (identity is not MicrosoftUpdatePackageIdentity microsoftIdentity)
                {
                    return GetPackage(identity) as MicrosoftUpdatePackage;
                }

                packageCache ??= [];
                if (!packageCache.TryGetValue(microsoftIdentity, out var package))
                {
                    package = GetPackage(microsoftIdentity) as MicrosoftUpdatePackage;
                    if (package is not null)
                    {
                        packageCache[microsoftIdentity] = package;
                    }
                }

                return package;
            }

            using var connection = GetConnection();
            using var command = connection.CreateCommand();

            BuildFilterQuery(metadataFilter, command, false);

            // TODO remove this by materializing driver metadata indexes
            var requiresDriverFiltering = metadataFilter switch
            {
                { HardwareIdFilter: not null and { Length: > 0 } } => true,
                { ExcludedHardwareIdFilter: not null and { Length: > 0 } } => true,
                { ComputerHardwareIdFilter: var id } when id != Guid.Empty => true,
                { ExcludedComputerHardwareIdFilter: var id } when id != Guid.Empty => true,
                _ => false
            };

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var guid = reader.GetGuid(0);
                var revision = reader.GetInt32(1);

                if (requiresDriverFiltering)
                {
                    identities.Add(new MicrosoftUpdatePackageIdentity(guid, revision));
                }
                else
                {
                    yield return new MicrosoftUpdatePackageIdentity(guid, revision);
                }
            }

            if (!requiresDriverFiltering)
            {
                yield break;
            }

            List<IPackageIdentity> driverMatches = [];
            foreach (var identity in identities)
            {
                var package = GetCachedPackage(identity);
                if (package is not DriverUpdate driverUpdate)
                {
                    continue;
                }

                var metadata = driverUpdate.GetDriverMetadata();
                if (!string.IsNullOrEmpty(metadataFilter.HardwareIdFilter))
                {
                    var hardwareMatch = metadata.Any(md =>
                        md.HardwareId.Equals(metadataFilter.HardwareIdFilter, StringComparison.OrdinalIgnoreCase));
                    if (!hardwareMatch)
                    {
                        continue;
                    }
                }

                if (!string.IsNullOrEmpty(metadataFilter.ExcludedHardwareIdFilter))
                {
                    var excludedHardwareMatch = metadata.Any(md =>
                        md.HardwareId.Equals(metadataFilter.ExcludedHardwareIdFilter, StringComparison.OrdinalIgnoreCase));
                    if (excludedHardwareMatch)
                    {
                        continue;
                    }
                }

                if (metadataFilter.ComputerHardwareIdFilter != Guid.Empty)
                {
                    var computerMatch = metadata.Any(md =>
                        md.DistributionComputerHardwareId.Contains(metadataFilter.ComputerHardwareIdFilter));
                    if (!computerMatch)
                    {
                        continue;
                    }
                }

                if (metadataFilter.ExcludedComputerHardwareIdFilter != Guid.Empty)
                {
                    var excludedComputerMatch = metadata.Any(md =>
                        md.DistributionComputerHardwareId.Contains(metadataFilter.ExcludedComputerHardwareIdFilter));
                    if (excludedComputerMatch)
                    {
                        continue;
                    }
                }

                driverMatches.Add(identity);
            }

            if (metadataFilter.AfterX > 0)
            {
                identities = driverMatches.Skip(metadataFilter.AfterX).ToList();
            }

            if (metadataFilter.FirstX > 0 && identities.Count > metadataFilter.FirstX)
            {
                identities = driverMatches.Take(metadataFilter.FirstX).ToList();
            }

            foreach (var identity in identities)
            {
                yield return identity;
            }
        }
    }
}
