// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Prerequisites
{
    class PrerequisitesAnalyzer
    {
        public static bool IsApplicable(MicrosoftUpdatePackage update, List<Guid> installedPrerequisites)
        {
            if (update.Prerequisites is null)
            {
                return true;
            }

            foreach (var prereq in update.Prerequisites)
            {
                if (prereq is Simple simple)
                {
                    if (!installedPrerequisites.Contains(simple.UpdateId))
                    {
                        return false;
                    }
                }
                else if (prereq is AtLeastOne atLeastOne)
                {
                    var hasAtLeastOne = false;
                    foreach (var atLeastOnePrereq in atLeastOne.Simple)
                    {
                        if (installedPrerequisites.Contains(atLeastOnePrereq.UpdateId))
                        {
                            hasAtLeastOne = true;
                            break;
                        }
                    }

                    if (!hasAtLeastOne)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
