// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.ObjectModel;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Stores metadata in a SQLite database.
    /// </summary>
    class SqliteMetadataBackingStore : DbContext, IMetadataBackingStore, IMetadataSource
    {
        private readonly SqliteConnection _connection;
        private bool _isDisposed;

#pragma warning disable 0067
        public event EventHandler<PackageStoreEventArgs> MetadataCopyProgress;
        public event EventHandler<PackageStoreEventArgs> OpenProgress;
#pragma warning restore 0067

        private static readonly ThreadLocal<SqliteTransaction> _currentTransaction = new();

        public SqliteMetadataBackingStore(string databasePath, FileMode mode)
        {
            if (mode == FileMode.CreateNew || mode == FileMode.Create)
            {
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }

            _connection = new SqliteConnection($"Data Source={databasePath}");
            _connection.Open();
            InitializeDatabase();
        }

        public override SqliteConnection GetConnection() => _connection;

        protected override void InitializeDatabase()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Identities (
                    Id INTEGER PRIMARY KEY,
                    Guid TEXT NOT NULL,
                    Revision INTEGER NOT NULL,
                    PackageType TEXT NOT NULL,
                    UNIQUE(Guid, Revision)
                );
                CREATE TABLE IF NOT EXISTS Metadata (
                    IdentityId INTEGER PRIMARY KEY,
                    Metadata BLOB NOT NULL,
                    Files TEXT,
                    FOREIGN KEY (IdentityId) REFERENCES Identities(Id)
                );
            ";
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
                return Convert.ToInt32(command.ExecuteScalar());
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
                insertIdentityCommand.CommandText = @"
                    INSERT INTO Identities (Guid, Revision, PackageType) 
                        VALUES (@Guid, @Revision, @PackageType);
                    SELECT last_insert_rowid();
                ";
                insertIdentityCommand.Parameters.AddWithValue("@Guid", microsoftUpdatePackageIdentity.ID.ToString());
                insertIdentityCommand.Parameters.AddWithValue("@Revision", microsoftUpdatePackageIdentity.Revision);
                insertIdentityCommand.Parameters.AddWithValue("@PackageType", package.GetType().Name); // TODO need change do not use reflection
                identityId = insertIdentityCommand.ExecuteScalar() as int? ?? 0;
            }

            using var insertMetadataCommand = _connection.CreateCommand();
            insertMetadataCommand.CommandText = "INSERT OR REPLACE INTO Metadata (IdentityId, Metadata, Files) VALUES (@IdentityId, @Metadata, @Files)";
            insertMetadataCommand.Parameters.AddWithValue("@IdentityId", identityId);

            using var metadataStream = package.GetMetadataStream();
            using var memoryStream = new MemoryStream();
            metadataStream.CopyTo(memoryStream);
            insertMetadataCommand.Parameters.AddWithValue("@Metadata", memoryStream.ToArray());

            if (package.Files?.Any() ?? false)
            {
                // TODO file should have IContentFile not just name
                insertMetadataCommand.Parameters.AddWithValue("@Files", JsonConvert.SerializeObject(package.Files.Select(f => f.FileName)));
            }
            else
            {
                insertMetadataCommand.Parameters.AddWithValue("@Files", DBNull.Value);
            }

            insertMetadataCommand.ExecuteNonQuery();
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

        public bool ContainsPackage(IPackageIdentity packageIdentity)
        {
            return GetPackageIndex(packageIdentity) != -1;
        }

        public void Flush()
        {
        }

        public IEnumerator<IPackage> GetEnumerator()
        {
            var identities = GetPackageIdentities();
            return identities.Select(GetPackage).GetEnumerator();
        }

        public List<T> GetFiles<T>(IPackageIdentity packageIdentity, IFileFactory<T> factory)
        {
            var identityId = GetPackageIndex(packageIdentity);
            if (identityId == -1)
            {
                return null;
            }

            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Files FROM Metadata WHERE IdentityId = @IdentityId";
            command.Parameters.AddWithValue("@IdentityId", identityId);

            var result = command.ExecuteScalar();
            if (result == null || result == DBNull.Value)
            {
                return new List<T>();
            }

            var filesString = (string)result;
            var fileNames = JsonConvert.DeserializeObject<List<string>>(filesString);
            return fileNames.Select(factory.Create).ToList();
        }

        public Stream GetMetadata(IPackageIdentity packageIdentity)
        {
            var identityId = GetPackageIndex(packageIdentity);
            if (identityId == -1)
            {
                return null;
            }

            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Metadata FROM Metadata WHERE IdentityId = @IdentityId";
            command.Parameters.AddWithValue("@IdentityId", identityId);

            using var reader = command.ExecuteReader(CommandBehavior.SingleRow);
            if (!reader.Read())
            {
                return null;
            }

            using var dbStream = reader.GetStream(0);
            var resultStream = new MemoryStream();
            dbStream.CopyTo(resultStream);
            resultStream.Position = 0;
            return resultStream;
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
            command.Parameters.AddWithValue("@Guid", microsoftUpdatePackageIdentity.ID);
            command.Parameters.AddWithValue("@Revision", microsoftUpdatePackageIdentity.Revision);

            return command.ExecuteScalar() as int? ?? -1;
        }

        public IPackageIdentity GetPackageIdentity(int packageIndex)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Guid, Revision FROM Identities WHERE Id = @Id";
            command.Parameters.AddWithValue("@Id", packageIndex);

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                var guid = reader.GetGuid(0);
                var revision = reader.GetInt32(1);
                return new MicrosoftUpdatePackageIdentity(guid, revision);
            }

            return null;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public bool ContainsMetadata(IPackageIdentity packageIdentity)
        {
            return ContainsPackage(packageIdentity);
        }

        public void CopyTo(IMetadataSink destination, CancellationToken cancelToken)
        {
            // TODO need implement
            throw new NotImplementedException();
        }

        public void CopyTo(IMetadataSink destination, IMetadataFilter filter, CancellationToken cancelToken)
        {
            throw new NotImplementedException();
        }
    }
}
