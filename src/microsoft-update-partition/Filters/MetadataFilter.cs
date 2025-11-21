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
    /// Specifies the order of sorting with specific criteria
    /// </summary>
    public enum SortOrder : byte
    {
        /// <summary>
        /// Not sorted
        /// </summary>
        None,
        /// <summary>
        /// Sort by ascending order
        /// </summary>
        Ascending,
        /// <summary>
        /// Sort by descending order
        /// </summary>
        Descending
    }

    /// <summary>
    /// Sorting orders of the filtered Microsoft updates based on the metadata
    /// </summary>
    public readonly struct MetadataSortOrder
    {
        /// <summary>
        /// Sort order by creation date
        /// </summary>
        public readonly SortOrder CreationDate;
        /// <summary>
        /// Sort order by update ID
        /// </summary>
        public readonly SortOrder Id;
        /// <summary>
        /// Sort order by KB article
        /// </summary>
        public readonly SortOrder KbArticle;
        /// <summary>
        /// Sort order by title
        /// </summary>
        public readonly SortOrder Title;

        /// <summary>
        /// Initializes a new instance of the <see cref="MetadataSortOrder"/> struct.
        /// </summary>
        /// <param name="creationDate">Sort order for creation date</param>
        /// <param name="id">Sort order for ID</param>
        /// <param name="kbArticle">Sort order for KB article</param>
        /// <param name="title">Sort order for title</param>
        public MetadataSortOrder(SortOrder creationDate, SortOrder id, SortOrder kbArticle, SortOrder title)
        {
            CreationDate = creationDate;
            Id = id;
            KbArticle = kbArticle;
            Title = title;
        }

        internal bool NeedSort()
        {
            return CreationDate != SortOrder.None ||
                Id != SortOrder.None ||
                KbArticle != SortOrder.None ||
                Title != SortOrder.None;
        }
    }

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
        private static int GetStoredPackageType(Type packageType)
        {
            if (packageType == typeof(DetectoidCategory))
            {
                return (int)StoredPackageType.MicrosoftUpdateDetectoid;
            }
            else if (packageType == typeof(ClassificationCategory))
            {
                return (int)StoredPackageType.MicrosoftUpdateClassification;
            }
            else if (packageType == typeof(ProductCategory))
            {
                return (int)StoredPackageType.MicrosoftUpdateProduct;
            }
            else if (packageType == typeof(SoftwareUpdate))
            {
                return (int)StoredPackageType.MicrosoftUpdateSoftware;
            }
            else if (packageType == typeof(DriverUpdate))
            {
                return (int)StoredPackageType.MicrosoftUpdateDriver;
            }
            else
            {
                return -1;
            }
        }

        private MetadataFilter CloneWithPackageType(int packageType)
        {
            return new MetadataFilter
            {
                PackageType = packageType,
                ProductFilter = ProductFilter,
                ExcludedProductFilter = ExcludedProductFilter,
                ClassificationFilter = ClassificationFilter,
                ExcludedClassificationFilter = ExcludedClassificationFilter,
                IdFilter = IdFilter,
                ExcludedIdFilter = ExcludedIdFilter,
                TitleFilter = TitleFilter,
                ExcludedTitleFilter = ExcludedTitleFilter,
                SkipSuperseded = SkipSuperseded,
                IncludeBundled = IncludeBundled,
                IncludeExpired = IncludeExpired,
                FirstX = FirstX,
                AfterX = AfterX,
                HardwareIdFilter = HardwareIdFilter,
                ExcludedHardwareIdFilter = ExcludedHardwareIdFilter,
                ComputerHardwareIdFilter = ComputerHardwareIdFilter,
                ExcludedComputerHardwareIdFilter = ExcludedComputerHardwareIdFilter,
                KbArticleFilter = KbArticleFilter,
                ExcludedKbArticleFilter = ExcludedKbArticleFilter,
                SortOrder = SortOrder
            };
        }

        private static IEnumerable<Guid> GetCategoryPrerequisiteIds(MicrosoftUpdatePackage update)
        {
            return update.Prerequisites
                .OfType<AtLeastOne>()
                .Where(p => p.IsCategory)
                .SelectMany(p => p.Simple)
                .Select(s => s.UpdateId);
        }

        private static bool TryGetStoreBackedFilter(IEnumerable<IPackage> packages, out IStoreBackedFilter storeBackedFilter)
        {
            switch (packages)
            {
                case IStoreBackedFilter filter:
                    storeBackedFilter = filter;
                    return true;
                case IMetadataStore metadataStore when metadataStore.TryGetStoreBackedFilter(out var providedFilter):
                    storeBackedFilter = providedFilter;
                    return true;
                default:
                    storeBackedFilter = null;
                    return false;
            }
        }

        /// <summary>
        /// Gets or sets the package type filter
        /// </summary>
        /// <value>Enum value of package type</value>
        public int PackageType = -1;

        /// <summary>
        /// Gets or sets the product filter
        /// </summary>
        /// <value>List of product IDs</value>
        public List<Guid> ProductFilter;

        /// <summary>
        /// Gets or sets the product exclusion filter
        /// </summary>
        /// <value>List of product IDs to exclude</value>
        public List<Guid> ExcludedProductFilter;

        /// <summary>
        /// Gets or sets the classification filter
        /// </summary>
        /// <value>List of classification IDs</value>
        public List<Guid> ClassificationFilter;

        /// <summary>
        /// Gets or sets the classification exclusion filter
        /// </summary>
        /// <value>List of classification IDs to exclude</value>
        public List<Guid> ExcludedClassificationFilter;
        /// <summary>
        /// Get or set the ID filter
        /// </summary>
        /// <value>List of update IDs (ID only, no revision)</value>
        public List<Guid> IdFilter;

        /// <summary>
        /// Get or set the ID exclusion filter
        /// </summary>
        /// <value>List of update IDs to exclude</value>
        public List<Guid> ExcludedIdFilter;

        /// <summary>
        /// Get or set the title filter
        /// </summary> 
        /// <value>Title filter string</value>
        public string TitleFilter;

        /// <summary>
        /// Get or set the title exclusion filter
        /// </summary>
        /// <value>Title filter string; matching titles are excluded</value>
        public string ExcludedTitleFilter;

        /// <summary>
        /// Get or set whether to filter out superseded updates
        /// </summary>
        /// <value>True to skip superseded updates, false otherwise</value>
        public bool SkipSuperseded;

        /// <summary>
        /// Get or set whether to include updates bundled by other updates
        /// </summary>
        /// <value>True to include bundled updates, false otherwise</value>
        public bool IncludeBundled;

        /// <summary>
        /// Get or set whether to include expired updates
        /// </summary>
        /// <value>True to include expired updates, false otherwise</value>
        public bool IncludeExpired;

        /// <summary>
        /// Returns up to Xth results
        /// </summary>
        /// <value>0 to include all updates, greater than 0 value to limit output.</value>
        public int FirstX;

        /// <summary>
        /// Skips the first X results
        /// </summary>
        /// <value>The number of skipped updates</value>
        public int AfterX;

        /// <summary>
        /// Returns only driver updates that match this hardware ID
        /// </summary>
        /// <value>Hardware id string</value>
        public string HardwareIdFilter;

        /// <summary>
        /// Returns driver updates that do not match this hardware ID
        /// </summary>
        /// <value>Excluded hardware id string</value>
        public string ExcludedHardwareIdFilter;

        /// <summary>
        /// Returns only driver updates that target this computer hardware ID
        /// </summary>
        /// <value>Computer hardware ID (GUID)</value>
        public Guid ComputerHardwareIdFilter;

        /// <summary>
        /// Returns only updates that do not target this computer hardware ID
        /// </summary>
        /// <value>Excluded computer hardware ID (GUID)</value>
        public Guid ExcludedComputerHardwareIdFilter;

        /// <summary>
        /// Get or set the KB article filter
        /// </summary>
        /// <value>List of KB article ids - numbers only</value>
        public List<string> KbArticleFilter;

        /// <summary>
        /// Get or set the KB article exclusion filter
        /// </summary>
        /// <value>List of excluded KB article ids - numbers only</value>
        public List<string> ExcludedKbArticleFilter;

        /// <summary>
        /// Get the sorting order of this filter
        /// </summary>
        public MetadataSortOrder SortOrder;

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

        private IEnumerable<T> Sort<T>(IEnumerable<T> updates) where T : MicrosoftUpdatePackage
        {
            if (!SortOrder.NeedSort())
            {
                return updates;
            }

            IOrderedEnumerable<T> orderedUpdates = null;

            void ApplySort<TKey>(SortOrder direction, Func<T, TKey> keySelector)
            {
                switch (direction)
                {
                    case Metadata.SortOrder.None:
                        return;

                    case Metadata.SortOrder.Ascending:
                        if (orderedUpdates == null)
                        {
                            orderedUpdates = updates.OrderBy(keySelector);
                        }
                        else
                        {
                            orderedUpdates = orderedUpdates.ThenBy(keySelector);
                        }
                        break;

                    case Metadata.SortOrder.Descending:
                        if (orderedUpdates == null)
                        {
                            orderedUpdates = updates.OrderByDescending(keySelector);
                        }
                        else
                        {
                            orderedUpdates = orderedUpdates.ThenByDescending(keySelector);
                        }
                        break;

                    default:
                        throw new InvalidOperationException("Invalid sorting order");
                }
            }

            ApplySort(SortOrder.KbArticle, u => u is SoftwareUpdate su ? su.KBArticleId : null);
            ApplySort(SortOrder.CreationDate, u => u.CreationDate);
            ApplySort(SortOrder.Id, u => u.Id.ID);
            ApplySort(SortOrder.Title, u => u.Title);

            return orderedUpdates ?? updates;
        }

        /// <summary>
        /// Apply the filter to a <see cref="IMetadataSource"/> and returns the matching packages of the specified type.
        /// </summary>
        /// <typeparam name="T">Package type to query. The type must inherit <see cref="MicrosoftUpdatePackage"/></typeparam>
        /// <param name="packages">The packages to filter</param>
        /// <returns>Matching packages</returns>
        public IEnumerable<T> Apply<T>(IEnumerable<IPackage> packages) where T : MicrosoftUpdatePackage
        {
            if (TryGetStoreBackedFilter(packages, out var storeBacked))
            {
                var packageType = GetStoredPackageType(typeof(T));
                return storeBacked
                    .FilterFromStore(CloneWithPackageType(packageType))
                    .Cast<T>();
            }

            var updates = packages.OfType<T>();

            IEnumerable<T> filteredUpdates;

            if (!string.IsNullOrEmpty(HardwareIdFilter) ||
                !string.IsNullOrEmpty(ExcludedHardwareIdFilter) ||
                ComputerHardwareIdFilter != Guid.Empty ||
                ExcludedComputerHardwareIdFilter != Guid.Empty)
            {
                filteredUpdates = updates.Where(u => u is DriverUpdate);
            }
            else if (KbArticleFilter is { Count: > 0 } || ExcludedKbArticleFilter is { Count: > 0 })
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

            if (!string.IsNullOrEmpty(ExcludedHardwareIdFilter))
            {
                filteredUpdates = filteredUpdates.Where(
                    u => u is not DriverUpdate driverUpdate ||
                    !driverUpdate.GetDriverMetadata()
                        .Any(metadata => metadata.HardwareId.Equals(ExcludedHardwareIdFilter, StringComparison.OrdinalIgnoreCase)));
            }

            if (ComputerHardwareIdFilter != Guid.Empty)
            {
                filteredUpdates = filteredUpdates.Where(
                    u => u is DriverUpdate driverUpdate &&
                    driverUpdate.GetDriverMetadata()
                    .Any(metadata => metadata.DistributionComputerHardwareId.Contains(ComputerHardwareIdFilter)));
            }

            if (ExcludedComputerHardwareIdFilter != Guid.Empty)
            {
                filteredUpdates = filteredUpdates.Where(
                    u => u is not DriverUpdate driverUpdate ||
                    !driverUpdate.GetDriverMetadata()
                        .Any(metadata => metadata.DistributionComputerHardwareId.Contains(ExcludedComputerHardwareIdFilter)));
            }

            if (ProductFilter is { Count: > 0 })
            {
                var productSet = ProductFilter.ToHashSet();
                filteredUpdates = filteredUpdates.Where(u => GetCategoryPrerequisiteIds(u).Any(productSet.Contains));
            }

            if (ExcludedProductFilter is { Count: > 0 })
            {
                var excludedProductSet = ExcludedProductFilter.ToHashSet();
                filteredUpdates = filteredUpdates.Where(u => !GetCategoryPrerequisiteIds(u).Any(excludedProductSet.Contains));
            }

            if (ClassificationFilter is { Count: > 0 })
            {
                var classificationSet = ClassificationFilter.ToHashSet();
                filteredUpdates = filteredUpdates.Where(u => GetCategoryPrerequisiteIds(u).Any(classificationSet.Contains));
            }

            if (ExcludedClassificationFilter is { Count: > 0 })
            {
                var excludedClassificationSet = ExcludedClassificationFilter.ToHashSet();
                filteredUpdates = filteredUpdates.Where(u => !GetCategoryPrerequisiteIds(u).Any(excludedClassificationSet.Contains));
            }

            if (KbArticleFilter is { Count: > 0 })
            {
                var kbLookup = KbArticleFilter.ToHashSet();
                filteredUpdates = filteredUpdates.OfType<SoftwareUpdate>().Where(u => kbLookup.Contains(u.KBArticleId)).Cast<T>();
            }

            if (ExcludedKbArticleFilter is { Count: > 0 })
            {
                var excludedKbLookup = ExcludedKbArticleFilter.ToHashSet();
                filteredUpdates = filteredUpdates.Where(u =>
                    u is not SoftwareUpdate softwareUpdate ||
                    !excludedKbLookup.Contains(softwareUpdate.KBArticleId));
            }

            // Apply the title filter
            if (!string.IsNullOrEmpty(TitleFilter))
            {
                var filterTokens = TitleFilter.Trim().Split([' '], StringSplitOptions.RemoveEmptyEntries);
                filteredUpdates = filteredUpdates.Where(category => category.MatchTitle(filterTokens));
            }

            if (!string.IsNullOrEmpty(ExcludedTitleFilter))
            {
                var excludeTokens = ExcludedTitleFilter.Trim().Split([' '], StringSplitOptions.RemoveEmptyEntries);
                filteredUpdates = filteredUpdates.Where(category => !category.MatchTitle(excludeTokens));
            }

            // Apply the id filter
            if (IdFilter is { Count: > 0 })
            {
                // Remove all updates that don't match the ID filter
                filteredUpdates = filteredUpdates.Where(u => IdFilter.Contains(u.Id.ID));
            }

            if (ExcludedIdFilter is { Count: > 0 })
            {
                var excludedIdSet = ExcludedIdFilter.ToHashSet();
                filteredUpdates = filteredUpdates.Where(u => !excludedIdSet.Contains(u.Id.ID));
            }

            if (SkipSuperseded)
            {
                filteredUpdates = filteredUpdates
                    .Where(u => u is not SoftwareUpdate softwareUpdate ||
                    (softwareUpdate.IsSupersededBy?.Count ?? 0) == 0);
            }

            if (!IncludeBundled)
            {
                filteredUpdates = filteredUpdates
                    .Where(u => u is not SoftwareUpdate softwareUpdate ||
                    (softwareUpdate.BundledWithUpdates?.Count ?? 0) == 0);
            }

            if (!IncludeExpired)
            {
                filteredUpdates = filteredUpdates.Where(u => !u.IsExpired);
            }

            // Skip X matches, if requested
            if (AfterX > 0)
            {
                filteredUpdates = filteredUpdates.Skip(AfterX);
            }

            // Return first X matches, if requested
            if (FirstX > 0)
            {
                filteredUpdates = filteredUpdates.Take(FirstX);
            }

            filteredUpdates = Sort(filteredUpdates);

            return filteredUpdates;
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

        /// <summary>
        /// Get the identities of packages that match the criteria from <see cref="IMetadataSource"/> 
        /// </summary>
        /// <typeparam name="T">Package identity type to get its identities. The type must inherit <see cref="MicrosoftUpdatePackage"/></typeparam>
        /// <param name="packages">The packages to filter</param>
        /// <returns>Matching packages' identities</returns>
        public IEnumerable<IPackageIdentity> GetMatchingIdentities<T>(IEnumerable<IPackage> packages) where T : MicrosoftUpdatePackage
        {
            if (TryGetStoreBackedFilter(packages, out var storeBacked))
            {
                var packageType = GetStoredPackageType(typeof(T));
                return storeBacked
                    .GetIdentitiesFromStore(CloneWithPackageType(packageType));
            }

            return Apply<MicrosoftUpdatePackage>(packages)
                .Select(p => p.Id);
        }

        /// <summary>
        /// Get the identities of packages that match the criteria from <see cref="IMetadataSource"/> 
        /// </summary>
        /// <param name="packages">The packages to filter</param>
        /// <returns>Matching packages' identities</returns>
        public IEnumerable<IPackageIdentity> GetMatchingIdentities(IEnumerable<IPackage> packages)
        {
            return GetMatchingIdentities<MicrosoftUpdatePackage>(packages);
        }

        /// <summary>
        /// Get the number of packages that match the criteria
        /// </summary>
        /// <param name="packages">The packages to filter</param>
        /// <returns>The number of matching packages</returns>
        public int GetCount(IEnumerable<IPackage> packages)
        {
            if (TryGetStoreBackedFilter(packages, out var storeBacked))
            {
                var packageType = GetStoredPackageType(typeof(MicrosoftUpdatePackage));
                return storeBacked.CountFromStore(CloneWithPackageType(packageType));
            }

            return Apply(packages).Count();
        }
    }
}

