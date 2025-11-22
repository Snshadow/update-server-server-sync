// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    public interface IMetadataFilterOptions
    {
        IEnumerable<string> ProductsFilter { get; }

        IEnumerable<string> ClassificationsFilter { get; }

        IEnumerable<string> IdFilter { get; }

        string HardwareIdFilter { get; }

        string ComputerHardwareIdFilter { get; set; }

        string TitleFilter { get; }

        bool SkipSuperseded { get; }

        bool IncludeBundled { get; }

        bool IncludeExpired { get; }

        IEnumerable<string> KbArticleFilter { get; }

        int FirstX { get; }

        int AfterX { get; }

        IEnumerable<string> SortOrder { get; }
    }

    /// <summary>
    /// Class for building metadata filters from command line options
    /// </summary>
    class FilterBuilder
    {
        internal static List<Guid> StringGuidsToGuids(IEnumerable<string> stringGuids)
        {
            var returnList = new List<Guid>();
            foreach (var guidString in stringGuids ?? [])
            {
                if (!Guid.TryParse(guidString, out Guid guid))
                {
                    return null;
                }

                returnList.Add(guid);
            }

            return returnList;
        }

        public static MetadataFilter MicrosoftUpdateFilterFromCommandLine(IMetadataFilterOptions filterOptions)
        {
            var filter = new MetadataFilter()
            {
                TitleFilter = filterOptions.TitleFilter,
                HardwareIdFilter = filterOptions.HardwareIdFilter,
                KbArticleFilter = filterOptions.KbArticleFilter?.ToList()
            };

            if (filterOptions.SortOrder != null)
            {
                var creationDateSort = SortOrder.None;
                var idSort = SortOrder.None;
                var kbArticleSort = SortOrder.None;
                var titleSort = SortOrder.None;

                foreach (var sortOption in filterOptions.SortOrder)
                {
                    var parts = sortOption.Split(':');
                    if (parts.Length != 2)
                    {
                        ConsoleOutput.WriteRed($"Invalid sort option format: {sortOption}. Expected format is 'field:direction'.");
                        return null;
                    }

                    var field = parts[0].ToLowerInvariant();
                    var directionStr = parts[1].ToLowerInvariant();
                    SortOrder direction;

                    switch (directionStr)
                    {
                        case "asc":
                            direction = SortOrder.Ascending;
                            break;
                        case "desc":
                            direction = SortOrder.Descending;
                            break;
                        default:
                            ConsoleOutput.WriteRed($"Invalid sort direction: {directionStr}. Use 'asc' or 'desc'.");
                            return null;
                    }

                    switch (field)
                    {
                        case "creationdate":
                            creationDateSort = direction;
                            break;
                        case "id":
                            idSort = direction;
                            break;

                        case "kbarticle":
                            kbArticleSort = direction;
                            break;
                        case "title":
                            titleSort = direction;
                            break;
                        default:
                            ConsoleOutput.WriteRed($"Invalid sort field: {field}. Available fields are 'creationDate', 'id', 'kbArticle', 'title'.");
                            return null;
                    }
                }

                filter.SortOrder = new MetadataSortOrder(creationDateSort, idSort, kbArticleSort, titleSort);
            }

            if (!string.IsNullOrEmpty(filterOptions.ComputerHardwareIdFilter))
            {
                if (!Guid.TryParse(filterOptions.ComputerHardwareIdFilter, out Guid computerHardwareIdFilterGuid))
                {
                    ConsoleOutput.WriteRed($"The computer hardware id must be a GUID. It was {filterOptions.ComputerHardwareIdFilter}");
                    return null;
                }
                else
                {
                    filter.ComputerHardwareIdFilter = computerHardwareIdFilterGuid;
                }
            }

            filter.ClassificationFilter = StringGuidsToGuids(filterOptions.ClassificationsFilter);
            if (filter.ClassificationFilter is null)
            {
                ConsoleOutput.WriteRed("The classification filter must contain only GUIDs!");
                return null;
            }

            filter.ProductFilter = StringGuidsToGuids(filterOptions.ProductsFilter);
            if (filter.ProductFilter is null)
            {
                ConsoleOutput.WriteRed("The product ID filter must contain only GUIDs!");
                return null;
            }

            filter.IdFilter = StringGuidsToGuids(filterOptions.IdFilter);
            if (filter.IdFilter is null)
            {
                ConsoleOutput.WriteRed("The update ID filter must contain only GUIDs!");
                return null;
            }

            filter.SkipSuperseded = filterOptions.SkipSuperseded;
            filter.IncludeExpired = filterOptions.IncludeExpired;
            if (!filterOptions.IncludeBundled)
            {
                filter.BundleFilter = BundleType.NotBundled;
            }
            filter.FirstX = filterOptions.FirstX;
            filter.AfterX = filterOptions.AfterX;

            return filter;
        }
    }
}
