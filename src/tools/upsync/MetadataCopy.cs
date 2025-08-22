// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.Utilitites.Upsync.Commands;
using System;
using System.Threading;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    class SourceMetadataStoreOptions : IMetadataStoreOptions
    {
        public string Alias { get; set; }
        public string Path { get; set; }
        public string Type { get; set; }
        public string StoreConnectionString { get; set; }
    }

    /// <summary>
    /// Implements copying of metadata from a source repository to a destination
    /// </summary>
    class MetadataCopy
    {
        public static void Run(MetadataCopyCommand.Settings options)
        {
            using var source = MetadataStoreCreator.OpenFromOptions(
                new SourceMetadataStoreOptions()
                {
                    Alias = options.SourceAlias,
                    StoreConnectionString = options.SourceConnectionString,
                    Path = options.SourcePath,
                    Type = options.SourceType
                });

            if (source is null)
            {
                ConsoleOutput.WriteRed("Failed to open source repository");
                return;
            }

            using var destination = MetadataStoreCreator.CreateFromOptions(
                new SourceMetadataStoreOptions()
                {
                    Alias = options.DestinationAlias,
                    StoreConnectionString = options.DestinationConnectionString,
                    Path = options.DestionationPath,
                    Type = options.DestinationType
                });

            if (destination is null)
            {
                ConsoleOutput.WriteRed("Failed to open or create destination repository");
                return;
            }

            var filter = FilterBuilder.MicrosoftUpdateFilterFromCommandLine(options);
            if (filter is null)
            {
                return;
            }

            Console.WriteLine("Copying packages ...");
            source.MetadataCopyProgress += Program.OnPackageCopyProgress;
            destination.PackagesAddProgress += Program.OnPackageCopyProgress;
            source.CopyTo(destination, filter, CancellationToken.None);
        }
    }
}
