// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using Prowl.Runtime.Resources;

namespace Prowl.Runtime.Rendering.Compute;

/// <summary>
/// Loads embedded compute GLSL sources with #include preprocessing.
/// Reuses the engine's EmbeddedResources for file resolution.
/// </summary>
public static class ComputeShaderLoader
{
    private static readonly Regex s_includeRegex =
        new(@"^\s*#include\s+""(.+?)""\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private const int MaxIncludeDepth = 16;

    /// <summary>
    /// Loads a compute shader source from embedded resources and resolves #include directives.
    /// </summary>
    /// <param name="resourcePath">
    /// Path relative to Assets/Defaults, e.g. "Compute/VoxelGI_Clear" (no extension).
    /// The ".glsl" extension is appended automatically.
    /// </param>
    public static string Load(string resourcePath)
    {
        string fullPath = $"Assets/Defaults/{resourcePath}.glsl";

        if (!EmbeddedResources.Exists(fullPath))
        {
            Debug.LogError($"ComputeShaderLoader: Resource not found: {fullPath}");
            return string.Empty;
        }

        string source = EmbeddedResources.ReadAllText(fullPath);
        return ResolveIncludes(source, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static string ResolveIncludes(string source, int depth, HashSet<string> visited)
    {
        if (depth > MaxIncludeDepth)
        {
            Debug.LogError("ComputeShaderLoader: Maximum include depth exceeded (possible circular include).");
            return source;
        }

        return s_includeRegex.Replace(source, match =>
        {
            string includeName = match.Groups[1].Value;

            // Prevent circular includes
            if (!visited.Add(includeName))
                return string.Empty;

            // Try resolving from Assets/Defaults (matches DefaultShaderInclude names)
            string includePath = $"Assets/Defaults/{includeName}.glsl";

            if (!EmbeddedResources.Exists(includePath))
            {
                Debug.LogError($"ComputeShaderLoader: Include not found: {includeName}");
                visited.Remove(includeName);
                return string.Empty;
            }

            string includeSource = EmbeddedResources.ReadAllText(includePath);
            string resolved = ResolveIncludes(includeSource, depth + 1, visited);
            visited.Remove(includeName);
            return resolved;
        });
    }
}
