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
        private static List<IContentFileDigest> GetAllFileDigests(IMetadataStore metadataSource, MicrosoftUpdatePackage update)
        {
            List<IContentFileDigest> fileDigestList = [];
            if (update.Files is not null)
            {
                fileDigestList.AddRange(update.Files.Select(file => file.Digest));
            }

            if (update is SoftwareUpdate { BundledUpdates: not null } softwareUpdate)
            {
                foreach (var bundledUpdate in softwareUpdate.BundledUpdates)
                {
                    fileDigestList.AddRange(
                        GetAllFileDigests(
                            metadataSource,
                            metadataSource.GetPackage(bundledUpdate) as MicrosoftUpdatePackage));
                }
            }

            return fileDigestList;
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
                    .SelectMany(unapproved => GetAllFileDigests(metadataStore, unapproved))
                    .Distinct();

                var approvedUpdates = deploymentStore.GetApprovedRevisionIds();
                filter.RevisionIdFilter = approvedUpdates.ToList();

                var usedFileDigests = filter.Apply<MicrosoftUpdatePackage>(metadataStore)
                    .SelectMany(approved => GetAllFileDigests(metadataStore, approved))
                    .Distinct();

                var unusedFileDigests = fileDigests.Except(usedFileDigests);

                ParallelOptions parallelOptions = new()
                {
                    MaxDegreeOfParallelism = 10
                };

                ContentOperationProgress deleteProgress = new()
                {
                    Maximum = unusedFileDigests.Count()
                };

                await Parallel.ForEachAsync(unusedFileDigests, parallelOptions, async (fileDigest, _) =>
                {
                    await contentStore.DeleteAsync(fileDigest, CancellationToken.None);
                    lock (deleteProgress)
                    {
                        deleteProgress.Current++;
                        UpdateConsoleForMessageRefresh();
                        Console.Write("Deleted {0} of {1} files", deleteProgress.Current, deleteProgress.Maximum);
                    }
                });
            }
        }
    }
}
