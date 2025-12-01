// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Azure.Storage.Blobs;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.Storage.Local;
using Microsoft.PackageGraph.Utilitites.Upsync.Commands;

using System;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    public interface IContentStoreOptions
    {
        public string ContentPath { get; }
        public string ContentStoreType { get; }
        public string ContentStoreConnectionString { get; }
    }

    class ContentStoreCreator
    {
        public static IContentStore GetFromOptions(IContentStoreOptions options)
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
