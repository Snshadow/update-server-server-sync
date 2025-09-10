// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using System;

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
        public abstract SqliteConnection GetConnection();

        /// <summary>
        /// Initializes the database by creating the necessary tables if they don't exist.
        /// </summary>
        protected abstract void InitializeDatabase();

        /// <summary>
        /// Releases the resources used by the database connection.
        /// </summary>
        public abstract void Dispose();
    }
}
