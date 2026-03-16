// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

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
    void RenderSceneView(Float3 cameraPosition, Quaternion cameraRotation,
        float fov, float nearClip, float farClip, int width, int height);

    /// <summary> The current scene-view render texture (null until first render). </summary>
    RenderTexture? SceneViewRT { get; }

    /// <summary>
    /// Renders the game view from the scene's game camera(s) into an internal render texture.
    /// Called each frame during play mode.
    /// </summary>
    void RenderGameView(int width, int height);

    /// <summary> The current game-view render texture (null until first render). </summary>
    RenderTexture? GameViewRT { get; }
}
