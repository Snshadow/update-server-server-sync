// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
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
    class SqliteMetadataBackingStore : DbContext, MetadataBackingStore, IMetadataSink, IMetadataSource
    {
        private readonly SqliteConnection _connection;
        private bool _isDisposed;
        public event EventHandler<PackageStoreEventArgs> MetadataCopyProgress;

#pragma warning disable 0067
        public event EventHandler<PackageStoreEventArgs> OpenProgress;
        public event EventHandler<PackageStoreEventArgs> PackagesAddProgress;
#pragma warning restore 0067

        private static readonly ThreadLocal<SqliteTransaction> _currentTransaction = new();

        public SqliteMetadataBackingStore(string path, FileMode mode)
        {
            var dbPath = Path.Combine(path, "metadata.db");
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

            _connection = new SqliteConnection($"Data Source={dbPath};");
            _connection.Open();

            // Enable WAL(Write-Ahead Logging) for performance.
            using var walCommand = _connection.CreateCommand();
            walCommand.CommandText = "PRAGMA journal_mode = 'WAL'";
            walCommand.ExecuteNonQuery();

            InitializeDatabase();
        }

        public override SqliteConnection GetConnection() => _connection;

        protected override void InitializeDatabase()
        {
            using var command = _connection.CreateCommand();
            /* Updates: Contains the stored identities(category, update, etc..)
             *  Id -> server specific update id(revision id)
             *  Guid -> global update guid
             *  Revision -> global update revision number
             *  PackageType -> the type of the package
             * Files: Contains file information used for updates
             *  FileDigest -> primary file digest as hex string
             *  Digests -> jsonb object containing array of content file digest
             *  Urls -> jsonb object containing array of original download url
             * Metadatas: Contains full metadata xml and file digests
             *  RevisionId -> server specific update id
             *  Metadata -> update metadata xml
             *  Files -> jsonb objects containing list of files primary hex digest
             * Superseded: Contains superseded update ids for updates
             *  RevisionId -> server specific update id
             *  SupersededId -> supseded update id
             */
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS Identities (
                Id INTEGER PRIMARY KEY,
                Guid TEXT NOT NULL,
                Revision INTEGER NOT NULL,
                PackageType INTEGER NOT NULL,
                Title TEXT NOT NULL,
                KbArticleId TEXT,
                Prerequisites BLOB,
                Bundled BLOB,
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
                Files BLOB,
                FOREIGN KEY (RevisionId) REFERENCES Identities(Id)
            );
            CREATE TABLE IF NOT EXISTS Superseded (
                RevisionId INTEGER NOT NULL,
                SupersededId INTEGER NOT NULL,
                PRIMARY KEY (RevisionId, SupersededId),
                FOREIGN KEY (RevisionId) REFERENCES Identities(Id),
                FOREIGN KEY (SupersededId) REFERENCES Identities(Id)
            ) WITHOUT ROWID;
            """;
            command.ExecuteNonQuery();
        }

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                _connection.Dispose();
                _isDisposed = true;
            }
            GC.SuppressFinalize(this);
        }

        public int PackageCount
        {
            get
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM Identities";
                return command.ExecuteScalar() as int? ?? 0;
            }
        }

        public void AddPackages(IEnumerable<IPackage> packages)
        {
            using var transaction = _connection.BeginTransaction();
            _currentTransaction.Value = transaction;
            try
            {
                foreach (var package in packages)
                {
                    AddPackage(package);
                }
                transaction.Commit();
            }
            finally
            {
                _currentTransaction.Value = null;
            }
        }

        public void AddPackage(IPackage package)
        {
            if (package?.Id is not MicrosoftUpdatePackageIdentity microsoftUpdatePackageIdentity)
            {
                return;
            }

            var transaction = _currentTransaction.Value;

            var identityId = GetPackageIndex(microsoftUpdatePackageIdentity);
            if (identityId == -1)
            {
                using var insertIdentityCommand = _connection.CreateCommand();
                if (transaction is not null)
                {
                    insertIdentityCommand.Transaction = transaction;
                }
                insertIdentityCommand.CommandText = """
                INSERT INTO Identities (Guid, Revision, PackageType)
                    VALUES (@Guid, @Revision, @PackageType);
                SELECT last_insert_rowid();
                """;
                insertIdentityCommand.Parameters.Add("@Guid", SqliteType.Text).Value = microsoftUpdatePackageIdentity.ID;
                insertIdentityCommand.Parameters.Add("@Revision", SqliteType.Integer).Value = microsoftUpdatePackageIdentity.Revision;
                insertIdentityCommand.Parameters.Add("@PackageType", SqliteType.Integer).Value = 0; // TODO need implementation
                identityId = (int)insertIdentityCommand.ExecuteScalar();
            }

            using var insertMetadataCommand = _connection.CreateCommand();
            if (transaction is not null)
            {
                insertMetadataCommand.Transaction = transaction;
            }
            insertMetadataCommand.CommandText = """
            INSERT INTO Metadata (IdentityId, Metadata, Files)
                VALUES (@IdentityId, zeroblob(@length), jsonb(@Files));
            SELECT last_insert_rowid();
            """;
            insertMetadataCommand.Parameters.Add("@IdentityId", SqliteType.Integer).Value = identityId;

            using var metadataStream = package.GetMetadataStream();
            insertMetadataCommand.Parameters.Add("@length", SqliteType.Integer).Value = metadataStream.Length;

            if (PartitionRegistration.TryGetPartitionFromPackage(package, out var partitionDefinition) &&
                    partitionDefinition.HasExternalContentFileMetadata &&
                    package.Files?.Any() == true)
            {
                var digests = JsonConvert.SerializeObject(package.Files.Select(f => f.Digest.DigestBase64));
                insertMetadataCommand.Parameters.Add("@Files", SqliteType.Blob).Value = Encoding.UTF8.GetBytes(digests);

                AddFiles(package.Files, transaction);
            }
            else
            {
                insertMetadataCommand.Parameters.Add("@Files", SqliteType.Blob).Value = DBNull.Value;
            }

            var rowid = (long)insertMetadataCommand.ExecuteScalar();

            // Copy metadata stream into SqliteBlob
            using var writeStream = new SqliteBlob(_connection, "Identities", "Metadata", rowid);
            metadataStream.CopyTo(writeStream);
        }

        private void AddFiles(IEnumerable<IContentFile> files, SqliteTransaction transaction)
        {
            // TODO encapsulate this for other types?
            foreach (var file in files)
            {
                using var addFileCommand = _connection.CreateCommand();
                if (transaction is not null)
                {
                    addFileCommand.Transaction = transaction;
                }
                addFileCommand.CommandText = """
                INSERT INTO Files (FileDigest, Name, ModifiedDate, Size, Digests, Urls, PatchingType)
                    VALUES (@FileDigest, @Name, @ModifiedDate, @Size, jsonb(@Digests), jsonb(@Urls), @PatchingType)
                    ON CONFLICT(FileDigest) DO NOTHING
                """;
                addFileCommand.Parameters.Add("@FileDigest", SqliteType.Text).Value = file.Digest.DigestBase64;
                addFileCommand.Parameters.Add("@Name", SqliteType.Text).Value = file.FileName;
                addFileCommand.Parameters.Add("@File", SqliteType.Integer).Value = file.Size;
                if (file is UpdateFile updateFile)
                {
                    var modifiedDate = updateFile.ModifiedDate.ToString("o", DateTimeFormatInfo.InvariantInfo);
                    addFileCommand.Parameters.Add("@ModifiedDate", SqliteType.Text).Value = modifiedDate;
                    var digests = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(updateFile.Digests));
                    addFileCommand.Parameters.Add("@Digests", SqliteType.Blob).Value = digests;
                    var urls = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(updateFile.Urls));
                    addFileCommand.Parameters.Add("@Urls", SqliteType.Blob).Value = urls;
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
            // TODO should get data from dedicated table
            var identityId = GetPackageIndex(packageIdentity);
            if (identityId == -1)
            {
                return null;
            }

            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Files FROM Metadata WHERE IdentityId = @IdentityId";
            command.Parameters.Add("@IdentityId", SqliteType.Integer).Value = identityId;
            var result = Encoding.UTF8.GetString((byte[])command.ExecuteScalar());

            return JsonConvert.DeserializeObject<List<T>>(result);
        }

        public Stream GetMetadata(IPackageIdentity packageIdentity)
        {
            var identityId = GetPackageIndex(packageIdentity);
            if (identityId == -1)
            {
                return null;
            }

            var command = _connection.CreateCommand();
            command.CommandText = "SELECT Metadata FROM Metadatas WHERE RevisionId = @IdentityId";
            command.Parameters.Add("@IdentityId", SqliteType.Integer).Value = identityId;

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
            using var command = _connection.CreateCommand();
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

            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Id FROM Identities WHERE Guid = @Guid AND Revision = @Revision";
            command.Parameters.Add("@Guid", SqliteType.Text).Value = microsoftUpdatePackageIdentity.ID;
            command.Parameters.Add("@Revision", SqliteType.Integer).Value = microsoftUpdatePackageIdentity.Revision;

            return command.ExecuteScalar() as int? ?? -1;
        }

        public IPackageIdentity GetPackageIdentity(int packageIndex)
        {
            using var command = _connection.CreateCommand();
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
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT PackageType FROM Identities WHERE Id = @Id";
            command.Parameters.Add("@Id", SqliteType.Integer).Value = packageIndex;

            return command.ExecuteScalar() as int? ?? -1;
        }

        /// <summary>
        /// Read-only stream used for reading SQLIte BLOB data.
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
