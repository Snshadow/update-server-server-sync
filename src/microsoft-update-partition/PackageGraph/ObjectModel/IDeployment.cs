// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UpdateServices.WebServices.ClientSync;
using System;

namespace Microsoft.PackageGraph.ObjectModel
{
    /// <summary>
    /// Represents a deployment for an update.
    /// </summary>
    public interface IDeployment
    {
        /// <summary>
        /// Gets a revision id of a deployment
        /// </summary>
        /// <value>Revision id value</value>
        int RevisionId { get; }

        /// <summary>
        /// Gets a deployment action of a deployment
        /// </summary>
        /// <value>Deployment action value</value>
        DeploymentAction Action { get; }

        /// <summary>
        /// Gets a deadline of a deployment
        /// </summary>
        /// <value>The time by which the deployment should occur</value>
        DateTime? Deadline { get; }

        /// <summary>
        /// Gets a last change time of a deployment
        /// </summary>
        /// <value>The time when the deployment was created</value>
        DateTime LastChangeTime { get; }
    }
}
