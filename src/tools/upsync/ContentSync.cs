// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Azure.Storage.Blobs;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.Storage.Local;
using Microsoft.PackageGraph.Utilitites.Upsync.Commands;
using System.Linq;
using System.Threading;
using System;
using System.Collections.Generic;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    class ContentSync
    {
        public static void SyncContent(ContentSyncCommand.Settings options)
        {
            var metadataSource = MetadataStoreCreator.OpenFromOptions(options as IMetadataStoreOptions);
            if (metadataSource is null)
            {
                return;
            }

            var contentStore = GetContentStoreFromOptions(options);
            if (contentStore is null)
            {
                return;
            }

            var filter = FilterBuilder.MicrosoftUpdateFilterFromCommandLine(options as IMetadataFilterOptions);
            if (filter is null)
            {
                return;
            }

            var filteredPackages = filter.Apply(metadataSource);

            var filesToDownload = filteredPackages.Where(p => p.Files is not null).SelectMany(p => p.Files).ToList();

            foreach (var microsoftUpdatePackage in filteredPackages.OfType<MicrosoftUpdatePackage>())
            {
                filesToDownload.AddRange(GetAllUpdateFiles(metadataSource, microsoftUpdatePackage));
            }

            filesToDownload = filesToDownload.Distinct().ToList();

            Console.WriteLine($"Sync {filesToDownload.Count} files, {filesToDownload.Sum(f => (long)f.Size)} bytes. Continue? (y/n)");
            if (Console.ReadKey().Key != ConsoleKey.Y)
            {
                return;
            }

            CancellationTokenSource cancelTokenSource = new();
            contentStore.Progress += ContentStore_Progress;
            contentStore.Download(filesToDownload, cancelTokenSource.Token);
        }

        /// <summary>
        /// Gets all files for an update, including files in bundled updates (recursive)
        /// </summary>
        /// <param name="update"></param>
        /// <returns></returns>
        private static List<IContentFile> GetAllUpdateFiles(IMetadataStore metadataSource, MicrosoftUpdatePackage update)
        {
            var filesList = new List<IContentFile>();
            if (update.Files is not null)
            {
                filesList.AddRange(update.Files);
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

        static string ContentSyncLastFileDigest = "";

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

        private static void ContentStore_Progress(object sender, ObjectModel.ContentOperationProgress e)
        {
            if (e.File.Digest.DigestBase64 != ContentSyncLastFileDigest)
            {
                Console.WriteLine();
                ContentSyncLastFileDigest = e.File.Digest.DigestBase64;
            }

            switch (e.CurrentOperation)
            {
                case PackagesOperationType.DownloadFileProgress:
                    UpdateConsoleForMessageRefresh();
                    Console.Write("Sync'ing update content [{0}]: {1:000.00}%", e.Maximum, e.PercentDone);
                    break;
            }
        }

        private static IContentStore GetContentStoreFromOptions(ContentSyncCommand.Settings options)
        {
            switch (options.ContentStoreType)
            {
                case "local":
                    return new FileSystemContentStore(options.ContentPath);

                case "azure":
                    try
                    {
                        var blobClient = new BlobServiceClient(options.ContentStoreConnectionString);
                        return Storage.Azure.BlobContentStore.OpenOrCreate(blobClient, options.ContentPath);
                    }
                    catch (Exception ex)
                    {
                        ConsoleOutput.WriteRed($"Failed to get azure content store: {ex.Message}");
                        return null;
                    }

                default:
                    ConsoleOutput.WriteRed("Content store type not supported.");
                    return null;

            }
        }
    }
}
