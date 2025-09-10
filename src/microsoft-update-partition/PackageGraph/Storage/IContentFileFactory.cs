// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.ObjectModel;

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Defines a factory for creating content files.
    /// </summary>
    public interface IContentFileFactory
    {
        /// <summary>
        /// Creates a new content file.
        /// </summary>
        /// <param name="fileName">The name of the file.</param>
        /// <returns>A new content file.</returns>
        IContentFile Create(string fileName);
    }
}