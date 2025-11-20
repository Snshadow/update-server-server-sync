// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Prerequisites;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage;
using Microsoft.UpdateServices.WebServices.ClientSync;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Endpoints.ClientSync
{
    /// <summary>
    /// Update server implementation. Provides updates to Windows Update clients.
    /// <para>The communication protocol with clients is SOAP.</para>
    /// </summary>
    public partial class ClientSyncWebService : IClientSyncWebService
    {
        /// <summary>
        /// The local repository from where updates are served.
        /// </summary>
        public IMetadataStore MetadataSource { get; private set; }

        private IDeploySyncStore _deployAndSyncStore;

        private Config _serviceConfiguration;

        private readonly IMemoryCache _memoryCache;
        private readonly IDistributedCache _distributedCache;

        private const string CacheKeyPrereqGraph = "PrerequisitesGraph";
        private const string CacheKeyRootUpdates = "RootUpdates";
        private const string CacheKeyNonLeafUpdates = "NonLeafUpdates";
        private const string CacheKeyLeafUpdates = "LeafUpdates";
        private const string CacheKeySoftwareLeafUpdates = "SoftwareLeafUpdates";

        private readonly ReaderWriterLockSlim _metadataSourceLock = new();

        private Timer _cacheRefreshTimer;
        private TimeSpan _cacheRefreshPeriod;

        private int _activeSyncOperations;

        private const int MaxUpdatesInResponse = 50;

        private string _contentRoot;

        /// <summary>
        /// Delegate for handling unapproved driver update requests
        /// </summary>
        /// <param name="unapprovedDrivers">List of unapproved driver updates that were requested</param>
        public delegate void UnApprovedDriverUpdatesRequestedDelegate(List<DriverUpdate> unapprovedDrivers);

        /// <summary>
        /// Event raised when unapproved driver updates are requested by clients
        /// </summary>
        public event UnApprovedDriverUpdatesRequestedDelegate OnUnApprovedDriverUpdatesRequested;

        /// <summary>
        /// Default constructor
        /// </summary>
        public ClientSyncWebService(IMemoryCache memoryCache, IDistributedCache distributedCache)
        {
            _memoryCache = memoryCache;
            _distributedCache = distributedCache;
        }

        /// <summary>
        /// Sets the host name for the server that serves the update content
        /// </summary>
        /// <param name="hostName"></param>
        public void SetContentURLBase(string hostName)
        {
            _contentRoot = hostName;
        }

        /// <summary>
        /// Sets the service configuration
        /// </summary>
        /// <param name="serviceConfiguration">Service configuration</param>
        public void SetServiceConfiguration(Config serviceConfiguration)
        {
            _serviceConfiguration = serviceConfiguration;
        }

        /// <summary>
        /// Sets the background cache refresh period
        /// </summary>
        /// <param name="refreshPeriod">Desired refresh period. Defaults to 5 minutes if non-positive.</param>
        public void SetCacheRefreshPeriod(TimeSpan refreshPeriod)
        {
            if (refreshPeriod <= TimeSpan.Zero)
            {
                refreshPeriod = TimeSpan.FromMinutes(5);
            }

            _cacheRefreshPeriod = refreshPeriod;

            // If timer already exists, update its period
            _cacheRefreshTimer?.Change(_cacheRefreshPeriod, _cacheRefreshPeriod);
        }

        /// <summary>
        /// Sets the source of deployment and synchronization
        /// </summary>
        /// <param name="dataStore">The source for deployment and synchronization data</param>
        public void SetDeploymentAndSyncStore(IDeploySyncStore dataStore)
        {
            _deployAndSyncStore = dataStore;
        }

        /// <summary>
        /// Sets the source of update metadata
        /// </summary>
        /// <param name="metadataSource">The source for updates metadata</param>
        public void SetPackageStore(IMetadataStore metadataSource)
        {
            _metadataSourceLock.EnterWriteLock();
            try
            {
                MetadataSource = metadataSource;
                if (MetadataSource is null)
                {
                    _deployAndSyncStore = null;
                    ClearCachedMetadataView();
                }
                else
                {
                    BuildCachedMetadataView();
                    EnsureRefreshTimer();
                }
            }
            finally
            {
                _metadataSourceLock.ExitWriteLock();
            }
        }

        private void BuildCachedMetadataView()
        {
            if (MetadataSource is null)
            {
                return;
            }

            var slices = ComputeGraphSlices();

            StoreGraphSlices(slices);
        }

        private void ClearCachedMetadataView()
        {
            _memoryCache.Remove(CacheKeyPrereqGraph);
            _memoryCache.Remove(CacheKeyRootUpdates);
            _memoryCache.Remove(CacheKeyNonLeafUpdates);
            _memoryCache.Remove(CacheKeyLeafUpdates);
            _memoryCache.Remove(CacheKeySoftwareLeafUpdates);

            _distributedCache.Remove(CacheKeyRootUpdates);
            _distributedCache.Remove(CacheKeyNonLeafUpdates);
            _distributedCache.Remove(CacheKeyLeafUpdates);
            _distributedCache.Remove(CacheKeySoftwareLeafUpdates);
        }

        private (PrerequisitesGraph Graph, List<Guid> Root, List<Guid> NonLeaf, List<Guid> Leaf, List<Guid> SoftwareLeaf) ComputeGraphSlices()
        {
            if (MetadataSource is null)
            {
                return (null, [], [], [], []);
            }

            var graph = PrerequisitesGraph.FromIndexedPackageSource(MetadataSource);

            var rootUpdates = graph.GetRootUpdates().ToList();
            var nonLeafUpdates = graph.GetNonLeafUpdates().ToList();
            var leafUpdates = graph.GetLeafUpdates().ToList();

            MetadataFilter filter = new()
            {
                IncludeExpired = true,
                IncludeBundled = true
            };
            var softwareLeafUpdates = filter.GetMatchingIdentities<SoftwareUpdate>(MetadataSource)
                .Cast<MicrosoftUpdatePackageIdentity>()
                .Select(i => i.ID)
                .Intersect(leafUpdates)
                .ToList();

            return (graph, rootUpdates, nonLeafUpdates, leafUpdates, softwareLeafUpdates);
        }

        private void StoreGraphSlices((PrerequisitesGraph Graph, List<Guid> Root, List<Guid> NonLeaf, List<Guid> Leaf, List<Guid> SoftwareLeaf) slices)
        {
            var graphSize = EstimateGraphSizeFromSlices(slices.Root, slices.NonLeaf, slices.Leaf);
            _memoryCache.Set(CacheKeyPrereqGraph, slices.Graph, CreateSizedEntryOptions(slices.Graph, CacheItemPriority.NeverRemove, graphSize));

            PersistGuidList(CacheKeyRootUpdates, slices.Root);
            PersistGuidList(CacheKeyNonLeafUpdates, slices.NonLeaf);
            PersistGuidList(CacheKeyLeafUpdates, slices.Leaf);
            PersistGuidList(CacheKeySoftwareLeafUpdates, slices.SoftwareLeaf);
        }

        private static MemoryCacheEntryOptions CreateSizedEntryOptions(object value, CacheItemPriority priority, long? sizeHint = null)
        {
            var options = new MemoryCacheEntryOptions
            {
                Priority = priority
            };

            options.SetSize(sizeHint ?? EstimateCacheEntrySize(value));

            return options;
        }

        private static long EstimateCacheEntrySize(object value) =>
            value switch
            {
                List<Guid> guids => Math.Max(guids.Count * 16L + 64, 1),
                // Graph sizing should be passed explicitly via sizeHint to avoid extra traversal
                PrerequisitesGraph => 1,
                byte[] bytes => Math.Max(bytes.Length, 1),
                _ => 1
            };

        private static long EstimateGraphSizeFromSlices(List<Guid> root, List<Guid> nonLeaf, List<Guid> leaf)
        {
            var uniqueNodes = new HashSet<Guid>(root ?? []);
            if (nonLeaf is not null)
            {
                uniqueNodes.UnionWith(nonLeaf);
            }
            if (leaf is not null)
            {
                uniqueNodes.UnionWith(leaf);
            }

            return Math.Max(uniqueNodes.Count * 256L, 1);
        }

        private void EnsureRefreshTimer()
        {
            if (_cacheRefreshTimer is not null)
            {
                return;
            }

            _cacheRefreshTimer = new Timer(_ => RefreshCacheIfIdle(), state: null, dueTime: _cacheRefreshPeriod, period: _cacheRefreshPeriod);
        }

        private void RefreshCacheIfIdle()
        {
            if (MetadataSource is null)
            {
                return;
            }

            // Only refresh when no client sync is running
            if (Interlocked.CompareExchange(ref _activeSyncOperations, 0, 0) == 0)
            {
                _metadataSourceLock.EnterWriteLock();
                try
                {
                    BuildCachedMetadataView();
                }
                finally
                {
                    _metadataSourceLock.ExitWriteLock();
                }
            }
        }

        /// <summary>
        /// Handle get configuration requests from clients
        /// </summary>
        /// <param name="clientConfiguration">The client configuration as received from a Windows client</param>
        /// <returns>The server configuration to be sent to a Windows client</returns>
        public Task<Config> GetConfig2Async(ClientConfiguration clientConfiguration)
        {
            return Task.FromResult(_serviceConfiguration);
        }

        /// <summary>
        /// Handle get configuration requests from clients
        /// </summary>
        /// <param name="protocolVersion">The version of the Windows client connecting to this server</param>
        /// <returns>The server configuration to be sent to a Windows client</returns>
        public Task<Config> GetConfigAsync(string protocolVersion)
        {
            return Task.FromResult(_serviceConfiguration);
        }

        /// <summary>
        /// Handle get cookie requests. All requests are all granted access and a cookie is issued.
        /// </summary>
        /// <param name="authCookies">Authorization cookies received from the client</param>
        /// <param name="oldCookie">Old cookie from client</param>
        /// <param name="lastChange"></param>
        /// <param name="currentTime"></param>
        /// <param name="protocolVersion">Client supported protocol version</param>
        /// <returns>A new cookie</returns>
        public Task<Cookie> GetCookieAsync(AuthorizationCookie[] authCookies, Cookie oldCookie, DateTime lastChange, DateTime currentTime, string protocolVersion)
        {
            var cookieData = authCookies?[0]?.CookieData;
            var cookieString = (cookieData is { Length: > 0 }) ? Convert.ToBase64String(cookieData) : string.Empty;
            var now = DateTime.UtcNow;

            return Task.FromResult(new Cookie()
            {
                Expiration = now.AddDays(5),
                EncryptedData = Encoding.UTF8.GetBytes($"{cookieString}:{now.ToBinary().ToString(CultureInfo.InvariantCulture)}")
            });
        }

        /// <summary>
        /// Handle requests for extended update information. The extended information is extracted from update metadata.
        /// Extended information also includes file URLs
        /// </summary>
        /// <param name="cookie">Access cookie</param>
        /// <param name="updateIDs">Update IDs for which to get extended information</param>
        /// <param name="infoTypes">The type of extended information requested</param>
        /// <param name="locales">The language to use when getting language dependent extended information</param>
        /// <param name="callerAttributes">Caller attributes; optional</param>
        /// <returns>Extended update information response.</returns>
        public Task<ExtendedUpdateInfo2> GetExtendedUpdateInfo2Async(Cookie cookie, UpdateIdentity[] updateIDs, XmlUpdateFragmentType[] infoTypes, string[] locales, string callerAttributes)
        {
            throw new NotImplementedException();
        }

        string GetCoreFragment(MicrosoftUpdatePackageIdentity updateIdentity)
        {
            using var xmlStream = MetadataSource.GetMetadata(updateIdentity);
            using var xmlReader = new StreamReader(xmlStream, Encoding.UTF8);
            return UpdateXmlTransformer.GetCoreFragmentFromMetadataXml(xmlReader.ReadToEnd());
        }

        string GetExtendedFragment(MicrosoftUpdatePackageIdentity updateIdentity)
        {
            using var xmlStream = MetadataSource.GetMetadata(updateIdentity);
            using var xmlReader = new StreamReader(xmlStream, Encoding.UTF8);
            return UpdateXmlTransformer.GetExtendedFragmentFromMetadataXml(xmlReader.ReadToEnd());
        }

        string[] GetLocalizedProperties(MicrosoftUpdatePackageIdentity updateIdentity, string[] languages)
        {
            using var xmlStream = MetadataSource.GetMetadata(updateIdentity);
            using var xmlReader = new StreamReader(xmlStream, Encoding.UTF8);
            return UpdateXmlTransformer.GetLocalizedPropertiesFromMetadataXml(xmlReader.ReadToEnd(), languages);
        }

        /// <summary>
        /// Handle requests for extended update information. The extended information is extracted from update metadata.
        /// Extended information also includes file URLs
        /// </summary>
        /// <param name="cookie">Access cookie</param>
        /// <param name="revisionIDs">Revision Ids for which to get extended information</param>
        /// <param name="infoTypes">The type of extended information requested</param>
        /// <param name="locales">The language to use when getting language dependent extended information</param>
        /// <param name="GeoId">The region for which to retrieve end user license agreement (EULA) XML fragments and digests</param>
        /// <param name="callerAttributes">Caller attributes; unused</param>
        /// <returns>Extended update information response.</returns>
        public Task<ExtendedUpdateInfo> GetExtendedUpdateInfoAsync(Cookie cookie, int[] revisionIDs, XmlUpdateFragmentType[] infoTypes, string[] locales, string GeoId, string callerAttributes)
        {
            if (MetadataSource is null)
            {
                throw new FaultException();
            }

            List<MicrosoftUpdatePackage> requestedUpdates = [];
            foreach (var requestedRevision in revisionIDs)
            {
                requestedUpdates.Add(MetadataSource.GetPackage(requestedRevision) as MicrosoftUpdatePackage);
            }

            var updateDataList = new List<UpdateData>();

            if (infoTypes.Contains(XmlUpdateFragmentType.Extended))
            {
                for (int i = 0; i < requestedUpdates.Count; i++)
                {
                    updateDataList.Add(new UpdateData()
                    {
                        ID = revisionIDs[i],
                        Xml = GetExtendedFragment(requestedUpdates[i].Id)
                    });
                }
            }

            if (infoTypes.Contains(XmlUpdateFragmentType.LocalizedProperties))
            {
                for (int i = 0; i < requestedUpdates.Count; i++)
                {
                    var localizedXmlArr = GetLocalizedProperties(requestedUpdates[i].Id, locales);

                    foreach (var localizedXml in localizedXmlArr)
                    {
                        updateDataList.Add(new UpdateData()
                        {
                            ID = revisionIDs[i],
                            Xml = localizedXml
                        });
                    }
                }
            }

            var files = requestedUpdates.Where(u => u.Files?.Any() ?? false).SelectMany(u => u.Files.OfType<UpdateFile>()).Distinct().ToList();
            var fileList = new List<FileLocation>();
            for (int i = 0; i < files.Count; i++)
            {
                fileList.Add(new FileLocation()
                {
                    FileDigest = Convert.FromBase64String(files[i].Digest.DigestBase64),
                    Url = string.IsNullOrEmpty(_contentRoot) ? files[i].Urls[0].MuUrl : $"{_contentRoot}/{files[i].Digest.HexString.ToLower()}"
                });
            }

            var response = new ExtendedUpdateInfo();

            if (updateDataList.Count > 0)
            {
                response.Updates = updateDataList.ToArray();
            }

            if (fileList.Count > 0)
            {
                response.FileLocations = fileList.ToArray();
            }

            return Task.FromResult(response);
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="cookie"></param>
        /// <param name="fileDigests"></param>
        /// <returns>Not implemented</returns>
        public Task<GetFileLocationsResults> GetFileLocationsAsync(Cookie cookie, byte[][] fileDigests)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns>Not implemented</returns>
        public Task<GetTimestampsResponse> GetTimestampsAsync(GetTimestampsRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="cookie"></param>
        /// <param name="globalIDs"></param>
        /// <param name="deviceAttributes"></param>
        /// <returns>Not implemented</returns>
        public Task<RefreshCacheResult[]> RefreshCacheAsync(Cookie cookie, UpdateIdentity[] globalIDs, string deviceAttributes)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="cookie"></param>
        /// <param name="computerInfo"></param>
        /// <returns>Not implemented</returns>
        public Task RegisterComputerAsync(Cookie cookie, ComputerInfo computerInfo)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns>Not implemented</returns>
        public Task<StartCategoryScanResponse> StartCategoryScanAsync(StartCategoryScanRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="cookie"></param>
        /// <param name="installedNonLeafUpdateIDs"></param>
        /// <param name="printerUpdateIDs"></param>
        /// <param name="deviceAttributes"></param>
        /// <returns>Not implemented</returns>
        public Task<SyncInfo> SyncPrinterCatalogAsync(Cookie cookie, int[] installedNonLeafUpdateIDs, int[] printerUpdateIDs, string deviceAttributes)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Handle requests to sync updates. A client presents the list of installed updates and detectoids and the server
        /// replies with a list of more applicable updates, if any.
        /// </summary>
        /// <param name="cookie">Access cookie</param>
        /// <param name="parameters">Request parameters: list of installed updates, list of known updates, etc.</param>
        /// <returns>SyncInfo containing updates applicable to the caller.</returns>
        public Task<SyncInfo> SyncUpdatesAsync(Cookie cookie, SyncUpdateParameters parameters)
        {
            return RunSyncWithTracking(() =>
            {
                if (parameters.SkipSoftwareSync)
                {
                    return DoDriversSync(cookie, parameters);
                }

                return DoSoftwareUpdateSync(cookie, parameters);
            });
        }

        private async Task<SyncInfo> RunSyncWithTracking(Func<Task<SyncInfo>> syncOperation)
        {
            Interlocked.Increment(ref _activeSyncOperations);
            try
            {
                return await syncOperation();
            }
            finally
            {
                Interlocked.Decrement(ref _activeSyncOperations);
            }
        }

        private List<Guid> GetCachedGuidSet(string key)
        {
            if (_memoryCache.TryGetValue(key, out List<Guid> cached))
            {
                return cached;
            }

            // Try disk-backed cache first
            var fromDistributed = TryLoadGuidList(key);
            if (fromDistributed.Count > 0)
            {
                return fromDistributed;
            }

            _metadataSourceLock.EnterWriteLock();
            try
            {
                var slices = ComputeGraphSlices();
                StoreGraphSlices(slices);

                return key switch
                {
                    CacheKeyRootUpdates => slices.Root,
                    CacheKeyNonLeafUpdates => slices.NonLeaf,
                    CacheKeyLeafUpdates => slices.Leaf,
                    CacheKeySoftwareLeafUpdates => slices.SoftwareLeaf,
                    _ => []
                };
            }
            finally
            {
                _metadataSourceLock.ExitWriteLock();
            }
        }

        private void PersistGuidList(string key, List<Guid> guidList)
        {
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(guidList);
                _distributedCache.Set(key, payload);
            }
            catch
            {
            }
        }

        private List<Guid> TryLoadGuidList(string key)
        {
            try
            {
                var bytes = _distributedCache.Get(key);
                if (bytes is null)
                {
                    return [];
                }
                return JsonSerializer.Deserialize<List<Guid>>(bytes) ?? [];
            }
            catch
            {
                return [];
            }
        }

        private static (string comptuerId, DateTime cookieTime) ParseCookie(Cookie cookie)
        {
            var cookieString = Encoding.UTF8.GetString(cookie.EncryptedData);
            var parts = cookieString.Split(':', 2);

            if (parts.Length == 2)
            {
                var dateData = long.Parse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture);

                // Remove null character at the end of cookie.EncryptedData
                var computerId = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])).Trim('\0');
                var cookieTime = DateTime.FromBinary(dateData);

                return (computerId, cookieTime);
            }

            throw new InvalidDataException("Invalid cookie data");
        }

        private IDeployment GetDeployment(int revisionId)
        {
            return _deployAndSyncStore.GetDeployment(revisionId);
        }

        /// <summary>
        /// Converts the a list of client supplied update indexes into a list of update identities
        /// </summary>
        /// <param name="clientIndexes">Client update indexes (ints)</param>
        /// <returns>List of update identities that correspond to the client's indexes</returns>
        private List<MicrosoftUpdatePackageIdentity> GetUpdateIdentitiesFromClientIndexes(int[] clientIndexes)
        {
            var updateIdentities = new List<MicrosoftUpdatePackageIdentity>();
            if (clientIndexes is not null)
            {
                foreach (var nonLeafRevision in clientIndexes)
                {
                    var nonLeafId = MetadataSource.GetPackage(nonLeafRevision).Id as MicrosoftUpdatePackageIdentity;

                    updateIdentities.Add(nonLeafId);
                }
            }
            return updateIdentities;
        }

        /// <summary>
        /// Extract installed non-leaf updates from the response and maps them to a GUID
        /// </summary>
        /// <param name="parameters">Sync parameters</param>
        /// <returns>List of update GUIDs</returns>
        private List<Guid> GetInstalledNotLeafGuidsFromSyncParameters(SyncUpdateParameters parameters)
        {
            return GetUpdateIdentitiesFromClientIndexes(parameters.InstalledNonLeafUpdateIDs)
                .Select(u => u.ID)
                .ToList();
        }

        /// <summary>
        /// Extract list of other known updates from the client and maps them to a GUID
        /// </summary>
        /// <param name="parameters">Sync parameters</param>
        /// <returns>List of update GUIDs</returns>
        private List<Guid> GetOtherCachedUpdateGuidsFromSyncParameters(SyncUpdateParameters parameters)
        {
            return GetUpdateIdentitiesFromClientIndexes(parameters.OtherCachedUpdateIDs)
                .Select(u => u.ID)
                .ToList();
        }
    }
}
