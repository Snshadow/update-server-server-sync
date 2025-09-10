// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Abstract base class for file-based metadata backing stores.
    /// Provides an in-memory implementation of <see cref="IMetadataMapping"/>.
    /// </summary>
    public abstract class FileBasedBackingStoreBase : IMetadataBackingStore
    {
        /// <summary>
        /// The root path of the backing store.
        /// </summary>
        protected readonly string RootPath;

        /// <summary>
        /// In-memory mapping from package identity to integer index.
        /// </summary>
        protected readonly Dictionary<IPackageIdentity, int> IdentityToIndexMap = new();

        /// <summary>
        /// In-memory mapping from integer index to package identity.
        /// </summary>
        protected readonly Dictionary<int, IPackageIdentity> IndexToIdentityMap = new();

        /// <summary>
        /// In-memory mapping from integer index to package type.
        /// </summary>
        protected readonly Dictionary<int, int> PackageTypeIndex = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="FileBasedBackingStoreBase"/> class.
        /// </summary>
        /// <param name="path">The root path of the backing store.</param>
        public FileBasedBackingStoreBase(string path)
        {
            RootPath = path;
        }

        /// <inheritdoc/>
        public int GetPackageIndex(IPackageIdentity packageIdentity) => IdentityToIndexMap.TryGetValue(packageIdentity, out var index) ? index : -1;

        /// <inheritdoc/>
        public IPackageIdentity GetPackageIdentity(int packageIndex) => IndexToIdentityMap.TryGetValue(packageIndex, out var identity) ? identity : null;

        /// <inheritdoc/>
        public bool ContainsPackage(IPackageIdentity packageIdentity) => IdentityToIndexMap.ContainsKey(packageIdentity);

        /// <inheritdoc/>
        public int PackageCount => IdentityToIndexMap.Count;

        /// <inheritdoc/>
        public IEnumerable<IPackageIdentity> GetPackageIdentities() => IdentityToIndexMap.Keys.ToList();

        /// <summary>
        /// Gets the in-memory identity map.
        /// </summary>
        /// <returns>The dictionary that maps indexes to identities.</returns>
        public IReadOnlyDictionary<int, IPackageIdentity> GetIdentitiesMap() => IndexToIdentityMap;

        /// <summary>
        /// Adds an identity to the in-memory mapping.
        /// </summary>
        /// <param name="packageIdentity">The package identity.</param>
        /// <param name="packageIndex">The package index.</param>
        public void AddIdentity(IPackageIdentity packageIdentity, int packageIndex)
        {
            IdentityToIndexMap.Add(packageIdentity, packageIndex);
            IndexToIdentityMap.Add(packageIndex, packageIdentity);
        }

        /// <inheritdoc/>
        public int GetPackageType(int packageIndex) => PackageTypeIndex.TryGetValue(packageIndex, out var packageType) ? packageType : -1;

        /// <summary>
        /// Adds a package type mapping.
        /// </summary>
        /// <param name="packageIndex">The package index.</param>
        /// <param name="packageType">The package type.</param>
        public void AddPackageType(int packageIndex, int packageType)
        {
            PackageTypeIndex.Add(packageIndex, packageType);
        }

        /// <summary>
        /// Gets the package type index.
        /// </summary>
        /// <returns>The package type index.</returns>
        public IReadOnlyDictionary<int, int> GetPackageTypeIndex() => PackageTypeIndex;

        /// <inheritdoc/>
        public abstract void AddPackage(IPackage package);

        /// <inheritdoc/>
        public abstract void AddPackages(IEnumerable<IPackage> packages);

        /// <inheritdoc/>
        public abstract Stream GetMetadata(IPackageIdentity packageIdentity);

        /// <inheritdoc/>
        public abstract List<T> GetFiles<T>(IPackageIdentity packageIdentity);

        /// <inheritdoc/>
        public abstract IPackage GetPackage(IPackageIdentity packageIdentity);

        /// <inheritdoc/>
        public abstract void Flush();

        /// <inheritdoc/>
        public abstract void Dispose();

        /// <inheritdoc/>
        public abstract IEnumerator<IPackage> GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
