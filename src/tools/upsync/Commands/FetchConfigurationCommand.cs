// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Source;
using Newtonsoft.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.ComponentModel;
using System.IO;

namespace Microsoft.PackageGraph.Utilitites.Upsync.Commands
{
    [Description("Retrieves upstream server configuration")]
    public class FetchConfigurationCommand : Command<FetchConfigurationCommand.Settings>
    {
        public class Settings : CommandSettings
        {
            [CommandOption("-e|--endpoint")]
            [Description("The endpoint from which to fetch updates")]
            public string UpstreamEndpoint { get; set; }

            [CommandOption("-m|--master")]
            [Description("Only fetch categories")]
            public bool MasterEndpoint { get; set; }

            [CommandOption("-d|--destination", true)]
            [Description("Destination JSON file.")]
            public string OutFile { get; set; }
            public override ValidationResult Validate()
            {
                if (!string.IsNullOrEmpty(UpstreamEndpoint) && MasterEndpoint)
                {
                    return ValidationResult.Error("Cannot specify both --endpoint and --master.");
                }
                return ValidationResult.Success();
            }
        }

        public override int Execute(CommandContext context, Settings settings)
        {
            Endpoint upstreamEndpoint;
            if (!string.IsNullOrEmpty(settings.UpstreamEndpoint))
            {
                upstreamEndpoint = new Endpoint(settings.UpstreamEndpoint);
            }
            else
            {
                upstreamEndpoint = Endpoint.Default;
            }

            var server = new UpstreamServerClient(upstreamEndpoint);
            server.MetadataQueryProgress += Server_MetadataQueryProgress;
            var configData = server.GetServerConfigData().GetAwaiter().GetResult();

            File.WriteAllText(settings.OutFile, JsonConvert.SerializeObject(configData));
            return 0;
        }

        private static void Server_MetadataQueryProgress(object sender, MetadataQueryProgress e)
        {
            switch (e.CurrentTask)
            {
                case MetadataQueryStage.AuthenticateStart:
                    Console.Write("Acquiring new access token...");
                    break;

                case MetadataQueryStage.GetServerConfigStart:
                    Console.Write("Retrieving service configuration data...");
                    break;

                case MetadataQueryStage.AuthenticateEnd:
                case MetadataQueryStage.GetServerConfigEnd:
                case MetadataQueryStage.GetRevisionIdsEnd:
                    ConsoleOutput.WriteGreen("Done!");
                    break;

                case MetadataQueryStage.GetRevisionIdsStart:
                    Console.Write("Retrieving revision IDs...");
                    break;

                case MetadataQueryStage.GetUpdateMetadataStart:
                    Console.Write("Retrieving updates metadata [{0}]: 0%", e.Maximum);
                    break;

                case MetadataQueryStage.GetUpdateMetadataEnd:
                    UpdateConsoleForMessageRefresh();
                    Console.Write("Retrieving updates metadata [{0}]: 100.00%", e.Maximum);
                    ConsoleOutput.WriteGreen(" Done!");
                    break;

                case MetadataQueryStage.GetUpdateMetadataProgress:
                    UpdateConsoleForMessageRefresh();
                    Console.Write("Retrieving updates metadata [{0}]: {1:000.00}%", e.Maximum, e.PercentDone);
                    break;
            }
        }

        private static void UpdateConsoleForMessageRefresh()
        {
            if (!Console.IsOutputRedirected)
            {
                Console.CursorLeft = 0;
            }
            else
            {
                Console.WriteLine();
            }
        }
    }
}
