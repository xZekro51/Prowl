// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Editor.Build;

/// <summary>
/// Identifies the platform a build targets.
/// Each value maps to a Runtime Identifier (RID) used by
/// <c>dotnet publish</c> during the build pipeline.
/// </summary>
public enum BuildTarget
{
    /// <summary>Windows x64.</summary>
    Windows,

    /// <summary>Linux x64.</summary>
    Linux,
}
