// Copyright (c) Snshadow. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.ObjectModel;
using Microsoft.PackageGraph.Storage;
using Microsoft.PackageGraph.Utilitites.Upsync.Commands;
using Microsoft.UpdateServices.WebServices.ClientSync;
using System;

namespace Microsoft.PackageGraph.Utilitites.Upsync
{
    class CleanupStore
    {
        public static void Cleanup(CleanupCommand.Settings options)
        {
            var store = MetadataStoreCreator.OpenFromOptions(options);
            if (store is not (IDeploySyncStore deploymentStore and IMetadataStore metadataStore))
            {
                return;
            }


            // TODO IContentStoreOption for all content using commands
            var contentStore = ContentStoreCreator.GetContentStoreFromOptions(options);

            using (store)
            {
                var unapprovedUpdates = deploymentStore.GetApprovedRevisionIds();
            }
        }
    }
}
