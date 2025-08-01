// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.Utilitites.Upsync.Commands;
using Spectre.Console.Cli;
using System;
using System.Threading;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    class Program
    {
        private static readonly Lock ProgressLock = new();
        static int Main(string[] args)
        {
            var app = new CommandApp();
            app.Configure(config =>
            {
                config.AddCommand<FetchConfigurationCommand>("fetch-config");
                config.AddCommand<FetchCommand>("fetch");
                config.AddCommand<QueryCommand>("query");
                config.AddCommand<StatusCommand>("status");
                config.AddCommand<ExportCommand>("export");
                config.AddCommand<ContentSyncCommand>("fetch-content");
                config.AddCommand<RunUpstreamServerCommand>("run-upstream-server");
                config.AddCommand<RunUpdateServerCommand>("run-update-server");
                config.AddCommand<FetchCategoriesCommand>("pre-fetch");
                config.AddCommand<ReindexCommand>("index");
                config.AddCommand<ApproveUpdateCommand>("approve-update");
                config.AddCommand<UnapproveUpdateCommand>("unapprove-update");
                config.AddCommand<MatchDriverCommand>("match-driver");
                config.AddCommand<MetadataCopyCommand>("copy-metadata");
                config.AddCommand<StoreAliasListCommand>("list-store-aliases");
                config.AddCommand<StoreAliasDeleteCommand>("delete-store-alias");
                config.AddCommand<StoreAliasCreateCommand>("create-store-alias");
            });

            return app.Run(args);
        }

        private static readonly Lock ConsoleWriteLock = new();

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

        public static void OnPackageCopyProgress(object sender, PackageStoreEventArgs e)
        {
            lock (ConsoleWriteLock)
            {
                UpdateConsoleForMessageRefresh();

                if (e.Total == 0)
                {
                    Console.Write($"Copying {e.Total} package(s)");
                }
                else
                {
                    Console.Write($"Copying {e.Total} package(s). {e.Current} {Math.Truncate((double)e.Current * 100 / e.Total)}%");
                }
            }
        }

        public static void OnOpenProgress(object sender, PackageStoreEventArgs e)
        {
            lock (ConsoleWriteLock)
            {
                UpdateConsoleForMessageRefresh();

                if (e.Total == 0)
                {
                    Console.Write(e.Current);
                }
                else
                {
                    Console.Write($"{e.Current}, {Math.Truncate((double)e.Current * 100) / e.Total}%");
                }
            }
        }

        public static void OnPackageIndexingProgress(object sender, PackageStoreEventArgs e)
        {
            lock (ProgressLock)
            {
                UpdateConsoleForMessageRefresh();


                if (e.Total == 0)
                {
                    Console.Write($"Indexing {e.Total} package(s)");
                }
                else
                {
                    Console.Write($"Indexing {e.Total} package(s). {e.Current} {Math.Truncate((double)e.Current * 100) / e.Total}%");
                }
            }
        }
    }
}
