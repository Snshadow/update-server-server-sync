// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.Storage.Index;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Index
{
    class IsExpiredIndex : SimpleIndex<int, bool>, ISimpleMetadataIndex<int, bool>
    {
        public const string Name = AvailableIndexes.IsExpiredIndexName;

        public override IndexDefinition Definition => MicrosoftUpdatePartitionRegistration.Categories;

        public IsExpiredIndex(IIndexContainer container) : base(container, Name, MicrosoftUpdatePartitionRegistration.MicrosoftUpdatePartitionName)
        {
        }

        public override void IndexPackage(IPackage package, int packageIndex)
        {
            if (package is MicrosoftUpdatePackage microsoftUpdate)
            {
                base.Add(packageIndex, microsoftUpdate.IsExpired);
            }
        }
    }
}
