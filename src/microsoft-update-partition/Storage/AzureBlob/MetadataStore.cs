// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Partitions;
using System.Text.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Microsoft.PackageGraph.Storage.Azure
{
    class MetadataStore
    {
        readonly BlobContainerClient Container;

        private const long InitialBlobSize = 32 * 1024 * 1024;
        private const int MetadataPageSize = 512;

        private long NextAvailableOffset;
        private long PageBlobSize;

        private const string MetadataBlobName = "metadata";

        private readonly object MetadataBlobLock = new();

        private const int UploadCacheSize = 32 * 1024 * 1024;
        private readonly MemoryStream UploadCache = new(UploadCacheSize);
        private long UploadCacheOffset = 0;

        private readonly EventWaitHandle BackBufferReadyEvent = new(true, EventResetMode.AutoReset);

        private const int DownloadCacheSize = 4 * 1024 * 1024;
        private long DownloadCacheOffset = long.MaxValue;
        private readonly MemoryStream DownloadCache = new(DownloadCacheSize);

        internal MetadataStore(BlobContainerClient container)
        {
            Container = container;
            var targetBlob = container.GetPageBlobClient(MetadataBlobName);

            if (!targetBlob.ExistsAsync().Result)
            {
                targetBlob.CreateAsync(InitialBlobSize).Wait();
                UploadCacheOffset = NextAvailableOffset = 0;
                PageBlobSize = 0;
            }
            else
            {
                var ranges = targetBlob.GetPageRangesAsync().Result.Value.PageRanges;
                UploadCacheOffset = NextAvailableOffset = !ranges.Any() ? 0 : ranges.Max(r => r.Offset + r.Length) ?? 0;
                PageBlobSize = targetBlob.GetPropertiesAsync().Result.Value.ContentLength;
            }
        }

        private void FillReadCache(long startOffset, long requiredLength)
        {
            DownloadCache.Seek(0, SeekOrigin.Begin);
            var targetBlob = Container.GetPageBlobClient(MetadataBlobName);

            if (startOffset > NextAvailableOffset)
            {
                throw new Exception("Download offset cannot be past the end of the page blob");
            }

            var fillSize = Math.Max(requiredLength, DownloadCacheSize);
            fillSize = Math.Min(fillSize, NextAvailableOffset - startOffset);

            if (fillSize < requiredLength)
            {
                throw new Exception("Not enough range available in the metadata blob");
            }

            DownloadCache.Seek(0, SeekOrigin.Begin);
            var response = targetBlob.DownloadAsync(new HttpRange(startOffset, fillSize)).Result;
            response.Value.Content.CopyTo(DownloadCache);
            DownloadCache.Seek(0, SeekOrigin.Begin);
            DownloadCache.SetLength(fillSize);
            DownloadCacheOffset = startOffset;
        }

        private void FillBufferForPackage(PackageStoreEntry packageEntry)
        {
            lock (DownloadCache)
            {
                var minBufferRequired = (packageEntry.FileListOffset - packageEntry.MetadataOffset) + packageEntry.FileListLength;

                if (packageEntry.MetadataOffset < DownloadCacheOffset ||
                    packageEntry.MetadataOffset + minBufferRequired >= DownloadCacheOffset + DownloadCache.Length)
                {
                    FillReadCache(packageEntry.MetadataOffset, minBufferRequired);
                }
            }
        }

        public Stream GetMetadata(PackageStoreEntry packageEntry)
        {
            lock(DownloadCache)
            {
                FillBufferForPackage(packageEntry);

                var cachedPackageBuffer = new byte[packageEntry.MetadataLength];
                DownloadCache.Seek(packageEntry.MetadataOffset - DownloadCacheOffset, SeekOrigin.Begin);
                DownloadCache.Read(cachedPackageBuffer, 0, cachedPackageBuffer.Length);
                return new GZipStream(new MemoryStream(cachedPackageBuffer), CompressionMode.Decompress);
            }
        }

        public List<T> GetFiles<T>(PackageStoreEntry packageEntry)
        {
            if (packageEntry.FileListLength == 0)
            {
                return new List<T>();
            }

            Stream inMemoryFilesList;
            lock (DownloadCache)
            {
                FillBufferForPackage(packageEntry);

                var cachedFileListBuffer = new byte[packageEntry.FileListLength];
                DownloadCache.Seek(packageEntry.FileListOffset - DownloadCacheOffset, SeekOrigin.Begin);
                DownloadCache.Read(cachedFileListBuffer);
                inMemoryFilesList = new GZipStream(new MemoryStream(cachedFileListBuffer), CompressionMode.Decompress);
            }

            using var filesReader = new StreamReader(inMemoryFilesList);
            var filesList = JsonSerializer.Deserialize<List<T>>(filesReader.ReadToEnd());

            return filesList;
        }

        private static long RoundToPageSize(long value) => value % MetadataPageSize == 0 ? value : MetadataPageSize * (value / MetadataPageSize) + MetadataPageSize;

        private static MemoryStream CreateFileMetadataStream(IPackage package)
        {
            var filesMetadata = new MemoryStream();
            using (var textWriter = new StreamWriter(filesMetadata, Encoding.UTF8, 4096, true))
            {
                textWriter.Write(JsonSerializer.Serialize(package.Files));
            }

            filesMetadata.Seek(0, SeekOrigin.Begin);
            return filesMetadata;
        }

        private static bool PackageHasExternalFileMetadata(IPackage package)
        {
            return (PartitionRegistration.TryGetPartitionFromPackage(package, out var partitionDefinition) &&
                partitionDefinition.HasExternalContentFileMetadata &&
                package.Files != null &&
                package.Files.Any());
        }

        public PackageStoreEntry AddPackage(IPackage package)
        {
            PackageStoreEntry newEntry = new(package.Id.ToString(), 0);

            using var uploadStream = new MemoryStream();
            using (var compressor = new GZipStream(uploadStream, CompressionLevel.Optimal, true))
            {
                package.GetMetadataStream().CopyTo(compressor);
            }

            newEntry.MetadataLength = uploadStream.Length;

            uploadStream.SetLength(RoundToPageSize(uploadStream.Length));

            var filesMetadataRelativeOffset = uploadStream.Length;

            if (PackageHasExternalFileMetadata(package))
            {
                var filesMetadata = new MemoryStream();
                using (var compressor = new GZipStream(filesMetadata, CompressionLevel.Optimal, true))
                {
                    CreateFileMetadataStream(package).CopyTo(compressor);
                }

                newEntry.FileListLength = filesMetadata.Length;

                uploadStream.Seek(0, SeekOrigin.End);
                filesMetadata.Seek(0, SeekOrigin.Begin);
                filesMetadata.CopyTo(uploadStream);
                uploadStream.SetLength(RoundToPageSize(uploadStream.Length));
            }

            uploadStream.Seek(0, SeekOrigin.Begin);

            lock (MetadataBlobLock)
            {
                newEntry.MetadataOffset = NextAvailableOffset;
                newEntry.FileListOffset = NextAvailableOffset + filesMetadataRelativeOffset;

                uploadStream.CopyTo(UploadCache);

                NextAvailableOffset += uploadStream.Length;

                if (UploadCache.Position > UploadCacheSize)
                {
                    UploadCache.SetLength(UploadCache.Position);
                    UploadCache.Seek(0, SeekOrigin.Begin);

                    UploadMetadata(UploadCache, UploadCacheOffset);

                    UploadCache.Seek(0, SeekOrigin.Begin);

                    UploadCacheOffset = NextAvailableOffset;
                }
            }

            return newEntry;
        }

        private void UploadMetadata(MemoryStream metadataBuffer, long offset)
        {
            var targetBlob = Container.GetPageBlobClient(MetadataBlobName);
            lock (MetadataBlobLock)
            {
                long requiredLength = offset + metadataBuffer.Length;
                if (requiredLength > PageBlobSize)
                {
                    PageBlobSize = Math.Max(RoundToPageSize(requiredLength), PageBlobSize + InitialBlobSize);
                    targetBlob.ResizeAsync(PageBlobSize).Wait();
                }
            }

            metadataBuffer.Seek(0, SeekOrigin.Begin);
            var uploadBuffer = new byte[4 * 1024 * 1024];
            int readCount;
            long pageBlobOffset = offset;

            do
            {
                readCount = metadataBuffer.Read(uploadBuffer, 0, uploadBuffer.Length);
                if (readCount > 0)
                {
                    targetBlob.UploadPagesAsync(new MemoryStream(uploadBuffer, 0, readCount), pageBlobOffset).Wait();
                    pageBlobOffset += readCount;
                }
            } while (readCount > 0);

            BackBufferReadyEvent.Set();
        }

        public void Flush()
        {
            if (UploadCache.Position > 0)
            {
                UploadCache.SetLength(UploadCache.Position);
                UploadCache.Seek(0, SeekOrigin.Begin);
                UploadMetadata(UploadCache, UploadCacheOffset);
            }
            else
            {
                BackBufferReadyEvent.WaitOne();
            }
        }
    }
}
