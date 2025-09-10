// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Factory for creating metadata backing stores.
    /// </summary>
    static class MetadataBackingStoreFactory
    {
        /// <summary>
        /// Creates a metadata backing store based on the specified configuration.
        /// </summary>
        /// <param name="configuration">The configuration for the backing store.</param>
        /// <param name="toc">The table of contents for compressed stores.</param>
        /// <returns>An instance of <see cref="IMetadataBackingStore"/>.</returns>
        public static IMetadataBackingStore Create(BackingStoreConfiguration configuration, TableOfContent toc)
        {
            return configuration.StoreType switch
            {
                BackingStoreType.Compressed => new CompressedDeltaStore(configuration.Path, toc),
                BackingStoreType.Directory => new DirectoryMetadataStore(configuration.Path),
                BackingStoreType.Sqlite => new SqliteMetadataBackingStore(configuration.Path, configuration.Mode),
                _ => throw new NotSupportedException($"Backing store type {configuration.StoreType} is not supported.")
            };
        }
    }
}
