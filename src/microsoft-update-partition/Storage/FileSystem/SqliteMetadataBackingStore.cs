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

        private readonly ThreadSafeSqliteConnection _connection;
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

            _connection = new ThreadSafeSqliteConnection($"Data Source={dbPath};");

            // Enable WAL(Write-Ahead Logging) for performance.
            using var walCommand = _connection.Connection.CreateCommand();
            walCommand.CommandText = "PRAGMA journal_mode = 'WAL'";
            walCommand.ExecuteNonQuery();

            // Request optimization for all tables.
            using var optimizeCommand = _connection.Connection.CreateCommand();
            optimizeCommand.CommandText = "PRAGMA optimize = 0x10002";
            optimizeCommand.ExecuteNonQuery();

            InitializeDatabase();
        }

        public override SqliteConnection GetConnection() => _connection.Connection;

        protected override void InitializeDatabase()
        {
            using var command = _connection.Connection.CreateCommand();
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
             *  Bundled -> jsonb object containing array of id of updates bundled in this update
             * Superseded: Contains superseded update ids for updates
             *  RevisionId -> server specific update id
             *  SupersededGuid -> supseded update global GUID
             */
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS Identities (
                Id INTEGER PRIMARY KEY,
                Guid TEXT NOT NULL,
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
                Prerequisties BLOB,
                FOREIGN KEY (RevisionId) REFERENCES Identities(Id)
            );
            CREATE TABLE IF NOT EXISTS SoftwareInformations (
                RevisionId INTEGER PRIMARY KEY,
                KbArticleId TEXT,
                Bundled BLOB,
                UNIQUE(KbArticleId),
                FOREIGN KEY (RevisionId) REFERENCES Identities(Id)
            );
            CREATE TABLE IF NOT EXISTS Superseded (
                RevisionId INTEGER NOT NULL,
                SupersededGuid TEXT NOT NULL,
                PRIMARY KEY (RevisionId, SupersededGuid),
                FOREIGN KEY (RevisionId) REFERENCES Identities(Id)
            ) WITHOUT ROWID;
            """;
            command.ExecuteNonQuery();
        }

        public override void Dispose()
        {
            _connection.Dispose();
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
                    using var checkCommand = _connection.Connection.CreateCommand();
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
            // Optimize the database before checking.
            using (var optimizeCommand = _connection.Connection.CreateCommand())
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
            using var reindexCommand = _connection.Connection.CreateCommand();
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
                using var command = _connection.Connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM Identities";
                return (int)(command.ExecuteScalar() as long? ?? 0);
            }
        }

        public void AddPackages(IEnumerable<IPackage> packages)
        {
            using var transaction = _connection.Connection.BeginTransaction();

            foreach (var package in packages)
            {
                AddPackage(package);
            }

            transaction.Commit();
        }

        void IMetadataSink.AddPackage(IPackage package)
        {
            _ = AddPackage(package);
        }

        public int AddPackage(IPackage package)
        {
            if (package is not MicrosoftUpdatePackage microsoftUpdate ||
                microsoftUpdate.Id is not MicrosoftUpdatePackageIdentity microsoftUpdatePackageIdentity)
            {
                return -1;
            }

            int identityId;
            using (var insertIdentityCommand = _connection.Connection.CreateCommand())
            {
                insertIdentityCommand.CommandText = """
                INSERT INTO Identities (Guid, Revision, Title)
                    VALUES (@Guid, @Revision, @Title);
                SELECT last_insert_rowid();
                """;
                insertIdentityCommand.Parameters.Add("@Guid", SqliteType.Text).Value = microsoftUpdatePackageIdentity.ID;
                insertIdentityCommand.Parameters.Add("@Revision", SqliteType.Integer).Value = microsoftUpdatePackageIdentity.Revision;
                insertIdentityCommand.Parameters.Add("@Title", SqliteType.Text).Value = package.Title;

                identityId = (int)(long)insertIdentityCommand.ExecuteScalar();
            }

            using (var insertMetadataCommand = _connection.Connection.CreateCommand())
            {
                insertMetadataCommand.CommandText = """
                INSERT INTO Metadatas (RevisionId, Metadata, Categories, Files)
                    VALUES (@RevisionId, @Metadata, jsonb(@Categories), jsonb(@Files))
                """;
                insertMetadataCommand.Parameters.Add("@RevisionId", SqliteType.Integer).Value = identityId;

                using (var metadataStream = package.GetMetadataStream())
                {
                    using var valueStream = new MemoryStream();
                    metadataStream.CopyTo(valueStream);

                    insertMetadataCommand.Parameters.Add("@Metadata", SqliteType.Blob).Value = valueStream.ToArray();
                }

                List<Guid> categoryGuids = new();
                foreach (var prereq in microsoftUpdate.Prerequisites.OfType<AtLeastOne>().Where(p => p.IsCategory))
                {
                    categoryGuids.AddRange(prereq.Simple.Select(s => s.UpdateId));
                }

                if (categoryGuids.Count > 0)
                {
                    insertMetadataCommand.Parameters.Add("@Categories", SqliteType.Blob).Value =
                        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(categoryGuids));
                }
                else
                {
                    insertMetadataCommand.Parameters.Add("@Categories", SqliteType.Blob).Value = DBNull.Value;
                }

                if (PartitionRegistration.TryGetPartitionFromPackage(package, out var partitionDefinition) &&
                    partitionDefinition.HasExternalContentFileMetadata &&
                    package.Files?.Any() == true)
                {
                    var digests = JsonConvert.SerializeObject(package.Files.Select(f => f.Digest.DigestBase64));
                    insertMetadataCommand.Parameters.Add("@Files", SqliteType.Blob).Value = Encoding.UTF8.GetBytes(digests);

                    AddFiles(package.Files);
                }
                else
                {
                    insertMetadataCommand.Parameters.Add("@Files", SqliteType.Blob).Value = DBNull.Value;
                }

                if (microsoftUpdate.Prerequisites.Count > 0)
                {
                    insertMetadataCommand.Parameters.Add("@Prerequisites", SqliteType.Blob).Value =
                        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(microsoftUpdate.Prerequisites));
                }
                else
                {
                    insertMetadataCommand.Parameters.Add("@Prerequisites", SqliteType.Blob).Value = DBNull.Value;
                }

                insertMetadataCommand.ExecuteNonQuery();
            }

            if (package is SoftwareUpdate softwareUpdate)
            {
                // Insert software update specific datas into database.
                using (var insertSoftwareCommand = _connection.Connection.CreateCommand())
                {
                    insertSoftwareCommand.CommandText = """
                    INSERT INTO SoftwareInformations (RevisionId, KbArticleId, Bundled)
                        VALUES (@RevisionId, @KbArticleId, jsonb(@Bundled));
                    """;
                    insertSoftwareCommand.Parameters.Add("@RevisionId", SqliteType.Integer).Value = identityId;
                    insertSoftwareCommand.Parameters.Add("@KbArticleId", SqliteType.Text).Value = softwareUpdate.KBArticleId;
                    insertSoftwareCommand.Parameters.Add("@Bundled", SqliteType.Blob).Value =
                        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(softwareUpdate.BundledUpdates));

                    insertSoftwareCommand.ExecuteNonQuery();
                }

                // Insert superseded updates relationship into database.
                foreach (var superseded in softwareUpdate.SupersededUpdates)
                {
                    using var insertSupersededCommand = _connection.Connection.CreateCommand();
                    insertSupersededCommand.CommandText = """
                    INSERT INTO Superseded (RevisionId, SupersededGuid) VALUES (@RevisionId, @SupersededGuid)
                    """;
                    insertSupersededCommand.Parameters.Add("@RevisionId", SqliteType.Integer).Value = identityId;
                    insertSupersededCommand.Parameters.Add("@SupersededGuid", SqliteType.Text).Value = superseded;

                    insertSupersededCommand.ExecuteNonQuery();
                }
            }

            PendingPackages.Add(package);

            return identityId;
        }

        public void AddPackageType(int packageIndex, int packageType)
        {
            using var updateTypeCommand = _connection.Connection.CreateCommand();
            updateTypeCommand.CommandText = "UPDATE Identities SET PackageType = @PackageType WHERE Id = @Id";
            updateTypeCommand.Parameters.Add("@PackageType", SqliteType.Integer).Value = packageType;
            updateTypeCommand.Parameters.Add("@Id", SqliteType.Integer).Value = packageIndex;

            var affected = updateTypeCommand.ExecuteNonQuery();
            if (affected != 1)
            {
                throw new InvalidDataException($"Expected 1 row affected, instead got ${affected} row(s).");
            }
        }

        private void AddFiles(IEnumerable<IContentFile> files)
        {
            // TODO encapsulate this for other types?
            foreach (var file in files)
            {
                using var addFileCommand = _connection.Connection.CreateCommand();
                addFileCommand.CommandText = """
                INSERT INTO Files (FileDigest, Name, Size, ModifiedDate, Digests, Urls, PatchingType)
                    VALUES (@FileDigest, @Name, @Size, @ModifiedDate, jsonb(@Digests), jsonb(@Urls), @PatchingType)
                    ON CONFLICT(FileDigest) DO NOTHING
                """;
                addFileCommand.Parameters.Add("@FileDigest", SqliteType.Text).Value = file.Digest.DigestBase64;
                addFileCommand.Parameters.Add("@Name", SqliteType.Text).Value = file.FileName;
                addFileCommand.Parameters.Add("@Size", SqliteType.Integer).Value = file.Size;
                if (file is UpdateFile updateFile)
                {
                    addFileCommand.Parameters.Add("@ModifiedDate", SqliteType.Text).Value =
                        updateFile.ModifiedDate.ToString("o", DateTimeFormatInfo.InvariantInfo);
                    addFileCommand.Parameters.Add("@Digests", SqliteType.Blob).Value =
                        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(updateFile.Digests));
                    addFileCommand.Parameters.Add("@Urls", SqliteType.Blob).Value =
                        Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(updateFile.Urls));
                    addFileCommand.Parameters.Add("@PatchingType", SqliteType.Text).Value = updateFile.PatchingType;
                }
                else
                {
                    addFileCommand.Parameters.Add("@ModifiedDate", SqliteType.Text).Value = "";
                    addFileCommand.Parameters.Add("@Digests", SqliteType.Blob).Value = (byte[])[];
                    addFileCommand.Parameters.Add("@Urls", SqliteType.Blob).Value = (byte[])[];
                    addFileCommand.Parameters.Add("@PatchingType", SqliteType.Text).Value = "";
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

        public List<T> GetFiles<T>(IPackageIdentity packageIdentity)
        {
            var identityId = GetPackageIndex(packageIdentity);
            if (identityId == -1)
            {
                return [];
            }

            string digests;
            using (var command = _connection.Connection.CreateCommand())
            {
                command.CommandText = "SELECT json(Files) FROM Metadatas WHERE RevisionId = @RevisionId";
                command.Parameters.Add("@RevisionId", SqliteType.Integer).Value = identityId;
                digests = command.ExecuteScalar() as string;
                if (string.IsNullOrEmpty(digests))
                {
                    return [];
                }
            }

            List<UpdateFile> files = new();
            var digestList = JsonConvert.DeserializeObject<List<string>>(digests);
            foreach (var digest in digestList)
            {
                using var getFileCommand = _connection.Connection.CreateCommand();
                getFileCommand.CommandText = """
                SELECT Name, Size, ModifiedDate, json(Digests), json(Urls), PatchingType
                    FROM Files WHERE FileDigest = @FileDigest
                """;
                getFileCommand.Parameters.Add("@FileDigest", SqliteType.Text).Value = digest;
                var reader = getFileCommand.ExecuteReader();
                while (reader.Read())
                {
                    var deserializer = new JsonSerializer();
                    List<ContentFileDigest> fileDigests;
                    List<UpdateFileUrl> fileUrls;
                    using (var digestStream = new StreamReader(reader.GetStream(3)))
                    {
                        fileDigests = deserializer.Deserialize(digestStream, typeof(List<ContentFileDigest>)) as List<ContentFileDigest>;
                    }
                    using (var urlStream = new StreamReader(reader.GetStream(4)))
                    {
                        fileUrls = deserializer.Deserialize(urlStream, typeof(List<UpdateFileUrl>)) as List<UpdateFileUrl>;
                    }

                    files.Add(new UpdateFile()
                    {
                        FileName = reader.GetString(0),
                        Size = (ulong)reader.GetInt64(1),
                        ModifiedDate = reader.GetDateTime(2),
                        Digests = fileDigests,
                        Urls = fileUrls,
                        PatchingType = reader.GetString(5)
                    });
                }
            }

            return files.Cast<T>().ToList();
        }

        public Stream GetMetadata(IPackageIdentity packageIdentity)
        {
            var identityId = GetPackageIndex(packageIdentity);
            if (identityId == -1)
            {
                return null;
            }

            var command = _connection.Connection.CreateCommand();
            command.CommandText = "SELECT Metadata FROM Metadatas WHERE RevisionId = @RevisionId";
            command.Parameters.Add("@RevisionId", SqliteType.Integer).Value = identityId;

            var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                reader.Dispose();
                command.Dispose();
                return null;
            }

            return new BlobStream(command, reader);
        }

        public IPackage GetPackage(IPackageIdentity packageIdentity)
        {
            var metadataStream = GetMetadata(packageIdentity);
            if (metadataStream == null)
            {
                return null;
            }

            return MicrosoftUpdatePackage.FromStoredMetadataXml(metadataStream, this);
        }

        public IEnumerable<IPackageIdentity> GetPackageIdentities()
        {
            var identities = new List<IPackageIdentity>();
            using var command = _connection.Connection.CreateCommand();
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

            using var command = _connection.Connection.CreateCommand();
            command.CommandText = "SELECT Id FROM Identities WHERE Guid = @Guid AND Revision = @Revision";
            command.Parameters.Add("@Guid", SqliteType.Text).Value = microsoftUpdatePackageIdentity.ID;
            command.Parameters.Add("@Revision", SqliteType.Integer).Value = microsoftUpdatePackageIdentity.Revision;

            return (int)(command.ExecuteScalar() as long? ?? -1);
        }

        public IPackageIdentity GetPackageIdentity(int packageIndex)
        {
            using var command = _connection.Connection.CreateCommand();
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
            using var command = _connection.Connection.CreateCommand();
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

            using var command = _connection.Connection.CreateCommand();
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
                value = [];
                return false;
            }

            using var command = _connection.Connection.CreateCommand();
            command.Parameters.Add("@RevisionId", SqliteType.Integer).Value = packageIndex;

            switch (indexName)
            {
                case AvailableIndexes.PrerequisitesIndexName:
                    command.CommandText = "SELECT json(Prerequisties) FROM Metadatas WHERE RevisionId = @RevisionId";
                    var prereq = command.ExecuteScalar() as string;
                    if (!string.IsNullOrEmpty(prereq))
                    {
                        value = JsonConvert.DeserializeObject<List<T>>(prereq);
                        return true;
                    }
                    break;

                case AvailableIndexes.FilesIndexName:
                    command.CommandText = "SELECT json(Files) FROM Metadatas WHERE RevisionId = @RevisionId";
                    var files = command.ExecuteScalar() as string;
                    if (!string.IsNullOrEmpty(files))
                    {
                        value = JsonConvert.DeserializeObject<List<T>>(files);
                        return true;
                    }
                    break;

                case AvailableIndexes.IsSupersedingIndexName:
                    command.CommandText = "SELECT SupersededGuid FROM Superseded WHERE RevisionId = @RevisionId";
                    List<Guid> guids = new();
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            guids.Add(reader.GetGuid(0));
                        }
                    }
                    if (typeof(T) == typeof(Guid))
                    {
                        value = guids.Cast<T>().ToList();
                        return true;
                    }
                    break;

                case AvailableIndexes.IsBundleIndexName:
                    command.CommandText = "SELECT Bundled FROM SoftwareInformations WHERE RevisionId = @RevisionId";
                    var bundled = command.ExecuteScalar() as string;
                    if (!string.IsNullOrEmpty(bundled))
                    {
                        value = JsonConvert.DeserializeObject<List<T>>(bundled);
                        return true;
                    }
                    break;

                case AvailableIndexes.DriverMetadataIndexName:
                    // TODO implement this
                    break;

                default:
                    throw new NotImplementedException($"Index '{indexName}' not implemented for list key lookup.");
            }

            value = [];
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
            using var command = _connection.Connection.CreateCommand();

            switch (indexName)
            {
                case AvailableIndexes.BundledWithIndexName:
                    var packageIndex = GetPackageIndex(key as MicrosoftUpdatePackageIdentity);
                    if (packageIndex == -1)
                    {
                        value = [];
                        return false;
                    }

                    command.CommandText = "SELECT RevisionId FROM SoftwareInformations, json_each(Bundled) WHERE json_each.value = @BundledId";
                    command.Parameters.Add("@BundledId", SqliteType.Integer).Value = packageIndex;

                    var bundledWith = new List<int>();
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            bundledWith.Add(reader.GetInt32(0));
                        }
                    }
                    value = bundledWith.Select(GetPackageIdentity).Where(id => id is not null).ToList();
                    return true;

                default:
                    throw new NotImplementedException($"Index '{indexName}' not implemented for package list lookup by custom key.");
            }
        }

        /// <summary>
        /// Read-only stream used for reading SQLite BLOB data.
        /// </summary>
        private class BlobStream : Stream
        {
            private readonly SqliteCommand _command;
            private readonly SqliteDataReader _reader;
            private readonly Stream _blobStream;

            public BlobStream(SqliteCommand command, SqliteDataReader reader)
            {
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
                }
                base.Dispose(disposing);
            }
        }

        public void CopyTo(IMetadataSink destination, CancellationToken cancelToken)
        {
            var packageEntries = GetPackageIdentities();

            var progressArgs = new PackageStoreEventArgs() { Total = packageEntries.Count(), Current = 0 };
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
            throw new NotImplementedException();
        }
    }
}
