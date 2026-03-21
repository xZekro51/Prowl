// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Prowl.Runtime;

public static class Window
{

    public static IWindow InternalWindow { get; internal set; }
    public static IInputContext InternalInput { get; internal set; }

    /// <summary>
    /// The rendering backend that was actually used when the window was created.
    /// This may differ from the requested backend if a fallback occurred.
    /// </summary>
    public static RenderingBackend ActiveBackend { get; private set; } = RenderingBackend.OpenGL;

    public static event Action? Load;
    public static event Action<float>? Update;
    public static event Action<float>? Render;
    public static event Action<float>? PostRender;
    public static event Action<bool>? FocusChanged;
    public static event Action<Vector2D<int>>? Resize;
    public static event Action<Vector2D<int>>? FramebufferResize;
    public static event Action? Closing;

    public static event Action<Vector2D<int>>? Move;
    public static event Action<WindowState>? StateChanged;
    public static event Action<string[]>? FileDrop;

    public static Vector2D<int> Size
    {
        get { return InternalWindow.Size; }
        set { InternalWindow.Size = value; }
    }

    public static bool IsVisible
    {
        get { return InternalWindow.IsVisible; }
        set { InternalWindow.IsVisible = value; }
    }

    public static bool VSync
    {
        get { return InternalWindow.VSync; }
        set { InternalWindow.VSync = value; }
    }

    public static float FramesPerSecond
    {
        get { return (float)InternalWindow.FramesPerSecond; }
        set { InternalWindow.FramesPerSecond = value; InternalWindow.UpdatesPerSecond = value; }
    }

    public static nint Handle
    {
        get { return InternalWindow.Handle; }
    }

    private static bool isFocused = true;
    private static DefaultInputHandler WindowInputHandler;

    public static bool IsFocused
    {
        get { return isFocused; }
    }

    public static void InitWindow(string title, int width, int height, WindowState startState = WindowState.Normal, bool VSync = true, RenderingBackend backend = RenderingBackend.OpenGL)
    {
        ActiveBackend = backend;

        WindowOptions options = WindowOptions.Default;
        options.Title = title;
        options.Size = new Vector2D<int>(width, height);
        options.WindowState = startState;
        options.VSync = VSync;

        GraphicsAPI api = backend switch
        {
            RenderingBackend.Vulkan => new GraphicsAPI(ContextAPI.Vulkan, ContextProfile.Core, ContextFlags.Default, new APIVersion(1, 2)),
            RenderingBackend.OpenGLES => new GraphicsAPI(ContextAPI.OpenGLES, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 0)),
            _ => new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 1)),
        };
        options.API = api;
        InternalWindow = Silk.NET.Windowing.Window.Create(options);

        InternalWindow.Load += OnLoad;
        InternalWindow.Update += OnUpdate;
        InternalWindow.Render += OnRender;
        InternalWindow.FocusChanged += OnFocusChanged;
        InternalWindow.Resize += OnResize;
        InternalWindow.FramebufferResize += OnFramebufferResize;
        InternalWindow.Move += OnMove;
        InternalWindow.Closing += OnClose;

        InternalWindow.StateChanged += (state) => { StateChanged?.Invoke(state); };
        InternalWindow.FileDrop += (files) => { FileDrop?.Invoke(files); };

        InternalWindow.FocusChanged += (focused) => { isFocused = focused; };
    }

    /// <summary>
    /// Disposes the current window and resets all static state so that
    /// <see cref="InitWindow"/> can be called again (e.g., for backend fallback).
    /// </summary>
    public static void Cleanup()
    {
        try { WindowInputHandler?.Dispose(); } catch { }
        try { InternalInput?.Dispose(); } catch { }
        try { InternalWindow?.Reset(); } catch { }
        try { InternalWindow?.Dispose(); } catch { }

        InternalWindow = null!;
        InternalInput = null!;
        WindowInputHandler = null!;
        isFocused = true;

        // Clear all static event subscribers so the next setup
        // can resubscribe cleanly without duplicate handlers.
        Load = null;
        Update = null;
        Render = null;
        PostRender = null;
        FocusChanged = null;
        Resize = null;
        FramebufferResize = null;
        Closing = null;
        Move = null;
        StateChanged = null;
        FileDrop = null;
    }

    private static void OnMove(Vector2D<int> d) => Move?.Invoke(d);
    public static void Start() => InternalWindow.Run();
    public static void Stop() => InternalWindow.Close();

    public static void OnLoad()
    {
        InternalInput = InternalWindow.CreateInput();
        WindowInputHandler = new DefaultInputHandler(InternalInput);
        Graphics.Initialize(false);

        // Push Default Handler
        Input.PushHandler(WindowInputHandler);
        Load?.Invoke();
    }

    public static void OnRender(double delta)
    {
        Render?.Invoke((float)delta);
        PostRender?.Invoke((float)delta);
    }

    public static void OnFocusChanged(bool focused)
    {
        FocusChanged?.Invoke(focused);
    }

    public static void OnResize(Vector2D<int> size)
    {
        Resize?.Invoke(size);
    }

    public static void OnFramebufferResize(Vector2D<int> size)
    {
        FramebufferResize?.Invoke(size);
    }

    public static void OnUpdate(double delta)
    {
        Update?.Invoke((float)delta);
        WindowInputHandler.LateUpdate();
    }

    public static void OnClose()
    {
        Closing?.Invoke();
        WindowInputHandler.Dispose();
        Input.PopHandler();
        Graphics.Dispose();
    }

}
