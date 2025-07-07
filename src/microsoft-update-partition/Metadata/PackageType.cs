// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Microsoft.PackageGraph.MicrosoftUpdate
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    enum StoredPackageType : int
    {
        MicrosoftUpdateDetectoid = 0,
        MicrosoftUpdateClassification,
        MicrosoftUpdateProduct,
        MicrosoftUpdateSoftware,
        MicrosoftUpdateDriver
    }
}
