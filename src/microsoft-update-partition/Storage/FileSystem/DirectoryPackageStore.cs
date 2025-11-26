// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Partitions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Microsoft.PackageGraph.Storage.Local
{
    class DirectoryPackageStore : IDeploySyncStore, IMetadataStore
    {
        private readonly string TargetPath;

        private readonly IMetadataBackingStore _metadataBackingStore;

        public bool SupportsParallelProcessing => _metadataBackingStore.SupportsParallelProcessing;

        private readonly Lock WriteLock = new();

        private readonly DeploySyncDbContext DbContext;
        private readonly DeploymentStore Deployments;
        private readonly ComputerSyncStore ComputerSync;

        private bool IsDisposed = false;

        public int PackageCount => _metadataBackingStore.PackageCount;

        public bool IsReindexingRequired => _metadataBackingStore.IsReindexingRequired;

        public bool IsMetadataIndexingSupported { get; private set; } = true;

#pragma warning disable 0067
        public event EventHandler<PackageStoreEventArgs> MetadataCopyProgress;
        public event EventHandler<PackageStoreEventArgs> OpenProgress;
        public event EventHandler<PackageStoreEventArgs> PackagesAddProgress;
#pragma warning restore 0067

        public event EventHandler<PackageStoreEventArgs> PackageIndexingProgress
        {
            add => _metadataBackingStore.PackageIndexingProgress += value;
            remove => _metadataBackingStore.PackageIndexingProgress -= value;
        }

        public DirectoryPackageStore(BackingStoreConfiguration configuration)
        {
            TargetPath = configuration.Path;

            _metadataBackingStore = MetadataBackingStoreFactory.Create(configuration);

            DbContext = new DeploySyncDbContext(Path.Combine(TargetPath, "deploySync.db"));
            Deployments = new DeploymentStore(DbContext);
            ComputerSync = new ComputerSyncStore(DbContext);
        }

        public DirectoryPackageStore(string path, FileMode mode, BackingStoreType storeType = BackingStoreType.Sqlite) :
            this(new BackingStoreConfiguration { Path = path, Mode = mode, StoreType = storeType })
        {
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

            var packagesToAdd = packagesIdsToCopy.Select(GetPackage);
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

        public IEnumerable<int> GetApprovedRevisionIds()
        {
            return Deployments.GetApprovedRevisionIds();
        }

        public IEnumerable<int> GetUnapprovedRevisionIds()
        {
            return Deployments.GetUnapprovedRevisionIds();
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

                IsDisposed = true;
            }
        }

        public void Flush()
        {
            _metadataBackingStore.Flush();
        }

        public void AddPackageType(int packageIndex, IPackage package)
        {
            if (PartitionRegistration.TryGetPartitionFromPackage(package, out var partitionDefinition))
            {
                _metadataBackingStore.AddPackageType(packageIndex, partitionDefinition.Factory.GetPackageType(package));
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
            AddPackages([package]);
        }

        public void AddPackages(IEnumerable<IPackage> packages)
        {
            _metadataBackingStore.CheckIndex();

            var packagesToAdd = packages.Where(p => !_metadataBackingStore.ContainsPackage(p.Id)).ToList();
            if (packagesToAdd.Count == 0)
            {
                return;
            }

            lock (WriteLock)
            {
                _metadataBackingStore.AddPackages(packagesToAdd);
                foreach (var package in packagesToAdd)
                {
                    AddPackageType(_metadataBackingStore.GetPackageIndex(package.Id), package);
                }
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
                return partitionDefinition.Factory.FromStore(_metadataBackingStore.GetPackageType(packageIndex),
                    packageIdentity, _metadataBackingStore, this);
            }

            throw new NotImplementedException(
                $"The package belongs to a partition that was not registered: {packageIdentity.Partition}");
        }

        IEnumerator<IPackage> IEnumerable<IPackage>.GetEnumerator()
        {
            return new MetadataEnumerator(this);
        }

        public IEnumerator<IPackage> GetEnumerator(IMetadataFilter filter)
        {
            return _metadataBackingStore.GetEnumerator(filter);
        }

        public bool TryGetStoreBackedFilter(out IStoreBackedFilter storeBackedFilter)
        {
            storeBackedFilter = _metadataBackingStore as IStoreBackedFilter;
            return storeBackedFilter is not null;
        }

        /// <inheritdoc/>
        public void ReIndex()
        {
            _metadataBackingStore.CheckIndex(true);
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
            if (packageIdentity is not null)
            {
                if (PartitionRegistration.TryGetPartitionFromPackageId(packageIdentity, out var partitionDefinition))
                {
                    return partitionDefinition.Factory.FromStore(_metadataBackingStore.GetPackageType(packageIndex),
                        packageIdentity, _metadataBackingStore, this);
                }

                throw new NotImplementedException(
                        $"The package belongs to a partition that was not registered: {packageIdentity.Partition}");
            }

            throw new KeyNotFoundException();
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

        public IReadOnlyList<IPackage> GetPendingPackages() => _metadataBackingStore.PendingPackages.AsReadOnly();

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
