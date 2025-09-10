// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using ICSharpCode.SharpZipLib.Zip;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Partitions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Microsoft.PackageGraph.Storage.Local
{
    class CompressedMetadataStore : FileBasedBackingStoreBase, IMetadataSource
    {
        private ZipFile _inputFile;
        private ZipOutputStream _outputFile;

        private bool _isDisposed;

        private Dictionary<string, long> _zipEntriesIndex;

#pragma warning disable 0067
        public event EventHandler<PackageStoreEventArgs> MetadataCopyProgress;
        public event EventHandler<PackageStoreEventArgs> OpenProgress;
#pragma warning restore 0067

        public CompressedMetadataStore(string path) : base(path)
        {
        }

        private static string GetPackageMetadataPath(IPackageIdentity identity)
        {
            return $"metadata/partitions/{identity.Partition}/{GetPackagePathIndex(identity)}/{identity.OpenIdHex}.xml";
        }

        private static string GetPackageFilesPath(IPackageIdentity identity)
        {
            return $"filemetadata/partitions/{identity.Partition}/{GetPackagePathIndex(identity)}/{identity.OpenIdHex}.files.json";
        }

        private static string GetPackagePathIndex(IPackageIdentity identity)
        {
            // The index is the last 8 bits of the update ID.
            return identity.OpenId.Last().ToString();
        }

        public override void AddPackage(IPackage package)
        {
            var metadataPath = GetPackageMetadataPath(package.Id);
            _outputFile.PutNextEntry(new ZipEntry(metadataPath));
            var packageMetadataStream = package.GetMetadataStream();
            packageMetadataStream.CopyTo(_outputFile);
            _outputFile.CloseEntry();

            var filesPath = GetPackageFilesPath(package.Id);
            _outputFile.PutNextEntry(new ZipEntry(filesPath));
            using (var textWriter = new StreamWriter(_outputFile, Encoding.UTF8, 4096, true))
            {
                var serializer = new JsonSerializer();
                serializer.Serialize(textWriter, package.Files);
            }
            _outputFile.CloseEntry();
        }

        public override Stream GetMetadata(IPackageIdentity packageIdentity)
        {
            var metadataPath = GetPackageMetadataPath(packageIdentity);
            if (_zipEntriesIndex.TryGetValue(metadataPath, out long entryIndex))
            {
                return _inputFile.GetInputStream(entryIndex);
            }
            else
            {
                throw new KeyNotFoundException();
            }
        }

        public override List<T> GetFiles<T>(IPackageIdentity packageIdentity, IFileFactory<T> _)
        {
            var filesPath = GetPackageFilesPath(packageIdentity);
            if (_zipEntriesIndex.TryGetValue(filesPath, out long entryIndex))
            {
                using (var filesStream = _inputFile.GetInputStream(entryIndex))
                using (var filesReader = new StreamReader(filesStream))
                {
                    var serializer = new JsonSerializer();
                    return serializer.Deserialize(filesReader, typeof(List<T>)) as List<T>;
                }
            }

            return new List<T>();
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
                using (var metadataStream = GetMetadata(packageIdentity))
                {
                    return partitionDefinition.Factory.FromStream(metadataStream, this);
                }
            }

            throw new KeyNotFoundException();
        }

        public override void Flush()
        {
            _outputFile?.Flush();
        }

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                _outputFile?.Close();
                _outputFile = null;

                _inputFile?.Close();
                _inputFile = null;

                _isDisposed = true;
            }
        }

        public override IEnumerator<IPackage> GetEnumerator()
        {
            return new MetadataEnumerator(GetPackagesList(), this);
        }

        private List<KeyValuePair<string, PartitionDefinition>> GetPackagesList()
        {
            var packagePaths = new List<KeyValuePair<string, PartitionDefinition>>();

            foreach (var entry in _inputFile)
            {
                if (entry is ZipEntry zipEntry)
                {
                    foreach (var partitionDefinition in PartitionRegistration.GetAllPartitions())
                    {
                        if (zipEntry.Name.StartsWith($"metadata/partitions/{partitionDefinition.Name}/"))
                        {
                            packagePaths.Add(new KeyValuePair<string, PartitionDefinition>(zipEntry.Name, partitionDefinition));
                            break;
                        }
                    }
                }
            }

            return packagePaths;
        }

        private IPackage GetPackage(string path, string partitionName)
        {
            if (PartitionRegistration.TryGetPartition(partitionName, out var partitionDefinition))
            {
                using (var metadataStream = GetMetadataFromPath(path))
                {
                    return partitionDefinition.Factory.FromStream(metadataStream, this);
                }
            }

            throw new KeyNotFoundException();
        }

        private Stream GetMetadataFromPath(string path)
        {
            if (_zipEntriesIndex.TryGetValue(path, out long entryIndex))
            {
                return _inputFile.GetInputStream(entryIndex);
            }
            else
            {
                throw new KeyNotFoundException();
            }
        }

        public static CompressedMetadataStore OpenExisting(string path)
        {
            var store = new CompressedMetadataStore(path);
            store._inputFile = new ZipFile(path);
            store.PopulateEntriesIndex();
            return store;
        }

        public static CompressedMetadataStore CreateNew(string path)
        {
            var store = new CompressedMetadataStore(path);
            store._outputFile = new ZipOutputStream(File.Create(path));
            return store;
        }

        private void PopulateEntriesIndex()
        {
            _zipEntriesIndex = new Dictionary<string, long>();
            for (int i = 0; i < _inputFile.Count; i++)
            {
                _zipEntriesIndex.Add(_inputFile[i].Name, i);
            }
        }

        bool IMetadataSource.ContainsMetadata(IPackageIdentity packageIdentity)
        {
            return ContainsPackage(packageIdentity);
        }

        public void CopyTo(IMetadataSink destination, CancellationToken cancelToken)
        {
            var packageEntries = GetPackagesList();

            var progressArgs = new PackageStoreEventArgs() { Total = packageEntries.Count, Current = 0 };
            MetadataCopyProgress?.Invoke(this, progressArgs);
            packageEntries.AsParallel().ForAll(packageEntry =>
            {
                if (cancelToken.IsCancellationRequested)
                {
                    return;
                }

                destination.AddPackage(GetPackage(packageEntry.Key, packageEntry.Value.Name));

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
            readonly CompressedMetadataStore _Source;
            readonly IEnumerator<KeyValuePair<string, PartitionDefinition>> PathsEnumerator;

            public MetadataEnumerator(List<KeyValuePair<string, PartitionDefinition>> paths, CompressedMetadataStore metadataSource)
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