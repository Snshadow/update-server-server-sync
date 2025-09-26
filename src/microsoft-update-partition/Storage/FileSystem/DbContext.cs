// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Data.Common;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Represents the database context, managing the connection and table creation.
    /// </summary>
    public abstract class DbContext : IDisposable
    {
        /// <summary>
        /// Gets the active database connection.
        /// </summary>
        /// <returns>The SQLite connection.</returns>
        public abstract DbConnection GetConnection();

        /// <summary>
        /// Initializes the database by creating the necessary tables if they don't exist.
        /// </summary>
        protected abstract void InitializeDatabase();

        /// <summary>
        /// Releases the resources used by the database connection.
        /// </summary>
        public abstract void Dispose();
    }

    /// <summary>
    /// Represents thread-safe connection for SQLite database.
    /// </summary>
    public class ThreadSafeSqliteConnection : IDisposable
    {
        private readonly string _connectionString;
        private readonly ThreadLocal<SqliteConnection> _connection = new(true);

        /// <summary>
        /// Creates thread-safe SQLite connection with connection string.
        /// </summary>
        /// <param name="connectionString">Connection string the SQLite database.</param>
        public ThreadSafeSqliteConnection(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <summary>
        /// Create or get the opened SQLite connection for the calling thread.
        /// </summary>
        public SqliteConnection Connection
        {
            get
            {
                var connection = _connection.Value ?? new SqliteConnection(_connectionString);
                _connection.Value ??= connection;
                connection.Open();
                return connection;
            }
        }

        /// <summary>
        /// Disposes SQLite connection across threads and releases other resources.
        /// </summary>
        public void Dispose()
        {
            foreach (var connection in _connection.Values ?? [])
            {
                connection?.Dispose();
            }
            _connection.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
