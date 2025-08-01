// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Microsoft.PackageGraph.Utilitites.Upsync.Commands
{
    [Description("Find drivers")]
    public class MatchDriverCommand : Command<MatchDriverCommand.Settings>
    {
        public class Settings : CommandSettings, IMetadataStoreOptions
        {
            [CommandOption("--store-alias")]
            [Description("Destination store alias")]
            public string Alias { get; set; }

            [CommandOption("--store-path")]
            [Description("Store to match drivers from")]
            public string Path { get; set; }

            [CommandOption("--store-type")]
            [DefaultValue("local")]
            [Description("Store type; local (default) or azure")]
            public string Type { get; set; }

            [CommandOption("--connection-string")]
            [Description("Azure connection string; required when the store type is azure")]
            public string StoreConnectionString { get; set; }

            [CommandOption("--hwid", true)]
            [Description("Match drivers for this list of hardware ids; Add HwIds from specific to generic")]
            public string HardwareIds { get; set; }

            [CommandOption("--computer-hwid")]
            [Description("Match drivers that target these computer hardware ids.")]
            public string ComputerHardwareIds { get; set; }

            [CommandOption("--installed-prerequisites", true)]
            [Description("Prerequisites installed on the target computer. Used for driver applicability checks")]
            public string InstalledPrerequisites { get; set; }

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
            MetadataQuery.MatchDrivers(settings);
            return 0;
        }
    }
}
