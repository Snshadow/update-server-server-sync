// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Microsoft.PackageGraph.Utilitites.Upsync.Commands
{
    [Description("Saves store information and create an alias for it")]
    public class StoreAliasCreateCommand : Command<StoreAliasCreateCommand.Settings>
    {
        public class Settings : CommandSettings, IMetadataStoreOptions
        {
            [CommandOption("--alias", true)]
            [Description("Alias for this store configuration")]
            public string Alias { get; set; }

            [CommandOption("--path", true)]
            [Description("Store path. Local path for a local path; store name for a cloud store")]
            public string Path { get; set; }

            [CommandOption("--type")]
            [DefaultValue("local")]
            [Description("Store type; local (default) or azure")]
            public string Type { get; set; }

            [CommandOption("--connection-string")]
            [Description("Azure connection string; required when the store type is azure")]
            public string StoreConnectionString { get; set; }

            public override ValidationResult Validate()
            {
                if (Type == "azure" && string.IsNullOrEmpty(StoreConnectionString))
                {
                    return ValidationResult.Error("--connection-string is required when --store-type is azure.");
                }
                return ValidationResult.Success();
            }
        }

        public override int Execute(CommandContext context, Settings settings)
        {
            MetadataStoreCreator.CreateAlias(settings);
            return 0;
        }
    }
}
