// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;

using Echo.Logging;

using Prowl.Runtime.Audio;
using Prowl.Runtime.Graphite;

using Prowl.PaperUI;
using Prowl.Runtime.GUI;
using Prowl.Runtime.Resources;
using Prowl.UI;
using Prowl.Vector;

using Prowl.Runtime.EventSystem;

namespace Prowl.Runtime;

public class EchoLogger : IEchoLogger
{
    public void Debug(string message) => Prowl.Runtime.Debug.Log(message);

    public void Error(string message, Exception? exception = null) => Prowl.Runtime.Debug.LogError(message);

    public void Info(string message) => Prowl.Runtime.Debug.Log(message);

    public void Warning(string message) => Prowl.Runtime.Debug.LogWarning(message);
}

public abstract class Game
{
    protected TimeData time = new();
    protected float fixedTimeAccumulator = 0.0f;

    private PaperRenderer _paperRenderer;
    private Paper _paper;
    protected int frameCounter;

    private readonly WindowManager _windowManager = new();
    private readonly ImGuiManager _imguiManager = new();
    private readonly StringBuilder _titleBuilder = new();

    public static EventSystem.EventManager<EventSystem.BaseEvents> BaseEventManager { get; } = new();

    public string WindowTitle => _title;

    private string _title; 

    public Paper PaperInstance => _paper;

    public bool DrawGizmos { get; set; }

    /// <summary>
    /// The window manager handling Silk.NET window creation and DPI-aware sizing.
    /// </summary>
    protected WindowManager WindowManager => _windowManager;

    /// <summary>
    /// The ImGui manager handling the Dear ImGui lifecycle.
    /// </summary>
    protected ImGuiManager ImGuiManager => _imguiManager;

    /// <summary>
    /// The DPI scale factor for the current monitor (1.0 at 96 DPI, 1.5 at 144 DPI, 2.0 at 192 DPI, etc.).
    /// UI code should multiply hard-coded pixel sizes by this value.
    /// Delegates to <see cref="DpiManager.Scale"/>.
    /// </summary>
    public static float DpiScale => DpiManager.Scale;

    public virtual void WindowUpdate(float delta)
    {
        try
        {
            UpdatePaperInput();

            AudioContext.Update();

            time.Update();
            Time.TimeStack.Clear();
            Time.TimeStack.Push(time);

            Input.UpdateActions(delta);

            BeginUpdate();

            // Fixed update loop — update all loaded scenes
            fixedTimeAccumulator += delta;
            int count = 0;
            while (fixedTimeAccumulator >= Time.FixedDeltaTime && count++ < 10)
            {
                if (SceneManager.LoadedSceneCount > 0)
                    SceneManager.FixedUpdateAll();
                else
                    Scene.Current?.FixedUpdate();
                fixedTimeAccumulator -= Time.FixedDeltaTime;
            }

            if (SceneManager.LoadedSceneCount > 0)
                SceneManager.UpdateAll();
            else
                Scene.Current?.Update();

            if (DrawGizmos)
            {
                if (SceneManager.LoadedSceneCount > 0)
                    SceneManager.DrawGizmosAll();
                else
                    Scene.Current?.DrawGizmos();
            }

            EndUpdate();

            if (frameCounter++ % 60 == 0)
            {
                _titleBuilder.Clear();
                _titleBuilder.Append(_title);
                _titleBuilder.Append(" - ");
                _titleBuilder.Append(Window.InternalWindow.FramebufferSize.X);
                _titleBuilder.Append('x');
                _titleBuilder.Append(Window.InternalWindow.FramebufferSize.Y);
                _titleBuilder.Append(" - FPS: ");
                _titleBuilder.Append(1.0 / Time.DeltaTime);
                Console.Title = _titleBuilder.ToString();
            }

        }
        catch (Exception e)
        {
            Debug.LogError("An exception occurred during the Update loop:");
            Debug.LogError(e.ToString());
            if (!HandleFrameException(e, "Update"))
                throw;
        }
    }

    public virtual void Run(string title, int width, int height, GraphicsBackendType backend = GraphicsBackendType.OpenGL)
    {
        _title = title;

        // Create a fresh engine context for this game instance.
        EngineContext.Current = new EngineContext();

        // Try with the requested backend; fall back to OpenGL on failure.
        if (backend != GraphicsBackendType.OpenGL)
        {
            try
            {
                SetupWindowAndStart(title, width, height, backend);
                return; // Normal exit — window ran and closed cleanly.
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Graphics] Failed to initialize {backend} backend: {ex.Message}");
                Debug.LogException(ex);
                Debug.LogWarning("[Graphics] Falling back to OpenGL...");
                Window.Cleanup();
                // Reset engine context for a clean retry.
                EngineContext.Current = new EngineContext();
            }
        }

        // OpenGL path (either requested directly or as fallback).
        SetupWindowAndStart(title, width, height, GraphicsBackendType.OpenGL);
    }

    private void SetupWindowAndStart(string title, int width, int height, GraphicsBackendType backend)
    {
        // Create the DPI-aware window.
        float systemScale = _windowManager.CreateWindow(title, width, height, backend);

        Window.Load += () =>
        {
            try
            {
                AudioContext.Initialize(44100, 2, 2048);
            }
            catch (DllNotFoundException ex)
            {
                Debug.LogWarning($"[Audio] Native audio library not found — audio will be disabled: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Audio] Failed to initialize audio context — audio will be disabled: {ex.Message}");
            }

            int scaledW = _windowManager.InitialScaledWidth;
            int scaledH = _windowManager.InitialScaledHeight;
            _paperRenderer = new PaperRenderer();
            _paperRenderer.Initialize(scaledW, scaledH);
            _paper = new Paper(_paperRenderer, scaledW, scaledH, new Prowl.Quill.FontAtlasSettings());

            // Refine DPI using per-window detection (handles multi-monitor setups).
            _windowManager.RefineWindowDpi(systemScale);

            // Initialize Dear ImGui (Silk.NET controller handles GL backend + input)
            _imguiManager.Initialize();

            // Subscribe to dynamic DPI changes
            DpiManager.DpiChanged += OnDpiChangedInternal;

            Initialize();
        };

        Window.Update += WindowUpdate;

        Window.Render += (delta) =>
        {
            if (!Window.IsVisible)
                return;
            try
            {
                // === Start Graphics ===

                if (Graphics.IsOpenGL)
                {
                    // Invalidate legacy caches that may be stale from Graphite
                    // command execution in the previous frame (PaperRenderer, etc.).
                    Graphics.InvalidateLegacyCaches();

                    Graphics.UnbindFramebuffer();
                    Graphics.Viewport(0, 0, (uint)Window.InternalWindow.FramebufferSize.X, (uint)Window.InternalWindow.FramebufferSize.Y);
                    Graphics.SetState(new(), true);

                    Graphics.BindVertexArray(null);
                    Graphics.Clear(0, 0, 0, 1, ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil);
                }

                // === End of Start Graphics ===

                // Reset per-frame swapchain tracking so the first render pass
                // targeting the swapchain knows to use Clear (Vulkan layout transition).
                Graphics.SwapchainClearedThisFrame = false;

                // Scene rendering is wrapped separately so that failures here
                // (e.g. during the Graphite migration) do not prevent UI from rendering.
                try
                {
                    Rendering.ShadowAtlas.TryInitialize();
                    Rendering.ShadowAtlas.Clear();

                    BeginRender();

                    RenderScenes();

                    EndRender();
                }
                catch (Exception e)
                {
                    Debug.LogError("An exception occurred during scene rendering:");
                    Debug.LogError(e.ToString());
                    if (!HandleFrameException(e, "SceneRender"))
                        throw;
                }

                // Reset GL state so Paper UI starts from a known-good state.
                if (Graphics.IsOpenGL)
                {
                    Graphics.UnbindFramebuffer();
                    Graphics.Viewport(0, 0, (uint)Window.InternalWindow.FramebufferSize.X, (uint)Window.InternalWindow.FramebufferSize.Y);
                }

                // Paper UI is also isolated so ImGui always gets a chance to render.
                try
                {
                    _paper.BeginFrame(delta);

                    BeginGui(_paper);

                    // OnGui runs on all loaded scenes, or just the current scene
                    RenderScenePaperGui(_paper);

                    EndGui(_paper);

                    _paperRenderer.RenderTarget = null; // Render to swapchain
                    _paper.EndFrame();
                }
                catch (Exception e)
                {
                    Debug.LogError("An exception occurred during Paper UI rendering:");
                    Debug.LogError(e.ToString());
                    if (!HandleFrameException(e, "PaperUI"))
                        throw;
                }

                // Reset GL state before ImGui so it always starts clean.
                if (Graphics.IsOpenGL)
                {
                    // PaperRenderer submits Graphite command lists that change
                    // the active GL program, VAO, and texture bindings directly.
                    // Invalidate the legacy caches so ImGui and the next frame
                    // start from a known-good state.
                    Graphics.InvalidateLegacyCaches();

                    Graphics.UnbindFramebuffer();
                    Graphics.Viewport(0, 0, (uint)Window.InternalWindow.FramebufferSize.X, (uint)Window.InternalWindow.FramebufferSize.Y);
                    Graphics.SetState(new(), true);
                }

                // Dear ImGui frame (editor / launcher UI) — requires OpenGL backend.
                if (_imguiManager.IsReady)
                {
                    _imguiManager.Update((float)delta);
                    _imguiManager.BeginFrame();

                    BeginImGui(_imguiManager.Renderer!);
                    EndImGui(_imguiManager.Renderer!);

                    _imguiManager.Render();
                }

                // === End Graphics ===

                RenderTexture.UpdatePool();

                // === End of End Graphics ===

                Debug.ClearGizmos();
            }
            catch (Exception e)
            {
                Debug.LogError("An exception occurred during the Render loop:");
                Debug.LogError(e.ToString());
                if (!HandleFrameException(e, "Render"))
                    throw;
            }
        };

        Window.Resize += (size) =>
        {
            _paper.SetResolution(size.X, size.Y);
            _paperRenderer.UpdateProjection(size.X, size.Y);
            Resize(size.X, size.Y);
        };

        // Monitor DPI changes when the window moves between monitors or framebuffer resizes.
        Window.Move += (_) => { if (_imguiManager.IsReady) DpiManager.CheckForChange(); };
        Window.FramebufferResize += (_) => { if (_imguiManager.IsReady) DpiManager.CheckForChange(); };

        Window.Closing += () =>
        {
            DpiManager.DpiChanged -= OnDpiChangedInternal;
            Closing();

            _imguiManager.Dispose();

            // Unload all scenes (additive + primary)
            SceneManager.UnloadAllExcept();
            Scene.Unload();
            SceneManager.Clear();

            AudioContext.Deinitialize();

            Debug.Log("Is terminating...");
        };

        Debug.LogSuccess("Initialization complete");
        Window.Start();
    }

    public virtual void Initialize() { }

    public virtual void BeginUpdate() { }
    public virtual void EndUpdate() { }
    public virtual void BeginRender() { }
    public virtual void RenderScenes()
    {
        if (SceneManager.LoadedSceneCount > 0)
            SceneManager.RenderAll();
        else
            Scene.Current?.Render();
    }
    public virtual void EndRender() { }
    public virtual void BeginGui(Paper paper) { }

    /// <summary>
    /// Runs the Paper/OnGui pass for all loaded scenes.
    /// Override in the editor to skip this — the editor handles scene Paper UI
    /// separately via <c>RenderOnGuiIntoRT</c> into the game/scene view render textures.
    /// </summary>
    protected virtual void RenderScenePaperGui(Paper paper)
    {
        if (SceneManager.LoadedSceneCount > 0)
        {
            foreach (var scene in SceneManager.LoadedScenes)
                if (scene.IsActive) scene.OnGui(paper);
        }
        else
        {
            Scene.Current?.OnGui(paper);
        }
    }

    public virtual void EndGui(Paper paper) { }
    public virtual void BeginImGui(IUIRenderer ui) { }
    public virtual void EndImGui(IUIRenderer ui) { }

    public virtual void Resize(int width, int height) { }
    public virtual void Closing() { }

    /// <summary>
    /// Called when an exception is caught during the frame update or render loop.
    /// Override to swallow exceptions (return <c>true</c>) instead of crashing.
    /// The default implementation returns <c>false</c>, causing the exception to be re-thrown.
    /// </summary>
    /// <param name="e">The caught exception.</param>
    /// <param name="phase">"Update" or "Render" — indicates which loop threw.</param>
    /// <returns><c>true</c> if the exception was handled and execution should continue; <c>false</c> to re-throw.</returns>
    protected virtual bool HandleFrameException(Exception e, string phase) => false;

    /// <summary>
    /// Called when the DPI scale changes at runtime (e.g., window moved to another monitor).
    /// Override in subclasses to reset theme / style scaling.
    /// </summary>
    public virtual void OnDpiChanged(float oldScale, float newScale) { }

    private void OnDpiChangedInternal(float oldScale, float newScale)
    {
        // Adjust ImGui font rendering to match the new DPI without rebuilding the font atlas.
        _imguiManager.OnDpiChanged(newScale);

        // Resize the window to maintain the same logical size (based on monitor DPI only).
        _windowManager.ResizeForDpi();

        Debug.Log($"DPI changed: {oldScale:F2} → {newScale:F2}");

        // Let subclasses react (e.g., reset theme scaling).
        OnDpiChanged(oldScale, newScale);
    }

    [RequiresDynamicCode("Calls System.Enum.GetValues(Type)")]
    protected void UpdatePaperInput()
    {
        // Handle mouse position and movement
        Int2 mousePos = Input.MousePosition;
        _paper.SetPointerState(PaperMouseBtn.Unknown, mousePos.X, mousePos.Y, false, true);

        // Handle mouse buttons
        if (Input.GetMouseButtonDown(0))
            _paper.SetPointerState(PaperMouseBtn.Left, mousePos.X, mousePos.Y, true, false);
        if (Input.GetMouseButtonUp(0))
            _paper.SetPointerState(PaperMouseBtn.Left, mousePos.X, mousePos.Y, false, false);

        if (Input.GetMouseButtonDown(1))
            _paper.SetPointerState(PaperMouseBtn.Right, mousePos.X, mousePos.Y, true, false);
        if (Input.GetMouseButtonUp(1))
            _paper.SetPointerState(PaperMouseBtn.Right, mousePos.X, mousePos.Y, false, false);

        if (Input.GetMouseButtonDown(2))
            _paper.SetPointerState(PaperMouseBtn.Middle, mousePos.X, mousePos.Y, true, false);
        if (Input.GetMouseButtonUp(2))
            _paper.SetPointerState(PaperMouseBtn.Middle, mousePos.X, mousePos.Y, false, false);

        // Handle mouse wheel
        float wheelDelta = Input.MouseWheelDelta;
        if (wheelDelta != 0)
            _paper.SetPointerWheel(wheelDelta);

        // Handle keyboard input
        char? c = Input.GetPressedChar();
        while (c != null)
        {
            _paper.AddInputCharacter((c.Value).ToString());
            c = Input.GetPressedChar();
        }

        // Handle key states for keys
        // Fortunately Papers key enums have almost all the same names
        // So we only need to map a few keys manually, the rest we can use reflection
        foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
            if (k != KeyCode.Unknown)
                if (Enum.TryParse(k.ToString(), out PaperKey paperKey))
                    HandleKey(k, paperKey);

        // Handle the few keys that are not the same
        HandleKey(KeyCode.Equal, PaperKey.Equals);
        HandleKey(KeyCode.BackSlash, PaperKey.Backslash);
        HandleKey(KeyCode.GraveAccent, PaperKey.Grave);
        HandleKey(KeyCode.KeypadEqual, PaperKey.KeypadEquals);

        HandleKey(KeyCode.Number0, PaperKey.Num0);
        HandleKey(KeyCode.Number1, PaperKey.Num1);
        HandleKey(KeyCode.Number2, PaperKey.Num2);
        HandleKey(KeyCode.Number3, PaperKey.Num3);
        HandleKey(KeyCode.Number4, PaperKey.Num4);
        HandleKey(KeyCode.Number5, PaperKey.Num5);
        HandleKey(KeyCode.Number6, PaperKey.Num6);
        HandleKey(KeyCode.Number7, PaperKey.Num7);
        HandleKey(KeyCode.Number8, PaperKey.Num8);
        HandleKey(KeyCode.Number9, PaperKey.Num9);

        HandleKey(KeyCode.KeypadSubtract, PaperKey.KeypadMinus);
        HandleKey(KeyCode.KeypadAdd, PaperKey.KeypadPlus);

        HandleKey(KeyCode.LeftBracket, PaperKey.LeftBracket);
        HandleKey(KeyCode.RightBracket, PaperKey.RightBracket);
        HandleKey(KeyCode.ShiftLeft, PaperKey.LeftShift);
        HandleKey(KeyCode.ShiftRight, PaperKey.RightShift);
        HandleKey(KeyCode.AltLeft, PaperKey.LeftAlt);
        HandleKey(KeyCode.AltRight, PaperKey.RightAlt);
        HandleKey(KeyCode.ControlLeft, PaperKey.LeftControl);
        HandleKey(KeyCode.ControlRight, PaperKey.RightControl);
        HandleKey(KeyCode.SuperLeft, PaperKey.LeftSuper);
        HandleKey(KeyCode.SuperRight, PaperKey.RightSuper);
    }

    void HandleKey(KeyCode silkKey, PaperKey paperKey)
    {
        if (Input.GetKeyDown(silkKey))
            _paper.SetKeyState(paperKey, true);
        else if (Input.GetKeyUp(silkKey))
            _paper.SetKeyState(paperKey, false);
    }

    public static void Quit()
    {
        Window.Stop();
        Debug.Log("Is terminating...");
    }

    }
