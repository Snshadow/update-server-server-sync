// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.IO;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Defines the backing store types for a DirectoryPackageStore
    /// </summary>
    public enum BackingStoreType
    {
        /// <summary>
        /// The store is backed by a collection of compressed zip files.
        /// </summary>
        Compressed,
        /// <summary>
        /// The store is backed by a directory structure.
        /// </summary>
        Directory,
        /// <summary>
        /// The store is backed by a SQLite database.
        /// </summary>
        Sqlite
    }

    /// <summary>
    /// Configuration for creating a metadata backing store.
    /// </summary>
    public class BackingStoreConfiguration
    {
        /// <summary>
        /// Gets or sets the path to the backing store.
        /// </summary>
        public string Path { get; set; }

        /// <summary>
        /// Gets or sets the type of the backing store.
        /// </summary>
        public BackingStoreType StoreType { get; set; }

        /// <summary>
        /// Gets or sets the file mode for opening the store.
        /// </summary>
        public FileMode Mode { get; set; }
    }

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
