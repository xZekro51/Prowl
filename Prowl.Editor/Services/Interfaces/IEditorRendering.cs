// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Rendering;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Editor.Services;

/// <summary>
/// Abstracts scene-view and game-view rendering so the editor is decoupled from the
/// concrete render pipeline. Swap this interface to port to another engine.
/// </summary>
public interface IEditorRendering : IDisposable
{
    /// <summary>
    /// Renders the scene from the given virtual camera into an internal render texture.
    /// Called each frame during the render phase.
    /// </summary>
    /// <param name="viewMode">Debug visualization mode for the scene view.</param>
    void RenderSceneView(Float3 cameraPosition, Quaternion cameraRotation,
        float fov, float nearClip, float farClip, int width, int height,
        SceneViewMode viewMode = SceneViewMode.Lit);

    /// <summary> The current scene-view render texture (null until first render). </summary>
    RenderTexture? SceneViewRT { get; }

    /// <summary>
    /// Renders the game view from the scene's game camera(s) into an internal render texture.
    /// Called each frame during play mode.
    /// </summary>
    void RenderGameView(int width, int height);

    /// <summary> The current game-view render texture (null until first render). </summary>
    RenderTexture? GameViewRT { get; }

    /// <summary>
    /// Renders a screen-space outline for the selected objects as a post-process
    /// effect on the scene-view render texture. Uses stencil/object-ID buffer to
    /// detect selected object fragments, then applies an edge-detection filter.
    /// <para>
    /// Implementations that do not support post-processing should fall back to a
    /// no-op; the <see cref="ScenePanel"/> already draws a wireframe bounding-box
    /// outline via ImGui's DrawList as a temporary measure.
    /// </para>
    /// </summary>
    /// <param name="selectedObjects">The GameObjects that should be outlined.</param>
    void RenderSelectionOutline(IReadOnlyList<GameObject> selectedObjects) { }
}
