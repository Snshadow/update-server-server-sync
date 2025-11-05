// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.PackageGraph.ObjectModel;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PackageGraph.Storage.Azure
{
    /// <summary>
    /// Implementation of <see cref="IContentStore"/> that downloads and stores update content in Azure Blob Storage
    /// </summary>
    public class BlobContentStore : IContentStore
    {
        private static readonly HttpClient _client = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        });

        /// <inheritdoc cref="IContentStore.Progress"/>
        public event EventHandler<ContentOperationProgress> Progress;

        private const long BlockSize = 64 * 1024 * 1024;

        readonly BlobContainerClient ParentContainer;

        /// <summary>
        /// List of pending downloads
        /// </summary>
        public ConcurrentDictionary<string, IContentFile> PendingFileDownloads = new();

        /// <inheritdoc cref="IContentStore.QueuedSize"/>
        public long QueuedSize => _QueuedSize;

        /// <inheritdoc cref="IContentStore.DownloadedSize"/>
        public long DownloadedSize => _DownloadedSize;

        /// <inheritdoc cref="IContentStore.QueuedCount"/>
        public int QueuedCount => _QueuedCount;

        long _QueuedSize;
        long _DownloadedSize;
        int _QueuedCount;

        private BlobContentStore(BlobContainerClient contentContainer)
        {
            ParentContainer = contentContainer;
        }

        /// <summary>
        /// Opens an exiting or creates a new <see cref="IContentStore"/> with storage in the specified Azure Blob account and container
        /// </summary>
        /// <param name="client">The Azure Blob client to use</param>
        /// <param name="containerName">The container name where to store update content</param>
        /// <returns></returns>
        public static BlobContentStore OpenOrCreate(BlobServiceClient client, string containerName)
        {
            var container = client.GetBlobContainerClient(containerName);
            container.CreateIfNotExists();

            return new BlobContentStore(container);
        }

        /// <inheritdoc cref="IContentStore.DownloadAsync(IEnumerable{IContentFile}, CancellationToken)"/>
        public async Task DownloadAsync(IEnumerable<IContentFile> files, CancellationToken cancelToken)
        {
            var queuedFiles = new List<IContentFile>();
            foreach (var file in files)
            {
                if (PendingFileDownloads.TryAdd(file.Source, file))
                {
                    queuedFiles.Add(file);
                }
            }

            Interlocked.Add(ref _QueuedCount, queuedFiles.Count);

            Interlocked.Add(ref _QueuedSize, queuedFiles.Sum(f => (long)f.Size));

            var progress = new ContentOperationProgress();
            progress.Maximum = queuedFiles.Count;

            Progress?.Invoke(this, progress);

            foreach (var file in queuedFiles)
            {
                progress.Current++;
                progress.BytesProcessed = 0;
                progress.TotalBytes = (long)file.Size;
                progress.CurrentOperation = PackagesOperationType.DownloadFileStart;
                Progress?.Invoke(this, progress);

                if (await ContainsAsync(file, cancelToken).ConfigureAwait(false))
                {
                    Interlocked.Add(ref _DownloadedSize, (long)file.Size);
                    Interlocked.Decrement(ref _QueuedCount);

                    progress.BytesProcessed = (long)file.Size;
                    progress.CurrentOperation = PackagesOperationType.DownloadFileEnd;
                    Progress?.Invoke(this, progress);

                    PendingFileDownloads.TryRemove(file.Source, out _);

                    continue;
                }

                progress.CurrentOperation = PackagesOperationType.DownloadFileProgress;
                var fileBlob = GetBlockBlobClientForFile(file);
                var fileSizeOnServer = await GetFileSizeOnSourceServerAsync(_client, file.Source, cancelToken).ConfigureAwait(false);
                if (cancelToken.IsCancellationRequested)
                {
                    break;
                }

                if ((ulong)fileSizeOnServer != file.Size)
                {
                    throw new Exception($"Mismatch in file size. Expected {file.Size}, server has {fileSizeOnServer}");
                }

                int startBlock = 0;
                var blockCount = fileSizeOnServer / BlockSize + (fileSizeOnServer % BlockSize == 0 ? 0 : 1);
                List<string> blockIdList;
                if ((await fileBlob.ExistsAsync(cancelToken).ConfigureAwait(false)).Value)
                {
                    var fileBlocks = (await fileBlob.GetBlockListAsync(BlockListTypes.Uncommitted, cancellationToken: cancelToken).ConfigureAwait(false)).Value.UncommittedBlocks;

                    if (fileBlocks.Count() <= blockCount)
                    {
                        startBlock = fileBlocks.Count();
                    }

                    blockIdList = fileBlocks.Select(b => b.Name).ToList();
                    progress.BytesProcessed = fileBlocks.Sum(b => b.Size);
                }
                else
                {
                    blockIdList = new List<string>();
                }

                for (int i = startBlock; i < blockCount; i++)
                {
                    var startOffset = i * BlockSize;
                    var blockSize = fileSizeOnServer % BlockSize != 0 && i == (blockCount - 1) ? fileSizeOnServer % BlockSize : BlockSize;

                    await fileBlob.StageBlockFromUriAsync(new Uri(file.Source), Convert.ToBase64String(BitConverter.GetBytes(i)), new StageBlockFromUriOptions { SourceRange = new HttpRange(startOffset, blockSize) }, CancellationToken.None).ConfigureAwait(false);
                    blockIdList.Add(Convert.ToBase64String(BitConverter.GetBytes(i)));

                    if (cancelToken.IsCancellationRequested)
                    {
                        break;
                    }

                    Interlocked.Add(ref _DownloadedSize, blockSize);
                    progress.BytesProcessed += blockSize;
                    Progress?.Invoke(this, progress);
                }

                await fileBlob.CommitBlockListAsync(blockIdList, cancellationToken: cancelToken).ConfigureAwait(false);
                using var markerFile = await GetBlockBlobClientMarkerForFile(file).OpenWriteAsync(true, cancellationToken: cancelToken).ConfigureAwait(false);
                await markerFile.WriteAsync(Convert.FromBase64String(file.Digest.DigestBase64), cancelToken).ConfigureAwait(false);


                Interlocked.Add(ref _DownloadedSize, (long)file.Size * -1);
                Interlocked.Add(ref _QueuedSize, (long)file.Size * -1);
                Interlocked.Decrement(ref _QueuedCount);
                progress.CurrentOperation = PackagesOperationType.DownloadFileEnd;
                Progress?.Invoke(this, progress);

                PendingFileDownloads.TryRemove(file.Source, out _);
            }
        }
        private static async Task<long> GetFileSizeOnSourceServerAsync(HttpClient client, string url, CancellationToken cancellationToken)
        {
            // First get the HEAD to check the server's size for the file
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!headResponse.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Failed to get HEAD of update from {url}: {headResponse.ReasonPhrase}", null, headResponse.StatusCode);
            }

            return headResponse.Content.Headers.ContentLength ??
                throw new InvalidOperationException($"Could not determine file size from {url}");
        }

        /// <inheritdoc cref="IContentStore.Contains(IContentFile)"/>
        public bool Contains(IContentFile file)
        {
            return ContainsAsync(file, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Asynchronously checks if a content file has been downloaded
        /// </summary>
        /// <param name="file">File to check if it was downloaded</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if the file was downloaded, false otherwise</returns>
        public async Task<bool> ContainsAsync(IContentFile file, CancellationToken cancellationToken)
        {
            return (await GetBlockBlobClientMarkerForFile(file).ExistsAsync(cancellationToken).ConfigureAwait(false)).Value;
        }

        /// <inheritdoc cref="IContentStore.Get(IContentFile)"/>
        public Stream Get(IContentFile contentFile)
        {
            var doneMarker = GetBlockBlobClientMarkerForFile(contentFile);
            if (doneMarker.Exists())
            {
                return GetBlockBlobClientForFile(contentFile).OpenRead();
            }
            else
            {
                throw new Exception("The requested file is not available");
            }
        }

        private BlockBlobClient GetBlockBlobClientMarkerForFile(IContentFile updateFile)
        {
            return ParentContainer.GetBlockBlobClient(updateFile.Digest.HexString.ToLower() + ".complete");
        }

        private BlockBlobClient GetBlockBlobClientForFile(IContentFile updateFile)
        {
            return ParentContainer.GetBlockBlobClient(updateFile.Digest.HexString.ToLower());
        }

        /// <inheritdoc cref="IContentStore.GetUri(IContentFile)"/>
        public string GetUri(IContentFile updateFile)
        {
            var fileBlob = GetBlockBlobClientForFile(updateFile);
            if (fileBlob.CanGenerateSasUri)
            {
                return fileBlob.GenerateSasUri(BlobSasPermissions.Read, DateTimeOffset.UtcNow.AddMinutes(10)).ToString();
            }
            else
            {
                return fileBlob.Uri.ToString();
            }
        }

        /// <inheritdoc cref="IContentStore.Contains(IContentFileDigest, out string)"/>
        public bool Contains(IContentFileDigest fileDigest, out string fileName)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc cref="IContentStore.Get(IContentFileDigest)"/>
        public Stream Get(IContentFileDigest fileDigest)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc cref="IContentStore.GetUri(IContentFileDigest)"/>
        public string GetUri(IContentFileDigest fileDigest)
        {
            throw new NotImplementedException();
        }
    }
}
