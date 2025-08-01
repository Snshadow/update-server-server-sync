// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using Spectre.Console.Cli;
using System.Collections.Generic;
using System.ComponentModel;

namespace Microsoft.PackageGraph.Utilitites.Upsync.Commands
{
    [Description("Downloads update content from an upstream server")]
    public class ContentSyncCommand : Command<ContentSyncCommand.Settings>
    {
        public class Settings : CommandSettings, IMetadataStoreOptions, IMetadataFilterOptions
        {
            [CommandOption("--metadata-store-alias")]
            [Description("Destination store alias")]
            public string Alias { get; set; }

            [CommandOption("--metadata-store-path")]
            [Description("Destination store")]
            public string Path { get; set; }

            [CommandOption("--metadata-store-type")]
            [DefaultValue("local")]
            [Description("Store type; local (default) or azure")]
            public string Type { get; set; }

            [CommandOption("--connection-string")]
            [Description("Azure connection string; required when the store type is azure")]
            public string StoreConnectionString { get; set; }

            [CommandOption("--content-store-path")]
            [Description("Destination content store")]
            public string ContentPath { get; set; }

            [CommandOption("--content-store-type")]
            [DefaultValue("local")]
            [Description("Content store type; default is local")]
            public string ContentStoreType { get; set; }

            [CommandOption("--content-connection-string")]
            [Description("Azure connection string; required when the store type is azure")]
            public string ContentStoreConnectionString { get; set; }

            [CommandOption("--product-filter")]
            [Description("Product filter for sync'ing updates")]
            public string ProductsFilter { get; set; }
            IEnumerable<string> IMetadataFilterOptions.ProductsFilter => ProductsFilter?.Split('+');

            [CommandOption("--classification-filter")]
            [Description("Classification filter for sync'ing updates")]
            public string ClassificationsFilter { get; set; }
            IEnumerable<string> IMetadataFilterOptions.ClassificationsFilter => ClassificationsFilter?.Split('+');

            [CommandOption("--id-filter")]
            [Description("ID filter")]
            public string IdFilter { get; set; }
            IEnumerable<string> IMetadataFilterOptions.IdFilter => IdFilter?.Split('+');

            [CommandOption("--title-filter")]
            [Description("Title filter")]
            public string TitleFilter { get; set; }

            [CommandOption("--hwid-filter")]
            [Description("Hardware ID filter")]
            public string HardwareIdFilter { get; set; }

            [CommandOption("--computer-hwid-filter")]
            [Description("Computer hardware ID filter")]
            public string ComputerHardwareIdFilter { get; set; }

            [CommandOption("--kbarticle-filter")]
            [Description("KB article filter (numbers only)")]
            public string KbArticleFilter { get; set; }
            IEnumerable<string> IMetadataFilterOptions.KbArticleFilter => KbArticleFilter?.Split('+');

            [CommandOption("--skip-superseded")]
            [Description("Do not consider superseded updates for download")]
            public bool SkipSuperseded { get; set; }

            [CommandOption("--first")]
            [Description("Content sync only the first x packages")]
            public int FirstX { get; set; }

            public override ValidationResult Validate()
            {
                if (string.IsNullOrEmpty(Alias) == string.IsNullOrEmpty(Path))
                {
                    return string.IsNullOrEmpty(Alias)
                        ? ValidationResult.Error("Either --metadata-store-alias or --metadata-store-path must be specified.")
                        : ValidationResult.Error("Cannot specify both --metadata-store-alias and --metadata-store-path.");
                }
                if (Type == "azure" && string.IsNullOrEmpty(StoreConnectionString))
                {
                    return ValidationResult.Error("--connection-string is required when --metadata-store-type is azure.");
                }
                if (ContentStoreType == "azure" && string.IsNullOrEmpty(ContentStoreConnectionString))
                {
                    return ValidationResult.Error("--content-connection-string is required when --content-store-type is azure.");
                }
                return ValidationResult.Success();
            }
        }

        public override int Execute(CommandContext context, Settings settings)
        {
            ContentSync.SyncContent(settings);
            return 0;
        }
    }
}
