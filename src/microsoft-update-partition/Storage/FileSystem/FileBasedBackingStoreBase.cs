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

namespace Microsoft.PackageGraph.Storage.Local
{
    /// <summary>
    /// Abstract base class for file-based metadata backing stores.
    /// Provides an in-memory implementation of <see cref="IMetadataMapping"/>.
    /// </summary>
    public abstract class FileBasedBackingStoreBase : IMetadataBackingStore
    {
        private const string IdentitiesFileName = ".identities.json";
        private const string TypesFileName = ".types.json";
        private const string IdentitiesDirectoryName = "identities";
        private const string IndexesContainerFileName = ".indexes.zip";

        /// <summary>
        /// The root path of the backing store.
        /// </summary>
        protected readonly string RootPath;

        /// <summary>
        /// In-memory mapping from package identity to integer index.
        /// </summary>
        protected readonly Dictionary<IPackageIdentity, int> IdentityToIndexMap = new();

        /// <summary>
        /// In-memory mapping from integer index to package identity.
        /// </summary>
        protected readonly Dictionary<int, IPackageIdentity> IndexToIdentityMap = new();

        /// <summary>
        /// In-memory mapping from integer index to package type.
        /// </summary>
        protected readonly Dictionary<int, int> PackageTypeIndex = new();

        /// <summary>
        /// Zip stream that contains indexes for fast lookup.
        /// </summary>
        private protected readonly ZipStreamIndexContainer Indexes;

        /// <inheritdoc/>
        public bool IsReindexingRequired { get; protected set; }

        /// <summary>
        /// Metadata store needs to be updated.
        /// </summary>
        protected bool IsDirty;

        /// <summary>
        /// Package indexes need to be updated.
        /// </summary>
        protected bool IsIndexDirty;

        /// <inheritdoc/>
        public List<IPackage> PendingPackages { get; } = new();

        /// <inheritdoc/>
        public event EventHandler<PackageStoreEventArgs> PackageIndexingProgress;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileBasedBackingStoreBase"/> class.
        /// </summary>
        /// <param name="path">The root path of the backing store.</param>
        protected FileBasedBackingStoreBase(string path)
        {
            RootPath = path;

            if (IsValid(RootPath))
            {
                var indexContainerPath = Path.Combine(RootPath, IndexesContainerFileName);
                Indexes = ZipStreamIndexContainer.Open(File.Exists(indexContainerPath)
                    ? File.OpenRead(indexContainerPath)
                    : null);
            }
            else
            {
                Indexes = ZipStreamIndexContainer.Create();
            }

            if (Indexes.GetStatus() != ZipStreamIndexContainer.IndexContainerStatus.Valid)
            {
                IsReindexingRequired = true;
            }

            ReadIdentities();
        }

        /// <inheritdoc/>
        public int GetPackageIndex(IPackageIdentity packageIdentity) => IdentityToIndexMap.TryGetValue(packageIdentity, out var index) ? index : -1;

        /// <inheritdoc/>
        public IPackageIdentity GetPackageIdentity(int packageIndex) => IndexToIdentityMap.TryGetValue(packageIndex, out var identity) ? identity : null;

        /// <inheritdoc/>
        public bool ContainsPackage(IPackageIdentity packageIdentity) => IdentityToIndexMap.ContainsKey(packageIdentity);

        /// <inheritdoc/>
        public int PackageCount => IdentityToIndexMap.Count;

        /// <inheritdoc/>
        public IEnumerable<IPackageIdentity> GetPackageIdentities() => IdentityToIndexMap.Keys.ToList();

        /// <summary>
        /// Adds an identity to the in-memory mapping.
        /// </summary>
        /// <param name="packageIdentity">The package identity.</param>
        /// <param name="packageIndex">The package index.</param>
        public void AddIdentity(IPackageIdentity packageIdentity, int packageIndex)
        {
            IdentityToIndexMap.Add(packageIdentity, packageIndex);
            IndexToIdentityMap.Add(packageIndex, packageIdentity);
        }

        /// <inheritdoc/>
        public int GetPackageType(int packageIndex) => PackageTypeIndex.TryGetValue(packageIndex, out var packageType) ? packageType : -1;

        /// <summary>
        /// Adds a package type mapping.
        /// </summary>
        /// <param name="packageIndex">The package index.</param>
        /// <param name="packageType">The package type.</param>
        public void AddPackageType(int packageIndex, int packageType)
        {
            PackageTypeIndex.Add(packageIndex, packageType);
        }

        /// <summary>
        /// Gets the package type index.
        /// </summary>
        /// <returns>The package type index.</returns>
        public IReadOnlyDictionary<int, int> GetPackageTypeIndex() => PackageTypeIndex;

        /// <inheritdoc/>
        public abstract int AddPackage(IPackage package);

        /// <inheritdoc/>
        public abstract void AddPackages(IEnumerable<IPackage> packages);

        /// <inheritdoc/>
        public abstract Stream GetMetadata(IPackageIdentity packageIdentity);

        /// <inheritdoc/>
        public abstract List<T> GetFiles<T>(IPackageIdentity packageIdentity);

        /// <inheritdoc/>
        public abstract IPackage GetPackage(IPackageIdentity packageIdentity);

        /// <inheritdoc/>
        public virtual void Flush()
        {
            if (IsDirty)
            {
                var packageTypesFile = Path.Combine(RootPath, TypesFileName);
                using (var typesWriter = File.CreateText(packageTypesFile))
                {
                    var serializer = new JsonSerializer();
                    serializer.Serialize(typesWriter, GetPackageTypeIndex());
                }

                WriteIndexes();

                foreach (var partitionEntry in PartitionRegistration.GetAllPartitions())
                {
                    if (!partitionEntry.HandlesIdentities)
                    {
                        continue;
                    }

                    var partitionIdentites = partitionEntry.Factory.FilterPartitionIdentities(IndexToIdentityMap);

                    var partitionDirectoryPath = Path.Combine(RootPath, IdentitiesDirectoryName, partitionEntry.Name);
                    if (!Directory.Exists(partitionDirectoryPath))
                    {
                        Directory.CreateDirectory(partitionDirectoryPath);
                    }

                    var partitionIdentitiesFile = Path.Combine(partitionDirectoryPath, IdentitiesFileName);
                    using var identitiesWriter = File.CreateText(partitionIdentitiesFile);
                    var serializer = new JsonSerializer();
                    serializer.Serialize(identitiesWriter, partitionIdentites);
                }

                IsDirty = false;

                PendingPackages.Clear();
            }
            else if (IsIndexDirty)
            {
                WriteIndexes();
                IsIndexDirty = false;
            }
        }

        /// <inheritdoc/>
        public static bool IsValid(string path)
        {
            if (!Directory.Exists(path))
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

        private void ReadIdentities()
        {
            var partitionDirectories = Directory.GetDirectories(Path.Combine(RootPath, IdentitiesDirectoryName));
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
                        AddIdentity(identityEntry.Value, identityEntry.Key);
                    }
                }
            }

            var typesFile = Path.Combine(RootPath, TypesFileName);
            using var typesFileReader = File.OpenText(typesFile);
            var serializer = new JsonSerializer();
            var packageTypeIndex = serializer.Deserialize(typesFileReader, typeof(Dictionary<int, int>)) as Dictionary<int, int>;
            foreach (var entry in packageTypeIndex ?? [])
            {
                AddPackageType(entry.Key, entry.Value);
            }
        }

        private void WriteIndexes()
        {
            var indexContainerPath = Path.Combine(RootPath, IndexesContainerFileName);
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

        /// <inheritdoc/>
        public void ReIndex()
        {
            CheckIndex(true);
        }

        /// <inheritdoc/>
        public void CheckIndex(bool forceReindex = false)
        {
            if (!forceReindex && !IsReindexingRequired)
            {
                return;
            }

            Indexes.ResetIndex();

            PackageStoreEventArgs progressEvent = new()
            {
                Total = PackageCount,
                Current = 0
            };

            foreach (var parsedPackage in (IEnumerable<IPackage>)this)
            {
                Indexes.IndexPackage(parsedPackage, GetPackageIndex(parsedPackage.Id));

                if (progressEvent.Current % 100 == 0)
                {
                    PackageIndexingProgress?.Invoke(this, progressEvent);
                }

                progressEvent.Current++;
            }

            IsReindexingRequired = false;
            IsIndexDirty = true;
        }

        /// <inheritdoc/>
        public abstract void Dispose();

        /// <inheritdoc/>
        public abstract IEnumerator<IPackage> GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc/>
        public bool TrySimpleKeyLookup<T>(IPackageIdentity packageIdentity, string indexName, out T value)
        {
            var packageIndex = GetPackageIndex(packageIdentity);
            return packageIndex == -1 ?
                throw new KeyNotFoundException() :
                Indexes.TrySimpleKeyLookup(packageIndex, indexName, out value);
        }

        /// <inheritdoc/>
        public bool TryPackageLookupByCustomKey<T>(T key, string indexName, out IPackageIdentity value)
        {
            if (Indexes.TryPackageLookupByCustomKey(key, indexName, out int packageIndex))
            {
                value = GetPackageIdentity(packageIndex);
                return value != null;
            }
            else
            {
                value = null;
                return false;
            }
        }

        /// <inheritdoc/>
        public bool TryPackageListLookupByCustomKey<T>(T key, string indexName, out List<IPackageIdentity> value)
        {
            if (Indexes.TryPackageListLookupByCustomKey(key, indexName, out List<int> packageIndex))
            {
                value = packageIndex.Select(index => GetPackageIdentity(index)).ToList();
                return true;
            }
            else
            {
                value = null;
                return false;
            }
        }

        /// <inheritdoc/>
        public bool TryListKeyLookup<T>(IPackageIdentity packageIdentity, string indexName, out List<T> value)
        {
            var packageIndex = GetPackageIndex(packageIdentity);
            return packageIndex == -1 ?
                throw new KeyNotFoundException() :
                Indexes.TryListKeyLookup(packageIndex, indexName, out value);
        }

        internal List<IndexDefinition> GetAvailableIndexes()
        {
            return Indexes.GetLoadedIndexes();
        }
    }
}
