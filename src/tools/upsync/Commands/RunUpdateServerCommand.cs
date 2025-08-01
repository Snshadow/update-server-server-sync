// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Microsoft.PackageGraph.Utilitites.Upsync.Commands
{
    [Description("Serve updates to Windows Update clients")]
    public class RunUpdateServerCommand : Command<RunUpdateServerCommand.Settings>
    {
        public class Settings : CommandSettings
        {
            [CommandOption("--metadata-store-alias")]
            [Description("Destination store alias")]
            public string Alias { get; set; }

            [CommandOption("--metadata-store-path")]
            [Description("Package metadata store to server packages from")]
            public string Path { get; set; }

            [CommandOption("--store-type")]
            [DefaultValue("local")]
            [Description("Store type; local (default) or azure")]
            public string Type { get; set; }

            [CommandOption("--connection-string")]
            [Description("Azure connection string; required when the store type is azure")]
            public string StoreConnectionString { get; set; }

            [CommandOption("--content-source")]
            [Description("Path to content source")]
            public string ContentSourcePath { get; set; }

            [CommandOption("--service-config")]
            [Description("Path to service configuration JSON file")]
            public string ServiceConfigurationPath { get; set; }

            [CommandOption("--port")]
            [DefaultValue(32150)]
            [Description("The port to bind the server to.")]
            public int Port { get; set; }

            [CommandOption("--endpoint")]
            [DefaultValue("*")]
            [Description("The endpoint to bind the server to.")]
            public string Endpoint { get; set; }

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
                    return ValidationResult.Error("--connection-string is required when --store-type is azure.");
                }
                return ValidationResult.Success();
            }
        }

        public override int Execute(CommandContext context, Settings settings)
        {
            UpdateServer.Run(settings);
            return 0;
        }
    }
}
