// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Microsoft.PackageGraph.Storage.Local
{
    class CompressedDeltaStore : FileBasedBackingStoreBase
    {
        private readonly TableOfContent TOC;
        private readonly List<CompressedMetadataStore> DeltaMetadataStores;
        private bool NewDeltaSubdirectoryCreated = false;

        public CompressedDeltaStore(string path, TableOfContent toc) : base(path)
        {
            TOC = toc;
            DeltaMetadataStores = new List<CompressedMetadataStore>();
            for (int i = 0; i < TOC.DeltaSectionCount; i++)
            {
                DeltaMetadataStores.Add(CompressedMetadataStore.OpenExisting(Path.Combine(path, $"{i}.zip")));
            }
        }

        public override void AddPackage(IPackage package)
        {
            if (!NewDeltaSubdirectoryCreated)
            {
                DeltaMetadataStores.Add(CompressedMetadataStore.CreateNew(Path.Combine(RootPath, $"{TOC.DeltaSectionCount}.zip")));
                TOC.DeltaSectionCount++;

                TOC.DeltaSectionPackageCount ??= new List<long>();

                if (TOC.DeltaSectionPackageCount.Count != 0)
                {
                    TOC.DeltaSectionPackageCount.Add(TOC.DeltaSectionPackageCount.Last());
                }
                else
                {
                    TOC.DeltaSectionPackageCount.Add(0);
                }

                NewDeltaSubdirectoryCreated = true;
            }

            TOC.DeltaSectionPackageCount[^1] = TOC.DeltaSectionPackageCount[^1] + 1;

            var newPackageIndex = IdentityToIndexMap.Count;
            AddIdentity(package.Id, newPackageIndex);

            DeltaMetadataStores.Last().AddPackage(package);
        }

        public override void AddPackages(IEnumerable<IPackage> packages)
        {
            foreach (var package in packages)
            {
                AddPackage(package);
            }
        }

        public override void Dispose()
        {
            DeltaMetadataStores.ForEach(s => s.Dispose());
            DeltaMetadataStores.Clear();
        }

        public override void Flush()
        {
            if (NewDeltaSubdirectoryCreated)
            {
                DeltaMetadataStores.Last().Flush();
            }
        }

        public override Stream GetMetadata(IPackageIdentity packageIdentity)
        {
            var packageIndex = GetPackageIndex(packageIdentity);
            if (packageIndex < 0)
            {
                throw new KeyNotFoundException();
            }

            var deltaIndex = GetDeltaIndexFromPackageIndex(packageIndex);
            return DeltaMetadataStores[deltaIndex].GetMetadata(packageIdentity);
        }

        public override List<T> GetFiles<T>(IPackageIdentity packageIdentity)
        {
            var packageIndex = GetPackageIndex(packageIdentity);
            if (packageIndex < 0)
            {
                throw new KeyNotFoundException();
            }

            var deltaIndex = GetDeltaIndexFromPackageIndex(packageIndex);
            return DeltaMetadataStores[deltaIndex].GetFiles<T>(packageIdentity);
        }

        public override IPackage GetPackage(IPackageIdentity packageIdentity)
        {
            var packageIndex = GetPackageIndex(packageIdentity);
            if (packageIndex < 0)
            {
                throw new KeyNotFoundException();
            }

            var deltaIndex = GetDeltaIndexFromPackageIndex(packageIndex);
            return DeltaMetadataStores[deltaIndex].GetPackage(packageIdentity);
        }

        private int GetDeltaIndexFromPackageIndex(int packageIndex)
        {
            var deltaIndex = TOC.DeltaSectionPackageCount.BinarySearch(packageIndex);
            if (deltaIndex < 0)
            {
                deltaIndex = ~deltaIndex;
            }
            else
            {
                deltaIndex++;
            }

            if (deltaIndex == TOC.DeltaSectionPackageCount.Count)
            {
                throw new KeyNotFoundException();
            }

            return deltaIndex;
        }

        public override IEnumerator<IPackage> GetEnumerator()
        {
            return DeltaMetadataStores.SelectMany(store => store).GetEnumerator();
        }
    }
}
