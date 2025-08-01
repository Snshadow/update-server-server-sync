// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.UpdateServices.WebServices.ClientSync;
using System;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Represents the database context for deployments and synchronization, managing the connection and table creation.
    /// </summary>
    public class DeploySyncDbContext : IDisposable
    {
        private readonly SqliteConnection _connection;
        private bool _isDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeploySyncDbContext"/> class.
        /// </summary>
        /// <param name="databasePath">The path to the SQLite database file.</param>
        public DeploySyncDbContext(string databasePath)
        {
            _connection = new SqliteConnection($"Data Source={databasePath}");
            _connection.Open();
            InitializeDatabase();
        }

        /// <summary>
        /// Initializes the database by creating the necessary tables if they don't exist.
        /// </summary>
        private void InitializeDatabase()
        {
            var command = _connection.CreateCommand();
            command.CommandText = @"
            CREATE TABLE IF NOT EXISTS Deployments (
                RevisionId INTEGER PRIMARY KEY,
                Action INTEGER NOT NULL,
                Deadline TEXT,
                LastChangeTime TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ComputerSyncStatus (
                ComputerId TEXT PRIMARY KEY,
                LastSyncTime TEXT NOT NULL
            );
        ";
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Gets the active database connection.
        /// </summary>
        /// <returns>The SQLite connection.</returns>
        public SqliteConnection GetConnection() => _connection;

        /// <summary>
        /// Releases the resources used by the database connection.
        /// </summary>
        public void Dispose()
        {
            if (!_isDisposed)
            {
                _connection.Dispose();

                _isDisposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Manages the storage of deployment data.
    /// </summary>
    public class DeploymentStore
    {
        private readonly DeploySyncDbContext _context;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeploymentStore"/> class.
        /// </summary>
        /// <param name="context">The deployment database context.</param>
        public DeploymentStore(DeploySyncDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Saves a deployment to the database
        /// </summary>
        /// <param name="deployment">The deployment to save</param>
        public void SaveDeployment(IDeployment deployment)
        {
            var connection = _context.GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO Deployments (RevisionId, Action, Deadline, LastChangeTime) VALUES (@RevisionId, @Action, @Deadline, @LastChangeTime)";
            command.Parameters.AddWithValue("@RevisionId", deployment.RevisionId);
            command.Parameters.AddWithValue("@Action", (int)deployment.Action);
            command.Parameters.AddWithValue("@Deadline", (object)deployment.Deadline?.ToString("o") ?? DBNull.Value);
            command.Parameters.AddWithValue("@LastChangeTime", deployment.LastChangeTime.ToString("o"));
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Deletes a deployment from the database
        /// </summary>
        /// <param name="revisionId">The revision id of a deployment to delete</param>
        public void DeleteDeployment(int revisionId)
        {
            var connection = _context.GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Deployments WHERE RevisionId = @RevisionId";
            command.Parameters.AddWithValue("@RevisionId", revisionId);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Retrieves a deployment from the database by its revision ID
        /// </summary>
        /// <param name="revisionId">The revision ID of the deployment to retrieve</param>
        /// <returns>The deployment, or null if not found</returns>
        public IDeployment GetDeployment(int revisionId)
        {
            var connection = _context.GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Action, Deadline, LastChangeTime FROM Deployments WHERE RevisionId = @RevisionId";
            command.Parameters.AddWithValue("@RevisionId", revisionId);

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return new DeploymentEntry
                {
                    RevisionId = revisionId,
                    Action = (DeploymentAction)reader.GetInt32(0),
                    Deadline = !reader.IsDBNull(1) ? reader.GetDateTime(1) : null,
                    LastChangeTime = reader.GetDateTime(2)
                };
            }

            return null;
        }
    }

    /// <summary>
    /// Manages the storage of computer synchronization status
    /// </summary>
    public class ComputerSyncStore
    {
        private readonly DeploySyncDbContext _context;

        /// <summary>
        /// Initializes a new instance of the <see cref="ComputerSyncStore"/> class
        /// </summary>
        /// <param name="context">The deployment database context</param>
        public ComputerSyncStore(DeploySyncDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Updates the synchronization status for a specific computer
        /// </summary>
        /// <param name="computerId">The ID of the computer</param>
        /// <param name="syncTime">The time of the synchronization</param>
        public void UpdateComputerSync(string computerId, DateTime syncTime)
        {
            var connection = _context.GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO ComputerSyncStatus (ComputerId, LastSyncTime) VALUES (@ComputerId, @LastSyncTime)";
            command.Parameters.AddWithValue("@ComputerId", computerId);
            command.Parameters.AddWithValue("@LastSyncTime", syncTime.ToString("o"));
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Deletes the synchronization status for a computer
        /// </summary>
        /// <param name="computerId">The ID of a computer</param>
        public void DeleteComputer(string computerId)
        {
            var connection = _context.GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ComputerSyncStatus WHERE ComputerId = @ComputerId";
            command.Parameters.AddWithValue("@ComputerId", computerId);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Retrieves a synchronization information from by its computer ID
        /// </summary>
        /// <param name="computerId"></param>
        /// <returns>A synchronization information of a computer</returns>
        public IComputerSync GetComputerSync(string computerId)
        {
            var connection = _context.GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT LastSyncTime FROM ComputerSyncStatus WHERE ComputerId = @ComputerId";
            command.Parameters.AddWithValue("@ComputerId", computerId);

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return new ComputerSync
                {
                    ComputerId = computerId,
                    LastSyncTime = reader.GetDateTime(0)
                };
            }

            return null;
        }
    }
}
