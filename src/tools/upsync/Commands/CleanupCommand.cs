// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Threading.Tasks;

namespace Microsoft.PackageGraph.Utilitites.Upsync.Commands
{
    [Description("Run cleanup for underlying store")]
    public class CleanupCommand : AsyncCommand<CleanupCommand.Settings>
    {
        public class Settings : CommandSettings, IContentStoreOptions, IMetadataStoreOptions
        {
            [CommandOption("--metadata-store-alias")]
            [Description("Destination store alias")]
            public string Alias { get; set; }

            [CommandOption("--metadata-store-path")]
            [Description("Destination store")]
            public string Path { get; set; }

            [CommandOption("--metadata-store-type")]
            [DefaultValue("local")]
            [Description("Store type; local or azure")]
            public string Type { get; set; }

            [CommandOption("--connection-string")]
            [Description("Azure connection string; required when the store type is azure")]
            public string StoreConnectionString { get; set; }

            [CommandOption("--content-store-path")]
            [Description("Destination content store")]
            public string ContentPath { get; set; }

            [CommandOption("--content-store-type")]
            [DefaultValue("local")]
            [Description("Content store type")]
            public string ContentStoreType { get; set; }

            [CommandOption("--content-connection-string")]
            [Description("Azure connection string; required when the store type is azure")]
            public string ContentStoreConnectionString { get; set; }

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

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            await Cleanup.CleanupStore(settings).ConfigureAwait(false);
            return 0;
        }
    }
}
