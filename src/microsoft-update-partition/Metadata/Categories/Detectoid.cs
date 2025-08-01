// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.Storage;
using System.Xml;
using System.Xml.XPath;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Metadata
{
    /// <summary>
    /// Represents a detectoid in the Microsoft Update catalog. Most software or driver update have one or more corresponding detectoids that
    /// check applicability of an update to a device.
    /// </summary>
    public class DetectoidCategory : MicrosoftUpdatePackage
    {
        internal DetectoidCategory(
            MicrosoftUpdatePackageIdentity id,
            XPathNavigator metadataNavigator,
            XmlNamespaceManager namespaceManager,
            string locale) : base(id, metadataNavigator, namespaceManager, locale)
        {
        }

        internal DetectoidCategory(
            MicrosoftUpdatePackageIdentity id,
            IMetadataLookup metadataLookup,
            IMetadataSource metadataSource,
            string locale) : base(id, metadataLookup, metadataSource, locale)
        {
        }

        internal override void LoadNonIndexedMetadata(XPathNavigator metadataNavigator, XmlNamespaceManager namespaceManager)
        {
        }
    }
}
