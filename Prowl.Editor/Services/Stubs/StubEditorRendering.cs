// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Editor.Services;

/// <summary>
/// Editor rendering service that renders the scene view and game view using the
/// engine's <see cref="DefaultRenderPipeline"/>. A hidden <see cref="Camera"/>
/// is maintained for the scene viewport so the full deferred pipeline (skybox,
/// lighting, post-processing) is used.
/// </summary>
public sealed class StubEditorRendering : IEditorRendering
{
    private RenderTexture? _sceneRT;
    private int _sceneW, _sceneH;

    private RenderTexture? _gameRT;
    private int _gameW, _gameH;

    // Hidden scene-view camera (lazy-init, added to the active scene)
    private GameObject? _editorCamGO;
    private Camera? _editorCam;

    public RenderTexture? SceneViewRT => _sceneRT;
    public RenderTexture? GameViewRT => _gameRT;

    public void RenderSceneView(Float3 cameraPosition, Quaternion cameraRotation,
        float fov, float nearClip, float farClip, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        Scene? scene = Scene.Current;
        if (scene == null) return;

        // ── Ensure render texture ──
        if (_sceneRT == null || _sceneW != width || _sceneH != height)
        {
            _sceneRT?.Dispose();
            _sceneRT = new RenderTexture(width, height, true, [TextureImageFormat.Color4b]);
            _sceneW = width;
            _sceneH = height;
        }

        // ── Ensure hidden editor camera exists and is in the current scene ──
        EnsureEditorCamera(scene);

        // ── Sync virtual SceneCamera state to the hidden Camera ──
        _editorCamGO!.Transform.Position = cameraPosition;
        _editorCamGO.Transform.Rotation = cameraRotation;

        _editorCam!.FieldOfView = fov;
        _editorCam.NearClipPlane = nearClip;
        _editorCam.FarClipPlane = farClip;
        _editorCam.ClearFlags = CameraClearFlags.Skybox;
        _editorCam.HDR = true;
        _editorCam.Target = _sceneRT;

        // ── Render through the engine's pipeline ──
        _editorCam.Render();

        _editorCam.Target = null;
    }

    public void RenderGameView(int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        Scene? scene = Scene.Current;
        if (scene == null) return;

        // ── Ensure render texture ──
        if (_gameRT == null || _gameW != width || _gameH != height)
        {
            _gameRT?.Dispose();
            _gameRT = new RenderTexture(width, height, true, [TextureImageFormat.Color4b]);
            _gameW = width;
            _gameH = height;
        }

        // Render the scene through its own cameras into the game RT
        scene.Render(_gameRT);
    }

    /// <summary>
    /// Creates (or re-parents) the hidden editor camera so it belongs to
    /// <paramref name="scene"/>. The GameObject is disabled so the normal
    /// <see cref="Scene.Render()"/> loop does not pick it up.
    /// </summary>
    private void EnsureEditorCamera(Scene scene)
    {
        // Create the hidden camera if it doesn't exist yet
        if (_editorCamGO == null || _editorCamGO.IsDisposed)
        {
            _editorCamGO = new GameObject("__EditorSceneCamera__")
            {
                HideFlags = HideFlags.HideAndDontSave,
                Enabled = false // stays out of ActiveObjects / Scene.Render()
            };

            _editorCam = _editorCamGO.AddComponent<Camera>();
            _editorCam.Depth = int.MinValue;
            _editorCam.Effects =
            [
                new FXAAEffect(),
                new TonemapperEffect(),
            ];
        }

        // Make sure the camera is registered in the current scene
        // (scene may have changed since last frame)
        if (_editorCamGO.Scene != scene)
        {
            scene.Add(_editorCamGO);
            // Re-disable after Add (Add may enable components on active scenes)
            _editorCamGO.Enabled = false;
        }
    }

    public void Dispose()
    {
        _sceneRT?.Dispose();
        _sceneRT = null;
        _gameRT?.Dispose();
        _gameRT = null;

        if (_editorCamGO != null && !_editorCamGO.IsDisposed)
        {
            _editorCamGO.Scene?.Remove(_editorCamGO);
            _editorCamGO.Dispose();
            _editorCamGO = null;
            _editorCam = null;
        }
    }
}
