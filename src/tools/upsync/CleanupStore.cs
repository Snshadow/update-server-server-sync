// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.Utilitites.Upsync.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    class Cleanup
    {
        /// <summary>
        /// Gets all files for an update, including files in bundled updates (recursive)
        /// </summary>
        /// <param name="update"></param>
        /// <returns></returns>
        private static List<IContentFileDigest> GetAllUpdateFiles(IMetadataStore metadataSource, MicrosoftUpdatePackage update)
        {
            List<IContentFileDigest> filesList = [];
            if (update.Files is not null)
            {
                filesList.AddRange(update.Files.Select(file => file.Digest));
            }

            if (update is SoftwareUpdate softwareUpdate && softwareUpdate.BundledUpdates is not null)
            {
                foreach (var bundledUpdate in softwareUpdate.BundledUpdates)
                {
                    filesList.AddRange(
                        GetAllUpdateFiles(
                            metadataSource,
                            metadataSource.GetPackage(bundledUpdate) as MicrosoftUpdatePackage));
                }
            }

            return filesList;
        }

        public static async Task CleanupStore(CleanupCommand.Settings options)
        {
            var store = MetadataStoreCreator.OpenFromOptions(options);
            if (store is not (IDeploySyncStore deploymentStore and IMetadataStore metadataStore))
            {
                return;
            }

            var contentStore = ContentStoreCreator.GetFromOptions(options);

            using (store)
            {
                Console.WriteLine("Collecting unused content files...");

                var unapprovedUpdates = deploymentStore.GetUnapprovedRevisionIds();
                MetadataFilter filter = new()
                {
                    IncludeExpired = true,
                    RevisionIdFilter = unapprovedUpdates.ToList(),
                };

                var fileDigests = filter.Apply<MicrosoftUpdatePackage>(metadataStore)
                    .SelectMany(unapproved => GetAllUpdateFiles(metadataStore, unapproved))
                    .Distinct();

                var approvedUpdates = deploymentStore.GetApprovedRevisionIds();
                filter.RevisionIdFilter = approvedUpdates.ToList();

                var usedFileDigests = filter.Apply<MicrosoftUpdatePackage>(metadataStore)
                    .SelectMany(approved => GetAllUpdateFiles(metadataStore, approved))
                    .Distinct();

                var unusedFileDigests = fileDigests.Except(usedFileDigests);

                Console.WriteLine("Removing unused content files...");

                ParallelOptions parallelOptions = new()
                {
                    MaxDegreeOfParallelism = 10
                };

                await Parallel.ForEachAsync(unusedFileDigests, parallelOptions, async (fileDigest, _) =>
                {
                    await contentStore.DeleteAsync(fileDigest, CancellationToken.None);
                });
            }
        }
    }
}
