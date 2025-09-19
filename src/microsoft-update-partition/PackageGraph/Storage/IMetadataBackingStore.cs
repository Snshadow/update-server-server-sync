// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using System;
using System.Collections.Generic;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Interface for a backing store for metadata for updates.
    /// </summary>
    interface IMetadataBackingStore : IDisposable, IEnumerable<IPackage>, IMetadataLookup, IMetadataMapping, IMetadataStoreOperations
    {
        /// <summary>
        /// Flushes any pending changes to the backing store.
        /// </summary>
        void Flush();

        /// <summary>
        /// Checks if the existing store is valid.
        /// </summary>
        /// <returns></returns>
        bool IsValid();
    }
}
