// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.Utilitites.Upsync.Commands;
using Microsoft.UpdateServices.WebServices.ClientSync;
using System;
using System.Linq;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    class ManageDeploy
    {
        public static void ApproveUpdates(ApproveUpdateCommand.Settings options)
        {
            var store = MetadataStoreCreator.OpenFromOptions(options);
            if (store is not (IDeploySyncStore deploymentStore and IMetadataStore metadataStore))
            {
                return;
            }

            using (store)
            {
                var filter = FilterBuilder.MicrosoftUpdateFilterFromCommandLine(options);
                if (filter is null)
                {
                    return;
                }

                var updatesToApprove = filter.Apply(store).ToList();

                if (updatesToApprove.Count == 0)
                {
                    Console.WriteLine("No updates matched the specified filter.");
                    return;
                }

                foreach (var update in updatesToApprove)
                {
                    var deployment = new DeploymentEntry()
                    {
                        RevisionId = metadataStore.GetPackageIndex(update.Id),
                        Action = DeploymentAction.Install,
                        LastChangeTime = DateTime.UtcNow,
                    };

                    deploymentStore.SaveDeployment(deployment);
                    Console.WriteLine($"Approved update {update.Id}");
                }
            }
        }

        public static void UnapproveUpdates(UnapproveUpdateCommand.Settings options)
        {
            var store = MetadataStoreCreator.OpenFromOptions(options);
            if (store is not (IDeploySyncStore deploymentStore and IMetadataStore metadataStore))
            {
                return;
            }

            using (store)
            {
                var filter = FilterBuilder.MicrosoftUpdateFilterFromCommandLine(options);
                if (filter is null)
                {
                    return;
                }

                var updatesToUnapprove = filter.Apply(store).ToList();

                if (updatesToUnapprove.Count == 0)
                {
                    Console.WriteLine("No updates matched the specified filter.");
                    return;
                }

                foreach (var update in updatesToUnapprove)
                {
                    var deployment = new DeploymentEntry()
                    {
                        RevisionId = metadataStore.GetPackageIndex(update.Id),
                        Action = DeploymentAction.PreDeploymentCheck,
                        LastChangeTime = DateTime.UtcNow,
                    };

                    deploymentStore.SaveDeployment(deployment);
                    Console.WriteLine($"Unapproved update {update.Id}");
                }
            }
        }
    }
}
