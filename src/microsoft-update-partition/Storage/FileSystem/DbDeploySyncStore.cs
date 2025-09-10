// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.UpdateServices.WebServices.ClientSync;
using System;
using System.Globalization;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Represents the <see cref="DbContext"/> for deployments and synchronization.
    /// </summary>
    public class DeploySyncDbContext : DbContext
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

            // Enable WAL(Write-Ahead Logging) for performance.
            using var walCommand = _connection.CreateCommand();
            walCommand.CommandText = "PRAGMA journal_mode = 'WAL'";
            walCommand.ExecuteNonQuery();

            InitializeDatabase();
        }

        /// <inheritdoc/>
        protected override void InitializeDatabase()
        {
            var command = _connection.CreateCommand();
            command.CommandText = """
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
            """;
            command.ExecuteNonQuery();
        }

        /// <inheritdoc/>
        public override SqliteConnection GetConnection() => _connection;

        /// <inheritdoc/>
        public override void Dispose()
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
            command.CommandText = """
            INSERT INTO Deployments (RevisionId, Action, Deadline, LastChangeTime)
                VALUES (@RevisionId, @Action, @Deadline, @LastChangeTime)
                ON CONFLICT(RevisionId) DO UPDATE SET 
                    Action = @Action, Deadline = @Deadline, LastChangeTime = @LastChangeTime
                WHERE LastChangeTime < @LastChangeTime
            """;
            command.Parameters.Add("@RevisionId", SqliteType.Integer).Value = deployment.RevisionId;
            command.Parameters.Add("@Action", SqliteType.Integer).Value = deployment.Action;
            command.Parameters.Add("@Deadline", SqliteType.Text).Value = (object)deployment.Deadline?.ToString("o") ?? DBNull.Value;
            command.Parameters.Add("@LastChangeTime", SqliteType.Text).Value = deployment.LastChangeTime.ToString("o");
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
            command.Parameters.Add("@RevisionId", SqliteType.Integer).Value = revisionId;
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
            command.Parameters.Add("@RevisionId", SqliteType.Integer).Value = revisionId;

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return new DeploymentEntry
                {
                    RevisionId = revisionId,
                    Action = (DeploymentAction)reader.GetInt32(0),
                    Deadline = reader.IsDBNull(1) ? null : reader.GetDateTime(1),
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
            command.CommandText = """
            INSERT INTO ComputerSyncStatus (ComputerId, LastSyncTime) VALUES (@ComputerId, @LastSyncTime)
                ON CONFLICT(ComputerId) DO UPDATE SET LastSyncTime = @LastSyncTime
                WHERE LastSyncTime < @LastSyncTime
            """;
            command.Parameters.Add("@ComputerId", SqliteType.Text).Value = computerId;
            command.Parameters.Add("@LastSyncTime", SqliteType.Text).Value = syncTime.ToString("o");
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
            command.Parameters.Add("@ComputerId", SqliteType.Text).Value = computerId;
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
            command.Parameters.Add("@ComputerId", SqliteType.Text).Value = computerId;

            var result = command.ExecuteScalar() as string;
            if (result is not null)
            {
                return new ComputerSync()
                {
                    ComputerId = computerId,
                    LastSyncTime = DateTime.Parse(result, DateTimeFormatInfo.InvariantInfo)
                };
            }

            return null;
        }
    }
}
