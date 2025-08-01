// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using Spectre.Console.Cli;
using System.Collections.Generic;
using System.ComponentModel;

namespace Microsoft.PackageGraph.Utilitites.Upsync.Commands
{
    [Description("Copy packages from one repository to another")]
    public class MetadataCopyCommand : Command<MetadataCopyCommand.Settings>
    {
        public class Settings : CommandSettings, IMetadataFilterOptions
        {
            [CommandOption("--source-alias")]
            [Description("Destination store alias")]
            public string SourceAlias { get; set; }

            [CommandOption("--source-path")]
            [Description("Package metadata source")]
            public string SourcePath { get; set; }

            [CommandOption("--source-type")]
            [DefaultValue("local")]
            [Description("Source store type; local (default), azure-blob, azure-table etc.")]
            public string SourceType { get; set; }

            [CommandOption("--source-connection-string")]
            [Description("Source connection string; required for non-local sources")]
            public string SourceConnectionString { get; set; }

            [CommandOption("--destination-alias")]
            [Description("Destination store alias")]
            public string DestinationAlias { get; set; }

            [CommandOption("--destination-path")]
            [Description("Package metadata destination")]
            public string DestionationPath { get; set; }

            [CommandOption("--destination-type")]
            [DefaultValue("local")]
            [Description("Destination store type; local (default), azure-blob, azure-table etc.")]
            public string DestinationType { get; set; }

            [CommandOption("--destination-connection-string")]
            [Description("Destination connection string; required for non-local destinations")]
            public string DestinationConnectionString { get; set; }

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

            [CommandOption("--product-filter")]
            [Description("Product filter")]
            public string ProductsFilter { get; set; }
            IEnumerable<string> IMetadataFilterOptions.ProductsFilter => ProductsFilter?.Split('+');

            [CommandOption("--kbarticle-filter")]
            [Description("KB article filter (numbers only)")]
            public string KbArticleFilter { get; set; }
            IEnumerable<string> IMetadataFilterOptions.KbArticleFilter => KbArticleFilter?.Split('+');

            [CommandOption("--classification-filter")]
            [Description("Classification filter")]
            public string ClassificationsFilter { get; set; }
            IEnumerable<string> IMetadataFilterOptions.ClassificationsFilter => ClassificationsFilter?.Split('+');

            [CommandOption("--skip-superseded")]
            [Description("Do not serve superseded updates")]
            public bool SkipSuperseded { get; set; }

            [CommandOption("--first")]
            [Description("Copy only the first x updates")]
            public int FirstX { get; set; }

            public override ValidationResult Validate()
            {
                if (string.IsNullOrEmpty(SourceAlias) && string.IsNullOrEmpty(SourcePath))
                {
                    return ValidationResult.Error("Either --source-alias or --source-path must be specified.");
                }
                if (!string.IsNullOrEmpty(SourceAlias) && !string.IsNullOrEmpty(SourcePath))
                {
                    return ValidationResult.Error("Cannot specify both --source-alias and --source-path.");
                }
                if (SourceType == "azure" && string.IsNullOrEmpty(SourceConnectionString))
                {
                    return ValidationResult.Error("--source-connection-string is required when --source-type is azure.");
                }
                if (string.IsNullOrEmpty(DestinationAlias) && string.IsNullOrEmpty(DestionationPath))
                {
                    return ValidationResult.Error("Either --destination-alias or --destination-path must be specified.");
                }
                if (!string.IsNullOrEmpty(DestinationAlias) && !string.IsNullOrEmpty(DestionationPath))
                {
                    return ValidationResult.Error("Cannot specify both --destination-alias and --destination-path.");
                }
                if (DestinationType == "azure" && string.IsNullOrEmpty(DestinationConnectionString))
                {
                    return ValidationResult.Error("--destination-connection-string is required when --destination-type is azure.");
                }
                return ValidationResult.Success();
            }
        }

        public override int Execute(CommandContext context, Settings settings)
        {
            MetadataCopy.Run(settings);
            return 0;
        }
    }
}
