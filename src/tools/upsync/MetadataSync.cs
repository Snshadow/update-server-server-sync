// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Source;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.Utilitites.Upsync.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    /// <summary>
    /// Implements operations to fetch update metadata from an upstream update server
    /// </summary>
    class MetadataSync
    {
        public static void ReIndex(ReindexCommand.Settings options)
        {
            var sourceToUpdate = MetadataStoreCreator.OpenFromOptions(options);
            if (sourceToUpdate is null)
            {
                return;
            }

            using (sourceToUpdate)
            {
                if (sourceToUpdate.IsMetadataIndexingSupported)
                {
                    sourceToUpdate.PackageIndexingProgress += Program.OnPackageIndexingProgress;
                    if (sourceToUpdate.IsReindexingRequired || options.ForceReindex)
                    {
                        Console.WriteLine("ReIndexing ...");
                        sourceToUpdate.ReIndex();
                        ConsoleOutput.WriteGreen("Done!");
                    }
                    else
                    {
                        ConsoleOutput.WriteGreen("Indexing not required!");
                    }
                }
                else
                {
                    ConsoleOutput.WriteRed("Package store does not support indexing!");
                }
            }
        }

        public static void FetchCategories(FetchCategoriesCommand.Settings options)
        {
            Endpoint upstreamEndpoint;
            if (!string.IsNullOrEmpty(options.UpstreamEndpoint))
            {
                upstreamEndpoint = new Endpoint(options.UpstreamEndpoint);
            }
            else
            {
                upstreamEndpoint = Endpoint.Default;
            }

            if (!string.IsNullOrEmpty(options.AccountName) &&
                !string.IsNullOrEmpty(options.AccountGuid))
            {
                throw new NotImplementedException();
            }

            var destinationStore = MetadataStoreCreator.CreateFromOptions(options);
            if (destinationStore is null)
            {
                return;
            }

            Console.WriteLine();
            Console.WriteLine($"Getting list of categories. This might take up to 1 minute ...");
            using (destinationStore)
            {
                var microsoftUpdateCategoriesSource = new UpstreamCategoriesSource(upstreamEndpoint);
                microsoftUpdateCategoriesSource.MetadataCopyProgress += Program.OnPackageCopyProgress;
                microsoftUpdateCategoriesSource.CopyTo(destinationStore, CancellationToken.None);
            }

            Console.WriteLine();
            ConsoleOutput.WriteGreen("Done!");
        }

        public static void FetchPackagesUpdates(FetchCommand.Settings options)
        {
            var store = MetadataStoreCreator.CreateFromOptions(options);
            if (store is null)
            {
                return;
            }

            switch (options.EndpointType)
            {
                case "microsoft-update":
                    FetchMicrosoftUpdatePackages(options, store);
                    break;

                default:
                    throw new NotImplementedException();
            }
        }

        private static void FetchMicrosoftUpdatePackages(FetchCommand.Settings options, IMetadataStore store)
        {
            var upstreamEndpoint = string.IsNullOrEmpty(options.UpstreamEndpoint) ? Endpoint.Default : new Endpoint(options.UpstreamEndpoint);

            if (!string.IsNullOrEmpty(options.AccountName) &&
                !string.IsNullOrEmpty(options.AccountGuid))
            {
                throw new NotImplementedException();
            }

            using (store)
            {
                var microsoftUpdateCategoriesSource = new UpstreamCategoriesSource(upstreamEndpoint);

                Console.WriteLine($"Getting list of categories. This might take up to 1 minute ...");

                microsoftUpdateCategoriesSource.MetadataCopyProgress += Program.OnPackageCopyProgress;
                microsoftUpdateCategoriesSource.CopyTo(store, CancellationToken.None);

                if (!string.IsNullOrEmpty(options.Ids))
                {
                    var server = new UpstreamServerClient(upstreamEndpoint);

                    foreach (var updateId in options.Ids.Split('+'))
                    {
                        if (Guid.TryParse(updateId, out var updateIdGuid))
                        {
                            Console.WriteLine();
                            Console.Write($"Searching for package {updateId}");
                            var foundPackage = server.TryGetExpiredUpdate(updateIdGuid, 300, 100).GetAwaiter().GetResult();
                            if (foundPackage is null)
                            {
                                ConsoleOutput.WriteRed($" Not found!");
                            }
                            else
                            {
                                ConsoleOutput.WriteGreen($" Found!");
                                store.AddPackage(foundPackage);
                            }
                        }
                        else
                        {
                            ConsoleOutput.WriteRed($"Update id must be in GUID format: {updateId}");
                            return;
                        }
                    }
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("Creating the query ...");
                    UpstreamSourceFilter sourceFilter;
                    try
                    {
                        sourceFilter = CreateValidFilterFromOptions(options, store);
                    }
                    catch (Exception ex)
                    {
                        ConsoleOutput.WriteRed(ex.Message);
                        return;
                    }

                    MetadataQuery.PrintFilter(sourceFilter, store);

                    Console.WriteLine($"Getting list of updates. This might take up to 1 minute ...");
                    var microsoftUpdateSource = new UpstreamUpdatesSource(upstreamEndpoint, sourceFilter);
                    microsoftUpdateSource.MetadataCopyProgress += Program.OnPackageCopyProgress;
                    microsoftUpdateSource.CopyTo(store, CancellationToken.None);

                    Console.WriteLine();
                    ConsoleOutput.WriteGreen("Done!");
                }
            }
        }

        private static void UpdateConsoleForMessageRefresh()
        {
            if (!Console.IsOutputRedirected)
            {
                Console.CursorLeft = 0;
            }
            else
            {
                Console.WriteLine();
            }
        }

        /// <summary>
        /// Handles progress notifications from a metadata query on an upstream server.
        /// Prints progress information to the console
        /// </summary>
        /// <param name="sender">The upstream server client that raised the event</param>
        /// <param name="e">Progress information</param>
        private static void Server_MetadataQueryProgress(object sender, MetadataQueryProgress e)
        {
            switch (e.CurrentTask)
            {
                case MetadataQueryStage.AuthenticateStart:
                    Console.Write("Acquiring new access token...");
                    break;

                case MetadataQueryStage.GetServerConfigStart:
                    Console.Write("Retrieving service configuration data...");
                    break;

                case MetadataQueryStage.AuthenticateEnd:
                case MetadataQueryStage.GetServerConfigEnd:
                case MetadataQueryStage.GetRevisionIdsEnd:
                    ConsoleOutput.WriteGreen("Done!");
                    break;

                case MetadataQueryStage.GetRevisionIdsStart:
                    Console.Write("Retrieving revision IDs...");
                    break;

                case MetadataQueryStage.GetUpdateMetadataStart:
                    Console.Write("Retrieving updates metadata [{0}]: 0%", e.Maximum);
                    break;

                case MetadataQueryStage.GetUpdateMetadataProgress:
                    UpdateConsoleForMessageRefresh();
                    Console.Write("Retrieving updates metadata [{0}]: {1:000.00}%", e.Maximum, e.PercentDone);
                    break;

                case MetadataQueryStage.GetUpdateMetadataEnd:
                    UpdateConsoleForMessageRefresh();
                    Console.Write("Retrieving updates metadata [{0}]: 100.00%", e.Maximum);
                    ConsoleOutput.WriteGreen(" Done!");
                    break;
            }
        }

        private static List<Guid> CreateFilterListForCategory<T>(IEnumerable<string> userFilterList, IMetadataStore metadataSource)
        {
            List<Guid> filterList;
            if (userFilterList.Any())
            {
                filterList = new List<Guid>();
                foreach (var guidString in userFilterList)
                {
                    if (Guid.TryParse(guidString, out Guid guid))
                    {
                        filterList.Add(guid);
                    }
                }
            }
            else
            {
                filterList = metadataSource.OfType<T>()
                    .Select(update => (update as MicrosoftUpdatePackage).Id.ID)
                    .ToList();

                if (filterList.Count == 0)
                {
                    throw new Exception("No products information available to create a filter");
                }
            }

            return filterList;
        }

        private static UpstreamSourceFilter CreateValidFilterFromOptions(FetchCommand.Settings options, IMetadataStore metadataSource)
        {
            List<Guid> productFilter = CreateFilterListForCategory<ProductCategory>(
                options.ProductsFilter?.Split('+') ?? Enumerable.Empty<string>(),
                metadataSource);

            List<Guid> classificationFilter = CreateFilterListForCategory<ClassificationCategory>(
                options.ClassificationsFilter?.Split('+') ?? Enumerable.Empty<string>(),
                metadataSource);

            return new UpstreamSourceFilter(productFilter, classificationFilter);
        }
    }
}
