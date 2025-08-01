// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Microsoft.PackageGraph.Utilitites.Upsync.Commands
{
    [Description("Retrieves metadata from an upstream server")]
    public class FetchCommand : Command<FetchCommand.Settings>
    {
        public class Settings : CommandSettings, IMetadataStoreOptions
        {
            [CommandOption("--store-alias")]
            [Description("Destination store alias")]
            public string Alias { get; set; }

            [CommandOption("--store-path")]
            [Description("Destination store")]
            public string Path { get; set; }

            [CommandOption("--store-type")]
            [DefaultValue("local")]
            [Description("Store type; local (default) or azure")]
            public string Type { get; set; }

            [CommandOption("--connection-string")]
            [Description("Azure connection string; required when the store type is azure")]
            public string StoreConnectionString { get; set; }

            [CommandOption("-e|--endpoint")]
            [Description("The endpoint from which to fetch updates.")]
            public string UpstreamEndpoint { get; set; }

            [CommandOption("--endpoint-type")]
            [DefaultValue("microsoft-update")]
            [Description("The endpoint from which to fetch updates.")]
            public string EndpointType { get; set; }

            [CommandOption("-p|--product-filter")]
            [Description("Product filter for sync'ing updates")]
            public string ProductsFilter { get; set; }

            [CommandOption("-c|--classification-filter")]
            [Description("Classification filter for sync'ing updates")]
            public string ClassificationsFilter { get; set; }

            [CommandOption("--account-name")]
            [Description("Account name; if not set, a random GUID is used.")]
            public string AccountName { get; set; }

            [CommandOption("--account-guid")]
            [Description("Account GUID. If not set, a random GUID is used.")]
            public string AccountGuid { get; set; }

            [CommandOption("--ids")]
            [Description("Try fetch metadata for this list of ids (GUIDs)")]
            public string Ids { get; set; }

            public override ValidationResult Validate()
            {
                if (string.IsNullOrEmpty(Alias) == string.IsNullOrEmpty(Path))
                {
                    return string.IsNullOrEmpty(Alias)
                        ? ValidationResult.Error("Either --store-alias or --store-path must be specified.")
                        : ValidationResult.Error("Cannot specify both --store-alias and --store-path.");
                }
                if (Type == "azure" && string.IsNullOrEmpty(StoreConnectionString))
                {
                    return ValidationResult.Error("--connection-string is required when --store-type is azure.");
                }
                return ValidationResult.Success();
            }
        }

        public override int Execute(CommandContext context, Settings settings)
        {
            MetadataSync.FetchPackagesUpdates(settings);
            return 0;
        }
    }
}
