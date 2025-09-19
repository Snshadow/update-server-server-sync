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
    class CompressedMetadataStore : FileBasedBackingStoreBase, IMetadataSink, IMetadataSource
    {
        private ZipFile InputFile;
        private ZipOutputStream OutputFile;

        private bool _isDisposed;
        private readonly Lock WriteLock = new();

        private Dictionary<string, long> ZipEntriesIndex;

        public event EventHandler<PackageStoreEventArgs> MetadataCopyProgress;

#pragma warning disable 0067
        public event EventHandler<PackageStoreEventArgs> OpenProgress;
        public event EventHandler<PackageStoreEventArgs> PackagesAddProgress;
#pragma warning restore 0067

        public CompressedMetadataStore(string path) : base(path)
        {
        }

        public static CompressedMetadataStore OpenExisting(string path)
        {
            var zipStorage = new CompressedMetadataStore(path)
            {
                InputFile = new ZipFile(path)
            };
            zipStorage.ZipEntriesIndex = zipStorage.InputFile.OfType<ZipEntry>().ToDictionary(entry => entry.Name, entry => entry.ZipFileIndex);

            return zipStorage;
        }

        public static CompressedMetadataStore CreateNew(string path)
        {
            var newZipStorage = new CompressedMetadataStore(path)
            {
                OutputFile = new ZipOutputStream(File.Create(path))
            };
            newZipStorage.OutputFile.SetLevel(1);

            return newZipStorage;
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

        public override Stream GetMetadata(IPackageIdentity packageIdentity)
        {
            if (InputFile is not null)
            {
                var metadataPath = GetPackageMetadataPath(packageIdentity);
                return GetEntryStream(metadataPath);
            }
            else
            {
                throw new Exception("Read not supported");
            }
        }

        private Stream GetEntryStream(string path)
        {
            if (ZipEntriesIndex.TryGetValue(path, out long entryIndex))
            {
                return InputFile.GetInputStream(entryIndex);
            }
            else
            {
                throw new KeyNotFoundException();
            }
        }

        public bool ContainsMetadata(IPackageIdentity packageIdentity)
        {
            if (InputFile is not null)
            {
                var metadataPath = GetPackageMetadataPath(packageIdentity);
                return ZipEntriesIndex.TryGetValue(metadataPath, out var _);
            }
            else
            {
                throw new Exception("Read not supported");
            }
        }

        public override List<T> GetFiles<T>(IPackageIdentity packageIdentity)
        {
            if (InputFile is null)
            {
                throw new Exception("Read not supported");
            }

            if (PartitionRegistration.TryGetPartitionFromPackageId(packageIdentity, out var partitionDefinition) &&
                partitionDefinition.HasExternalContentFileMetadata)
            {
                var filesPath = GetPackageFilesPath(packageIdentity);
                if (ZipEntriesIndex.TryGetValue(filesPath, out long entryIndex))
                {
                    using var filesStream = InputFile.GetInputStream(entryIndex);
                    using var filesReader = new StreamReader(filesStream);
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
                using var metadataStream = GetMetadata(packageIdentity);
                return partitionDefinition.Factory.FromStream(metadataStream, this);
            }

            throw new KeyNotFoundException();
        }

        public override void AddPackage(IPackage package)
        {
            if (OutputFile is null)
            {
                throw new Exception("Write not supported");
            }

            lock (WriteLock)
            {
                WritePackageMetadata(package);

                if (PartitionRegistration.TryGetPartitionFromPackage(package, out var partitionDefinition) &&
                    partitionDefinition.HasExternalContentFileMetadata &&
                    (package.Files?.Any() == true))
                {
                    WritePackageFiles(package);
                }
            }
        }

        private void WritePackageMetadata(IPackage package)
        {
            var metadataPath = GetPackageMetadataPath(package.Id);
            OutputFile.PutNextEntry(new ZipEntry(metadataPath));
            var packageMetadataStream = package.GetMetadataStream();
            packageMetadataStream.CopyTo(OutputFile);
            OutputFile.CloseEntry();
        }

        private void WritePackageFiles(IPackage package)
        {
            var filesFilePath = GetPackageFilesPath(package.Id);
            OutputFile.PutNextEntry(new ZipEntry(filesFilePath));

            var serializer = new JsonSerializer();
            using (var jsonWriter = new StreamWriter(OutputFile, Encoding.UTF8, 4096, true))
            {
                serializer.Serialize(jsonWriter, package.Files);
            }

            OutputFile.CloseEntry();
        }

        public override IEnumerator<IPackage> GetEnumerator()
        {
            if (InputFile is null)
            {
                throw new Exception("Read not supported");
            }

            return new MetadataEnumerator(GetPackagesList(), this);
        }

        private List<KeyValuePair<string, PartitionDefinition>> GetPackagesList()
        {
            var packagePaths = new List<KeyValuePair<string, PartitionDefinition>>();

            foreach (var entry in InputFile)
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
                using var metadataStream = GetMetadataFromPath(path);
                return partitionDefinition.Factory.FromStream(metadataStream, this);
            }

            throw new KeyNotFoundException();
        }

        private Stream GetMetadataFromPath(string path)
        {
            if (ZipEntriesIndex.TryGetValue(path, out long entryIndex))
            {
                return InputFile.GetInputStream(entryIndex);
            }
            else
            {
                throw new KeyNotFoundException();
            }
        }

        public override void Flush()
        {
            if (OutputFile is not null)
            {
                lock (WriteLock)
                {
                    OutputFile?.Flush();
                }
            }
        }

        public override bool IsValid()
        {
            // TODO implement this
            return true;
        }

        public override void Dispose()
        {
            if (!_isDisposed)
            {
                OutputFile?.Close();
                OutputFile = null;

                InputFile?.Close();
                InputFile = null;

                _isDisposed = true;
            }
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
