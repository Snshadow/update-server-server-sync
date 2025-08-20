// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;

namespace Microsoft.PackageGraph.ObjectModel
{
    /// <summary>
    /// An implementation for <see cref="IComputerSync"/> for a computer synchronization state.
    /// </summary>
    public class ComputerSync : IComputerSync
    {
        /// <inheritdoc cref="IComputerSync.ComputerId"/>
        public string ComputerId { get; set; }

        /// <inheritdoc cref="IComputerSync.LastSyncTime"/>
        public DateTime LastSyncTime { get; set; }
    }
}
