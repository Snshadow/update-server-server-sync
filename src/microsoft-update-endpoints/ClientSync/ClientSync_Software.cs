// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Prerequisites;
using Microsoft.UpdateServices.WebServices.ClientSync;
using System;
using System.Collections.Generic;
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
        /// <param name="parameters">Sync parameters</param>
        /// <returns></returns>
        private Task<SyncInfo> DoSoftwareUpdateSync(SyncUpdateParameters parameters)
        {
            MetadataSourceLock.EnterReadLock();

            if (MetadataSource is null)
            {
                throw new FaultException();
            }

            // Get list of installed non leaf updates; these are prerequisites that the client has installed.
            // This list is used to check what updates are applicable to the client
            // We will not send updates that already appear on this list
            var installedNonLeafUpdatesGuids = GetInstalledNotLeafGuidsFromSyncParameters(parameters);

            // Other known updates to the client; we will not send any updates that are on this list
            var otherCachedUpdatesGuids = GetOtherCachedUpdateGuidsFromSyncParameters(parameters);

            // Initialize the response
            var response = new SyncInfo()
            {
                NewCookie = new Cookie() { Expiration = DateTime.Now.AddDays(5), EncryptedData = new byte[12] },
                DriverSyncNotNeeded = "false"
            };

            _categoryFilter = parameters.FilterCategoryIds?.ToList();

            var allApplicableUpdates = GetApplicableSoftwareUpdates(installedNonLeafUpdatesGuids);
            var clientCachedUpdateGuids = new HashSet<Guid>(installedNonLeafUpdatesGuids.Concat(otherCachedUpdatesGuids));
            var outOfScopeGuids = clientCachedUpdateGuids.Except(allApplicableUpdates.Select(u => u.Id.ID)).ToList();
            response.OutOfScopeRevisionIDs = outOfScopeGuids.Select(g => IdToRevisionMap[g]).ToArray();

            // Add root updates first; if any new root updates were added, return the response to the client immediatelly
            AddMissingRootUpdatesToSyncUpdatesResponse(installedNonLeafUpdatesGuids, otherCachedUpdatesGuids, response, allApplicableUpdates, out bool rootUpdatesAdded);
            if (!rootUpdatesAdded)
            {
                // No root updates were added; add non-leaf updates now
                AddMissingNonLeafUpdatesToSyncUpdatesResponse(installedNonLeafUpdatesGuids, otherCachedUpdatesGuids, response, allApplicableUpdates, out bool nonLeafUpdatesAdded);
                if (!nonLeafUpdatesAdded)
                {
                    // No leaf updates were added; add leaf bundle updates now
                    AddMissingBundleUpdatesToSyncUpdatesResponse(installedNonLeafUpdatesGuids, otherCachedUpdatesGuids, response, allApplicableUpdates, out bool bundleUpdatesAdded);
                    if (!bundleUpdatesAdded)
                    {
                        // No bundles were added; finally add leaf software updates
                        AddMissingSoftwareUpdatesToSyncUpdatesResponse(installedNonLeafUpdatesGuids, otherCachedUpdatesGuids, response, allApplicableUpdates, out var _);
                    }
                }
            }

            MetadataSourceLock.ExitReadLock();

            return Task.FromResult(response);
        }

        private List<MicrosoftUpdatePackage> GetApplicableSoftwareUpdates(List<Guid> installedNonLeaf)
        {
            var allApplicableUpdates = new List<MicrosoftUpdatePackage>();

            // Get root updates
            var rootUpdates = RootUpdates
                .Where(guid => IdToFullIdentityMap.ContainsKey(guid))
                .Select(guid => IdToFullIdentityMap[guid]) // Map the GUID to a fully qualified identity
                .Select(id => MetadataSource.GetPackage(id) as MicrosoftUpdatePackage); // Get the update by identity
            allApplicableUpdates.AddRange(rootUpdates);

            // Get non-leaf updates
            var nonLeafUpdates = NonLeafUpdates
                .Where(guid => IdToFullIdentityMap.ContainsKey(guid))
                .Select(guid => IdToFullIdentityMap[guid])
                .Select(id => MetadataSource.GetPackage(id) as MicrosoftUpdatePackage)
                .Where(u => u.IsApplicable(installedNonLeaf)); // Eliminate not applicable updates
            allApplicableUpdates.AddRange(nonLeafUpdates);

            // Get leaf updates (software and bundles)
            var leafUpdates = SoftwareLeafUpdateGuids
                .Where(guid => IdToFullIdentityMap.ContainsKey(guid))
                .Select(guid => IdToFullIdentityMap[guid])
                .Select(id => MetadataSource.GetPackage(id) as SoftwareUpdate)
                .Where(u => u.IsApplicable(installedNonLeaf));
            allApplicableUpdates.AddRange(leafUpdates);

            // Filter out unapproved software updates
            if (!AreAllSoftwareUpdatesApproved)
            {
                allApplicableUpdates.RemoveAll(u => u is SoftwareUpdate && !ApprovedSoftwareUpdates.Contains(u.Id));
            }

            if (_categoryFilter is not null && _categoryFilter.Count > 0)
            {
                var categoryGuids = new HashSet<Guid>(_categoryFilter.Select(c => c.Id));
                allApplicableUpdates.RemoveAll(u =>
                {
                    if (u.Prerequisites is not null)
                    {
                        foreach (var prereq in u.Prerequisites.OfType<AtLeastOne>().Where(p => p.IsCategory))
                        {
                            if (prereq.Simple.Any(s => categoryGuids.Contains(s.UpdateId)))
                            {
                                return false;
                            }
                        }
                    }

                    return true;
                });
            }

            return allApplicableUpdates;
        }

        /// <summary>
        /// For a client request, gathers applicable root updates (detectoids, categories, etc.) that the client does not have yet
        /// </summary>
        /// <param name="installedNonLeaf">List of non leaf updates installed on the client</param>
        /// <param name="otherCached">List of other updates known to the client</param>
        /// <param name="response">The response  to append new updates to</param>
        /// <param name="allApplicableUpdates">A list of all applicable updates for the client.</param>
        /// <param name="updatesAdded">On return: true of updates were added to the response, false otherwise</param>
        private void AddMissingRootUpdatesToSyncUpdatesResponse(List<Guid> installedNonLeaf, List<Guid> otherCached, SyncInfo response, List<MicrosoftUpdatePackage> allApplicableUpdates, out bool updatesAdded)
        {
            var clientCachedUpdateGuids = new HashSet<Guid>(installedNonLeaf.Concat(otherCached));
            var missingRootUpdates = allApplicableUpdates
                .Where(u => RootUpdates.Contains(u.Id.ID) && !clientCachedUpdateGuids.Contains(u.Id.ID))
                .ToList();

            if (missingRootUpdates.Count > 0)
            {
                response.NewUpdates = CreateUpdateInfoListFromNonLeafUpdates(missingRootUpdates.Take(MaxUpdatesInResponse + 1)
                .ToList())
                .ToArray();
                response.Truncated = missingRootUpdates.Count > MaxUpdatesInResponse;
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
        /// <param name="installedNonLeaf">List of non leaf updates installed on the client</param>
        /// <param name="otherCached">List of other updates known to the client</param>
        /// <param name="response">The response  to append new updates to</param>
        /// <param name="allApplicableUpdates">A list of all applicable updates for the client.</param>
        /// <param name="updatesAdded">On return: true of updates were added to the response, false otherwise</param>
        private void AddMissingNonLeafUpdatesToSyncUpdatesResponse(List<Guid> installedNonLeaf, List<Guid> otherCached, SyncInfo response, List<MicrosoftUpdatePackage> allApplicableUpdates, out bool updatesAdded)
        {
            var clientCachedUpdateGuids = new HashSet<Guid>(installedNonLeaf.Concat(otherCached));
            var missingNonLeafs = allApplicableUpdates
                .Where(u => NonLeafUpdates.Contains(u.Id.ID) && !clientCachedUpdateGuids.Contains(u.Id.ID))
                .ToList();

            if (missingNonLeafs.Count > 0)
            {
                response.NewUpdates = CreateUpdateInfoListFromNonLeafUpdates(missingNonLeafs.Take(MaxUpdatesInResponse + 1)
                .ToList())
                .ToArray();
                response.Truncated = missingNonLeafs.Count > MaxUpdatesInResponse;
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
        /// <param name="installedNonLeaf">List of non leaf updates installed on the client</param>
        /// <param name="otherCached">List of other updates known to the client</param>
        /// <param name="response">The response  to append new updates to</param>
        /// <param name="allApplicableUpdates">A list of all applicable updates for the client.</param>
        /// <param name="updatesAdded">On return: true of updates were added to the response, false otherwise</param>
        private void AddMissingBundleUpdatesToSyncUpdatesResponse(List<Guid> installedNonLeaf, List<Guid> otherCached, SyncInfo response, List<MicrosoftUpdatePackage> allApplicableUpdates, out bool updatesAdded)
        {
            var clientCachedUpdateGuids = new HashSet<Guid>(installedNonLeaf.Concat(otherCached));
            var allMissingBundles = allApplicableUpdates
                .OfType<SoftwareUpdate>()
                .Where(u => (u.BundledWithUpdates?.Count ?? 0) > 0 && !clientCachedUpdateGuids.Contains(u.Id.ID)) // Remove not bundles
                .ToList();

            if (allMissingBundles.Count > 0)
            {
                response.NewUpdates = CreateUpdateInfoListFromSoftwareUpdate(allMissingBundles.Take(MaxUpdatesInResponse + 1)
                .ToList())
                .ToArray();
                response.Truncated = allMissingBundles.Count > MaxUpdatesInResponse;
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
        /// <param name="installedNonLeaf">List of non leaf updates installed on the client</param>
        /// <param name="otherCached">List of other updates known to the client</param>
        /// <param name="response">The response  to append new updates to</param>
        /// <param name="allApplicableUpdates">A list of all applicable updates for the client.</param>
        /// <param name="updatesAdded">On return: true of updates were added to the response, false otherwise</param>
        private void AddMissingSoftwareUpdatesToSyncUpdatesResponse(List<Guid> installedNonLeaf, List<Guid> otherCached, SyncInfo response, List<MicrosoftUpdatePackage> allApplicableUpdates, out bool updatesAdded)
        {
            var clientCachedUpdateGuids = new HashSet<Guid>(installedNonLeaf.Concat(otherCached));
            var allMissingApplicableUpdates = allApplicableUpdates
                .OfType<SoftwareUpdate>()
                .Where(u => (u.BundledWithUpdates?.Count ?? 0) > 0 && !clientCachedUpdateGuids.Contains(u.Id.ID))
                .ToList();

            response.Truncated = allMissingApplicableUpdates.Count > MaxUpdatesInResponse;

            if (allMissingApplicableUpdates.Count > 0)
            {
                response.NewUpdates = CreateUpdateInfoListFromSoftwareUpdate(allMissingApplicableUpdates.Take(MaxUpdatesInResponse)
                .ToList())
                .ToArray();
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
                // Get the update index; it will be sent to the client
                var revision = IdToRevisionMap[softwareUpdates[i].Id.ID];

                // Generate the core XML fragment
                var identity = softwareUpdates[i].Id;
                var coreXml = GetCoreFragment(identity);

                var isBundle = softwareUpdates[i].BundledUpdates is not null && softwareUpdates[i].BundledUpdates.Count > 0;
                var isBundled = softwareUpdates[i].BundledWithUpdates is not null && softwareUpdates[i].BundledWithUpdates.Count > 0;

                // Add the update information to the return array
                returnList.Add(new UpdateInfo()
                {
                    Deployment = new Deployment()
                    {
                        // Action is Install for bundles of updates that are not part of a bundle
                        // Action is Bundle for updates that are part of a bundle
                        Action = (isBundle || !isBundled) ? DeploymentAction.Install : DeploymentAction.Bundle,
                        ID = isBundle ? 20000 : (isBundled ? 20001 : 20002),
                        AutoDownload = "0",
                        AutoSelect = "0",
                        SupersedenceBehavior = "0",
                        IsAssigned = true,
                        LastChangeTime = "2019-08-06"
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
                var revision = IdToRevisionMap[nonLeafUpdates[i].Id.ID];

                var identity = nonLeafUpdates[i].Id;

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
                        LastChangeTime = "2019-08-06"
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
    }
}
