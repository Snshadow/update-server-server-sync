// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.PackageGraph.MicrosoftUpdate.Index
{
    abstract class AvailableIndexes
    {
        public const string DriverMetadataIndexName = "mu_driver_metadata";
        public const string KbArticleIndexName = "mu_kbarticle";
        public const string IsSupersededIndexName = "mu_is_superseded";
        public const string IsSupersedingIndexName = "mu_is_superseding";
        public const string IsBundleIndexName = "mu_is_bundled";
        public const string BundledWithIndexName = "mu_bundled_with";
        public const string PrerequisitesIndexName = "mu_prerequisites";
        public const string CategoriesIndexName = "mu_categories";
        public const string FilesIndexName = "mu_files";
    }
}
