// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.MicrosoftUpdate.Metadata;
using Microsoft.PackageGraph.Storage;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Prerequisites
{
    /// <summary>
    /// Models the prerequisite graph for all packages contained within a metadata store
    /// </summary>
    public class PrerequisitesGraph
    {
        private readonly Dictionary<Guid, PrerequisiteGraphNode> Graph;

        private PrerequisitesGraph(Dictionary<Guid, PrerequisiteGraphNode> graph)
        {
            Graph = graph;
        }

        /// <summary>
        /// Creates a prerequisite graph for all the packages contained in the specified store
        /// </summary>
        /// <param name="source">Package metadata store</param>
        /// <returns></returns>
        /// <exception cref="Exception">If an unknown prerequisite type is encountered</exception>
        public static PrerequisitesGraph FromIndexedPackageSource(IMetadataStore source)
        {
            Dictionary<Guid, PrerequisiteGraphNode> graph = [];

            var packages = source.OfType<MicrosoftUpdatePackage>();

            foreach (var package in packages)
            {
                if (package.Prerequisites is { Count: > 0 } prerequisites)
                {
                    var updateGuid = package.Id.ID;
                    if (!graph.TryGetValue(updateGuid, out PrerequisiteGraphNode updateNode))
                    {
                        updateNode = new PrerequisiteGraphNode(updateGuid);
                        graph.Add(updateGuid, updateNode);
                    }

                    var flatListPrerequisites = prerequisites.SelectMany(p =>
                    {
                        if (p is Simple simple)
                        {
                            return [simple.UpdateId];
                        }
                        else if (p is AtLeastOne atLeastOne)
                        {
                            return atLeastOne.Simple.Select(s => s.UpdateId);
                        }
                        else
                        {
                            throw new Exception("Unknown prerequisite type");
                        }
                    });

                    foreach (var prerequisite in flatListPrerequisites)
                    {
                        if (!graph.TryGetValue(prerequisite, out PrerequisiteGraphNode prerequisiteNode))
                        {
                            prerequisiteNode = new PrerequisiteGraphNode(prerequisite);
                            graph.Add(prerequisite, prerequisiteNode);
                        }

                        updateNode.Prerequisites.TryAdd(prerequisite, prerequisiteNode);
                        prerequisiteNode.Dependents.TryAdd(updateGuid, updateNode);
                    }
                }
            }

            return new PrerequisitesGraph(graph);
        }

        /// <summary>
        /// Gets updates that have prerequisites but no other update depends on them
        /// </summary>
        /// <returns>List of GUIDS of leaf updates</returns>
        public IEnumerable<Guid> GetLeafUpdates() => Graph.Values.Where(node => node.Dependents.Count == 0).Select(node => node.UpdateId);

        /// <summary>
        /// Gets updates that have prerequisites and also have other updates depend on them
        /// </summary>
        /// <returns>List of GUIDS of non leaf updates</returns>
        public IEnumerable<Guid> GetNonLeafUpdates() => Graph.Values.Where(node => node.Dependents.Count > 0 && node.Prerequisites.Count > 0).Select(node => node.UpdateId);

        /// <summary>
        /// Get updates with no prerequisites
        /// </summary>
        /// <returns>List of GUIDS of root updates</returns>
        public IEnumerable<Guid> GetRootUpdates() => Graph.Values.Where(node => node.Prerequisites.Count == 0).Select(node => node.UpdateId);
    }
}
