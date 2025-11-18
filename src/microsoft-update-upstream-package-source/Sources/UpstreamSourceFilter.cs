// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Prerequisites;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage;
using Microsoft.UpdateServices.WebServices.ServerSync;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Source
{
    /// <summary>
    /// Implements a filter for updates based by product and/or classification.
    /// <para>
    /// The filter is used for selectively sync'ing updates from an upstream update server through <see cref="IMetadataSource"/>
    /// </para>
    /// <para>
    /// The filter can also be used to selectively copy updates between <see cref="IMetadataStore"/>. The more versatile <see cref="MetadataFilter"/> should be used for this scenario instead.
    /// </para>
    /// </summary>
    public class UpstreamSourceFilter : IMetadataFilter
    {
        /// <summary>
        /// Gets the list of products allowed by the filter.
        /// If this list if empty, no updates will match the filter. Add product IDs to this list to have them match the filter.
        /// </summary>
        /// <value>List of product identities.</value>
        [JsonProperty]
        public List<Guid> ProductFilter { get; internal set; }

        /// <summary>
        /// Gets the list of classifications allowed by the filter.
        /// If this list if empty, no updates will match the filter. Add classification IDs to this list to have them match the filter.
        /// </summary>
        /// <value>List of classification identities.</value>
        [JsonProperty]
        public List<Guid> ClassificationFilter { get; internal set; }

        /// <summary>
        /// Creates an empty filter.
        /// </summary>
        [JsonConstructor]
        public UpstreamSourceFilter()
        {
            ProductFilter = new List<Guid>();
            ClassificationFilter = new List<Guid>();
        }

        /// <summary>
        /// Initialize a new SourceFilter from the specified products and classifications.
        /// </summary>
        /// <param name="products">The products to match</param>
        /// <param name="classifications">The classifications to match</param>
        public UpstreamSourceFilter(IEnumerable<Guid> products, IEnumerable<Guid> classifications)
        {
            ProductFilter = new List<Guid>(products);
            ClassificationFilter = new List<Guid>(classifications);
        }

        /// <summary>
        /// Creates a ServerSyncFilter object to be used with GetRevisionIdListAsync
        /// </summary>
        /// <returns>A ServerSyncFilter instance</returns>
        internal ServerSyncFilter ToServerSyncFilter(string anchor = null)
        {
            ServerSyncFilter filter = new();

            if (ProductFilter.Count > 0)
            {
                filter.Categories = new IdAndDelta[ProductFilter.Count];
                for (int i = 0; i < ProductFilter.Count; i++)
                {
                    filter.Categories[i] = new IdAndDelta
                    {
                        // Request deltas if we have an anchor from a previous query
                        Delta = !string.IsNullOrEmpty(anchor),
                        Id = ProductFilter[i].ToString()
                    };
                }
            }

            if (ClassificationFilter.Count > 0)
            {
                filter.Classifications = new IdAndDelta[ClassificationFilter.Count];
                for (int i = 0; i < ClassificationFilter.Count; i++)
                {
                    filter.Classifications[i] = new IdAndDelta
                    {
                        // Request deltas if we have an anchor from a previous query
                        Delta = !string.IsNullOrEmpty(anchor),
                        Id = ClassificationFilter[i].ToString()
                    };
                }
            }

            filter.Anchor = anchor;

            return filter;
        }

        /// <summary>
        /// Override Equals for 2 SourceFilter objects
        /// </summary>
        /// <param name="obj">Other SourceFilter</param>
        /// <returns>
        /// <para>True if the two SourceFilter are identical (same product and classification filters).</para>
        /// <para>False otherwise</para>
        /// </returns>
        public override bool Equals(object obj)
        {
            if (obj is not UpstreamSourceFilter)
            {
                return false;
            }

            var other = obj as UpstreamSourceFilter;
            if (ProductFilter.Count != other.ProductFilter.Count ||
                ClassificationFilter.Count != other.ClassificationFilter.Count)
            {
                return false;
            }

            return ProductFilter.All(cat => other.ProductFilter.Contains(cat))
                && ClassificationFilter.All(cat => other.ClassificationFilter.Contains(cat));
        }

        /// <summary>
        /// Override equality operator SourceFilter objects
        /// </summary>
        /// <param name="lhs">Left SourceFilter</param>
        /// <param name="rhs">Right SourceFilter</param>
        /// <returns>
        /// <para>True if both lhs and rhs are SourceFilter and they contain the same classification and product filters</para>
        /// <para>False otherwise</para>
        /// </returns>
        public static bool operator ==(UpstreamSourceFilter lhs, UpstreamSourceFilter rhs)
        {
            if (lhs is null)
            {
                return rhs is null;
            }
            else
            {
                return lhs.Equals(rhs);
            }
        }

        /// <summary>
        /// Override inequality operator SourceFilter objects
        /// </summary>
        /// <param name="lhs">Left SourceFilter</param>
        /// <param name="rhs">Right SourceFilter</param>
        /// <returns>
        /// <para>True if both lhs and rhs are not QueryFilter or they contain different classification and product filters</para>
        /// <para>False otherwise</para>
        /// </returns>
        public static bool operator !=(UpstreamSourceFilter lhs, UpstreamSourceFilter rhs)
        {
            return !(lhs == rhs);
        }

        /// <summary>
        /// Returns a hash code based on the hash codes of the contained classification and products
        /// </summary>
        /// <returns>Hash code</returns>
        public override int GetHashCode()
        {
            int hash = 0;
            ProductFilter.ForEach(cat => hash |= cat.GetHashCode());
            ClassificationFilter.ForEach(cat => hash |= cat.GetHashCode());

            return hash;
        }

        /// <summary>
        /// Applies the filter to a <see cref="IMetadataStore"/> and returns the matched packages
        /// </summary>
        /// <param name="packages">The packages to filter</param>
        /// <returns>List of packages that match the filter</returns>
        public IEnumerable<IPackage> Apply(IEnumerable<IPackage> packages)
        {
            var filteredUpdates = packages.OfType<MicrosoftUpdatePackage>();

            return filteredUpdates.Where(u =>
            {
                if (u.Prerequisites is null)
                {
                    return false;
                }
                else
                {
                    var prereqs = u.Prerequisites.OfType<AtLeastOne>().SelectMany(p => p.Simple).Select(s => s.UpdateId).ToList();
                    var classificationsMatch = ClassificationFilter.Count == 0 || prereqs.Intersect(ClassificationFilter).Any();
                    var productsMatch = ProductFilter.Count == 0 || prereqs.Intersect(ProductFilter).Any();
                    return classificationsMatch && productsMatch;
                }
            });
        }

        /// <summary>
        /// Get the identities of packages that match the criteria 
        /// </summary>
        /// <param name="packages">The packages to filter</param>
        /// <returns>Matching packages' identities</returns>
        public IEnumerable<IPackageIdentity> GetMatchingIdentities(IEnumerable<IPackage> packages)
        {
            return Apply(packages).Select(p => p.Id);
        }

        /// <summary>
        /// Get the number of packages that match the criteria
        /// </summary>
        /// <param name="packages">The packages to filter</param>
        /// <returns>The number of matching packages</returns>
        public int GetCount(IEnumerable<IPackage> packages)
        {
            return Apply(packages).Count();
        }
    }
}
