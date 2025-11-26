// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using System.Collections.Generic;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Interface for filtering packages from a <see cref="IMetadataSource"/> or for selectively quering packages from a <see cref="IMetadataSource"/>
    /// </summary>
    public interface IMetadataFilter
    {
        /// <summary>
        /// Apply the filter to a collection of packages
        /// </summary>
        /// <param name="source">Source to filter packages from</param>
        /// <returns>Matching packages</returns>
        IEnumerable<IPackage> Apply(IMetadataSource source);

        /// <summary>
        /// Get the identities of packages that match the criteria
        /// </summary>
        /// <param name="source">Source to filter packages from</param>
        /// <returns>Matching packages' identities</returns>
        IEnumerable<IPackageIdentity> GetMatchingIdentities(IMetadataSource source);

        /// <summary>
        /// Get the number of packages that match the criteria
        /// </summary>
        /// <param name="source">Source to filter packages from</param>
        /// <returns>The number of matching packages</returns>
        int GetCount(IMetadataSource source);
    }
}
