// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.PackageGraph.Storage
{
    /// <summary>
    /// Defines a factory for creating content files.
    /// </summary>
    /// <typeparam name="T">The type of content file to create.</typeparam>
    public interface IFileFactory<T>
    {
        /// <summary>
        /// Creates a new content file.
        /// </summary>
        /// <param name="fileName">The name of the file.</param>
        /// <returns>A new content file.</returns>
        T Create(string fileName);
    }
}