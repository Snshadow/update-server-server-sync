// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.Storage.Local;
using Microsoft.UpdateServices.WebServices.ClientSync;
using Newtonsoft.Json;
using SoapCore;
using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Endpoints.ClientSync
{
    /// <summary>
    /// Startup class for a ASP.NET Core web service that implements the Client-Server sync protocol.
    /// This startup runs a SOAP web service that serves updates to Windows Update clients
    /// <para>This startup configures the required SOAP adapter required for the SOAP based Client-Server sync protocol.</para>
    /// </summary>
    public class UpdateServerStartup
    {
        readonly IMetadataStore MetadataSource;

        readonly Config UpdateServiceConfiguration;

        readonly IContentStore ContentSource = null;

        readonly string ContentRoot;

        readonly string CacheDatabasePath;

        readonly TimeSpan CacheRefreshPeriod;

        readonly long MemoryCacheSizeLimit;

        /// <summary>
        /// Creates the update server startup using the specified configuration an update metadata store
        /// </summary>
        /// <param name="config">Startup configuration
        /// <para>ASP.NET configuration.</para>
        /// 
        /// <para>Must contain a string entry "metadata-path" with the path to the metadata source to use</para>
        /// 
        /// <para>Must contain a string entry "service-config-json" with the service configuration JSON</para>
        /// 
        /// <para>Can contain a string entry "content-path" with the path to the content store to use if serving update content</para>
        /// </param>
        /// <exception cref="Exception">If the content store specified in the configuration cannot be opened</exception>
        public UpdateServerStartup(IConfiguration config)
        {
            var metadataPath = config.GetValue<string>("metadata-path");
            MetadataSource = PackageStore.Open(metadataPath);

            UpdateServiceConfiguration = JsonConvert.DeserializeObject<Config>(config.GetValue<string>("service-config-json"));

            // A file that contains mapping of update identity to a 32 bit, locally assigned revision ID.
            var contentPath = config.GetValue<string>("content-path");
            if (!string.IsNullOrEmpty(contentPath))
            {
                ContentSource = new FileSystemContentStore(contentPath);
                if (ContentSource is null)
                {
                    throw new Exception($"Cannot open updates content source from path {contentPath}");
                }

                ContentRoot = config.GetValue<string>("content-http-root");
            }

            CacheDatabasePath = config.GetValue<string>("client-sync-cache-path")
                ?? Path.Combine(Path.GetTempPath(), "client-sync-cache");

            var refreshMinutes = config.GetValue<int?>("client-sync-refresh-minutes") ?? 5;
            if (refreshMinutes <= 0)
            {
                refreshMinutes = 5;
            }
            CacheRefreshPeriod = TimeSpan.FromMinutes(refreshMinutes);

            MemoryCacheSizeLimit = config.GetValue<long?>("client-sync-cache-size-bytes") ?? 2147483648; // 2GB
            if (MemoryCacheSizeLimit <= 0)
            {
                MemoryCacheSizeLimit = 2147483648;
            }
        }

        /// <summary>
        /// Called by ASP.NET to configure services
        /// </summary>
        /// <param name="services">Service collection.
        /// <para>The client-server sync and simple authentication services are added to this list.</para>
        /// </param>
        public void ConfigureServices(IServiceCollection services)
        {
            // Enable SoapCore; this middleware provides translation services from WCF/SOAP to Asp.net
            services.AddSoapCore();

            services.AddMemoryCache(options =>
            {
                options.SizeLimit = MemoryCacheSizeLimit;
            });
            services.AddSingleton<IDistributedCache>(_ => new FileSystemDistributedCache(CacheDatabasePath));

            // Enable the upstream WCF services
            services.AddSingleton(provider =>
            {
                var memoryCache = provider.GetRequiredService<IMemoryCache>();
                var distributedCache = provider.GetRequiredService<IDistributedCache>();

                var clientSyncService = new ClientSyncWebService(memoryCache, distributedCache);
                clientSyncService.SetContentURLBase(ContentSource is null ? null : ContentRoot);
                clientSyncService.SetServiceConfiguration(UpdateServiceConfiguration);
                clientSyncService.SetCacheRefreshPeriod(CacheRefreshPeriod);
                clientSyncService.SetPackageStore(MetadataSource);

                if (MetadataSource is IDeploySyncStore dataStore)
                {
                    clientSyncService.SetDeploymentAndSyncStore(dataStore);
                }

                return clientSyncService;
            });
            services.TryAddSingleton<SimpleAuthenticationWebService>();
            services.TryAddSingleton<ReportingWebService>();

            // Enable the content controller if serving content
            if (ContentSource is not null)
            {
                services.AddSingleton(ContentSource);
                // Add ContentController from this assembly
                services.AddMvc().AddApplicationPart(Assembly.GetExecutingAssembly()).AddControllersAsServices();
            }
        }

        /// <summary>
        /// Called by ASP.NET to configure a web app's application pipeline
        /// </summary>
        /// <param name="app">App builder to configure</param>
        /// <param name="env">Hosting environment</param>
        /// <param name="loggerFactory">Logging factory</param>
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILoggerFactory loggerFactory)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            if (ContentSource is not null)
            {
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllerRoute(
                        name: "getContent",
                        pattern: "microsoftupdate/content/{contentHash}",
                        defaults: new { controller = "MicrosoftUpdateContent", action = "GetMicrosoftUpdateContent" });
                });
            }

            // Wire the upstream WCF services
            app.UseSoapEndpoint<ClientSyncWebService>(
                "/ClientWebService/client.asmx",
                new SoapEncoderOptions() { WriteEncoding = new UTF8Encoding(false) },
                SoapSerializer.XmlSerializer);

            app.UseSoapEndpoint<SimpleAuthenticationWebService>(
                "/SimpleAuthWebService/SimpleAuth.asmx",
                new SoapEncoderOptions() { WriteEncoding = new UTF8Encoding(false) },
                SoapSerializer.XmlSerializer);
        }
    }
}
