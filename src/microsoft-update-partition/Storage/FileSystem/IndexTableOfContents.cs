// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.Storage.Index;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace Microsoft.PackageGraph.Storage.Local
{
    class IndexTableOfContents
    {
        [JsonInclude]
        public int Version;

        [JsonInclude]
        public List<IndexDefinition> ContainedIndexes;

        [JsonIgnore]
        public const int CurrentVersion = 0;
    }
}
