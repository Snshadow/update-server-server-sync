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
        /// True if the path has the valid store, false otherwise.
        /// </summary>
        /// <param name="path">Path used by the metadata store.</param>
        /// <returns></returns>
        static abstract bool IsValid(string path);

        /// <summary>
        /// Reindex an underlying store.
        /// </summary>
        /// <param name="forceReindex">If true, always reindex a store.</param>
        void CheckIndex(bool forceReindex = false);

        /// <summary>
        /// A list of packages that were added recently to the store.
        /// </summary>
        List<IPackage> PendingPackages { get; }
    }
}
