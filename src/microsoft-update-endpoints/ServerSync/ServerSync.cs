// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.Storage.Local;
using Microsoft.UpdateServices.WebServices.ServerSync;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Endpoints.ServerSync
{
    /// <summary>
    /// Upstream update server implementation. Provides updates over the ServerSync protocol to downstream servers.
    /// <para>The communication protocol with clients is SOAP</para>
    /// </summary>
    public partial class ServerSyncWebService : IServerSyncWebService
    {
        /// <summary>
        /// The source of upate metadata that this server serves.
        /// </summary>
        private IMetadataStore PackageStore;
        private readonly ReaderWriterLockSlim PackageStoreLock = new();

        /// <summary>
        /// Cached service configuration
        /// </summary>
        private ServerSyncConfigData ServiceConfiguration;
        private readonly ReaderWriterLockSlim ServiceConfigurationLock = new();

        /// <summary>
        /// Default constructor
        /// </summary>
        public ServerSyncWebService()
        {
        }

        /// <summary>
        /// ASP.NET extension method for setting service configuration
        /// </summary>
        /// <param name="serviceConfig"></param>
        public void SetServerConfiguration(ServerSyncConfigData serviceConfig)
        {
            ServiceConfigurationLock.EnterWriteLock();
            ServiceConfiguration = serviceConfig;
            ServiceConfigurationLock.ExitWriteLock();
        }

        /// <summary>
        /// Sets the package store for this instance of the server
        /// </summary>
        /// <param name="packageSource">The package store to server updates from</param>
        public void SetPackageStore(IMetadataStore packageSource)
        {
            PackageStoreLock.EnterWriteLock();
            PackageStore = packageSource;
            PackageStoreLock.ExitWriteLock();
        }

        /// <summary>
        /// Handle authentication data requests
        /// </summary>
        /// <param name="request">The request data. Not used</param>
        /// <returns>Exactly one canned authentication method</returns>
        public Task<ServerAuthConfig> GetAuthConfigAsync(GetAuthConfigRequest request)
        {
            // Build the standard response
            var result = new ServerAuthConfig()
            {
                LastChange = DateTime.UtcNow,
                AuthInfo =
                [
                    new AuthPlugInInfo()
                    {
                        PlugInID = "DssTargeting",
                        ServiceUrl = "DssAuthWebService/DssAuthWebService.asmx"
                    }
                ]
            };

            GetAuthConfigResponse response = new(
                new GetAuthConfigResponseBody
                {
                    GetAuthConfigResult = result
                }
            );

            return Task.FromResult(response.Body.GetAuthConfigResult);
        }

        /// <summary>
        /// Handle service configuration requests
        /// </summary>
        /// <param name="request">Service configuration request</param>
        /// <returns>Returns the cached service configuration of the upstream server the local repo is tracking</returns>
        public Task<ServerSyncConfigData> GetConfigDataAsync(GetConfigDataRequest request)
        {
            ServerSyncConfigData capturedConfigData;
            ServiceConfigurationLock.EnterReadLock();
            capturedConfigData = ServiceConfiguration;
            ServiceConfigurationLock.ExitReadLock();

            GetConfigDataResponse response = new(
                new GetConfigDataResponseBody
                {
                    GetConfigDataResult = capturedConfigData
                }
            );
            return Task.FromResult(response.Body.GetConfigDataResult);
        }

        /// <summary>
        /// Handle request for a cookie
        /// </summary>
        /// <param name="request">Cookie request. Not used; all requests are granted</param>
        /// <returns>A cookie that expires in 5 days.</returns>
        public Task<Cookie> GetCookieAsync(GetCookieRequest request)
        {
            return Task.FromResult(new Cookie()
            {
                Expiration = DateTime.UtcNow.AddDays(5),
                EncryptedData = new byte[12]
            });
        }

        /// <summary>
        /// Return a list of update ids
        /// </summary>
        /// <param name="request">Request data. Can specify categories or updates, filters, etc.</param>
        /// <returns></returns>
        public Task<RevisionIdList> GetRevisionIdListAsync(GetRevisionIdListRequest request)
        {
            var response = new RevisionIdList
            {
                Anchor = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
            };

            PackageStoreLock.EnterReadLock();

            try
            {
                if (request.Body.filter.GetConfig)
                {
                    MetadataFilter filter = new();

                    var categories = filter.Apply<ProductCategory>(PackageStore)
                        .Cast<MicrosoftUpdatePackage>()
                        .Concat(filter.Apply<ClassificationCategory>(PackageStore)
                            .Cast<MicrosoftUpdatePackage>())
                        .Concat(filter.Apply<DetectoidCategory>(PackageStore)
                            .Cast<MicrosoftUpdatePackage>());

                    response.NewRevisions = categories
                        .Select(u => new UpdateIdentity()
                        {
                            UpdateID = u.Id.ID.ToString(),
                            RevisionNumber = u.Id.Revision
                        })
                        .ToArray();
                }
                else
                {
                    MetadataFilter filter = new()
                    {
                        IncludeExpired = true
                    };
                    if (request.Body.filter.Categories != null)
                    {
                        filter.ProductFilter = request
                            .Body
                            .filter
                            .Categories
                            .Select(p => Guid.Parse(p.Id))
                            .ToList();
                    }
                    if (request.Body.filter.Classifications != null)
                    {
                        filter.ClassificationFilter = request
                            .Body
                            .filter
                            .Classifications
                            .Select(c => Guid.Parse(c.Id))
                            .ToList();
                    }

                    var filteredPackages = filter.Apply(PackageStore);

                    // Also select all updates that are bundled with updates matching the filter
                    List<MicrosoftUpdatePackageIdentity> GetAllBundledUpdates(IPackage update)
                    {
                        if (update is SoftwareUpdate { BundledUpdates.Count: > 0 } softwareUpdate)
                        {
                            List<MicrosoftUpdatePackageIdentity> bundledList = [];
                            bundledList.AddRange(softwareUpdate.BundledUpdates);

                            MetadataFilter bundledFilter = new()
                            {
                                IncludeExpired = true,
                                IdFilter = softwareUpdate.BundledUpdates.Select(id => id.ID)
                                    .ToList()
                            };

                            foreach (var bundledUpdate in bundledFilter
                                .Apply<MicrosoftUpdatePackage>(PackageStore))
                            {
                                bundledList.AddRange(GetAllBundledUpdates(bundledUpdate));
                            }

                            return bundledList;
                        }

                        return [];
                    }

                    List<MicrosoftUpdatePackageIdentity> bundledUpdates = [];
                    foreach (var package in filteredPackages)
                    {
                        bundledUpdates.AddRange(GetAllBundledUpdates(package));
                    }

                    // Deduplicate result and convert to raw identity format
                    response.NewRevisions = filteredPackages
                        .Select(p => p.Id as MicrosoftUpdatePackageIdentity)
                        .Union(bundledUpdates)
                        .Distinct()
                        .Select(u => new UpdateIdentity()
                        {
                            UpdateID = u.ID.ToString(),
                            RevisionNumber = u.Revision
                        })
                        .ToArray();
                }
            }
            catch (Exception)
            {
            }

            PackageStoreLock.ExitReadLock();

            return Task.FromResult(response);
        }

        /// <summary>
        /// Return metadata for updates
        /// </summary>
        /// <param name="request">The request; contains IDs for updates to retrieve metadata for</param>
        /// <returns>Update metadata for requested updates</returns>
        public Task<ServerUpdateData> GetUpdateDataAsync(GetUpdateDataRequest request)
        {
            var response = new ServerUpdateData();

            ServerSyncConfigData serviceConfiguration;
            ServiceConfigurationLock.EnterReadLock();
            serviceConfiguration = ServiceConfiguration;
            ServiceConfigurationLock.ExitReadLock();

            if (serviceConfiguration is null || PackageStore is null)
            {
                return Task.FromResult(response);
            }

            // Make sure the request is not larger than the config says
            var updateRequestCount = request.Body.updateIds.Length;
            if (updateRequestCount > serviceConfiguration.MaxNumberOfUpdatesPerRequest)
            {
                return null;
            }

            PackageStoreLock.EnterReadLock();

            List<ServerSyncUpdateData> returnUpdatesList = [];
            List<ServerSyncUrlData> returnFilesList = [];

            try
            {
                foreach (var rawIdentity in request.Body.updateIds)
                {
                    var updateIdentity = new MicrosoftUpdatePackageIdentity(Guid.Parse(rawIdentity.UpdateID), rawIdentity.RevisionNumber);

                    if (!PackageStore.ContainsPackage(updateIdentity))
                    {
                        throw new Exception("Update not found");
                    }

                    var update = PackageStore.GetPackage(updateIdentity) as MicrosoftUpdatePackage;
                    if (update?.Files is not null)
                    {
                        // if update contains files, we must also gather file information
                        foreach (var updateFile in update.Files)
                        {
                            var microsoftUpdateFile = updateFile as UpdateFile;
                            returnFilesList.Add(
                                new ServerSyncUrlData()
                                {
                                    FileDigest = Convert.FromBase64String(updateFile.Digest.DigestBase64),
                                    MUUrl = microsoftUpdateFile.Urls[0].MuUrl,
                                    UssUrl = $"microsoftupdate/content/{FileSystemContentStore.GetContentDirectoryName(microsoftUpdateFile.Digest)}/{updateFile.FileName}"
                                });
                        }
                    }

                    var rawUpdateData = new ServerSyncUpdateData
                    {
                        Id = rawIdentity
                    };

                    using (var metadataReader = new StreamReader(PackageStore.GetMetadata(update.Id)))
                    {
                        rawUpdateData.XmlUpdateBlob = metadataReader.ReadToEnd();
                    }

                    returnUpdatesList.Add(rawUpdateData);
                }
            }
            catch (Exception) { }

            response.updates = returnUpdatesList.ToArray();
            // Deduplicate list of files
            response.fileUrls = returnFilesList
                .GroupBy(f => f.MUUrl)
                .Select(k => k.First())
                .ToArray();

            PackageStoreLock.ExitReadLock();

            return Task.FromResult(response);
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<GetUpdateDecryptionDataResponse> GetUpdateDecryptionDataAsync(GetUpdateDecryptionDataRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<PingResponse> PingAsync(PingRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<GetDeploymentsResponse> GetDeploymentsAsync(GetDeploymentsRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<GetDriverIdListResponse> GetDriverIdListAsync(GetDriverIdListRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<GetDriverSetDataResponse> GetDriverSetDataAsync(GetDriverSetDataRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<GetRelatedRevisionsForUpdatesResponse> GetRelatedRevisionsForUpdatesAsync(GetRelatedRevisionsForUpdatesRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task<DownloadFilesResponse> DownloadFilesAsync(DownloadFilesRequest request)
        {
            throw new NotImplementedException();
        }
    }
}
