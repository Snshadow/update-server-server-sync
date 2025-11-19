// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Blake3;
using Microsoft.Extensions.Caching.Distributed;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Endpoints.ClientSync
{
    internal sealed class FileSystemDistributedCache : IDistributedCache
    {
        private static readonly ConcurrentDictionary<string, object> KeyLocks = new();

        private readonly string _rootPath;

        public FileSystemDistributedCache(string rootPath)
        {
            _rootPath = rootPath;
            Directory.CreateDirectory(_rootPath);
        }

        public byte[] Get(string key)
        {
            var (path, hash) = GetPath(key);
            if (!File.Exists(path))
            {
                return null;
            }

            lock (GetLock(hash))
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
        }

        public async Task<byte[]> GetAsync(string key, CancellationToken token = default)
        {
            return await Task.FromResult(Get(key));
        }

        public void Refresh(string key)
        {
            // No sliding expiration support; nothing to refresh
        }

        public Task RefreshAsync(string key, CancellationToken token = default)
        {
            Refresh(key);
            return Task.CompletedTask;
        }

        public void Remove(string key)
        {
            var (path, hash) = GetPath(key);
            lock (GetLock(hash))
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            if (value is null)
            {
                Remove(key);
                return;
            }

            var (path, hash) = GetPath(key);
            lock (GetLock(hash))
            {
                File.WriteAllBytes(path, value);
            }
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        private (string path, string hash) GetPath(string key)
        {
            var hashBytes = Hasher.Hash(Encoding.UTF8.GetBytes(key)).AsSpan();
            var hash = Convert.ToHexString(hashBytes);
            var path = Path.Combine(_rootPath, hash);
            return (path, hash);
        }

        private static object GetLock(string hash)
        {
            return KeyLocks.GetOrAdd(hash, _ => new object());
        }
    }
}
