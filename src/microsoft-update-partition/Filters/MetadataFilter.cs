// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Prerequisites;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Metadata
{
    /// <summary>
    /// <para>
    /// A filter that can be applied to Microsoft updates based on their metadata: title, hardware id, KB article, etc.
    /// </para>
    /// <para>
    /// Use this filter to filter updates when copying updates between <see cref="IMetadataSource"/> and <see cref="IMetadataStore"/>
    /// </para>
    /// </summary>
    public class MetadataFilter : IMetadataFilter
    {
        /// <summary>
        /// Gets or sets the product filter.
        /// </summary>
        /// <value>List of product IDs</value>
        public List<Guid> ProductFilter { get; set; }

        /// <summary>
        /// Gets or sets the classification filter.
        /// </summary>
        /// <value>List of classification IDs</value>
        public List<Guid> ClassificationFilter { get; set; }

        /// <summary>
        /// Get or set the ID filter.
        /// </summary>
        /// <value>List of update IDs (ID only, no revision)</value>
        public List<Guid> IdFilter { get; set; }

        /// <summary>
        /// Get or set the title filter.
        /// </summary>
        /// <value>Title filter string</value>
        public string TitleFilter { get; set; }

        /// <summary>
        /// Get or set whether to filter out superseded updates
        /// </summary>
        /// <value>True to skip superseded updates, false otherwise</value>
        public bool SkipSuperseded;

        /// <summary>
        /// Returns the first X results only
        /// </summary>
        /// <value>0 to include all updates, greater than 0 value to limit output.</value>
        public int FirstX;

        /// <summary>
        /// Returns only driver updates that match this hardware ID.
        /// </summary>
        /// <value>Hardware id string</value>
        public string HardwareIdFilter;

        /// <summary>
        /// Returns only driver updates that target this computer hardware ID
        /// </summary>
        /// <value>Computer hardware ID (GUID)</value>
        public Guid ComputerHardwareIdFilter;

        /// <summary>
        /// Get or set the KB article filter
        /// </summary>
        /// <value>List of KB article ids - numbers only</value>
        public List<string> KbArticleFilter;

        /// <summary>
        /// Initialize a new filter. An empty filter matches all updates or categories.
        /// </summary>
        public MetadataFilter()
        {

        }

        /// <summary>
        /// Create a filter from JSON
        /// </summary>
        /// <param name="source">The JSON string</param>

        /// <returns>A filter for metadata in a updates metadata source</returns>
        public static MetadataFilter FromJson(string source)
        {
            return JsonConvert.DeserializeObject<MetadataFilter>(source);
        }

        /// <summary>
        /// Serializes this filter to JSON
        /// </summary>
        /// <returns>The JSON string</returns>
        public string ToJson()
        {
            return JsonConvert.SerializeObject(this);
        }

        /// <summary>
        /// Apply the filter to a <see cref="IMetadataSource"/> and returns the matching packages of the specified type.
        /// </summary>
        /// <typeparam name="T">Package type to query. The type must inherit <see cref="MicrosoftUpdatePackage"/></typeparam>
        /// <param name="packages">The packages to filter</param>
        /// <returns>Matching packages</returns>
        public IEnumerable<T> Apply<T>(IEnumerable<IPackage> packages) where T : MicrosoftUpdatePackage
        {
            IEnumerable<T> filteredUpdates;
            var updates = packages.OfType<T>();

            if (!string.IsNullOrEmpty(HardwareIdFilter) || (Guid.Empty != ComputerHardwareIdFilter))
            {
                filteredUpdates = updates.Where(u => u is DriverUpdate);
            }
            else if (KbArticleFilter is { Count: > 0 })
            {
                filteredUpdates = updates.Where(u => u is SoftwareUpdate);
            }
            else
            {
                filteredUpdates = updates;
            }

            if (!string.IsNullOrEmpty(HardwareIdFilter))
            {
                filteredUpdates = filteredUpdates.Where(
                    u => u is DriverUpdate driverUpdate &&
                    driverUpdate.GetDriverMetadata()
                    .Any(metadata => metadata.HardwareId.Equals(HardwareIdFilter, StringComparison.OrdinalIgnoreCase)));
            }

            if (ComputerHardwareIdFilter != Guid.Empty)
            {
                filteredUpdates = filteredUpdates.Where(
                    u => u is DriverUpdate driverUpdate &&
                    driverUpdate.GetDriverMetadata()
                    .Any(metadata => metadata.DistributionComputerHardwareId.Contains(ComputerHardwareIdFilter)));
            }

            if (ProductFilter is { Count: > 0 })
            {
                filteredUpdates = filteredUpdates.Where(u =>
                {
                    var categories = u.Prerequisites.OfType<AtLeastOne>().Where(p => p.IsCategory).SelectMany(p => p.Simple).Select(s => s.UpdateId);
                    return categories.Intersect(ProductFilter).Any();
                });
            }

            if (ClassificationFilter is { Count: > 0 })
            {
                filteredUpdates = filteredUpdates.Where(u =>
                {
                    var categories = u.Prerequisites.OfType<AtLeastOne>().Where(p => p.IsCategory).SelectMany(p => p.Simple).Select(s => s.UpdateId);
                    return categories.Intersect(ClassificationFilter).Any();
                });
            }

            if (KbArticleFilter is { Count: > 0 })
            {
                var kbLookup = KbArticleFilter.ToHashSet();
                filteredUpdates = filteredUpdates.Where(u => kbLookup.Contains((u as SoftwareUpdate).KBArticleId));
            }

            // Apply the title filter
            if (!string.IsNullOrEmpty(TitleFilter))
            {
                var filterTokens = TitleFilter.Trim().Split([' '], StringSplitOptions.RemoveEmptyEntries);
                filteredUpdates = filteredUpdates.Where(category => category.MatchTitle(filterTokens));
            }

            // Apply the id filter
            if (IdFilter is { Count: > 0 })
            {
                // Remove all updates that don't match the ID filter
                filteredUpdates = filteredUpdates.Where(u => IdFilter.Contains(u.Id.ID));
            }

            if (SkipSuperseded)
            {
                filteredUpdates = filteredUpdates
                    .Where(u => u is not SoftwareUpdate softwareUpdate ||
                    (softwareUpdate.IsSupersededBy?.Count ?? 0) == 0);
            }

            // Return first X matches, if requested
            if (FirstX > 0)
            {
                return filteredUpdates.Take(FirstX);
            }
            else
            {
                return filteredUpdates;
            }
        }

        /// <summary>
        /// Apply the filter to a <see cref="IMetadataSource"/> and returns matching packages of type <see cref="MicrosoftUpdatePackage"/>
        /// </summary>
        /// <param name="packages">The packages to filter</param>
        /// <returns>Matching packages</returns>
        public IEnumerable<IPackage> Apply(IEnumerable<IPackage> packages)
        {
            return Apply<MicrosoftUpdatePackage>(packages);
        }
    }
}
