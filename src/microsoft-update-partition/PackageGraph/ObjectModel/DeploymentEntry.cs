// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.UpdateServices.WebServices.ClientSync;
using System;

namespace Microsoft.PackageGraph.ObjectModel
{
    /// <summary>
    /// An implementation for <see cref="IDeployment"/> for a deployment for an update.
    /// </summary>
    public class DeploymentEntry : IDeployment
    {
        /// <inheritdoc cref="IDeployment.RevisionId"/>
        public int RevisionId { get; set; }

        /// <inheritdoc cref="IDeployment.Action"/>
        public DeploymentAction Action { get; set; }

        /// <inheritdoc cref="IDeployment.Deadline"/>
        public DateTime? Deadline { get; set; }

        /// <inheritdoc cref="IDeployment.LastChangeTime"/>
        public DateTime LastChangeTime { get; set; }
    }
}
