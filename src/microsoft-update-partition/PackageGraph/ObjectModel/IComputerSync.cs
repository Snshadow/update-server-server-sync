// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;

namespace Microsoft.PackageGraph.ObjectModel
{
    /// <summary>
    /// Represents a computer synchronization state.
    /// </summary>
    public interface IComputerSync
    {
        /// <summary>
        /// An id of a computer
        /// </summary>
        string ComputerId { get; }

        /// <summary>
        /// Last synchronized time
        /// </summary>
        DateTime LastSyncTime { get; }
    }
}
