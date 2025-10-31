// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.ObjectModel;
using System.Collections.Generic;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Represents a store-backed filter which uses <see cref="MetadataFilter"/>.
    /// </summary>
    public interface IStoreBackedFilter
    {
        /// <summary>
        /// Counts the packages that match the specified filter.
        /// </summary>
        /// <param name="filter">The metadata filter to apply</param>
        /// <returns>The number of matching packages</returns>
        int CountFromStore(MetadataFilter filter);

        /// <summary>
        /// Applies the provided metadata filter and returns the matching packages.
        /// </summary>
        /// <param name="filter">The metadata filter containing the package type</param>
        /// <returns>Matching packages</returns>
        IEnumerable<IPackage> FilterFromStore(MetadataFilter filter);
    }
}
