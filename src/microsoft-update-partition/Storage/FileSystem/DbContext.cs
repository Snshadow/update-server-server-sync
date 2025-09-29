// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using System.Data.Common;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Represents the database context, managing the connection and table creation.
    /// </summary>
    public abstract class DbContext
    {
        /// <summary>
        /// Gets the active database connection.
        /// </summary>
        /// <returns>The database connection.</returns>
        public abstract DbConnection GetConnection();

        /// <summary>
        /// Initializes the database by creating the necessary tables if they don't exist.
        /// </summary>
        protected abstract void InitializeDatabase();
    }
}
