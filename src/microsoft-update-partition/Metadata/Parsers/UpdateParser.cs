// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Globalization;
using System.Xml;
using System.Xml.XPath;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Parsers
{
    abstract class UpdateParser
    {
        private static string GetLocalizedProperty(XPathNavigator metadataNavigator, XmlNamespaceManager namespaceManager, string property, string locale)
        {
            var propertyQuery = metadataNavigator.Compile($"upd:Update/upd:LocalizedPropertiesCollection/upd:LocalizedProperties[upd:Language='{locale}']/{property}");
            propertyQuery.SetContext(namespaceManager);

            var result = metadataNavigator.Evaluate(propertyQuery) as XPathNodeIterator;
            if (result.MoveNext())
            {
                return result.Current.Value;
            }
            else if (locale != "en")
            {
                // Fallback to 'en'
                propertyQuery = metadataNavigator.Compile($"upd:Update/upd:LocalizedPropertiesCollection/upd:LocalizedProperties[upd:Language='en']/{property}");
                propertyQuery.SetContext(namespaceManager);
                result = metadataNavigator.Evaluate(propertyQuery) as XPathNodeIterator;
                if (result.MoveNext())
                {
                    return result.Current.Value;
                }
            }

            return null;
        }

        public static DateTime GetCreationDate(XPathNavigator metadataNavigator, XmlNamespaceManager namespaceManager)
        {
            XPathExpression creationDateQuery = metadataNavigator.Compile("upd:Update/upd:Properties/@CreationDate");
            creationDateQuery.SetContext(namespaceManager);

            var result = metadataNavigator.Evaluate(creationDateQuery) as XPathNodeIterator;

            if (!result.MoveNext())
            {
                throw new Exception("Invalid XML");
            }

            return DateTime.Parse(result.Current.Value, DateTimeFormatInfo.InvariantInfo, DateTimeStyles.RoundtripKind);
        }

        public static string GetDescription(XPathNavigator metadataNavigator, XmlNamespaceManager namespaceManager, string locale)
        {
            return GetLocalizedProperty(metadataNavigator, namespaceManager, "upd:Description", locale);
        }

        public static string GetTitle(XPathNavigator metadataNavigator, XmlNamespaceManager namespaceManager, string locale)
        {
            var title = GetLocalizedProperty(metadataNavigator, namespaceManager, "upd:Title", locale) ?? throw new Exception("Invalid XML");
            return title;
        }

        public static string GetPublicationState(XPathNavigator metadataNavigator, XmlNamespaceManager namespaceManager)
        {
            var publicationStateQuery = metadataNavigator.Compile("upd:Update/upd:Properties/@PublicationState");
            publicationStateQuery.SetContext(namespaceManager);

            var result = metadataNavigator.Evaluate(publicationStateQuery) as XPathNodeIterator;

            if (!result.MoveNext())
            {
                throw new Exception("Invalid XML");
            }

            return result.Current.Value;
        }

        public static string GetUpdateType(XPathNavigator metadataNavigator, XmlNamespaceManager namespaceManager)
        {
            var updateTypeQuery = metadataNavigator.Compile("upd:Update/upd:Properties/@UpdateType");
            updateTypeQuery.SetContext(namespaceManager);

            var result = metadataNavigator.Evaluate(updateTypeQuery) as XPathNodeIterator;

            if (!result.MoveNext())
            {
                throw new Exception("Invalid XML");
            }

            return result.Current.Value;
        }

        public static MicrosoftUpdatePackageIdentity GetUpdateId(XPathNavigator metadataNavigator, XmlNamespaceManager namespaceManager)
        {
            XPathExpression updateIdQuery = metadataNavigator.Compile("upd:Update/upd:UpdateIdentity/@UpdateID");
            XPathExpression revisionQuery = metadataNavigator.Compile("upd:Update/upd:UpdateIdentity/@RevisionNumber");
            updateIdQuery.SetContext(namespaceManager);
            revisionQuery.SetContext(namespaceManager);

            var idResult = metadataNavigator.Evaluate(updateIdQuery) as XPathNodeIterator;
            var revisionResult = metadataNavigator.Evaluate(revisionQuery) as XPathNodeIterator;

            if (!revisionResult.MoveNext() || !idResult.MoveNext())
            {
                throw new Exception("Invalid XML");
            }

            return new MicrosoftUpdatePackageIdentity(Guid.Parse(idResult.Current.Value), int.Parse(revisionResult.Current.Value));
        }

        public static string GetCategory(XPathNavigator metadataNavigator, XmlNamespaceManager namespaceManager)
        {
            XPathExpression categoryQuery = metadataNavigator.Compile("upd:Update/upd:HandlerSpecificData/cat:CategoryInformation/@CategoryType");
            categoryQuery.SetContext(namespaceManager);

            var result = metadataNavigator.Evaluate(categoryQuery) as XPathNodeIterator;

            if (!result.MoveNext())
            {
                throw new Exception("Invalid XML");
            }

            return result.Current.Value;
        }
    }
}
