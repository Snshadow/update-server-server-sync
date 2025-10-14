// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using System.Collections.Generic;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Represents a set of packages that can be filtered.
    /// </summary>
    public interface IFilterablePackageSet
    {
        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <param name="filter">The filter to apply.</param>
        /// <returns>An enumerator that can be used to iterate through the collection.</returns>
        IEnumerator<IPackage> GetEnumerator(IMetadataFilter filter);
    }
}