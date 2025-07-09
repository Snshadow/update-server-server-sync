// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using Azure.Storage.Blobs;

namespace Microsoft.PackageGraph.Storage.Azure
{
    /// <summary>
    /// Creates an instance of <see cref="IMetadataStore"/> that stores metadata in Azure Blob Storage
    /// </summary>
    public abstract class PackageStore
    {
        /// <summary>
        /// Opens an existing <see cref="IMetadataStore"/> from the specified Blob storage account and container name
        /// </summary>
        /// <param name="client">Azure blob client</param>
        /// <param name="containerName">Container name that contains the metadata store</param>
        /// <returns>An instance of IMetadataStore</returns>
        public static IMetadataStore Open(BlobServiceClient client, string containerName)
        {
            return ContainerPackageStore.OpenExisting(client, containerName);
        }

        /// <summary>
        /// Permanently deletes a <see cref="IMetadataStore"/> stored in the specified Azure Blob account and container
        /// </summary>
        /// <param name="client">Azure blob client</param>
        /// <param name="containerName">Container name that contains the metadata store</param>
        public static void Erase(BlobServiceClient client, string containerName)
        {
            ContainerPackageStore.Erase(client, containerName);
        }

        /// <summary>
        /// Opens an existing <see cref="IMetadataStore"/> from the specified blob storage container reference
        /// </summary>
        /// <param name="storeContainer">Reference to the container from which to open the metadata store</param>
        /// <returns>An instance of IMetadataStore</returns>
        public static IMetadataStore Open(BlobContainerClient storeContainer)
        {
            return ContainerPackageStore.OpenExisting(storeContainer);
        }

        /// <summary>
        /// Opens or create a <see cref="IMetadataStore"/> from the specified blob storage container reference
        /// </summary>
        /// <param name="client">Azure blob client</param>
        /// <param name="containerName">Container name that contains the metadata store</param>
        /// <returns>An instance of IMetadataStore</returns>
        public static IMetadataStore OpenOrCreate(BlobServiceClient client, string containerName)
        {
            return ContainerPackageStore.OpenOrCreate(client, containerName);
        }

        /// <summary>
        /// Checks if a <see cref="IMetadataStore"/> exists in the specified Azure Blob account and container
        /// </summary>
        /// <param name="client">Azure blob client</param>
        /// <param name="container">Container name that contains the metadata store</param>
        /// <returns>True if the metadata store exists, false otherwise</returns>
        public static bool Exists(BlobServiceClient client, BlobContainerClient container)
        {
            throw new NotImplementedException();
        }
    }
}