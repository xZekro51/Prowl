// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Graphite;

using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

using GlfwApi = Silk.NET.GLFW.Glfw;

namespace Prowl.Runtime;

public static class Window
{

    public static IWindow InternalWindow { get; internal set; }
    public static IInputContext InternalInput { get; internal set; }

    /// <summary>
    /// The rendering backend that was actually used when the window was created.
    /// This may differ from the requested backend if a fallback occurred.
    /// </summary>
    public static GraphicsBackendType ActiveBackend { get; private set; } = GraphicsBackendType.OpenGL;

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

    /// <summary>
    /// Stores an exception that occurred during <see cref="OnLoad"/> so it can
    /// be rethrown after <see cref="Start"/> returns.  This guarantees the
    /// exception reaches <see cref="Game.Run"/>'s fallback logic even if
    /// Silk.NET's event pump does not propagate exceptions from callbacks.
    /// </summary>
    private static Exception? _loadException;

    /// <summary>
    /// When <c>true</c>, all Silk.NET window callbacks become no-ops.
    /// Set after a fatal error in <see cref="OnLoad"/> to prevent subsequent
    /// Render/Update callbacks from executing against uninitialized state.
    /// Without this guard, GLFW may fire one more frame callback after
    /// <see cref="Silk.NET.Windowing.IWindow.Close"/> is called, causing
    /// exceptions to propagate through the native event loop boundary and
    /// terminate the process before the managed fallback logic can run.
    /// </summary>
    private static bool _fatalError;

    public static bool IsFocused
    {
        get { return isFocused; }
    }

    public static void InitWindow(string title, int width, int height, WindowState startState = WindowState.Normal, bool VSync = true, GraphicsBackendType backend = GraphicsBackendType.OpenGL)
    {
        ActiveBackend = backend;

        // Pre-validate Vulkan support via GLFW to produce a catchable managed
        // exception instead of a fatal native access-violation (0xC0000005) in
        // glfwCreateWindow that would bypass Game.Run's Vulkan → OpenGL fallback.
        if (backend == GraphicsBackendType.Vulkan)
            PreValidateVulkanSupport();

        WindowOptions options = WindowOptions.Default;
        options.Title = title;
        options.Size = new Vector2D<int>(width, height);
        options.WindowState = startState;
        options.VSync = VSync;

        GraphicsAPI api = backend switch
        {
            GraphicsBackendType.Vulkan => new GraphicsAPI(ContextAPI.Vulkan, ContextProfile.Core, ContextFlags.Default, new APIVersion(1, 3)),
            _ => new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 1)),
        };
        options.API = api;

        Debug.Log($"Defined window options...");

        InternalWindow = Silk.NET.Windowing.Window.Create(options);

        Debug.Log($"Created Window through options...");


        InternalWindow.Load += OnLoad;
        InternalWindow.Update += OnUpdate;
        InternalWindow.Render += OnRender;
        InternalWindow.FocusChanged += OnFocusChanged;
        InternalWindow.Resize += OnResize;
        InternalWindow.FramebufferResize += OnFramebufferResize;
        InternalWindow.Move += OnMove;
        InternalWindow.Closing += OnClose;

        Debug.Log($"Added all stuff to callbacks");

        InternalWindow.StateChanged += (state) => { WindowEvents.InvokeOnStateChanged(new WindowStateChangedArgs((int)state)); };
        InternalWindow.FileDrop += (files) => { WindowEvents.InvokeOnFileDrop(new WindowFileDropArgs(files)); };

        InternalWindow.FocusChanged += (focused) => { isFocused = focused; };
    }

    /// <summary>
    /// Verifies that GLFW can initialize and reports Vulkan as supported.
    /// Throws a catchable <see cref="PlatformNotSupportedException"/> instead of
    /// allowing a fatal native access-violation in <c>glfwCreateWindow</c> that
    /// would kill the process before <see cref="Game.Run"/>'s fallback logic runs.
    /// </summary>
    private static void PreValidateVulkanSupport()
    {
        try
        {
            GlfwApi glfw = GlfwApi.GetApi();
            if (!glfw.Init())
                throw new PlatformNotSupportedException(
                    "GLFW initialization failed — cannot create a Vulkan window.");

            if (!glfw.VulkanSupported())
                throw new PlatformNotSupportedException(
                    "Vulkan is not supported on this system (glfwVulkanSupported returned false).");
        }
        catch (PlatformNotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PlatformNotSupportedException(
                $"Failed to verify Vulkan support via GLFW: {ex.Message}", ex);
        }
        // Do NOT call glfw.Terminate() — Silk.NET reuses the initialized GLFW
        // state and glfwInit is a safe no-op when already initialized.
    }

    /// <summary>
    /// Disposes the current window and resets all static state so that
    /// <see cref="InitWindow"/> can be called again (e.g., for backend fallback).
    /// </summary>
    public static void Cleanup()
    {
        try { WindowInputHandler?.Dispose(); } catch { }
        try { InternalInput?.Dispose(); } catch { }
        try { Graphics.Dispose(); } catch { }
        try { InternalWindow?.Reset(); } catch { }
        try { InternalWindow?.Dispose(); } catch { }

        InternalWindow = null!;
        InternalInput = null!;
        WindowInputHandler = null!;
        isFocused = true;
        _loadException = null;
        _fatalError = false;
    }

    private static void OnMove(Vector2D<int> d) => WindowEvents.InvokeOnMove(new WindowMoveArgs(d.X, d.Y));

    public static void Start()
    {
        _loadException = null;
        _fatalError = false;
        Debug.Log("[Window] Starting event loop...");
        try
        {
            InternalWindow.Run();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Window] Exception escaped from event loop: {ex}");
            // If we already captured an init exception in OnLoad, prefer it —
            // the current exception is a secondary failure from cleanup.
            _loadException ??= ex;
        }
        Debug.Log($"[Window] Event loop exited. _loadException={(_loadException != null ? _loadException.GetType().Name : "null")}");

        // If OnLoad caught an exception, rethrow it now so callers
        // (Game.Run) can fall back to another backend.
        if (_loadException is { } loadEx)
        {
            _loadException = null;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(loadEx);
        }
    }

    public static void Stop() => InternalWindow.Close();

    public static void OnLoad()
    {
        try
        {
            Debug.Log($"[SILK] OnLoad fired — creating input context...");
            InternalInput = InternalWindow.CreateInput();
            WindowInputHandler = new DefaultInputHandler(InternalInput);
            Debug.Log($"[SILK] INITIALIZING GRAPHICS WITH BACKEND: {ActiveBackend}");
            Graphics.Initialize(ActiveBackend, false);
            Debug.Log($"[SILK] Graphics initialized successfully.");

            // Push Default Handler
            Input.PushHandler(WindowInputHandler);
            WindowEvents.InvokeOnLoad();
            Debug.Log($"[SILK] OnLoad completed successfully.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SILK] Fatal error during window load ({ActiveBackend}): {ex.GetType().Name}: {ex.Message}");
            Debug.LogError($"[SILK] Stack trace: {ex.StackTrace}");
            if (ex.InnerException != null)
                Debug.LogError($"[SILK] Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            _loadException = ex;
            _fatalError = true;
            try { InternalWindow.Close(); } catch { }
        }
    }

    public static void OnRender(double delta)
    {
        if (_fatalError || !Graphics.IsGraphiteReady)
            return;

        if (!Graphics.Graphite.BeginFrame())
            return;

        try
        {
            Rendering.GraphiteMaterialBinder.BeginFrame();

            WindowEvents.InvokeOnRender(new WindowRenderArgs((float)delta));
            WindowEvents.InvokeOnPostRender(new WindowRenderArgs((float)delta));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Window] Exception during render: {ex}");
        }
        finally
        {
            Graphics.Graphite.Present();
        }
    }

    public static void OnFocusChanged(bool focused)
    {
        WindowEvents.InvokeOnFocusChanged(new WindowFocusChangedArgs(focused));
    }

    public static void OnResize(Vector2D<int> size)
    {
        WindowEvents.InvokeOnResize(new WindowResizeArgs(size.X, size.Y));
    }

    public static void OnFramebufferResize(Vector2D<int> size)
    {
        if (Graphics.IsGraphiteReady)
            Graphics.Graphite.ResizeSwapchain((uint)size.X, (uint)size.Y);
        WindowEvents.InvokeOnFramebufferResize(new WindowResizeArgs(size.X, size.Y));
    }

    public static void OnUpdate(double delta)
    {
        if (_fatalError)
            return;

        WindowEvents.InvokeOnUpdate(new WindowUpdateArgs((float)delta));
        WindowInputHandler?.LateUpdate();
    }

    public static void OnClose()
    {
        try { WindowEvents.InvokeOnClosing(); } catch { }
        try { WindowInputHandler?.Dispose(); } catch { }
        try { Input.PopHandler(); } catch { }
        try { Graphics.Dispose(); } catch { }
    }

}
