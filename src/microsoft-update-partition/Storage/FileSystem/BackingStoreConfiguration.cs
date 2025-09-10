// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

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
        /// The store is backed by a Sqlite database.
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
}
