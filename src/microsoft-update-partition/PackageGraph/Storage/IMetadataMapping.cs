// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Defines a mapping between package identities and integer indexes.
    /// </summary>
    public interface IMetadataMapping
    {
        /// <summary>
        /// Gets the integer index for a package identity.
        /// </summary>
        /// <param name="packageIdentity">The package identity to look up.</param>
        /// <returns>The package index, or -1 if not found.</returns>
        int GetPackageIndex(IPackageIdentity packageIdentity);

        /// <summary>
        /// Gets the package identity for an integer index.
        /// </summary>
        /// <param name="packageIndex">The package index to look up.</param>
        /// <returns>The package identity, or null if not found.</returns>
        IPackageIdentity GetPackageIdentity(int packageIndex);

        /// <summary>
        /// Checks if a package identity exists in the mapping.
        /// </summary>
        /// <param name="packageIdentity">The package identity to check.</param>
        /// <returns>True if the package exists, false otherwise.</returns>
        bool ContainsPackage(IPackageIdentity packageIdentity);

        /// <summary>
        /// Gets the total number of packages in the mapping.
        /// </summary>
        int PackageCount { get; }

        /// <summary>
        /// Gets the package type for a given index.
        /// </summary>
        /// <param name="packageIndex">The package index.</param>
        /// <returns>The package type from <see cref="MicrosoftUpdate.StoredPackageType"/>, or -1 if not exist.</returns>
        int GetPackageType(int packageIndex);
    }
}
