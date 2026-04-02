// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Rendering;
using Prowl.PaperUI;
using Prowl.Runtime;
using Prowl.Runtime.GUI;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Runtime.UI;
using Prowl.Vector;

using Silk.NET.OpenGL;

using Material = Prowl.Runtime.Resources.Material;
using Mesh = Prowl.Runtime.Resources.Mesh;
using Shader = Prowl.Runtime.Resources.Shader;

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

    // Lazily-loaded debug view material (DebugView.shader)
    private Material? _debugViewMat;

    // Selection outline effect
    private readonly OutlineEffect _outlineEffect = new();

    // Paper UI rendering for game view
    private PaperRenderer? _gamePaperRenderer;
    private Paper? _gamePaper;
    private int _gamePaperW, _gamePaperH;

    public RenderTexture? SceneViewRT => _sceneRT;
    public RenderTexture? GameViewRT => _gameRT;

    public void RenderSceneView(Float3 cameraPosition, Quaternion cameraRotation,
        float fov, float nearClip, float farClip, int width, int height,
        SceneViewMode viewMode = SceneViewMode.Lit)
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

        // ── Render based on the selected view mode ──
        switch (viewMode)
        {
            case SceneViewMode.Lit:
                _editorCam.Render();
                break;

            case SceneViewMode.Wireframe:
                RenderWireframe();
                break;

            case SceneViewMode.Depth:
                RenderDepthView(width, height);
                break;

            case SceneViewMode.Overdraw:
                RenderOverdrawView(scene);
                break;

            case SceneViewMode.ShadowAtlas:
                RenderShadowAtlasView(scene);
                break;

            default:
                _editorCam.Render();
                break;
        }

        // Screen-space Canvas overlays are intentionally skipped in the
        // Scene View because they obscure the 3D content and make editing
        // difficult. They are only rendered into the Game View.

        _editorCam.Target = null;
    }

    // ── Wireframe ──────────────────────────────────────────────
    // Sets GL polygon mode to Line around the normal render call.

    private void RenderWireframe()
    {
        if (Graphics.IsOpenGL)
            Graphics.GL.PolygonMode(GLEnum.FrontAndBack, GLEnum.Line);

        _editorCam!.Render();

        if (Graphics.IsOpenGL)
            Graphics.GL.PolygonMode(GLEnum.FrontAndBack, GLEnum.Fill);
    }

    // ── Depth ──────────────────────────────────────────────────
    // Renders normally (to populate depth), then blits a linearised
    // depth visualisation onto the scene RT.

    private void RenderDepthView(int width, int height)
    {
        // Render the scene so the depth buffer is populated
        _editorCam!.Render();

        EnsureDebugMaterial();

        // We cannot read _sceneRT.InternalDepth while _sceneRT is bound for
        // writing (feedback loop), so blit via a temporary RT.
        RenderTexture tempRT = RenderTexture.GetTemporaryRT(width, height, false,
            [TextureImageFormat.Color4b]);

        _debugViewMat!.SetTexture("_MainTex", _sceneRT!.InternalDepth);
        RenderPipeline.Blit(tempRT, _debugViewMat, 0, false, true); // pass 0 = DepthVisualize

        // Copy the visualisation back into the scene RT colour attachment
        RenderPipeline.Blit(tempRT, _sceneRT);

        RenderTexture.ReleaseTemporaryRT(tempRT);
    }

    // ── Overdraw ───────────────────────────────────────────────
    // After a normal render (to set up camera matrices), clears the
    // scene RT to black and re-draws every renderable with additive
    // blending and a flat constant colour.  Brighter = more overdraw.

    private void RenderOverdrawView(Scene scene)
    {
        // Normal render first — populates global uniforms / camera matrices
        _editorCam!.Render();

        EnsureDebugMaterial();

        // Clear the scene RT to black (colour only — keep depth from render)
        Graphics.BindFramebuffer(_sceneRT!.frameBuffer);
        Graphics.Clear(0, 0, 0, 1, ClearFlags.Color);

        IReadOnlyList<IRenderable> renderables = scene.Renderables;
        if (renderables.Count == 0) return;

        Float3 camPos = _editorCamGO!.Transform.Position;
        Float3 camFwd = _editorCamGO.Transform.Forward;
        Float3 camUp = _editorCamGO.Transform.Up;
        Float3 camRight = _editorCamGO.Transform.Right;
        var viewer = new ViewerData(camPos, camFwd, camRight, camUp);

        // For each renderable, set the model matrix and draw with the
        // additive overdraw pass (pass 1 of DebugView.shader).
        foreach (IRenderable renderable in renderables)
        {
            renderable.GetCullingData(out bool isRenderable, out _);
            if (!isRenderable) continue;

            renderable.GetRenderingData(viewer, out _, out Mesh mesh,
                out Float4x4 model, out InstanceData[]? instanceData);

            if (mesh == null || mesh.VertexCount <= 0) continue;

            // Skip instanced renderables for simplicity
            if (instanceData != null && instanceData.Length > 0) continue;

            PropertyState.SetGlobalMatrix("prowl_ObjectToWorld", model);
            PropertyState.SetGlobalMatrix("prowl_WorldToObject", model.Invert());

            RenderPipeline.DrawMeshNow(mesh, _debugViewMat!, 1); // pass 1 = Overdraw
        }
    }

    // ── Shadow Atlas ───────────────────────────────────────────
    // Renders the scene (to populate the shadow atlas), then blits
    // the atlas depth texture to the scene RT.  If no directional
    // light exists the view is cleared to black.

    private void RenderShadowAtlasView(Scene scene)
    {
        // Render the scene so the shadow atlas is populated
        _editorCam!.Render();

        EnsureDebugMaterial();

        RenderTexture? atlas = ShadowAtlas.GetAtlas();
        bool hasDirectionalLight = false;

        foreach (IRenderableLight light in scene.Lights)
        {
            if (light.GetLightType() == LightType.Directional)
            {
                hasDirectionalLight = true;
                break;
            }
        }

        if (atlas != null && atlas.InternalDepth != null && hasDirectionalLight)
        {
            // Blit shadow atlas depth → scene RT using the linearised
            // depth pass.  The atlas is a *different* FBO so no feedback.
            _debugViewMat!.SetTexture("_MainTex", atlas.InternalDepth);
            RenderPipeline.Blit(_sceneRT!, _debugViewMat, 0, false, false); // pass 0 = DepthVisualize
        }
        else
        {
            // No directional light — clear to black
            Graphics.BindFramebuffer(_sceneRT!.frameBuffer);
            Graphics.Clear(0, 0, 0, 1, ClearFlags.Color);
        }
    }

    // ── Debug material helper ──────────────────────────────────

    private void EnsureDebugMaterial()
    {
        if (_debugViewMat == null || _debugViewMat.Shader.IsNotValid())
        {
            _debugViewMat = new Material(Shader.LoadDefault(DefaultShader.DebugView));
        }
    }

    // ── Game view (unchanged) ──────────────────────────────────

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

        // ── Render screen-space UI (Canvas) into the game RT ──
        RenderOnGuiIntoRT(scene, _gameRT, width, height,
            ref _gamePaperRenderer, ref _gamePaper,
            ref _gamePaperW, ref _gamePaperH);
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

    public void RenderSelectionOutline(IReadOnlyList<GameObject> selectedObjects)
    {
        if (_sceneRT == null || selectedObjects == null || selectedObjects.Count == 0)
            return;

        _outlineEffect.Render(selectedObjects, _sceneRT);
    }

    public void Dispose()
    {
        _sceneRT?.Dispose();
        _sceneRT = null;
        _gameRT?.Dispose();
        _gameRT = null;

        _gamePaperRenderer?.Dispose();
        _gamePaperRenderer = null;
        _gamePaper = null;

        if (_editorCamGO != null && !_editorCamGO.IsDisposed)
        {
            _editorCamGO.Scene?.Remove(_editorCamGO);
            _editorCamGO.Dispose();
            _editorCamGO = null;
            _editorCam = null;
        }
    }

    /// <summary>
    /// Runs a Paper / OnGui pass into the given render texture so that
    /// screen-space <see cref="Canvas"/> elements are composited on top
    /// of the 3D scene.
    /// </summary>
    private static void RenderOnGuiIntoRT(
        Scene scene, RenderTexture rt, int width, int height,
        ref PaperRenderer? paperRenderer, ref Paper? paper,
        ref int paperW, ref int paperH)
    {
        // Lazily create or resize the Paper renderer / instance
        if (paperRenderer == null || paperW != width || paperH != height)
        {
            paperRenderer?.Dispose();
            paperRenderer = new PaperRenderer();
            paperRenderer.Initialize(width, height);
            paper = new Paper(paperRenderer, width, height, new Prowl.Quill.FontAtlasSettings());
            paperW = width;
            paperH = height;
        }

        // Tell Canvas to use the RT dimensions for layout
        Canvas.ScreenSizeOverride = new Float2(width, height);

        try
        {
            // Bind the RT and set viewport for the UI overlay pass
            rt.Begin();
            Graphics.Viewport(0, 0, (uint)width, (uint)height);

            paper!.BeginFrame(Time.DeltaTime);
            paperRenderer!.RenderTarget = rt;
            scene.OnGui(paper);
            paper.EndFrame();

            rt.End();
        }
        finally
        {
            Canvas.ScreenSizeOverride = null;
        }
    }
}
