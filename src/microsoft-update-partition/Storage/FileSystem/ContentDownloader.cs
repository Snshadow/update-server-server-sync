// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Simple HTTP content downloader implementation
    /// </summary>
    public class ContentDownloader
    {
        private static readonly HttpClient _client = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        });

        /// <summary>
        /// Provides progress notifications during download
        /// </summary>
        public event EventHandler<ContentOperationProgress> OnDownloadProgress;

        /// <summary>
        /// Downloads data to a stream
        /// </summary>
        /// <param name="source">Data source URL</param>
        /// <param name="destination">Target stream</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True on success, false otherwise</returns>
        public static async Task<bool> DownloadToStreamAsync(
            string source,
            Stream destination,
            CancellationToken cancellationToken)
        {
            using var updateRequest = new HttpRequestMessage(HttpMethod.Get, source);

            // Stream the file
            using var response = await _client.SendAsync(updateRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var streamToReadFrom = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await streamToReadFrom.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Downloads a single file belonging to an update package. Supports resuming a partial download
        /// </summary>
        /// <param name="destinationFilePath">Download destination file</param>
        /// <param name="updateFile">The update file to download</param>
        /// <param name="progress">The current download progress</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task DownloadToFileAsync(
            string destinationFilePath,
            IContentFile updateFile,
            ContentOperationProgress progress,
            CancellationToken cancellationToken)
        {
            // Destination file does not exist; create it and then download it
            using var fileStream = new FileStream(destinationFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            if (fileStream.Length < (long)updateFile.Size)
            {
                fileStream.Seek(fileStream.Length, SeekOrigin.Begin);
                await DownloadToStreamAsync(fileStream, updateFile, fileStream.Length, progress, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Downloads the specified URL to the destination file stream
        /// </summary>
        /// <param name="destination">The file stream to write content to</param>
        /// <param name="updateFile">The update to download</param>
        /// <param name="startOffset">Offset to resume download at</param>
        /// <param name="progress">The current download progress</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task DownloadToStreamAsync(
            Stream destination,
            IContentFile updateFile,
            long startOffset,
            ContentOperationProgress progress,
            CancellationToken cancellationToken)
        {
            progress.File = updateFile;
            progress.BytesProcessed = startOffset;
            progress.TotalBytes = (long)updateFile.Size;
            progress.CurrentOperation = PackagesOperationType.DownloadFileProgress;

            if (startOffset > progress.TotalBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(startOffset),
                    $"Start offset {startOffset} cannot be greater than expected file size {progress.TotalBytes}");
            }

            var url = updateFile.Source;
            var uri = new Uri(url);
            if (uri.Scheme == "file")
            {
                if (startOffset < progress.TotalBytes)
                {
                    using var source = File.OpenRead(uri.LocalPath);
                    destination.Seek(0, SeekOrigin.Begin);
                    destination.SetLength(0);
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var fileSizeOnServer = await GetFileSizeOnServerAsync(url, cancellationToken).ConfigureAwait(false);

                if (fileSizeOnServer != progress.TotalBytes)
                {
                    throw new InvalidDataException($"File size mismatch. Expected {progress.TotalBytes}, server advertised {fileSizeOnServer}");
                }

                if (startOffset == fileSizeOnServer)
                {
                    return;
                }

                using var updateRequest = new HttpRequestMessage(HttpMethod.Get, uri);
                updateRequest.Headers.Range = new RangeHeaderValue(startOffset, fileSizeOnServer - 1);

                using var response = await _client
                    .SendAsync(updateRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    using var streamToReadFrom = await response
                        .Content
                        .ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);

                    var readBuffer = new byte[2097152 * 5];
                    var readBytesCount = await streamToReadFrom.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                    while (readBytesCount > 0 && !cancellationToken.IsCancellationRequested)
                    {
                        await destination.WriteAsync(readBuffer.AsMemory(0, readBytesCount), cancellationToken).ConfigureAwait(false);

                        progress.BytesProcessed += readBytesCount;
                        OnDownloadProgress?.Invoke(this, progress);

                        readBytesCount = await streamToReadFrom.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    throw new HttpRequestException($"Failed to get content of update from {url}: {response.ReasonPhrase}", null, response.StatusCode);
                }
            }
        }

        /// <summary>
        /// Retrieves the size of HTTP resource using a HEAD request.
        /// </summary>
        /// <param name="url">The URL of the resource</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The size of the resource in bytes</returns>
        /// <exception cref="HttpRequestException">If the HEAD request fails</exception>
        /// <exception cref="InvalidOperationException">If the content length cannot be determined</exception>
        private static async Task<long> GetFileSizeOnServerAsync(string url, CancellationToken cancellationToken)
        {
            // First get the HEAD to check the server's size for the file
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!headResponse.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Failed to get HEAD of update from {url}: {headResponse.ReasonPhrase}", null, headResponse.StatusCode);
            }

            return headResponse.Content.Headers.ContentLength ??
                throw new InvalidOperationException($"Could not determine file size from {url}");
        }
    }
}
