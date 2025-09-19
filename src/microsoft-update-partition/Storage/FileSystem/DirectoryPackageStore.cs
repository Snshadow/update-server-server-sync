// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Partitions;
using Microsoft.PackageGraph.Storage.Index;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Microsoft.PackageGraph.Storage.Local
{
    class DirectoryPackageStore : IDeploySyncStore, IMetadataSink, IMetadataStore, IMetadataLookup
    {
        private readonly string TargetPath;

        private readonly MetadataBackingStore _metadataBackingStore;

        private readonly Lock WriteLock = new();

        private readonly DeploySyncDbContext DbContext;
        private readonly DeploymentStore Deployments;
        private readonly ComputerSyncStore ComputerSync;

        private bool IsDirty = false;
        private bool IsIndexDirty = false;
        private bool IsDisposed = false;

        public int PackageCount => _metadataBackingStore.PackageCount;

        /// <inheritdoc cref="IMetadataStore.IsReindexingRequired"/>
        public bool IsReindexingRequired => _IsReindexingRequired;

        /// <inheritdoc cref="IMetadataStore.IsMetadataIndexingSupported"/>
        public bool IsMetadataIndexingSupported { get; private set; } = true;

        private bool _IsReindexingRequired = false;

        // TODO wrap this into FileBasedBackingStoreBase to support swapping with sqlite implementation
        private readonly ZipStreamIndexContainer Indexes;

#pragma warning disable 0067
        public event EventHandler<PackageStoreEventArgs> MetadataCopyProgress;
        public event EventHandler<PackageStoreEventArgs> OpenProgress;
        public event EventHandler<PackageStoreEventArgs> PackagesAddProgress;
#pragma warning restore 0067

        public event EventHandler<PackageStoreEventArgs> PackageIndexingProgress;

        private readonly List<IPackage> PendingPackages = new();

        public DirectoryPackageStore(BackingStoreConfiguration configuration)
        {
            TargetPath = configuration.Path;

            if (Directory.Exists(TargetPath) && IsValidDirectory(TargetPath))
            {
                _metadataBackingStore = MetadataBackingStoreFactory.Create(configuration);

                ReadIdentities();

                DbContext = new DeploySyncDbContext(Path.Combine(TargetPath, "deploySync.db"));
                Deployments = new DeploymentStore(DbContext);
                ComputerSync = new ComputerSyncStore(DbContext);

                var indexContainerPath = Path.Combine(TargetPath, IndexesContainerFileName);
                if (File.Exists(indexContainerPath))
                {
                    Indexes = ZipStreamIndexContainer.Open(File.OpenRead(indexContainerPath));
                }
                else
                {
                    Indexes = ZipStreamIndexContainer.Open(null);
                }

                if (Indexes.GetStatus() != ZipStreamIndexContainer.IndexContainerStatus.Valid)
                {
                    _IsReindexingRequired = true;
                }
            }
            else
            {
                if (configuration.Mode == FileMode.Open)
                {
                    throw new Exception("The store does not exist or is corrupted");
                }

                Directory.CreateDirectory(TargetPath);
                Indexes = ZipStreamIndexContainer.Create();

                _metadataBackingStore = MetadataBackingStoreFactory.Create(configuration);

                DbContext = new DeploySyncDbContext(Path.Combine(TargetPath, "deploySync.db"));
                Deployments = new DeploymentStore(DbContext);
                ComputerSync = new ComputerSyncStore(DbContext);
            }
        }

        public DirectoryPackageStore(string path, FileMode mode, BackingStoreType storeType = BackingStoreType.Compressed) :
            this(new BackingStoreConfiguration { Path = path, Mode = mode, StoreType = storeType })
        {
        }

        public static bool Exists(string path)
        {
            return Directory.Exists(path) && IsValidDirectory(path);
        }

        private static bool IsValidDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            var tocFile = Path.Combine(path, TableOfContentsFileName);
            if (!File.Exists(tocFile))
            {
                return false;
            }

            var identitiesDirectory = Path.Combine(path, IdentitiesDirectoryName);
            if (!Directory.Exists(identitiesDirectory))
            {
                return false;
            }

            var partitions = Directory.GetDirectories(identitiesDirectory);
            foreach (var partition in partitions)
            {
                var identitiesFile = Path.Combine(partition, IdentitiesFileName);
                if (!File.Exists(identitiesFile))
                {
                    return false;
                }
            }

            return true;
        }

        // TODO hide this into file based implementation
        private void WriteIndexes()
        {
            var indexContainerPath = Path.Combine(TargetPath, IndexesContainerFileName);
            var tempIndexContainerPath = indexContainerPath + ".tmp";
            using (var fileStream = File.Create(tempIndexContainerPath))
            {
                Indexes.Save(fileStream);
            }

            Indexes.CloseInput();

            if (File.Exists(indexContainerPath))
            {
                File.Delete(indexContainerPath);
            }

            File.Move(tempIndexContainerPath, indexContainerPath);
        }

        // TODO hide this into file based implementation
        private void ReadIdentities()
        {
            if (_metadataBackingStore is not FileBasedBackingStoreBase fileBasedBackingStore)
            {
                return;
            }

            var partitionDirectories = Directory.GetDirectories(Path.Combine(TargetPath, IdentitiesDirectoryName));
            foreach (var partitionDirectory in partitionDirectories)
            {
                var identitiesFilePath = Path.Combine(partitionDirectory, IdentitiesFileName);
                var partitionName = Path.GetFileName(partitionDirectory);

                if (PartitionRegistration.TryGetPartition(partitionName, out var partitionDefinition))
                {
                    using var identitiesFileReader = File.OpenText(identitiesFilePath);
                    var partitionIdentities = partitionDefinition.Factory.IdentitiesFromJson(identitiesFileReader);
                    foreach (var identityEntry in partitionIdentities)
                    {
                        fileBasedBackingStore.AddIdentity(identityEntry.Value, identityEntry.Key);
                    }
                }
            }

            var typesFile = Path.Combine(TargetPath, TypesFileName);
            using var typesFileReader = File.OpenText(typesFile);
            var serializer = new JsonSerializer();
            var packageTypeIndex = serializer.Deserialize(typesFileReader, typeof(Dictionary<int, int>)) as Dictionary<int, int>;
            foreach (var entry in packageTypeIndex)
            {
                fileBasedBackingStore.AddPackageType(entry.Key, entry.Value);
            }
        }

        public bool ContainsPackage(IPackageIdentity packageIdentity)
        {
            return _metadataBackingStore.ContainsPackage(packageIdentity);
        }

        public void CopyTo(IMetadataSink destination, CancellationToken cancelToken)
        {
            var packagesIdsToCopy = _metadataBackingStore.GetPackageIdentities().ToList();

            if (destination is IMetadataStore destinationPackageStore)
            {
                packagesIdsToCopy = packagesIdsToCopy.Except(destinationPackageStore.GetPackageIdentities()).ToList();
            }

            var packagesToAdd = packagesIdsToCopy.Select(id => GetPackage(id));
            destination.AddPackages(packagesToAdd);
        }

        public void SaveDeployment(IDeployment deployment)
        {
            Deployments.SaveDeployment(deployment);
        }

        public void DeleteDeployment(int revisionId)
        {
            Deployments.DeleteDeployment(revisionId);
        }

        public IDeployment GetDeployment(int revisionId)
        {
            return Deployments.GetDeployment(revisionId);
        }

        public void UpdateComputerSync(string computerId, DateTime syncTime)
        {
            ComputerSync.UpdateComputerSync(computerId, syncTime);
        }

        public void DeleteComputer(string computerId)
        {
            ComputerSync.DeleteComputer(computerId);
        }

        public IComputerSync GetComputerSync(string computerId)
        {
            return ComputerSync.GetComputerSync(computerId);
        }

        public void Dispose()
        {
            lock (WriteLock)
            {
                if (!IsDisposed)
                {
                    Flush();
                }

                _metadataBackingStore.Dispose();

                DbContext?.Dispose();


                IsDisposed = true;
            }
        }

        public void Flush()
        {
            if (IsDirty)
            {
                _metadataBackingStore.Flush();

                // TODO hide method call into file-based store
                WriteToc();

                foreach (var partitionEntry in PartitionRegistration.GetAllPartitions())
                {
                    if (!partitionEntry.HandlesIdentities)
                    {
                        continue;
                    }

                    if (_metadataBackingStore is FileBasedBackingStoreBase fileBasedBackingStore)
                    {
                        var partitionIdentites = partitionEntry.Factory.FilterPartitionIdentities(new Dictionary<int, IPackageIdentity>(fileBasedBackingStore.GetIdentitiesMap()));

                        var partitionDirectoryPath = Path.Combine(TargetPath, IdentitiesDirectoryName, partitionEntry.Name);
                        if (!Directory.Exists(partitionDirectoryPath))
                        {
                            Directory.CreateDirectory(partitionDirectoryPath);
                        }

                        var partitionIdentitiesFile = Path.Combine(partitionDirectoryPath, IdentitiesFileName);
                        using var identitiesWriter = File.CreateText(partitionIdentitiesFile);
                        var serializer = new JsonSerializer();
                        serializer.Serialize(identitiesWriter, partitionIdentites);
                    }
                }

                var packageTypesFile = Path.Combine(TargetPath, TypesFileName);
                using (var typesWriter = File.CreateText(packageTypesFile))
                {
                    var serializer = new JsonSerializer();
                    if (_metadataBackingStore is FileBasedBackingStoreBase fileBasedBackingStore)
                    {
                        serializer.Serialize(typesWriter, fileBasedBackingStore.GetPackageTypeIndex());
                    }
                }

                WriteIndexes();

                IsDirty = false;

                PendingPackages.Clear();
            }
            else if (IsIndexDirty)
            {
                WriteIndexes();
            }

            IsIndexDirty = false;
        }

        private void CheckIndex(bool forceReindex = false)
        {
            lock (WriteLock)
            {
                if (!_IsReindexingRequired && !forceReindex)
                {
                    return;
                }

                Indexes.ResetIndex();

                PackageStoreEventArgs progressEvent = new()
                {
                    Total = _metadataBackingStore.PackageCount,
                    Current = 0
                };

                foreach (var parsedPackage in (IEnumerable<IPackage>)_metadataBackingStore)
                {
                    Indexes.IndexPackage(parsedPackage, _metadataBackingStore.GetPackageIndex(parsedPackage.Id));

                    if (progressEvent.Current % 100 == 0)
                    {
                        PackageIndexingProgress?.Invoke(this, progressEvent);
                    }
                    progressEvent.Current++;
                }

                _IsReindexingRequired = false;
                IsIndexDirty = true;
            }
        }

        public void AddPackage(IPackage package, out int packageIndex)
        {
            CheckIndex();

            packageIndex = _metadataBackingStore.GetPackageIndex(package.Id);
            if (packageIndex != -1)
            {
                return;
            }

            lock (WriteLock)
            {
                _metadataBackingStore.AddPackage(package);
                packageIndex = _metadataBackingStore.GetPackageIndex(package.Id);

                AddPackageType(packageIndex, package);

                Indexes.IndexPackage(package, packageIndex);
                IsIndexDirty = true;

                PendingPackages.Add(package);

                IsDirty = true;
            }
        }

        public void AddPackageType(int packageIndex, IPackage package)
        {
            if (PartitionRegistration.TryGetPartitionFromPackage(package, out var partitionDefinition))
            {
                if (_metadataBackingStore is FileBasedBackingStoreBase fileBasedBackingStore)
                {
                    fileBasedBackingStore.AddPackageType(packageIndex, partitionDefinition.Factory.GetPackageType(package));
                }
            }
        }

        public int IndexOf(IPackageIdentity packageIdentity)
        {
            return _metadataBackingStore.GetPackageIndex(packageIdentity);
        }

        public IEnumerator<IPackageIdentity> GetEnumerator()
        {
            return _metadataBackingStore.GetPackageIdentities().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new MetadataEnumerator(this);
        }

        public void AddPackage(IPackage package)
        {
            AddPackage(package, out var _);
        }

        public void AddPackages(IEnumerable<IPackage> packages)
        {
            foreach (var package in packages)
            {
                AddPackage(package);
            }
        }

        public List<IPackageIdentity> GetPackageIdentities()
        {
            return _metadataBackingStore.GetPackageIdentities().ToList();
        }

        public IPackage GetPackage(IPackageIdentity packageIdentity)
        {
            var packageIndex = _metadataBackingStore.GetPackageIndex(packageIdentity);
            if (packageIndex < 0)
            {
                throw new KeyNotFoundException();
            }

            if (PartitionRegistration.TryGetPartitionFromPackageId(packageIdentity, out var partitionDefinition))
            {
                return partitionDefinition.Factory.FromStore(_metadataBackingStore.GetPackageType(packageIndex), packageIdentity, this, this);
            }
            else
            {
                throw new NotImplementedException($"The package belongs to a partition that was not registered: {packageIdentity.Partition}");
            }
        }

        IEnumerator<IPackage> IEnumerable<IPackage>.GetEnumerator()
        {
            return new MetadataEnumerator(this);
        }

        public bool TrySimpleKeyLookup<T>(IPackageIdentity packageIdentity, string indexName, out T value)
        {
            var packageIndex = _metadataBackingStore.GetPackageIndex(packageIdentity);
            if (packageIndex < 0)
            {
                throw new KeyNotFoundException();
            }

            return Indexes.TrySimpleKeyLookup(packageIndex, indexName, out value);
        }

        public bool TryPackageLookupByCustomKey<T>(T key, string indexName, out IPackageIdentity value)
        {
            if (Indexes.TryPackageLookupByCustomKey(key, indexName, out int packageIndex))
            {
                value = _metadataBackingStore.GetPackageIdentity(packageIndex);
                return value != null;
            }
            else
            {
                value = null;
                return false;
            }
        }

        public bool TryPackageListLookupByCustomKey<T>(T key, string indexName, out List<IPackageIdentity> value)
        {
            if (Indexes.TryPackageListLookupByCustomKey(key, indexName, out List<int> packageIndex))
            {
                value = packageIndex.Select(index => _metadataBackingStore.GetPackageIdentity(index)).ToList();
                return true;
            }
            else
            {
                value = null;
                return false;
            }
        }

        public List<IndexDefinition> GetAvailableIndexes()
        {
            return Indexes.GetLoadedIndexes();
        }

        /// <inheritdoc/>
        public void ReIndex()
        {
            CheckIndex(true);
        }

        public bool TryListKeyLookup<T>(IPackageIdentity packageIdentity, string indexName, out List<T> value)
        {
            var packageIndex = _metadataBackingStore.GetPackageIndex(packageIdentity);
            if (packageIndex < 0)
            {
                throw new KeyNotFoundException();
            }

            return Indexes.TryListKeyLookup(packageIndex, indexName, out value);
        }

        public bool ContainsMetadata(IPackageIdentity packageIdentity)
        {
            return _metadataBackingStore.ContainsPackage(packageIdentity);
        }

        public Stream GetMetadata(IPackageIdentity packageIdentity)
        {
            if (!_metadataBackingStore.ContainsPackage(packageIdentity))
            {
                throw new KeyNotFoundException();
            }

            return _metadataBackingStore.GetMetadata(packageIdentity);
        }

        public List<T> GetFiles<T>(IPackageIdentity packageIdentity)
        {
            if (!_metadataBackingStore.ContainsPackage(packageIdentity))
            {
                throw new KeyNotFoundException();
            }

            return _metadataBackingStore.GetFiles<T>(packageIdentity);
        }

        public int GetPackageIndex(IPackageIdentity packageIdentity)
        {
            return _metadataBackingStore.GetPackageIndex(packageIdentity);
        }

        public IPackage GetPackage(int packageIndex)
        {
            var packageIdentity = _metadataBackingStore.GetPackageIdentity(packageIndex);
            if (packageIdentity != null)
            {
                if (PartitionRegistration.TryGetPartitionFromPackageId(packageIdentity, out var partitionDefinition))
                {
                    if (_metadataBackingStore is FileBasedBackingStoreBase fileBasedBackingStore)
                    {
                        return partitionDefinition.Factory.FromStore(fileBasedBackingStore.GetPackageType(packageIndex), packageIdentity, this, this);
                    }

                    return null;
                }
                else
                {
                    throw new NotImplementedException($"The package belongs to a partition that was not registered: {packageIdentity.Partition}");
                }
            }
            else
            {
                throw new KeyNotFoundException();
            }
        }

        public void CopyTo(IMetadataSink destination, IMetadataFilter filter, CancellationToken cancelToken)
        {
            var packagesMatchingFilter = filter.Apply(this);

            var packagesIdsToCopy = packagesMatchingFilter.Select(p => p.Id);
            if (destination is IMetadataStore destinationPackageStore)
            {
                packagesIdsToCopy = packagesIdsToCopy.Except(destinationPackageStore.GetPackageIdentities()).ToList();
            }

            var packagesToAdd = packagesIdsToCopy.Select(id => GetPackage(id));
            destination.AddPackages(packagesToAdd);
        }

        public IReadOnlyList<IPackage> GetPendingPackages()
        {
            return PendingPackages.AsReadOnly();
        }

        class MetadataEnumerator : IEnumerator<IPackage>
        {
            readonly DirectoryPackageStore _Source;
            readonly IEnumerator<IPackageIdentity> IdentitiesEnumerator;

            public MetadataEnumerator(DirectoryPackageStore metadataSource)
            {
                _Source = metadataSource;
                IdentitiesEnumerator = _Source.GetEnumerator();
            }

            public object Current => GetCurrent();

            IPackage IEnumerator<IPackage>.Current => GetCurrent();

            private IPackage GetCurrent() => _Source.GetPackage(IdentitiesEnumerator.Current);

            public void Dispose() => IdentitiesEnumerator.Dispose();

            public bool MoveNext() => IdentitiesEnumerator.MoveNext();

            public void Reset() => IdentitiesEnumerator.Reset();
        }
    }
}
