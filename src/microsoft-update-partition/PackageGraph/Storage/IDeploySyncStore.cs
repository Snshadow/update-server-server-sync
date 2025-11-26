// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using System;
using System.Collections.Generic;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Defines an interface for storing and retrieving deployment and synchronization information
    /// </summary>
    public interface IDeploySyncStore
    {
        /// <summary>
        /// Persists a deployment entry to the underlying store
        /// </summary>
        /// <param name="deployment">The deployment to save</param>
        void SaveDeployment(IDeployment deployment);

        /// <summary>
        /// Deletes a deployment entry from the underlying store
        /// </summary>
        /// <param name="revisionId">The revision ID of the deployment to delete</param>
        void DeleteDeployment(int revisionId);

        /// <summary>
        /// Retrieves a deployment entry from the underlying store
        /// </summary>
        /// <param name="revisionId">The revision ID of the deployment to retrieve</param>
        /// <returns>The requested deployment</returns>
        IDeployment GetDeployment(int revisionId);

        /// <summary>
        /// Retrieves revision ids of approved deployments from the underlying store
        /// </summary>
        IEnumerable<int> GetApprovedRevisionIds();

        /// <summary>
        /// Retriteves revision ids of unapproved deployments from the underlying store
        /// </summary>
        IEnumerable<int> GetUnapprovedRevisionIds();

        /// <summary>
        /// Updates the last sync time for a given computer
        /// </summary>
        /// <param name="computerId">The ID of the computer that synced</param>
        /// <param name="syncTime">The time that the computer synced</param>
        void UpdateComputerSync(string computerId, DateTime syncTime);

        /// <summary>
        /// Deletes the computer from an underlying storage
        /// </summary>
        /// <param name="computerId">The ID of the computer to be deleted</param>
        void DeleteComputer(string computerId);

        /// <summary>
        /// Retrieves a computer synchronization info for a given id.
        /// </summary>
        /// <param name="computerId"></param>
        /// <returns></returns>
        IComputerSync GetComputerSync(string computerId);
    }
}
