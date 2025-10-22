// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.UpdateServices.WebServices.ClientSync;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Represents the <see cref="DbContext"/> for deployments and synchronization.
    /// </summary>
    public class DeploySyncDbContext : DbContext
    {
        private readonly string _dbPath;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeploySyncDbContext"/> class.
        /// </summary>
        /// <param name="dbPath">The path to the SQLite database file.</param>
        public DeploySyncDbContext(string dbPath)
        {
            _dbPath = dbPath;

            InitializeDatabase();
        }

        /// <inheritdoc/>
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
            createTableCommand.CommandText = """
            CREATE TABLE IF NOT EXISTS deployments (
                revision_id INTEGER PRIMARY KEY,
                action INTEGER NOT NULL,
                deadline TEXT,
                last_change_time TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS computer_sync_status (
                computer_id TEXT PRIMARY KEY,
                last_sync_time TEXT NOT NULL
            );
            """;
            createTableCommand.ExecuteNonQuery();
        }

        /// <inheritdoc/>
        public override SqliteConnection GetConnection()
        {
            var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            return connection;
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
            SaveDeployments([deployment]);
        }

        /// <summary>
        /// Saves a list of deployments to the database in a single transaction.
        /// </summary>
        /// <param name="deployments">The deployments to save.</param>
        public void SaveDeployments(IEnumerable<IDeployment> deployments)
        {
            using var connection = _context.GetConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();

            command.Transaction = transaction;
            command.CommandText = """
            INSERT INTO deployments (revision_id, action, deadline, last_change_time)
                VALUES (@revision_id, @action, @deadline, @last_change_time)
                ON CONFLICT(revision_id) DO UPDATE SET
                    action = @action, deadline = @deadline, last_change_time = @last_change_time
                WHERE last_change_time < @last_change_time
            """;
            var revisionIdParam = command.Parameters.Add("@revision_id", SqliteType.Integer);
            var actionParam = command.Parameters.Add("@action", SqliteType.Integer);
            var deadlineParam = command.Parameters.Add("@deadline", SqliteType.Text);
            var lastChangeTimeParam = command.Parameters.Add("@last_change_time", SqliteType.Text);

            foreach (var deployment in deployments)
            {
                revisionIdParam.Value = deployment.RevisionId;
                actionParam.Value = deployment.Action;
                deadlineParam.Value = (object)deployment.Deadline?.ToString("o", DateTimeFormatInfo.InvariantInfo) ?? DBNull.Value;
                lastChangeTimeParam.Value = deployment.LastChangeTime.ToString("o", DateTimeFormatInfo.InvariantInfo);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        /// <summary>
        /// Deletes a deployment from the database
        /// </summary>
        /// <param name="revisionId">The revision id of a deployment to delete</param>
        public void DeleteDeployment(int revisionId)
        {
            using var connection = _context.GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM deployments WHERE revision_id = @revision_id";
            command.Parameters.Add("@revision_id", SqliteType.Integer).Value = revisionId;
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Retrieves a deployment from the database by its revision ID
        /// </summary>
        /// <param name="revisionId">The revision ID of the deployment to retrieve</param>
        /// <returns>The deployment, or null if not found</returns>
        public IDeployment GetDeployment(int revisionId)
        {
            using var connection = _context.GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT action, deadline, last_change_time FROM deployments WHERE revision_id = @revision_id";
            command.Parameters.Add("@revision_id", SqliteType.Integer).Value = revisionId;

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
            UpdateComputerSyncs(new[] { new ComputerSync { ComputerId = computerId, LastSyncTime = syncTime } });
        }

        /// <summary>
        /// Updates the synchronization status for multiple computers in a single transaction.
        /// </summary>
        /// <param name="computerSyncs">The computer sync data to save.</param>
        public void UpdateComputerSyncs(IEnumerable<IComputerSync> computerSyncs)
        {
            using var connection = _context.GetConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();

            command.Transaction = transaction;
            command.CommandText = """
            INSERT INTO computer_sync_status (computer_id, last_sync_time) VALUES (@computer_id, @last_sync_time)
                ON CONFLICT(computer_id) DO UPDATE SET last_sync_time = @last_sync_time
                WHERE last_sync_time < @last_sync_time
            """;
            var computerIdParam = command.Parameters.Add("@computer_id", SqliteType.Text);
            var lastSyncTimeParam = command.Parameters.Add("@last_sync_time", SqliteType.Text);

            foreach (var sync in computerSyncs)
            {
                computerIdParam.Value = sync.ComputerId;
                lastSyncTimeParam.Value = sync.LastSyncTime.ToString("o", DateTimeFormatInfo.InvariantInfo);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        /// <summary>
        /// Deletes the synchronization status for a computer
        /// </summary>
        /// <param name="computerId">The ID of a computer</param>
        public void DeleteComputer(string computerId)
        {
            using var connection = _context.GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM computer_sync_status WHERE computer_id = @computer_id";
            command.Parameters.Add("@computer_id", SqliteType.Text).Value = computerId;
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Retrieves a synchronization information from by its computer ID
        /// </summary>
        /// <param name="computerId"></param>
        /// <returns>A synchronization information of a computer</returns>
        public IComputerSync GetComputerSync(string computerId)
        {
            using var connection = _context.GetConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT last_sync_time FROM computer_sync_status WHERE computer_id = @computer_id";
            command.Parameters.Add("@computer_id", SqliteType.Text).Value = computerId;

            var result = command.ExecuteScalar() as string;
            if (!string.IsNullOrEmpty(result))
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
