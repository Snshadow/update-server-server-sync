// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Defines CRUD operations for a metadata store.
    /// </summary>
    public interface IMetadataStoreOperations
    {
        /// <summary>
        /// Adds a package to the store.
        /// </summary>
        /// <param name="package">The package to add.</param>
        void AddPackage(IPackage package);

        /// <summary>
        /// Adds a collection of packages to the store.
        /// </summary>
        /// <param name="packages">The packages to add.</param>
        void AddPackages(IEnumerable<IPackage> packages);

        /// <summary>
        /// Gets the metadata for a package as a stream.
        /// </summary>
        /// <param name="packageIdentity">The identity of the package to retrieve metadata for.</param>
        /// <returns>A stream containing the package metadata.</returns>
        Stream GetMetadata(IPackageIdentity packageIdentity);

        /// <summary>
        /// Gets the list of files for a package.
        /// </summary>
        /// <typeparam name="T">The type of content file to return.</typeparam>
        /// <param name="packageIdentity">The identity of the package to retrieve files for.</param>
        /// <returns>A list of content files.</returns>
        List<T> GetFiles<T>(IPackageIdentity packageIdentity);

        /// <summary>
        /// Gets all package identities in the store.
        /// </summary>
        /// <returns>An enumerable of package identities.</returns>
        IEnumerable<IPackageIdentity> GetPackageIdentities();

        /// <summary>
        /// Gets a package from the store.
        /// </summary>
        /// <param name="packageIdentity">The identity of the package to retrieve.</param>
        /// <returns>The package.</returns>
        IPackage GetPackage(IPackageIdentity packageIdentity);
    }
}
