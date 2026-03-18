// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Editor.Rendering;

/// <summary>
/// Rendering modes available in the editor Scene View.
/// </summary>
public enum SceneViewMode
{
    /// <summary>Default fully-lit rendering.</summary>
    Lit,

    /// <summary>Wireframe overlay showing triangle edges.</summary>
    Wireframe,

    /// <summary>Visualize the depth buffer (near = white, far = black).</summary>
    Depth,

    /// <summary>Overdraw heat-map — brighter areas have more overlapping fragments.</summary>
    Overdraw,

    /// <summary>Debug the shadow atlas depth texture for the first directional light.</summary>
    ShadowAtlas,
}
