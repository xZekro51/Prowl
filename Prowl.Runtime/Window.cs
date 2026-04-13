// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Runtime.Events;

using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Prowl.Runtime;

public static class Window
{

    public static IWindow InternalWindow { get; internal set; }
    public static IInputContext InternalInput { get; internal set; }

    //public static event Action? Load;
    //public static event Action<float>? Update;
    //public static event Action<float>? Render;
    //public static event Action<float>? PostRender;
    //public static event Action<bool>? FocusChanged;
    //public static event Action<Vector2D<int>>? Resize;
    //public static event Action<Vector2D<int>>? FramebufferResize;
    //public static event Action? Closing;

    //public static event Action<Vector2D<int>>? Move;
    //public static event Action<WindowState>? StateChanged;
    //public static event Action<string[]>? FileDrop;

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

    public static void InitWindow(string title, int width, int height, WindowState startState = WindowState.Normal, bool VSync = true)
    {
        WindowOptions options = WindowOptions.Default;
        options.Title = title;
        options.Size = new Vector2D<int>(width, height);
        options.WindowState = startState;
        options.VSync = VSync;
        var api = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 1));
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

        InternalWindow.StateChanged += (state) => { WindowEvents.StateChanged.Invoke(new(state)); };
        InternalWindow.FileDrop += (files) => { WindowEvents.FileDrop.Invoke(new(files)); };

        InternalWindow.FocusChanged += (focused) => { isFocused = focused; };
    }

    private static void OnMove(Vector2D<int> d) => WindowEvents.Move.Invoke(new(d));
    public static void Start() => InternalWindow.Run();
    public static void Stop() => InternalWindow.Close();

    public static void OnLoad()
    {
        InternalInput = InternalWindow.CreateInput();
        WindowInputHandler = new(InternalInput);
        Graphics.Initialize(false);

        // Push Default Handler
        Input.PushHandler(WindowInputHandler);
        WindowEvents.Load.Invoke();
    }

    public static void OnRender(double delta)
    {
        WindowEvents.Render.Invoke(new((float)delta));
        WindowEvents.PostRender.Invoke(new((float)delta));
    }

    public static void OnFocusChanged(bool focused)
    {
        WindowEvents.FocusChanged.Invoke(new(focused));
    }

    public static void OnResize(Vector2D<int> size)
    {
        WindowEvents.Resize.Invoke(new(size));
    }

    public static void OnFramebufferResize(Vector2D<int> size)
    {
        WindowEvents.FramebufferResize.Invoke(new(size));
    }

    public static void OnUpdate(double delta)
    {
        WindowEvents.Update.Invoke(new WindowEvents.FloatArgs((float)delta));
        WindowInputHandler.LateUpdate();
    }

    public static void OnClose()
    {
        WindowEvents.Closing.Invoke();
        WindowInputHandler.Dispose();
        Input.PopHandler();
        Graphics.Dispose();
    }

}
