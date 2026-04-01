// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;

using Echo.Logging;

using Prowl.Runtime.Audio;
using Prowl.Runtime.Graphite;
using Prowl.Runtime.Profiling;

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

    private IDisposable? _dpiSubscription;
    private IDisposable? _windowLoadSub;
    private IDisposable? _windowUpdateSub;
    private IDisposable? _windowRenderSub;
    private IDisposable? _windowResizeSub;
    private IDisposable? _windowMoveSub;
    private IDisposable? _windowFbResizeSub;
    private IDisposable? _windowClosingSub;

    private readonly WindowManager _windowManager = new();
    private IOverlayManager? _overlayManager;
    private readonly StringBuilder _titleBuilder = new();

    // Pre-computed KeyCode→PaperKey mapping table to avoid per-frame
    // Enum.GetValues allocation, ToString(), and TryParse overhead.
    private static readonly (KeyCode key, PaperKey paper)[] s_keyMapping = BuildKeyMapping();

    private static (KeyCode, PaperKey)[] BuildKeyMapping()
    {
        var keyCodes = (KeyCode[])Enum.GetValues(typeof(KeyCode));
        var list = new System.Collections.Generic.List<(KeyCode, PaperKey)>(keyCodes.Length);
        foreach (KeyCode k in keyCodes)
        {
            if (k == KeyCode.Unknown) continue;
            if (Enum.TryParse(k.ToString(), out PaperKey paperKey))
                list.Add((k, paperKey));
        }
        // Add the manual mappings that don't share names
        list.Add((KeyCode.Equal, PaperKey.Equals));
        list.Add((KeyCode.BackSlash, PaperKey.Backslash));
        list.Add((KeyCode.GraveAccent, PaperKey.Grave));
        list.Add((KeyCode.KeypadEqual, PaperKey.KeypadEquals));
        list.Add((KeyCode.Number0, PaperKey.Num0));
        list.Add((KeyCode.Number1, PaperKey.Num1));
        list.Add((KeyCode.Number2, PaperKey.Num2));
        list.Add((KeyCode.Number3, PaperKey.Num3));
        list.Add((KeyCode.Number4, PaperKey.Num4));
        list.Add((KeyCode.Number5, PaperKey.Num5));
        list.Add((KeyCode.Number6, PaperKey.Num6));
        list.Add((KeyCode.Number7, PaperKey.Num7));
        list.Add((KeyCode.Number8, PaperKey.Num8));
        list.Add((KeyCode.Number9, PaperKey.Num9));
        list.Add((KeyCode.KeypadSubtract, PaperKey.KeypadMinus));
        list.Add((KeyCode.KeypadAdd, PaperKey.KeypadPlus));
        list.Add((KeyCode.LeftBracket, PaperKey.LeftBracket));
        list.Add((KeyCode.RightBracket, PaperKey.RightBracket));
        list.Add((KeyCode.ShiftLeft, PaperKey.LeftShift));
        list.Add((KeyCode.ShiftRight, PaperKey.RightShift));
        list.Add((KeyCode.AltLeft, PaperKey.LeftAlt));
        list.Add((KeyCode.AltRight, PaperKey.RightAlt));
        list.Add((KeyCode.ControlLeft, PaperKey.LeftControl));
        list.Add((KeyCode.ControlRight, PaperKey.RightControl));
        list.Add((KeyCode.SuperLeft, PaperKey.LeftSuper));
        list.Add((KeyCode.SuperRight, PaperKey.RightSuper));
        return [.. list];
        }

    // ── Backward-compatible event-manager shims ──────────────────────
    // These delegate to the source-generated Manager on each domain class.
    // User code should migrate to the generated convenience methods
    // (e.g. GameLoopEvents.InvokeOnFrameBegin, GameLoopEvents.SubscribeOnFrameBegin)
    // or access the manager directly via GameLoopEvents.Manager.

    /// <inheritdoc cref="EventSystem.BaseEvents.Manager"/>
    [Obsolete("Use BaseEvents.Manager or the generated convenience methods instead.")]
    public static EventSystem.EventManager<EventSystem.BaseEvents.EventTypes> BaseEventManager
        => EventSystem.BaseEvents.Manager;

    /// <inheritdoc cref="EventSystem.GameLoopEvents.Manager"/>
    [Obsolete("Use GameLoopEvents.Manager or the generated convenience methods instead.")]
    public static EventSystem.EventManager<EventSystem.GameLoopEvents.EventTypes> GameLoopEventManager
        => EventSystem.GameLoopEvents.Manager;

    /// <inheritdoc cref="EventSystem.RenderingEvents.Manager"/>
    [Obsolete("Use RenderingEvents.Manager or the generated convenience methods instead.")]
    public static EventSystem.EventManager<EventSystem.RenderingEvents.EventTypes> RenderingEventManager
        => EventSystem.RenderingEvents.Manager;

    /// <inheritdoc cref="EventSystem.PhysicsEvents.Manager"/>
    [Obsolete("Use PhysicsEvents.Manager or the generated convenience methods instead.")]
    public static EventSystem.EventManager<EventSystem.PhysicsEvents.EventTypes> PhysicsEventManager
        => EventSystem.PhysicsEvents.Manager;

    /// <inheritdoc cref="EventSystem.AssetEvents.Manager"/>
    [Obsolete("Use AssetEvents.Manager or the generated convenience methods instead.")]
    public static EventSystem.EventManager<EventSystem.AssetEvents.EventTypes> AssetEventManager
        => EventSystem.AssetEvents.Manager;

    public string WindowTitle => _title;

    private string _title; 

    public Paper PaperInstance => _paper;

    public bool DrawGizmos { get; set; }

    /// <summary>
    /// The window manager handling Silk.NET window creation and DPI-aware sizing.
    /// </summary>
    protected WindowManager WindowManager => _windowManager;

    /// <summary>
    /// The overlay manager handling the Dear ImGui (or similar) lifecycle.
    /// Set by subclasses via <see cref="CreateOverlayManager"/> before the window loads.
    /// </summary>
    protected IOverlayManager? OverlayManager => _overlayManager;

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
            Profiler.BeginFrame();

            EventSystem.GameLoopEvents.InvokeOnFrameBegin(
                new EventSystem.FrameBeginArgs(frameCounter, delta));

            using (Profiler.Section("Input"))
            {
                UpdatePaperInput();
                Input.UpdateActions(delta);
            }

            using (Profiler.Section("Audio"))
                AudioContext.Update();

            time.Update();
            Time.TimeStack.Clear();
            Time.TimeStack.Push(time);

            using (Profiler.Section("BeginUpdate"))
                BeginUpdate();

            // Cache once — each access takes a lock.
            int loadedScenes = SceneManager.LoadedSceneCount;

            // Fixed update loop — update all loaded scenes
            using (Profiler.Section("FixedUpdate"))
            {
                fixedTimeAccumulator += delta;
                int count = 0;
                while (fixedTimeAccumulator >= Time.FixedDeltaTime && count++ < 10)
                {
                    if (loadedScenes > 0)
                        SceneManager.FixedUpdateAll();
                    else
                        Scene.Current?.FixedUpdate();
                    fixedTimeAccumulator -= Time.FixedDeltaTime;
                }
                // Clamp accumulator to prevent spiral-of-death: if physics can't
                // keep up, drop the excess time instead of queuing more steps.
                if (fixedTimeAccumulator > Time.FixedDeltaTime)
                    fixedTimeAccumulator = 0;
            }

            using (Profiler.Section("Update"))
            {
                if (loadedScenes > 0)
                    SceneManager.UpdateAll();
                else
                    Scene.Current?.Update();
            }

            if (DrawGizmos)
            {
                using (Profiler.Section("Gizmos"))
                {
                    if (loadedScenes > 0)
                        SceneManager.DrawGizmosAll();
                    else
                        Scene.Current?.DrawGizmos();
                }
            }

            using (Profiler.Section("EndUpdate"))
                EndUpdate();

            EventSystem.GameLoopEvents.InvokeOnFrameEnd(
                new EventSystem.FrameEndArgs(
                    frameCounter,
                    time.DeltaTime,
                    time.UnscaledDeltaTime,
                    time.Time));

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
                Window.InternalWindow.Title = _titleBuilder.ToString();
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

    public virtual void Run(string title, int width, int height, GraphicsBackendType backend = GraphicsBackendType.Vulkan)
    {
        _title = title;

        // Install a last-resort handler so native crashes or unobserved exceptions
        // are written to the log before the process terminates.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Debug.LogError($"[FATAL] Unhandled exception (isTerminating={args.IsTerminating}): {args.ExceptionObject}");
        };

        // Create a fresh engine context for this game instance.
        EngineContext.Current = new EngineContext();

        Debug.Log($"[Game.Run] Requested backend: {backend}");

        // Try with the requested backend; fall back to OpenGL on failure.
        if (backend != GraphicsBackendType.OpenGL)
        {
            try
            {
                Debug.LogSuccess($"[Graphics] Initializing Window...");
                SetupWindowAndStart(title, width, height, backend);
                return; // Normal exit — window ran and closed cleanly.
            }
            catch (Exception ex)
            {
                // Log FULL exception details — ToString() includes FileName for
                // FileNotFoundException, all inner exceptions, and full stack traces.
                Debug.LogWarning($"[Graphics] Failed to initialize {backend} backend:");
                Debug.LogWarning($"[Graphics]   Exception: {ex}");
                if (ex is System.IO.FileNotFoundException fnf)
                {
                    Debug.LogWarning($"[Graphics]   FileName: {fnf.FileName}");
                    Debug.LogWarning($"[Graphics]   FusionLog: {fnf.FusionLog}");
                }
                Debug.LogWarning("[Graphics] Falling back to OpenGL...");
                try { Window.Cleanup(); } catch (Exception cleanupEx) { Debug.LogWarning($"[Graphics] Cleanup error: {cleanupEx.Message}"); }
                // Reset engine context for a clean retry.
                EngineContext.Current = new EngineContext();
            }
        }

        // OpenGL path (either requested directly or as fallback).
        SetupWindowAndStart(title, width, height, GraphicsBackendType.OpenGL);
    }

    private void SetupWindowAndStart(string title, int width, int height, GraphicsBackendType backend)
    {
        Debug.Log($"[SetupWindowAndStart] Entering method with backend={backend}");

        Debug.Log("[SetupWindowAndStart] Creating window...");
        float systemScale = _windowManager.CreateWindow(title, width, height, backend);
        Debug.Log($"[SetupWindowAndStart] Window created, systemScale={systemScale}");

        Debug.Log("[SetupWindowAndStart] Registering event handlers...");
        DisposeWindowSubscriptions();
        _windowLoadSub = WindowEvents.SubscribeOnLoad(() =>
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

            // Initialize overlay manager (e.g. Dear ImGui) if the subclass provides one.
            _overlayManager = CreateOverlayManager();
            _overlayManager?.Initialize();

            // Subscribe to dynamic DPI changes
            _dpiSubscription = EventSystem.DpiEvents.SubscribeOnDpiChanged(
                args => OnDpiChangedInternal(args.OldScale, args.NewScale));

            // Register built-in profiler section descriptions
            BuiltInProfilerSections.Register();

            Initialize();

            EventSystem.GameLoopEvents.InvokeOnInitialized(
                new EventSystem.InitializedArgs(
                    backend,
                    Graphics.IsGraphiteReady ? Graphics.Graphite.BackendName : backend.ToString(),
                    scaledW,
                    scaledH));
        });

        Debug.Log("[SetupWindowAndStart] Registering Update handler...");
        _windowUpdateSub = WindowEvents.SubscribeOnUpdate(args => WindowUpdate(args.DeltaTime));

        Debug.Log("[SetupWindowAndStart] Registering Render handler...");
        _windowRenderSub = WindowEvents.SubscribeOnRender((args) => WindowRender(args.DeltaTime));

        Debug.Log("[SetupWindowAndStart] Registering Resize handler...");
        _windowResizeSub = WindowEvents.SubscribeOnResize((args) =>
        {
            _paper.SetResolution(args.Width, args.Height);
            _paperRenderer.UpdateProjection(args.Width, args.Height);
            Resize(args.Width, args.Height);
        });

        Debug.Log("[SetupWindowAndStart] Registering Move handler...");
        // Monitor DPI changes when the window moves between monitors or framebuffer resizes.
        _windowMoveSub = WindowEvents.SubscribeOnMove((_) => { if (_overlayManager is { IsReady: true }) DpiManager.CheckForChange(); });

        Debug.Log("[SetupWindowAndStart] Registering FramebufferResize handler...");
        _windowFbResizeSub = WindowEvents.SubscribeOnFramebufferResize((_) => { if (_overlayManager is { IsReady: true }) DpiManager.CheckForChange(); });

        Debug.Log("[SetupWindowAndStart] Registering Closing handler...");
        _windowClosingSub = WindowEvents.SubscribeOnClosing(() =>
        {
            EventSystem.GameLoopEvents.InvokeOnClosing(
                new EventSystem.ClosingArgs(Time.TimeSinceStartup, Time.FrameCount));

            _dpiSubscription?.Dispose();
            Closing();

            _overlayManager?.Dispose();

            // Unload all scenes (additive + primary)
            SceneManager.UnloadAllExcept();
            Scene.Unload();
            SceneManager.Clear();

            AudioContext.Deinitialize();

            Debug.Log("Is terminating...");
        });

        Debug.Log("[SetupWindowAndStart] All handlers registered. Starting window...");
        Window.Start();
        Debug.Log("[SetupWindowAndStart] Window.Start() returned.");
    }

    public virtual void Initialize() { }

    /// <summary>
    /// Disposes all window event subscriptions so they can be re-registered
    /// cleanly (e.g. during backend fallback).
    /// </summary>
    private void DisposeWindowSubscriptions()
    {
        _windowLoadSub?.Dispose();      _windowLoadSub = null;
        _windowUpdateSub?.Dispose();    _windowUpdateSub = null;
        _windowRenderSub?.Dispose();    _windowRenderSub = null;
        _windowResizeSub?.Dispose();    _windowResizeSub = null;
        _windowMoveSub?.Dispose();      _windowMoveSub = null;
        _windowFbResizeSub?.Dispose();  _windowFbResizeSub = null;
        _windowClosingSub?.Dispose();   _windowClosingSub = null;
    }

    public virtual void BeginUpdate() { }
    public virtual void EndUpdate() { }

    /// <summary>
    /// Called once per frame to render the scene, Paper UI, and overlay (ImGui).
    /// Subclasses that need only a lightweight overlay (e.g. the launcher) can
    /// override this to skip scene rendering, shadow atlas, Paper UI, etc.
    /// </summary>
    public virtual void WindowRender(float delta)
    {
        if (!Window.IsVisible)
            return;
        try
        {
            // === Start Graphics ===

            if (Graphics.IsOpenGL)
            {
                Graphics.InvalidateLegacyCaches();
                Graphics.UnbindFramebuffer();
                Graphics.Viewport(0, 0, (uint)Window.InternalWindow.FramebufferSize.X, (uint)Window.InternalWindow.FramebufferSize.Y);
                Graphics.SetState(new(), true);
                Graphics.BindVertexArray(null);
                Graphics.Clear(0, 0, 0, 1, ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil);
            }

            // Reset per-frame swapchain tracking so the first render pass
            // targeting the swapchain knows to use Clear (Vulkan layout transition).
            Graphics.SwapchainClearedThisFrame = false;

            // Scene rendering is wrapped separately so that failures here
            // do not prevent UI from rendering.
            try
            {
                using (Profiler.Section("Shadows"))
                {
                    Rendering.ShadowAtlas.TryInitialize();
                    Rendering.ShadowAtlas.Clear();
                }

                EventSystem.RenderingEvents.InvokeOnShadowsReady();

                using (Profiler.Section("BeginRender"))
                    BeginRender();

                EventSystem.RenderingEvents.InvokeOnBeginRender();

                using (Profiler.Section("RenderScenes"))
                    RenderScenes();

                using (Profiler.Section("EndRender"))
                    EndRender();

                EventSystem.RenderingEvents.InvokeOnEndRender();
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
                using (Profiler.Section("PaperUI"))
                {
                    _paper.BeginFrame(delta);

                    BeginGui(_paper);

                    // OnGui runs on all loaded scenes, or just the current scene
                    RenderScenePaperGui(_paper);

                    EndGui(_paper);

                    PaperStatsMonitor.Draw(_paper);

                    _paperRenderer.RenderTarget = null; // Render to swapchain
                    _paper.EndFrame();
                }
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
                Graphics.InvalidateLegacyCaches();
                Graphics.UnbindFramebuffer();
                Graphics.Viewport(0, 0, (uint)Window.InternalWindow.FramebufferSize.X, (uint)Window.InternalWindow.FramebufferSize.Y);
                Graphics.SetState(new(), true);
            }

            // Overlay UI frame (editor / launcher UI) — works on all backends via Graphite.
            RenderOverlay(delta);

            // === End Graphics ===

            RenderTexture.UpdatePool();

            Debug.ClearGizmos();

            Profiler.EndFrame();

            EventSystem.GameLoopEvents.InvokeOnRenderComplete(
                new EventSystem.RenderCompleteArgs(frameCounter, delta));
        }
        catch (Exception e)
        {
            Debug.LogError("An exception occurred during the Render loop:");
            Debug.LogError(e.ToString());
            if (!HandleFrameException(e, "Render"))
                throw;
        }
    }

    /// <summary>
    /// Renders the ImGui overlay. Extracted so lightweight subclasses can call
    /// this directly from an overridden <see cref="WindowRender"/> without
    /// running the full scene/Paper UI pipeline.
    /// </summary>
    protected void RenderOverlay(float delta)
    {
        if (_overlayManager is { IsReady: true } overlay)
        {
            // Reset swapchain tracking so the overlay's render pass uses
            // Clear (required for Vulkan layout transitions) when it is
            // the first pass targeting the swapchain this frame.
            Graphics.SwapchainClearedThisFrame = false;

            using (Profiler.Section("ImGui"))
            {
                overlay.Update(delta);
                overlay.BeginFrame();

                BeginImGui(overlay.Renderer!);
                EndImGui(overlay.Renderer!);

                overlay.Render();
            }
        }
    }

    public virtual void BeginRender() { }
    public virtual void RenderScenes()
    {
        if (SceneManager.LoadedSceneCount > 0)
        {
            //Debug.Log($"[Game.RenderScenes] Rendering via SceneManager ({SceneManager.LoadedSceneCount} scenes)");
            SceneManager.RenderAll();
        }
        else if (Scene.Current != null)
        {
            //Debug.Log($"[Game.RenderScenes] Rendering Scene.Current (active={Scene.Current.IsActive})");
            Scene.Current?.Render();
        }
        else
        {
            //Debug.LogWarning("[Game.RenderScenes] No scenes to render! SceneManager empty and Scene.Current is null.");
        }
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
            SceneManager.OnGuiAll(paper);
        else
            Scene.Current?.OnGui(paper);
    }

    public virtual void EndGui(Paper paper) { }
    public virtual void BeginImGui(IUIRenderer ui) { }
    public virtual void EndImGui(IUIRenderer ui) { }

    /// <summary>
    /// Factory method that subclasses override to provide an overlay manager
    /// (e.g. Dear ImGui). Returns <c>null</c> for standalone game builds
    /// that don't need an editor overlay.
    /// </summary>
    protected virtual IOverlayManager? CreateOverlayManager() => null;

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
        // Adjust overlay font rendering to match the new DPI without rebuilding the font atlas.
        _overlayManager?.OnDpiChanged(newScale);

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

        // Use pre-computed mapping (zero allocation per frame)
        foreach (var (key, paper) in s_keyMapping)
            HandleKey(key, paper);
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
