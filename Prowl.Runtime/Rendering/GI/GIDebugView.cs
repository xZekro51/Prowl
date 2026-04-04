// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.Rendering.GI;

/// <summary>
/// Debug visualization modes for Global Illumination.
/// Accessible from the Scene panel's view options dropdown.
/// </summary>
public enum GIDebugMode
{
    /// <summary>No debug visualization — normal rendering.</summary>
    None,

    /// <summary>Only the GI contribution (no direct light, no ambient).</summary>
    IndirectOnly,

    /// <summary>Voxel grid as colored cubes (VoxelGI only).</summary>
    VoxelGrid,

    /// <summary>2D slice through the SDF cascade (SDFGI only).</summary>
    SDFSlice,

    /// <summary>Probe positions as colored spheres (SDFGI only).</summary>
    ProbeGrid,
}

/// <summary>
/// Provides debug draw modes for GI visualization.
/// </summary>
public static class GIDebugView
{
    /// <summary>
    /// Current debug visualization mode. Set from the editor's Scene panel
    /// view options dropdown.
    /// </summary>
    public static GIDebugMode ActiveMode { get; set; } = GIDebugMode.None;
}
