// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.UpdateServices.WebServices.ClientSync;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.ServiceModel;
using System.Threading.Tasks;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Endpoints.ClientSync
{
    public partial class ClientSyncWebService
    {
        /// <summary>
        /// Handle software sync request from a client
        /// </summary>
        /// <param name="cookie">Request cookie</param>
        /// <param name="parameters">Sync parameters</param>
        /// <returns></returns>
        private Task<SyncInfo> DoSoftwareUpdateSync(Cookie cookie, SyncUpdateParameters parameters)
        {
            var now = DateTime.UtcNow;

            _metadataSourceLock.EnterReadLock();
            try
            {
                if (MetadataSource is null)
                {
                    throw new FaultException();
                }

                List<Guid> cachedGuids = [];

                // Get list of installed non leaf updates; these are prerequisites that the client has installed.
                // This list is used to check what updates are applicable to the client
                // We will not send updates that already appear on this list
                var installedNonLeafUpdatesGuids = GetInstalledNotLeafGuidsFromSyncParameters(parameters);
                cachedGuids.AddRange(installedNonLeafUpdatesGuids);

                // Other known updates to the client; we will not send any updates that are on this list
                cachedGuids.AddRange(GetOtherCachedUpdateGuidsFromSyncParameters(parameters));

                cachedGuids = cachedGuids.Distinct().ToList();

                // Initialize the response
                var response = new SyncInfo()
                {
                    NewCookie = new Cookie()
                    {
                        Expiration = now.AddDays(5),
                        EncryptedData = cookie.EncryptedData
                    },
                    DriverSyncNotNeeded = "false"
                };

                // Add root updates first; if any new root updates were added, return the response to the client immediately
                AddMissingRootUpdatesToSyncUpdatesResponse(cachedGuids, response, out var rootUpdatesAdded);
                if (!rootUpdatesAdded)
                {
                    // No root updates were added; add non-leaf updates now
                    AddMissingNonLeafUpdatesToSyncUpdatesResponse(cachedGuids, installedNonLeafUpdatesGuids, response, out var nonLeafUpdatesAdded);
                    if (!nonLeafUpdatesAdded)
                    {
                        // No leaf updates were added; add leaf bundle updates now
                        AddMissingBundleUpdatesToSyncUpdatesResponse(cachedGuids, installedNonLeafUpdatesGuids, response, out var bundleUpdatesAdded);
                        if (!bundleUpdatesAdded)
                        {
                            // No bundles were added; finally add leaf software updates
                            AddMissingSoftwareUpdatesToSyncUpdatesResponse(cachedGuids, installedNonLeafUpdatesGuids, response, out var _);
                        }
                    }
                }

                var (computerId, _) = ParseCookie(cookie);
                var computerSync = _deployAndSyncStore.GetComputerSync(computerId);

                response.ChangedUpdates = GetChangedUpdates(
                    cachedGuids,
                    computerSync?.LastSyncTime ?? DateTime.MinValue)
                    .ToArray();

                // response.OutOfScopeRevisionIDs = [];
                // response.DeployedOutOfScopeRevisionIds = [];

                // Update last synchronization time for computer
                _deployAndSyncStore.UpdateComputerSync(computerId, DateTime.UtcNow);

                return Task.FromResult(response);
            }
            finally
            {
                _metadataSourceLock.ExitReadLock();
            }
        }

        private MicrosoftUpdatePackageIdentity GetLatestRevision(Guid id)
        {
            MetadataFilter filter = new()
            {
                IncludeBundled = true,
                IncludeExpired = true,
                IdFilter = [id]
            };

            return filter.GetMatchingIdentities<MicrosoftUpdatePackage>(MetadataSource)
                .Cast<MicrosoftUpdatePackageIdentity>()
                .OrderByDescending(i => i.Revision)
                .FirstOrDefault();
        }

        /// <summary>
        /// For a client request, gathers applicable root updates (detectoids, categories, etc.) that the client does not have yet
        /// </summary>
        /// <param name="cachedGuids">List of known updates from the client</param>
        /// <param name="response">The response to append new updates to</param>
        /// <param name="updatesAdded">On return: true of updates were added to the response, false otherwise</param>
        private void AddMissingRootUpdatesToSyncUpdatesResponse(List<Guid> cachedGuids, SyncInfo response, out bool updatesAdded)
        {
            var rootUpdatesIds = GetCachedGuidSet(CacheKeyRootUpdates)
                .Except(cachedGuids) // Do not search for known updates
                .ToList();

            if (rootUpdatesIds.Count == 0)
            {
                updatesAdded = false;
                return;
            }

            MetadataFilter filter = new()
            {
                IncludeBundled = true,
                IncludeExpired = true,
                IdFilter = rootUpdatesIds,
                FirstX = MaxUpdatesInResponse // Only take the maximum number of updates allowed 
            };

            var missingRootUpdates = filter.Apply(MetadataSource)
                .Cast<MicrosoftUpdatePackage>()
                .ToList();

            if (missingRootUpdates.Count > 0)
            {
                response.NewUpdates = CreateUpdateInfoListFromNonLeafUpdates(missingRootUpdates).ToArray();
                response.Truncated = true;
                updatesAdded = true;
            }
            else
            {
                updatesAdded = false;
            }
        }

        /// <summary>
        /// For a client request, gathers applicable software updates that are not leafs in the prerequisite tree; 
        /// </summary>
        /// <param name="cachedGuids">List of known updates from the client</param>
        /// <param name="installedNonLeaf">List of known updates leaf updates installed on the client</param>
        /// <param name="response">The response  to append new updates to</param>
        /// <param name="updatesAdded">On return: true of updates were added to the response, false otherwise</param>
        private void AddMissingNonLeafUpdatesToSyncUpdatesResponse(List<Guid> cachedGuids, List<Guid> installedNonLeaf, SyncInfo response, out bool updatesAdded)
        {
            var nonLeafUpdateIds = GetCachedGuidSet(CacheKeyNonLeafUpdates)
                .Except(cachedGuids) // Do not search for known updates
                .ToList();

            if (nonLeafUpdateIds.Count == 0)
            {
                updatesAdded = false;
                return;
            }

            MetadataFilter filter = new()
            {
                IncludeBundled = true,
                IncludeExpired = true,
                IdFilter = nonLeafUpdateIds,
                FirstX = MaxUpdatesInResponse // Only take the maximum number of updates allowed
            };

            var missingNonLeafs = filter.Apply(MetadataSource)
                .Cast<MicrosoftUpdatePackage>()
                .Where(u => u.IsApplicable(installedNonLeaf))    // Eliminate not applicable updates
                .ToList();

            if (missingNonLeafs.Count > 0)
            {
                response.NewUpdates = CreateUpdateInfoListFromNonLeafUpdates(missingNonLeafs).ToArray();
                response.Truncated = true;
                updatesAdded = true;
            }
            else
            {
                updatesAdded = false;
            }
        }

        /// <summary>
        /// For a client request, gathers applicable leaf bundle updates that the client does not have yet
        /// </summary>
        /// <param name="cachedGuids">List of known updates from the client</param>
        /// <param name="installedNonLeaf">List of non leaf updates installed on the client</param>
        /// <param name="response">The response  to append new updates to</param>
        /// <param name="updatesAdded">On return: true of updates were added to the response, false otherwise</param>
        private void AddMissingBundleUpdatesToSyncUpdatesResponse(List<Guid> cachedGuids, List<Guid> installedNonLeaf, SyncInfo response, out bool updatesAdded)
        {
            var leafUpdateIds = GetCachedGuidSet(CacheKeySoftwareLeafUpdates)
                .Except(cachedGuids) // Do not search for known updates
                .ToList();

            if (leafUpdateIds.Count == 0)
            {
                updatesAdded = false;
                return;
            }

            MetadataFilter filter = new()
            {
                IncludeBundled = true,
                IncludeExpired = true,
                IdFilter = leafUpdateIds,
                FirstX = MaxUpdatesInResponse // Only take the maximum number of updates allowed
            };

            var allMissingBundles = filter.Apply(MetadataSource)
                .OfType<SoftwareUpdate>()          // Select the software update by identity
                .Where(u => u.IsApplicable(installedNonLeaf) && (u.BundledWithUpdates?.Count ?? 0) > 0) // Remove not applicable and not bundles
                .ToList();

            if (allMissingBundles.Count > 0)
            {
                response.NewUpdates = CreateUpdateInfoListFromSoftwareUpdate(allMissingBundles).ToArray();
                response.Truncated = true;
                updatesAdded = true;
            }
            else
            {
                updatesAdded = false;
            }
        }

        /// <summary>
        /// For a client sync request, gathers applicable software updates that the client does not have yet
        /// </summary>
        /// <param name="cachedGuids">List of known updates from the client</param>
        /// <param name="installedNonLeaf">List of non leaf updates installed on the client</param>
        /// <param name="response">The response  to append new updates to</param>
        /// <param name="updatesAdded">On return: true of updates were added to the response, false otherwise</param>
        private void AddMissingSoftwareUpdatesToSyncUpdatesResponse(List<Guid> cachedGuids, List<Guid> installedNonLeaf, SyncInfo response, out bool updatesAdded)
        {
            var leafUpdateIds = GetCachedGuidSet(CacheKeySoftwareLeafUpdates)
                .Except(cachedGuids) // Do not search for known updates
                .ToList();

            if (leafUpdateIds.Count == 0)
            {
                updatesAdded = false;
                return;
            }

            MetadataFilter filter = new()
            {
                IncludeBundled = true,
                IncludeExpired = true,
                IdFilter = leafUpdateIds,
                FirstX = MaxUpdatesInResponse // Only take the maximum number of updates allowed
            };

            var allMissingApplicableUpdates = filter.Apply(MetadataSource)
                .OfType<SoftwareUpdate>()
                .Where(u => u.IsApplicable(installedNonLeaf) && ((u.BundledWithUpdates?.Count ?? 0) == 0)) // Remove not applicable and bundles
                .ToList();

            if (allMissingApplicableUpdates.Count > 0)
            {
                response.NewUpdates = CreateUpdateInfoListFromSoftwareUpdate(allMissingApplicableUpdates).ToArray();
                response.Truncated = true;
                updatesAdded = true;
            }
            else
            {
                updatesAdded = false;
            }
        }

        /// <summary>
        /// Creates a list of updates to be sent to the client, based on the specified list of software updates.
        /// The update information sent to the client contains a deployment field and a core XML fragment extracted
        /// from the full metadata of the update
        /// </summary>
        /// <param name="softwareUpdates">List of software updates to send to the client</param>
        /// <returns>List of updates that can be appended to a SyncUpdates SOAP response to a client</returns>
        private List<UpdateInfo> CreateUpdateInfoListFromSoftwareUpdate(List<SoftwareUpdate> softwareUpdates)
        {
            var returnListLength = Math.Min(MaxUpdatesInResponse, softwareUpdates.Count);
            var returnList = new List<UpdateInfo>(returnListLength);

            for (int i = 0; i < returnListLength; i++)
            {
                var update = softwareUpdates[i];
                var identity = update.Id;

                // Get the update index; it will be sent to the client
                var revision = MetadataSource.GetPackageIndex(identity);

                // Generate the core XML fragment
                var coreXml = GetCoreFragment(identity);

                var isBundle = update.BundledUpdates is { Count: > 0 };
                var isBundled = update.BundledWithUpdates is { Count: > 0 };

                var deploymentData = GetDeployment(revision);

                // Add the update information to the return array
                returnList.Add(new UpdateInfo()
                {
                    Deployment = new Deployment()
                    {
                        // Use the Action value if deployment was created for this update
                        // Action is Evaluate for bundles of updates that are not part of a bundle
                        // Action is Bundle for updates that are part of a bundle
                        Action = deploymentData?.Action ?? ((isBundle || !isBundled) ? DeploymentAction.Evaluate : DeploymentAction.Bundle),
                        ID = isBundle ? 20000 : (isBundled ? 20001 : 20002),
                        AutoDownload = "0",
                        AutoSelect = "0",
                        SupersedenceBehavior = "0",
                        IsAssigned = true,
                        LastChangeTime = deploymentData?.LastChangeTime.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo) ?? "2019-08-06",
                        Deadline = deploymentData?.Deadline?.ToString("yyyy-MM-ddTHH:mm:ssK", DateTimeFormatInfo.InvariantInfo)
                    },
                    IsLeaf = true,
                    ID = revision,
                    IsShared = false,
                    Verification = null,
                    Xml = coreXml
                });
            }

            return returnList;
        }

        /// <summary>
        /// Creates a list of updates to be sent to the client, based on the specified list of category updates.
        /// The update information sent to the client contains a deployment field and a core XML fragment extracted
        /// from the full metadata of the update
        /// </summary>
        /// <param name="nonLeafUpdates">List of non-software updates to send to the client. These are usually detectoids, categories and classifications</param>
        /// <returns>List of updates that can be appended to a SyncUpdates SOAP response to a client</returns>
        private List<UpdateInfo> CreateUpdateInfoListFromNonLeafUpdates(List<MicrosoftUpdatePackage> nonLeafUpdates)
        {
            var returnListLength = Math.Min(MaxUpdatesInResponse, nonLeafUpdates.Count);
            var returnList = new List<UpdateInfo>(returnListLength);

            for (int i = 0; i < returnListLength; i++)
            {
                var update = nonLeafUpdates[i];
                var identity = update.Id;
                var revision = MetadataSource.GetPackageIndex(identity);

                // Generate the core XML fragment
                var coreXml = GetCoreFragment(identity);

                // Add the update information to the return array
                returnList.Add(new UpdateInfo()
                {
                    Deployment = new Deployment()
                    {
                        Action = DeploymentAction.Evaluate,
                        ID = 15000,
                        AutoDownload = "0",
                        AutoSelect = "0",
                        SupersedenceBehavior = "0",
                        IsAssigned = true,
                        LastChangeTime = update.CreationDate.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo)
                    },
                    IsLeaf = false,
                    ID = revision,
                    IsShared = false,
                    Verification = null,
                    Xml = coreXml
                });
            }

            return returnList;
        }

        private List<UpdateInfo> GetChangedUpdates(List<Guid> cached, DateTime lastSyncTime)
        {
            var changedUpdates = new List<UpdateInfo>();
            var cachedUpdates = cached
                .Select(GetLatestRevision)
                .Where(id => id != null)
                .Select(MetadataSource.GetPackage)
                .OfType<SoftwareUpdate>();

            foreach (var update in cachedUpdates)
            {
                if (update is null)
                {
                    continue;
                }

                var revision = MetadataSource.GetPackageIndex(update.Id);
                var deployment = GetDeployment(revision);

                if (deployment is not null && deployment.LastChangeTime > lastSyncTime)
                {
                    var isBundle = update.BundledUpdates is { Count: > 0 };
                    var isBundled = update.BundledWithUpdates is { Count: > 0 };

                    changedUpdates.Add(new UpdateInfo()
                    {
                        Deployment = new Deployment()
                        {
                            Action = deployment.Action,
                            ID = isBundle ? 20000 : (isBundled ? 20001 : 20002),
                            AutoDownload = "0",
                            AutoSelect = "0",
                            SupersedenceBehavior = "0",
                            IsAssigned = true,
                            LastChangeTime = deployment.LastChangeTime.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo),
                            Deadline = deployment.Deadline?.ToString("yyyy-MM-ddTHH:mm:ssK", DateTimeFormatInfo.InvariantInfo)
                        },
                        ID = revision,
                        IsLeaf = true,
                        IsShared = false,
                        Verification = null,
                        Xml = GetCoreFragment(update.Id)
                    });
                }
            }

            return changedUpdates;
        }
    }
}
