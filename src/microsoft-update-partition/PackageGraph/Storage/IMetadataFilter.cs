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
        /// Get the number of packages that match the criteria
        /// </summary>
        /// <param name="packages">The packages to filter</param>
        /// <returns>The number of matching packages</returns>
        int GetCount(IEnumerable<IPackage> packages);

        /// <summary>
        /// Apply the filter to a collection of packages
        /// </summary>
        /// <param name="packages">The packages to filter</param>
        /// <returns>Matching packages</returns>
        IEnumerable<IPackage> Apply(IEnumerable<IPackage> packages);
    }
}
