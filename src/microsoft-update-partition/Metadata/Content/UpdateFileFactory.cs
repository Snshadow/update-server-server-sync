// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.PackageGraph.Storage;

namespace Microsoft.PackageGraph.MicrosoftUpdate.Metadata.Content
{
    /// <summary>
    /// Factory for creating <see cref="UpdateFile"/> objects.
    /// </summary>
    public class UpdateFileFactory : IFileFactory<UpdateFile>
    {
        /// <summary>
        /// Creates a new <see cref="UpdateFile"/> object.
        /// </summary>
        /// <param name="fileName">The name of the file.</param>
        /// <returns>A new <see cref="UpdateFile"/> object.</returns>
        public UpdateFile Create(string fileName)
        {
            return new UpdateFile { FileName = fileName };
        }
    }
}