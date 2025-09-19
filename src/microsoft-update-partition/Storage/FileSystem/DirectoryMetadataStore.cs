// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Partitions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Linq;
namespace Microsoft.PackageGraph.Storage.Local
{
    class DirectoryMetadataStore : FileBasedBackingStoreBase, IMetadataSink, IMetadataSource
    {
        private readonly Lock WriteLock = new();

        public event EventHandler<PackageStoreEventArgs> MetadataCopyProgress;

#pragma warning disable 0067
        public event EventHandler<PackageStoreEventArgs> OpenProgress;
        public event EventHandler<PackageStoreEventArgs> PackagesAddProgress;
#pragma warning restore 0067

        public DirectoryMetadataStore(string path, FileMode mode) : base(path)
        {
            switch (mode)
            {
                case FileMode.CreateNew:
                    if (Directory.Exists(path))
                    {
                        throw new IOException($"Directory already exists: {path}");
                    }
                    break;
                case FileMode.Open:
                    if (!Directory.Exists(path))
                    {
                        throw new FileNotFoundException($"Directory not found: {path}", path);
                    }
                    break;
                case FileMode.Create:
                case FileMode.OpenOrCreate:
                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                    }
                    break;
                default:
                    throw new NotSupportedException($"The file mode {mode} is not supported.");
            }
        }

        private string GetPackageMetadataPath(IPackageIdentity identity)
        {
            return Path.Combine(RootPath, "metadata", "partitions", identity.Partition, GetPackagePathIndex(identity), $"{identity.OpenIdHex}.xml");
        }

        private string GetPackageFilesPath(IPackageIdentity identity)
        {
            return Path.Combine(RootPath, "filemetadata", "partitions", identity.Partition, GetPackagePathIndex(identity), $"{identity.OpenIdHex}.files.json");
        }

        private static string GetPackagePathIndex(IPackageIdentity identity)
        {
            // The index is the last 8 bits of the update ID.
            return identity.OpenId.Last().ToString();
        }

        bool IMetadataSource.ContainsMetadata(IPackageIdentity packageIdentity)
        {
            return ContainsPackage(packageIdentity);
        }

        Stream IMetadataSource.GetMetadata(IPackageIdentity packageIdentity)
        {
            return GetMetadata(packageIdentity);
        }

        List<T> IMetadataSource.GetFiles<T>(IPackageIdentity packageIdentity)
        {
            return GetFiles<T>(packageIdentity);
        }

        public override Stream GetMetadata(IPackageIdentity packageIdentity)
        {
            var metadataPath = GetPackageMetadataPath(packageIdentity);
            return GetEntryStream(metadataPath);
        }

        private static Stream GetEntryStream(string path)
        {
            if (File.Exists(path))
            {
                return File.OpenRead(path);
            }
            else
            {
                throw new KeyNotFoundException();
            }
        }

        public override List<T> GetFiles<T>(IPackageIdentity packageIdentity)
        {
            var filesPath = GetPackageFilesPath(packageIdentity);
            if (File.Exists(filesPath))
            {
                using var filesStream = File.OpenText(filesPath);
                var serializer = new JsonSerializer();
                return serializer.Deserialize(filesStream, typeof(List<T>)) as List<T>;
            }

            return new List<T>();
        }

        public override void AddPackage(IPackage package)
        {
            lock (WriteLock)
            {
                WritePackageMetadata(package);

                if (PartitionRegistration.TryGetPartitionFromPackage(package, out var partitionDefinition) &&
                    partitionDefinition.HasExternalContentFileMetadata &&
                    package.Files?.Any() == true)
                {
                    WritePackageFiles(package);
                }
            }
        }

        private void WritePackageMetadata(IPackage package)
        {
            var metadataPath = GetPackageMetadataPath(package.Id);
            if (!Directory.Exists(Path.GetDirectoryName(metadataPath)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(metadataPath));
            }

            using var packageMetadata = File.Create(metadataPath);
            package.GetMetadataStream().CopyTo(packageMetadata);
        }

        private void WritePackageFiles(IPackage package)
        {
            var filesFilePath = GetPackageFilesPath(package.Id);
            if (!Directory.Exists(Path.GetDirectoryName(filesFilePath)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filesFilePath));
            }

            using var filesFile = File.CreateText(filesFilePath);
            var serializer = new JsonSerializer();
            serializer.Serialize(filesFile, package.Files);
        }

        public override void AddPackages(IEnumerable<IPackage> packages)
        {
            foreach (var package in packages)
            {
                AddPackage(package);
            }
        }

        public override IPackage GetPackage(IPackageIdentity packageIdentity)
        {
            if (PartitionRegistration.TryGetPartitionFromPackageId(packageIdentity, out var partitionDefinition))
            {
                using var metadataStream = GetMetadata(packageIdentity);
                return partitionDefinition.Factory.FromStream(metadataStream, this);
            }

            throw new KeyNotFoundException();
        }

        public override void Flush()
        {
        }

        public override bool IsValid()
        {
            return Directory.Exists(RootPath);
        }

        public override void Dispose()
        {
        }

        public override IEnumerator<IPackage> GetEnumerator()
        {
            return new MetadataEnumerator(GetPackagesList(), this);
        }

        private List<KeyValuePair<string, PartitionDefinition>> GetPackagesList()
        {
            List<KeyValuePair<string, PartitionDefinition>> packagePaths = new();

            var partitions = Directory.GetDirectories(Path.Combine(RootPath, "metadata", "partitions"));
            foreach (var partition in partitions)
            {
                var partitionName = Path.GetFileName(partition);

                if (PartitionRegistration.TryGetPartition(partitionName, out var partitionDefinition))
                {
                    var packagesInPartition = Directory.GetFiles(partition);
                    foreach (var package in packagesInPartition)
                    {
                        packagePaths.Add(new KeyValuePair<string, PartitionDefinition>(package, partitionDefinition));
                    }
                }
            }

            return packagePaths;
        }

        private IPackage GetPackage(string path, string partitionName)
        {
            if (PartitionRegistration.TryGetPartition(partitionName, out var partitionDefinition))
            {
                using var metadataStream = File.OpenRead(path);
                return partitionDefinition.Factory.FromStream(metadataStream, this);
            }

            throw new KeyNotFoundException();
        }

        public void CopyTo(IMetadataSink destination, CancellationToken cancelToken)
        {
            var packages = GetPackagesList();

            var progressArgs = new PackageStoreEventArgs() { Total = packages.Count, Current = 0 };
            MetadataCopyProgress?.Invoke(this, progressArgs);
            packages.AsParallel().ForAll(package =>
            {
                if (cancelToken.IsCancellationRequested)
                {
                    return;
                }

                destination.AddPackage(GetPackage(package.Key, package.Value.Name));

                lock (progressArgs)
                {
                    progressArgs.Current++;
                }

                if (progressArgs.Current % 100 == 0)
                {
                    MetadataCopyProgress?.Invoke(this, progressArgs);
                }
            });
        }

        public void CopyTo(IMetadataSink destination, IMetadataFilter filter, CancellationToken cancelToken)
        {
            throw new NotImplementedException();
        }

        class MetadataEnumerator : IEnumerator<IPackage>
        {
            readonly DirectoryMetadataStore _Source;
            readonly IEnumerator<KeyValuePair<string, PartitionDefinition>> PathsEnumerator;

            public MetadataEnumerator(List<KeyValuePair<string, PartitionDefinition>> paths, DirectoryMetadataStore metadataSource)
            {
                _Source = metadataSource;
                PathsEnumerator = paths.GetEnumerator();
            }

            public object Current => GetCurrent();

            IPackage IEnumerator<IPackage>.Current => GetCurrent();

            private IPackage GetCurrent()
            {
                return _Source.GetPackage(PathsEnumerator.Current.Key, PathsEnumerator.Current.Value.Name);
            }

            public void Dispose()
            {
                PathsEnumerator.Dispose();
            }

            public bool MoveNext()
            {
                return PathsEnumerator.MoveNext();
            }

            public void Reset()
            {
                PathsEnumerator.Reset();
            }
        }
    }
}
