// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console.Cli;
using System.ComponentModel;

namespace Microsoft.PackageGraph.Utilitites.Upsync.Commands
{
    [Description("Lists stored store aliases")]
    public class StoreAliasListCommand : Command<StoreAliasListCommand.Settings>
    {
        public class Settings : CommandSettings
        {
            [CommandOption("--alias")]
            [Description("List only the specified alias")]
            public string Alias { get; set; }
        }

        public override int Execute(CommandContext context, Settings settings)
        {
            MetadataStoreCreator.ListAliases(settings);
            return 0;
        }
    }
}
