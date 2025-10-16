// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
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
    class SqliteMetadataBackingStore : DbContext, IMetadataBackingStore, IMetadataSink, IMetadataSource
    {
        private const string DbName = "metadata.db";
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
            var dbPath = Path.Combine(path, DbName);
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
             *  Id -> server specific update id(revision id)
             *  Guid -> global update GUID
             *  Revision -> global update revision number
             *  PackageType -> the type of the package
             * Files: Contains file information used for updates
             *  FileDigest -> primary file digest as hex string
             *  Digests -> jsonb object containing array of content file digest
             *  Urls -> jsonb object containing array of original download url
             * Metadatas: Contains full metadata xml and file digests
             *  RevisionId -> server specific update id
             *  Metadata -> update metadata xml
             *  Categories -> jsonb object containing list of categories of this update
             *  Files -> jsonb object containing list of files primary hex digest
             *  Prerequisites -> jsonb object containing data of prerequisites of this update
             * SoftwareInformation: Contains information specific for software updates
             *  RevisionId -> server specific update id
             * Bundled: Contains updates bundled with the update
             *  RevisionId -> server specific update id
             *  Guid -> bundled update guid
             *  Revision -> bundled update revision number
             * Superseded: Contains superseded update ids for updates
             *  RevisionId -> server specific update id
             *  SupersededGuid -> supseded update global GUID
             */
            createTableCommand.CommandText = """
            CREATE TABLE IF NOT EXISTS Identities (
                Id INTEGER PRIMARY KEY,
                Guid TEXT NOT NULL COLLATE NOCASE,
                Revision INTEGER NOT NULL,
                Title TEXT NOT NULL,
                PackageType INTEGER NOT NULL DEFAULT(-1),
                UNIQUE(Guid, Revision)
            );
            CREATE TABLE IF NOT EXISTS Files (
                FileDigest TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Size INTEGER NOT NULL,
                ModifiedDate TEXT NOT NULL,
                Digests BLOB NOT NULL,
                Urls BLOB NOT NULL,
                PatchingType TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Metadatas (
                RevisionId INTEGER PRIMARY KEY,
                Metadata BLOB NOT NULL,
                Categories BLOB,
                Files BLOB,
                Prerequisites BLOB,
                FOREIGN KEY (RevisionId) REFERENCES Identities(Id)
            );
            CREATE TABLE IF NOT EXISTS SoftwareInformations (
                RevisionId INTEGER PRIMARY KEY,
                KbArticleId TEXT,
                Bundled BLOB,
                FOREIGN KEY (RevisionId) REFERENCES Identities(Id)
            );
            CREATE TABLE IF NOT EXISTS Bundled (
                RevisionId INTEGER NOT NULL,
                Guid TEXT NOT NULL COLLATE NOCASE,
                Revision INTEGER NOT NULL,
                PRIMARY KEY (RevisionId, Guid, Revision),
                FOREIGN KEY (RevisionId) REFERENCES Identities(Id)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS Superseded (
                RevisionId INTEGER NOT NULL,
                SupersededGuid TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (RevisionId, SupersededGuid),
                FOREIGN KEY (RevisionId) REFERENCES Identities(Id)
            ) WITHOUT ROWID;
            """;
            createTableCommand.ExecuteNonQuery();
        }

        public void Dispose()
        {
        }

        public static bool IsValid(string path)
        {
            var dbPath = Path.Combine(path, DbName);

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
                command.CommandText = "SELECT COUNT(*) FROM Identities";
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
                INSERT INTO Identities (Guid, Revision, Title)
                    VALUES (@Guid, @Revision, @Title);
                SELECT last_insert_rowid();
                """;
            var guidIdentityParam = insertIdentityCommand.Parameters.Add("@Guid", SqliteType.Text);
            var revisionIdentityParam = insertIdentityCommand.Parameters.Add("@Revision", SqliteType.Integer);
            var titleIdentityParam = insertIdentityCommand.Parameters.Add("@Title", SqliteType.Text);

            using var insertMetadataCommand = connection.CreateCommand();
            insertMetadataCommand.Transaction = transaction;
            insertMetadataCommand.CommandText = """
                INSERT INTO Metadatas (RevisionId, Metadata, Categories, Files, Prerequisites)
                    VALUES (@RevisionId, @Metadata, jsonb(@Categories), jsonb(@Files), jsonb(@Prerequisites))
                """;
            var revisionIdMetadataParam = insertMetadataCommand.Parameters.Add("@RevisionId", SqliteType.Integer);
            var metadataMetadataParam = insertMetadataCommand.Parameters.Add("@Metadata", SqliteType.Blob);
            var categoriesMetadataParam = insertMetadataCommand.Parameters.Add("@Categories", SqliteType.Blob);
            var filesMetadataParam = insertMetadataCommand.Parameters.Add("@Files", SqliteType.Blob);
            var prerequisitesMetadataParam = insertMetadataCommand.Parameters.Add("@Prerequisites", SqliteType.Blob);

            using var insertSoftwareCommand = connection.CreateCommand();
            insertSoftwareCommand.Transaction = transaction;
            insertSoftwareCommand.CommandText = """
                    INSERT INTO SoftwareInformations (RevisionId, KbArticleId, Bundled)
                        VALUES (@RevisionId, @KbArticleId, jsonb(@Bundled));
                    """;
            var revisionIdSoftwareParam = insertSoftwareCommand.Parameters.Add("@RevisionId", SqliteType.Integer);
            var kbArticleIdSoftwareParam = insertSoftwareCommand.Parameters.Add("@KbArticleId", SqliteType.Text);
            var bundledSoftwareParam = insertSoftwareCommand.Parameters.Add("@Bundled", SqliteType.Blob);

            using var insertBundledCommand = connection.CreateCommand();
            insertBundledCommand.Transaction = transaction;
            insertBundledCommand.CommandText = """
                    INSERT INTO Bundled (RevisionId, Guid, Revision)
                        VALUES (@RevisionId, @Guid, @Revision)
                    """;
            var revisionIdBundledParam = insertBundledCommand.Parameters.Add("@RevisionId", SqliteType.Integer);
            var guidBundledParam = insertBundledCommand.Parameters.Add("@Guid", SqliteType.Text);
            var revisionBundledParam = insertBundledCommand.Parameters.Add("@Revision", SqliteType.Integer);

            using var insertSupersededCommand = connection.CreateCommand();
            insertSupersededCommand.Transaction = transaction;
            insertSupersededCommand.CommandText = """
                    INSERT INTO Superseded (RevisionId, SupersededGuid) VALUES (@RevisionId, @SupersededGuid)
                    """;
            var revisionIdSupersededParam = insertSupersededCommand.Parameters.Add("@RevisionId", SqliteType.Integer);
            var supersededGuidSupersededParam = insertSupersededCommand.Parameters.Add("@SupersededGuid", SqliteType.Text);

            using var addFileCommand = connection.CreateCommand();
            addFileCommand.Transaction = transaction;
            addFileCommand.CommandText = """
                INSERT INTO Files (FileDigest, Name, Size, ModifiedDate, Digests, Urls, PatchingType)
                    VALUES (@FileDigest, @Name, @Size, @ModifiedDate, jsonb(@Digests), jsonb(@Urls), @PatchingType)
                    ON CONFLICT(FileDigest) DO NOTHING
                """;
            var fileDigestFileParam = addFileCommand.Parameters.Add("@FileDigest", SqliteType.Text);
            var nameFileParam = addFileCommand.Parameters.Add("@Name", SqliteType.Text);
            var sizeFileParam = addFileCommand.Parameters.Add("@Size", SqliteType.Integer);
            var modifiedDateFileParam = addFileCommand.Parameters.Add("@ModifiedDate", SqliteType.Text);
            var digestsFileParam = addFileCommand.Parameters.Add("@Digests", SqliteType.Blob);
            var urlsFileParam = addFileCommand.Parameters.Add("@Urls", SqliteType.Blob);
            var patchingTypeFileParam = addFileCommand.Parameters.Add("@PatchingType", SqliteType.Text);

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

            insertIdentityCommand.Parameters["@Guid"].Value = microsoftUpdatePackageIdentity.ID;
            insertIdentityCommand.Parameters["@Revision"].Value = microsoftUpdatePackageIdentity.Revision;
            insertIdentityCommand.Parameters["@Title"].Value = package.Title;

            var identityId = (int)(long)insertIdentityCommand.ExecuteScalar();

            insertMetadataCommand.Parameters["@RevisionId"].Value = identityId;

            using (var metadataStream = package.GetMetadataStream())
            {
                // Encode to UTF-8 then gzip compress it to save space.
                using var encodeStream = Encoding.CreateTranscodingStream(metadataStream, Encoding.Unicode, Encoding.UTF8, true);
                using var valueStream = new MemoryStream();
                using (var compressStream = new GZipStream(valueStream, CompressionLevel.Optimal, true))
                {
                    encodeStream.CopyTo(compressStream);
                }

                insertMetadataCommand.Parameters["@Metadata"].Value = valueStream.ToArray();
            }

            List<Guid> categoryGuids = new();
            foreach (var prereq in microsoftUpdate.Prerequisites.OfType<AtLeastOne>().Where(p => p.IsCategory))
            {
                categoryGuids.AddRange(prereq.Simple.Select(s => s.UpdateId));
            }

            if (categoryGuids.Count > 0)
            {
                insertMetadataCommand.Parameters["@Categories"].Value =
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(categoryGuids));
            }
            else
            {
                insertMetadataCommand.Parameters["@Categories"].Value = DBNull.Value;
            }

            if (PartitionRegistration.TryGetPartitionFromPackage(package, out var partitionDefinition) &&
                partitionDefinition.HasExternalContentFileMetadata &&
                package.Files?.Any() == true)
            {
                var digests = JsonConvert.SerializeObject(package.Files.Select(f => f.Digest.DigestBase64));
                insertMetadataCommand.Parameters["@Files"].Value = Encoding.UTF8.GetBytes(digests);

                AddFiles(package.Files, addFileCommand);
            }
            else
            {
                insertMetadataCommand.Parameters["@Files"].Value = DBNull.Value;
            }

            if (microsoftUpdate.Prerequisites.Count > 0)
            {
                insertMetadataCommand.Parameters["@Prerequisites"].Value =
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(microsoftUpdate.Prerequisites));
            }
            else
            {
                insertMetadataCommand.Parameters["@Prerequisites"].Value = DBNull.Value;
            }

            insertMetadataCommand.ExecuteNonQuery();

            if (package is SoftwareUpdate softwareUpdate)
            {
                // Insert software update specific datas into database.
                insertSoftwareCommand.Parameters["@RevisionId"].Value = identityId;
                insertSoftwareCommand.Parameters["@KbArticleId"].Value =
                    (object)softwareUpdate.KBArticleId ?? DBNull.Value;
                if (softwareUpdate.BundledUpdates.Count > 0)
                {
                    insertSoftwareCommand.Parameters["@Bundled"].Value =
                        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(softwareUpdate.BundledUpdates));
                }
                else
                {
                    insertSoftwareCommand.Parameters["@Bundled"].Value = DBNull.Value;
                }

                insertSoftwareCommand.ExecuteNonQuery();

                // Insert bundled updates relationship into database.
                foreach (var bundled in softwareUpdate.BundledUpdates)
                {
                    insertBundledCommand.Parameters["@RevisionId"].Value = identityId;
                    insertBundledCommand.Parameters["@Guid"].Value = bundled.ID;
                    insertBundledCommand.Parameters["@Revision"].Value = bundled.Revision;

                    insertBundledCommand.ExecuteNonQuery();
                }

                // Insert superseded updates relationship into database.
                foreach (var superseded in softwareUpdate.SupersededUpdates)
                {
                    insertSupersededCommand.Parameters["@RevisionId"].Value = identityId;
                    insertSupersededCommand.Parameters["@SupersededGuid"].Value = superseded;

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
            updateTypeCommand.CommandText = "UPDATE Identities SET PackageType = @PackageType WHERE Id = @Id";
            updateTypeCommand.Parameters.Add("@PackageType", SqliteType.Integer).Value = packageType;
            updateTypeCommand.Parameters.Add("@Id", SqliteType.Integer).Value = packageIndex;

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
                addFileCommand.Parameters["@FileDigest"].Value = file.Digest.DigestBase64;
                addFileCommand.Parameters["@Name"].Value = file.FileName;
                addFileCommand.Parameters["@Size"].Value = file.Size;
                if (file is UpdateFile updateFile)
                {
                    addFileCommand.Parameters["@ModifiedDate"].Value =
                        updateFile.ModifiedDate.ToUniversalTime().ToString("o", DateTimeFormatInfo.InvariantInfo);
                    addFileCommand.Parameters["@Digests"].Value =
                        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(updateFile.Digests));
                    addFileCommand.Parameters["@Urls"].Value =
                        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(updateFile.Urls));
                    addFileCommand.Parameters["@PatchingType"].Value = updateFile.PatchingType;
                }
                else
                {
                    addFileCommand.Parameters["@ModifiedDate"].Value = "";
                    addFileCommand.Parameters["@Digests"].Value = (byte[])[];
                    addFileCommand.Parameters["@Urls"].Value = (byte[])[];
                    addFileCommand.Parameters["@PatchingType"].Value = "";
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
            SELECT f.Name, f.Size, f.ModifiedDate, json(f.Digests), json(f.Urls), f.PatchingType
            FROM Metadatas as m INNER JOIN json_each(m.Files) as d
                INNER JOIN Files as f ON f.FileDigest = d.value
            WHERE m.RevisionId = @RevisionId
            """;
            command.Parameters.Add("@RevisionId", SqliteType.Integer).Value = identityId;

            List<UpdateFile> files = new();
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
            command.CommandText = "SELECT Metadata FROM Metadatas WHERE RevisionId = @RevisionId";
            command.Parameters.Add("@RevisionId", SqliteType.Integer).Value = identityId;

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
            var metadataStream = GetMetadata(packageIdentity);
            if (metadataStream == null)
            {
                return null;
            }

            return MicrosoftUpdatePackage.FromStoredMetadataXml(metadataStream, this, this);
        }

        public IEnumerable<IPackageIdentity> GetPackageIdentities()
        {
            var identities = new List<IPackageIdentity>();

            using var connection = GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Guid, Revision FROM Identities";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var guid = reader.GetGuid(0);
                var revision = reader.GetInt32(1);
                identities.Add(new MicrosoftUpdatePackageIdentity(guid, revision));
            }

            return identities;
        }

        public int GetPackageIndex(IPackageIdentity packageIdentity)
        {
            if (packageIdentity is not MicrosoftUpdatePackageIdentity microsoftUpdatePackageIdentity)
            {
                return -1;
            }

            using var connection = GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM Identities WHERE Guid = @Guid AND Revision = @Revision";
            command.Parameters.Add("@Guid", SqliteType.Text).Value = microsoftUpdatePackageIdentity.ID;
            command.Parameters.Add("@Revision", SqliteType.Integer).Value = microsoftUpdatePackageIdentity.Revision;

            return (int)(command.ExecuteScalar() as long? ?? -1);
        }

        public IPackageIdentity GetPackageIdentity(int packageIndex)
        {
            using var connection = GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Guid, Revision FROM Identities WHERE Id = @Id";
            command.Parameters.Add("@Id", SqliteType.Integer).Value = packageIndex;

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
            command.CommandText = "SELECT PackageType FROM Identities WHERE Id = @Id";
            command.Parameters.Add("@Id", SqliteType.Integer).Value = packageIndex;

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
            command.Parameters.Add("@RevisionId", SqliteType.Integer).Value = packageIndex;

            switch (indexName)
            {
                case Index.AvailableIndexes.TitlesIndexName:
                    command.CommandText = "SELECT Title FROM Identities WHERE Id = @RevisionId";
                    var title = command.ExecuteScalar() as string;
                    if (!string.IsNullOrEmpty(title))
                    {
                        value = (T)Convert.ChangeType(title, typeof(T));
                        return true;
                    }
                    break;

                case AvailableIndexes.KbArticleIndexName:
                    command.CommandText = "SELECT KbArticleId FROM SoftwareInformations WHERE RevisionId = @RevisionId";
                    var kbArticle = command.ExecuteScalar() as string;
                    if (!string.IsNullOrEmpty(kbArticle))
                    {
                        value = (T)Convert.ChangeType(kbArticle, typeof(T));
                        return true;
                    }
                    break;

                case AvailableIndexes.CategoriesIndexName:
                    command.CommandText = "SELECT json(Categories) FROM Metadatas WHERE RevisionId = @RevisionId";
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
            command.Parameters.Add("@RevisionId", SqliteType.Integer).Value = packageIndex;

            switch (indexName)
            {
                case AvailableIndexes.PrerequisitesIndexName:
                    command.CommandText = """
                    SELECT p.value,
                    CASE
                        WHEN json_type(p.value, '$.Simple') IS NULL
                            THEN 1
                        ELSE 0
                    END AS IsSimple
                    FROM Metadatas AS m INNER JOIN json_each(m.Prerequisites) AS p
                    WHERE m.RevisionId = @RevisionId
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
                    SELECT f.Name, f.Size, f.ModifiedDate, json(f.Digests), json(f.Urls), f.PatchingType
                    FROM Metadatas as m INNER JOIN json_each(m.Files) as d
                        INNER JOIN Files as f ON f.FileDigest = d.value
                    WHERE m.RevisionId = @RevisionId
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
                    command.CommandText = "SELECT SupersededGuid FROM Superseded WHERE RevisionId = @RevisionId";
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
                    command.CommandText = "SELECT Guid, Revision FROM Bundled WHERE RevisionId = @RevisionId";
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
                        SELECT i.Guid, i.Revision
                            FROM Bundled as b INNER JOIN Identities as i
                            ON i.Id = b.RevisionId
                            WHERE b.Guid = @Guid AND b.Revision = @Revision
                        """;
                        command.Parameters.Add("@Guid", SqliteType.Text).Value = packageIdentity.ID;
                        command.Parameters.Add("@Revision", SqliteType.Integer).Value = packageIdentity.Revision;

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
                        SELECT i.Guid, i.Revision
                            FROM Superseded AS s INNER JOIN Identities AS i
                            ON i.Id = s.RevisionId 
                            WHERE s.SupersededGuid = @SupersededGuid
                        """;
                        command.Parameters.Add("@SupersededGuid", SqliteType.Text).Value = supersededGuid;

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

        public void CopyTo(IMetadataSink destination, IMetadataFilter filter, CancellationToken cancelToken)
        {
            var packageEntries = GetPackageIdentities(filter);

            var progressArgs = new PackageStoreEventArgs()
            {
                Total = packageEntries.Count,
                Current = 0
            };
            MetadataCopyProgress?.Invoke(this, progressArgs);
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

        private List<IPackageIdentity> GetPackageIdentities(IMetadataFilter filter)
        {
            if (filter is null)
            {
                return GetPackageIdentities().ToList();
            }

            var metadataFilter = filter as MetadataFilter;
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

            StringBuilder queryBuilder = new("SELECT DISTINCT i.Guid, i.Revision");
            StringBuilder tableBuilder = new("Identities AS i");
            StringBuilder whereBuilder = new("1=1");
            StringBuilder groupByBuilder = new();

            var requiresDriverFiltering = metadataFilter switch
            {
                { HardwareIdFilter: not null and { Length: > 0 } } => true,
                { ComputerHardwareIdFilter: var id } when id != Guid.Empty => true,
                _ => false
            };
            var hasProductFilter = metadataFilter is { ProductFilter.Count: > 0 };
            var hasClassificationFilter = metadataFilter is { ClassificationFilter.Count: > 0 };

            if (hasProductFilter || hasClassificationFilter)
            {
                tableBuilder.Append("""

                INNER JOIN Metadatas AS m ON i.Id = m.RevisionId
                INNER JOIN json_each(m.Categories) AS c
                """);
                groupByBuilder.Append("i.Guid, i.Revision\nHAVING 1=1");

                List<string> idParamList = [];

                if (hasProductFilter)
                {
                    var productParams = metadataFilter.ProductFilter.Select((id, index) =>
                    {
                        var paramName = $"@Product{index}";
                        command.Parameters.Add(paramName, SqliteType.Text).Value = id;

                        return paramName;
                    }).ToList();
                    groupByBuilder.Append($"\nAND count(CASE WHEN c.value COLLATE NOCASE IN ({string.Join(",", productParams)}) THEN 1 END) > 0");
                    idParamList.AddRange(productParams);
                }

                if (hasClassificationFilter)
                {
                    var classificationParams = metadataFilter.ClassificationFilter.Select((id, index) =>
                    {
                        var paramName = $"@Classification{index}";
                        command.Parameters.Add(paramName, SqliteType.Text).Value = id;

                        return paramName;
                    }).ToList();
                    groupByBuilder.Append($"\nAND count(CASE WHEN c.value COLLATE NOCASE IN ({string.Join(",", classificationParams)}) THEN 1 END) > 0");
                    idParamList.AddRange(classificationParams);
                }

                whereBuilder.Append($"\nAND c.VALUE COLLATE NOCASE IN ({string.Join(",", idParamList)})");
            }

            if (!string.IsNullOrEmpty(metadataFilter.TitleFilter))
            {
                whereBuilder.Append("\nAND i.Title LIKE @Title");
                command.Parameters.Add("@Title", SqliteType.Text).Value = $"%{filter.TitleFilter}%";
            }

            if (filter.IdFilter?.Any() == true)
            {
                var index = 0;
                List<string> idParams = [];
                foreach (var id in filter.IdFilter)
                {
                    var paramName = $"@Id{index}";
                    idParams.Add(paramName);
                    command.Parameters.Add(paramName, SqliteType.Text).Value = id;
                    index++;
                }

                whereBuilder.Append($"\nAND i.Guid IN ({string.Join(",", idParams)})");
            }

            if (metadataFilter is { KbArticleFilter.Count: > 0 })
            {
                tableBuilder.Append("\nINNER JOIN SoftwareInformations AS si ON si.RevisionId = i.Id");

                var index = 0;
                List<string> kbParams = [];
                foreach (var kb in metadataFilter.KbArticleFilter)
                {
                    var paramName = $"@Kb{index}";
                    kbParams.Add(paramName);
                    command.Parameters.Add(paramName, SqliteType.Text).Value = kb;
                    index++;
                }

                whereBuilder.Append($"\nAND si.KbArticleId IN ({string.Join(",", kbParams)})");
            }

            if (metadataFilter is { SkipSuperseded: true })
            {
                whereBuilder.Append("\nAND NOT EXISTS (SELECT 1 FROM Superseded AS sup WHERE sup.SupersededGuid = i.Guid)");
            }

            queryBuilder.Append($" FROM {tableBuilder}\nWHERE {whereBuilder}");

            if (groupByBuilder.Length > 0)
            {
                queryBuilder.Append($"\nGROUP BY {groupByBuilder}");
            }

            if (metadataFilter is { FirstX: > 0 } && !requiresDriverFiltering)
            {
                queryBuilder.Append("\nLIMIT @Limit");
                command.Parameters.Add("@Limit", SqliteType.Integer).Value = metadataFilter.FirstX;
            }

            command.CommandText = queryBuilder.ToString();

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var guid = reader.GetGuid(0);
                var revision = reader.GetInt32(1);

                identities.Add(new MicrosoftUpdatePackageIdentity(guid, revision));
            }

            // TODO push the post-filter limit into SQL by materializing driver metadata indexes.
            if (metadataFilter is not null && requiresDriverFiltering)
            {
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

                    if (metadataFilter.ComputerHardwareIdFilter != Guid.Empty)
                    {
                        var computerMatch = metadata.Any(md =>
                            md.DistributionComputerHardwareId.Contains(metadataFilter.ComputerHardwareIdFilter));
                        if (!computerMatch)
                        {
                            continue;
                        }
                    }

                    driverMatches.Add(identity);
                }

                if (metadataFilter.FirstX > 0 && identities.Count > metadataFilter.FirstX)
                {
                    identities = driverMatches.Take(metadataFilter.FirstX).ToList();
                }
                else
                {
                    identities = driverMatches;
                }
            }

            return identities;
        }
    }
}
