// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using System;
using System.Collections.Generic;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Interface for filtering packages from a <see cref="IMetadataSource"/> or for selectively quering packages from a <see cref="IMetadataSource"/>
    /// </summary>
    public interface IMetadataFilter
    {

        /// <summary>
        /// Gets the query for update IDs in the filter.
        /// </summary>
        IEnumerable<Guid> IdFilter { get; }

        /// <summary>
        /// Gets the query for titles in the filter.
        /// </summary>
        string TitleFilter { get; }

        /// <summary>
        /// Apply the filter to a collection of packages
        /// </summary>
        /// <param name="packages">The packages to filter</param>
        /// <returns>Matching packages</returns>
        IEnumerable<IPackage> Apply(IEnumerable<IPackage> packages);
    }
}
