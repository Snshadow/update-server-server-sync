// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace Microsoft.PackageGraph.Storage.Local
{
    class TableOfContent
    {
        [JsonInclude]
        public int TocVersion { get; set; }
        [JsonInclude]
        public int DeltaSectionCount { get; set; }
        [JsonInclude]
        public List<long> DeltaSectionPackageCount { get; set; }

        [JsonIgnore]
        public const int CurrentVersion = 0;

        public TableOfContent()
        {
            DeltaSectionPackageCount = new List<long>();
        }
    }
}
