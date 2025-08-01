// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Microsoft.PackageGraph.Utilitites.Upsync.Commands
{
    [Description("Deletes a store configuration by alias")]
    public class StoreAliasDeleteCommand : Command<StoreAliasDeleteCommand.Settings>
    {
        public class Settings : CommandSettings
        {
            [CommandOption("--alias")]
            [Description("Delete only the specified alias")]
            public string Alias { get; set; }

            [CommandOption("--all")]
            [Description("Delete all aliases")]
            public bool All { get; set; }

            public override ValidationResult Validate()
            {
                if (string.IsNullOrEmpty(Alias) && !All)
                {
                    return ValidationResult.Error("Either --alias or --all must be specified.");
                }
                if (!string.IsNullOrEmpty(Alias) && All)
                {
                    return ValidationResult.Error("Cannot specify both --alias and --all.");
                }
                return ValidationResult.Success();
            }
        }

        public override int Execute(CommandContext context, Settings settings)
        {
            MetadataStoreCreator.DeleteAlias(settings);
            return 0;
        }
    }
}
