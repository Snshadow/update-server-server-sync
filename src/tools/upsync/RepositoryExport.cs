// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.Utilitites.Upsync.Commands;
using Microsoft.UpdateServices.WebServices.ServerSync;
using Newtonsoft.Json;
using System;
using System.IO;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    class UpdateMetadataExport
    {
        /// <summary>
        /// Export filtered or all update metadata from a source
        /// </summary>
        /// <param name="options">Export options</param>
        public static void ExportUpdates(ExportCommand.Settings options)
        {
            var source = MetadataStoreCreator.OpenFromOptions(options);
            if (source is null)
            {
                return;
            }

            var filter = FilterBuilder.MicrosoftUpdateFilterFromCommandLine(options);
            if (filter is null)
            {
                return;
            }

            ServerSyncConfigData serverConfig;
            try
            {
                serverConfig = JsonConvert.DeserializeObject<ServerSyncConfigData>(File.ReadAllText(options.ServerConfigFile));
            }
            catch (Exception)
            {
                ConsoleOutput.WriteRed($"Failed to read server configuration file from {options.ServerConfigFile}");
                return;
            }

            var exporter = new WsusExporter(source, serverConfig);
            exporter.Export(filter, options.ExportFile);
        }
    }
}
