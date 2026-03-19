// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using Echo.Logging;

using Prowl.Runtime.Audio;

using Prowl.PaperUI;
using Prowl.Runtime.GUI;
using Prowl.Runtime.Resources;
using Prowl.UI;
using Prowl.Vector;

using ImGuiNET;
using SilkImGui = Silk.NET.OpenGL.Extensions.ImGui;
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

    private SilkImGui.ImGuiController _imguiController;
    private ImGuiUIRenderer _imguiRenderer;

    public static EventSystem.EventManager<EventSystem.BaseEvents> BaseEventManager { get; } = new();

    public string WindowTitle => _title;

    private string _title; 

    public Paper PaperInstance => _paper;

    public bool DrawGizmos { get; set; }

    /// <summary>
    /// The DPI scale factor for the current monitor (1.0 at 96 DPI, 1.5 at 144 DPI, 2.0 at 192 DPI, etc.).
    /// UI code should multiply hard-coded pixel sizes by this value.
    /// Delegates to <see cref="DpiManager.Scale"/>.
    /// </summary>
    public static float DpiScale => DpiManager.Scale;

    private int _logicalWidth;
    private int _logicalHeight;
    private bool _imguiReady;

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

            Scene? currentScene = Scene.Current;

            // Fixed update loop
            fixedTimeAccumulator += delta;
            int count = 0;
            while (fixedTimeAccumulator >= Time.FixedDeltaTime && count++ < 10)
            {
                currentScene?.FixedUpdate();
                fixedTimeAccumulator -= Time.FixedDeltaTime;
            }

            currentScene?.Update();

            if (DrawGizmos)
            {
                currentScene?.DrawGizmos();
            }

            EndUpdate();

            if (frameCounter++ % 60 == 0)
            {
                Console.Title = $"{_title} - {Window.InternalWindow.FramebufferSize.X}x{Window.InternalWindow.FramebufferSize.Y} - FPS: {1.0 / Time.DeltaTime}";
            }

        }
        catch (Exception e)
        {
            Debug.LogError("An exception occurred during the Update loop:");
            Debug.LogError(e.ToString());
            throw;
        }
    }

    public virtual void Run(string title, int width, int height)
    {
        _logicalWidth = width;
        _logicalHeight = height;

        _title = title;

        // Use DpiManager for system-level DPI detection before window creation.
        DpiManager.EnsureProcessDpiAware();
        float systemScale = DpiManager.GetSystemScale();
        DpiManager.Initialize(systemScale);

        int scaledW = (int)MathF.Round(width * systemScale);
        int scaledH = (int)MathF.Round(height * systemScale);

        Window.InitWindow(title, scaledW, scaledH, Silk.NET.Windowing.WindowState.Normal, false);

        Window.Load += () =>
        {
            AudioContext.Initialize(44100, 2, 2048);

            _paperRenderer = new PaperRenderer();
            _paperRenderer.Initialize(scaledW, scaledH);
            _paper = new Paper(_paperRenderer, scaledW, scaledH, new Prowl.Quill.FontAtlasSettings());

            // Refine DPI using per-window detection (handles multi-monitor setups).
            float windowScale = DpiManager.GetWindowScale();
            DpiManager.Initialize(windowScale);
            if (MathF.Abs(windowScale - systemScale) > 0.01f)
            {
                int newW = (int)MathF.Round(width * windowScale);
                int newH = (int)MathF.Round(height * windowScale);
                Window.InternalWindow.Size = new Silk.NET.Maths.Vector2D<int>(newW, newH);
            }

            // Initialize Dear ImGui (Silk.NET controller handles GL backend + input)
            string? systemFont = FindSystemFont();
            int baseFontSize = (int)MathF.Round(14 * DpiScale);
            _imguiController = new SilkImGui.ImGuiController(
                Graphics.GL,
                Window.InternalWindow,
                Window.InternalInput,
                systemFont != null ? new SilkImGui.ImGuiFontConfig(systemFont, baseFontSize) : null,
                () =>
                {
                    var io = ImGui.GetIO();
                    io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
                    if (systemFont != null)
                        ImGuiUIRenderer.LoadFonts(systemFont, DpiScale);
                });
            _imguiRenderer = new ImGuiUIRenderer();

            // Subscribe to dynamic DPI changes
            DpiManager.DpiChanged += OnDpiChangedInternal;
            _imguiReady = true;

            Initialize();
        };

        Window.Update += WindowUpdate;

        Window.Render += (delta) =>
        {
            if (!Window.IsVisible)
                return;
            try
            {
                Scene? currentScene = Scene.Current;

                // === Start Graphics ===

                Graphics.UnbindFramebuffer();
                Graphics.Viewport(0, 0, (uint)Window.InternalWindow.FramebufferSize.X, (uint)Window.InternalWindow.FramebufferSize.Y);
                Graphics.SetState(new(), true);

                Graphics.BindVertexArray(null);
                Graphics.Clear(0, 0, 0, 1, ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil);

                Rendering.ShadowAtlas.TryInitialize();
                Rendering.ShadowAtlas.Clear();

                // === End of Start Graphics ===

                BeginRender();

                currentScene?.Render();

                EndRender();

                Graphics.UnbindFramebuffer();
                Graphics.Viewport(0, 0, (uint)Window.InternalWindow.FramebufferSize.X, (uint)Window.InternalWindow.FramebufferSize.Y);

                _paper.BeginFrame(delta);

                BeginGui(_paper);

                currentScene?.OnGui(_paper);

                EndGui(_paper);

                _paper.EndFrame();

                // Dear ImGui frame (editor / launcher UI)
                _imguiController.Update((float)delta);
                _imguiRenderer.BeginFrame();

                BeginImGui(_imguiRenderer);
                EndImGui(_imguiRenderer);

                _imguiController.Render();

                // === End Graphics ===

                RenderTexture.UpdatePool();

                // === End of End Graphics ===

                Debug.ClearGizmos();
            }
            catch (Exception e)
            {
                Debug.LogError("An exception occurred during the Update loop:");
                Debug.LogError(e.ToString());
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
        Window.Move += (_) => { if (_imguiReady) DpiManager.CheckForChange(); };
        Window.FramebufferResize += (_) => { if (_imguiReady) DpiManager.CheckForChange(); };

        Window.Closing += () =>
        {
            DpiManager.DpiChanged -= OnDpiChangedInternal;
            Closing();

            _imguiController?.Dispose();

            // Unload the current scene
            Scene.Unload();

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
    public virtual void EndRender() { }
    public virtual void BeginGui(Paper paper) { }
    public virtual void EndGui(Paper paper) { }
    public virtual void BeginImGui(IUIRenderer ui) { }
    public virtual void EndImGui(IUIRenderer ui) { }

    public virtual void Resize(int width, int height) { }
    public virtual void Closing() { }

    /// <summary>
    /// Called when the DPI scale changes at runtime (e.g., window moved to another monitor).
    /// Override in subclasses to reset theme / style scaling.
    /// </summary>
    public virtual void OnDpiChanged(float oldScale, float newScale) { }

    private void OnDpiChangedInternal(float oldScale, float newScale)
    {
        // Adjust ImGui font rendering to match the new DPI without rebuilding the font atlas.
        var io = ImGui.GetIO();
        io.FontGlobalScale = newScale / DpiManager.BaseFontScale;

        // Resize the window to maintain the same logical size (based on monitor DPI only).
        int newW = (int)MathF.Round(_logicalWidth * DpiManager.MonitorScale);
        int newH = (int)MathF.Round(_logicalHeight * DpiManager.MonitorScale);
        Window.InternalWindow.Size = new Silk.NET.Maths.Vector2D<int>(newW, newH);

        Debug.Log($"DPI changed: {oldScale:F2} → {newScale:F2} (FontGlobalScale={io.FontGlobalScale:F2})");

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

    private static string? FindSystemFont()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"),
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            "/System/Library/Fonts/SFNS.ttf",
            "/System/Library/Fonts/Helvetica.ttc",
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    }
