// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using Spectre.Console.Cli;
using System.Collections.Generic;
using System.ComponentModel;

namespace Microsoft.PackageGraph.Utilitites.Upsync.Commands
{
    [Description("Unapprove updates from being installed")]
    public class UnapproveUpdateCommand : Command<UnapproveUpdateCommand.Settings>
    {
        public class Settings : CommandSettings, IMetadataStoreOptions, IMetadataFilterOptions
        {
            [CommandOption("--store-alias")]
            [Description("Destination store alias")]
            public string Alias { get; set; }

            [CommandOption("--store-path")]
            [Description("Store to query")]
            public string Path { get; set; }

            [CommandOption("--store-type")]
            [DefaultValue("local")]
            [Description("Store type; local (default) or azure")]
            public string Type { get; set; }

            [CommandOption("--connection-string")]
            [Description("Azure connection string; required when the store type is azure")]
            public string StoreConnectionString { get; set; }

            [CommandOption("--id-filter")]
            [Description("ID filter")]
            public string IdFilter { get; set; }
            IEnumerable<string> IMetadataFilterOptions.IdFilter => IdFilter?.Split('+');

            [CommandOption("--file-hash")]
            [Description("File hash")]
            public string FileHash { get; set; }

            [CommandOption("--title-filter")]
            [Description("Title filter")]
            public string TitleFilter { get; set; }

            [CommandOption("--hwid-filter")]
            [Description("Hardware ID filter")]
            public string HardwareIdFilter { get; set; }

            [CommandOption("--computer-hwid-filter")]
            [Description("Computer hardware ID filter")]
            public string ComputerHardwareIdFilter { get; set; }

            [CommandOption("--product-filter")]
            [Description("Product filter")]
            public string ProductsFilter { get; set; }
            IEnumerable<string> IMetadataFilterOptions.ProductsFilter => ProductsFilter?.Split('+');

            [CommandOption("--classification-filter")]
            [Description("Classification filter")]
            public string ClassificationsFilter { get; set; }
            IEnumerable<string> IMetadataFilterOptions.ClassificationsFilter => ClassificationsFilter?.Split('+');

            [CommandOption("--kbarticle-filter")]
            [Description("KB article filter (numbers only)")]
            public string KbArticleFilter { get; set; }
            IEnumerable<string> IMetadataFilterOptions.KbArticleFilter => KbArticleFilter?.Split('+');

            [CommandOption("--skip-superseded")]
            [Description("Ignore superseded updates")]
            public bool SkipSuperseded { get; set; }

            [CommandOption("--first")]
            [Description("Handle first x updates only")]
            public int FirstX { get; set; }

            public override ValidationResult Validate()
            {
                if (string.IsNullOrEmpty(Alias) == string.IsNullOrEmpty(Path))
                {
                    ;
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
            ManageDeploy.UnapproveUpdates(settings);
            return 0;
        }
    }
}
