// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Events raised by the platform window (Silk.NET) during its lifecycle.
/// Subscribe via <c>WindowEvents.SubscribeOnXxx(...)</c> or
/// invoke globally with <c>WindowEvents.GlobalInvokeOnXxx(...)</c>.
/// </summary>
[EventDomain(Global = true)]
public static partial class WindowEvents
{
    /// <summary>Raised after the window has loaded and graphics have been initialized.</summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnLoad = new();

    /// <summary>Raised each frame during the window's update tick.</summary>
    [EventArgs(typeof(WindowUpdateArgs))]
    private static readonly EventKey _OnUpdate = new();

    /// <summary>Raised each frame during the window's render tick.</summary>
    [EventArgs(typeof(WindowRenderArgs))]
    private static readonly EventKey _OnRender = new();

    /// <summary>Raised after the main render pass but before Present.</summary>
    [EventArgs(typeof(WindowRenderArgs))]
    private static readonly EventKey _OnPostRender = new();

    /// <summary>Raised when the window gains or loses focus.</summary>
    [EventArgs(typeof(WindowFocusChangedArgs))]
    private static readonly EventKey _OnFocusChanged = new();

    /// <summary>Raised when the window's logical (DPI-scaled) size changes.</summary>
    [EventArgs(typeof(WindowResizeArgs))]
    private static readonly EventKey _OnResize = new();

    /// <summary>Raised when the window's framebuffer size changes.</summary>
    [EventArgs(typeof(WindowResizeArgs))]
    private static readonly EventKey _OnFramebufferResize = new();

    /// <summary>Raised when the application window is closing.</summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnClosing = new();

    /// <summary>Raised when the window is moved to a new position.</summary>
    [EventArgs(typeof(WindowMoveArgs))]
    private static readonly EventKey _OnMove = new();

    /// <summary>Raised when the window state (minimized, maximized, etc.) changes.</summary>
    [EventArgs(typeof(WindowStateChangedArgs))]
    private static readonly EventKey _OnStateChanged = new();

    /// <summary>Raised when files are dropped onto the window from the OS file manager.</summary>
    [EventArgs(typeof(WindowFileDropArgs))]
    private static readonly EventKey _OnFileDrop = new();
}

/// <summary>Typed argument for <see cref="WindowEvents.OnUpdate"/>.</summary>
/// <param name="DeltaTime">Wall-clock time since the previous frame, in seconds.</param>
public readonly record struct WindowUpdateArgs(float DeltaTime);

/// <summary>Typed argument for <see cref="WindowEvents.OnRender"/> and <see cref="WindowEvents.OnPostRender"/>.</summary>
/// <param name="DeltaTime">Wall-clock time allocated to this render tick, in seconds.</param>
public readonly record struct WindowRenderArgs(float DeltaTime);

/// <summary>Typed argument for <see cref="WindowEvents.OnFocusChanged"/>.</summary>
/// <param name="IsFocused">True when the window has gained focus, false when lost.</param>
public readonly record struct WindowFocusChangedArgs(bool IsFocused);

/// <summary>
/// Typed argument for <see cref="WindowEvents.OnResize"/> and <see cref="WindowEvents.OnFramebufferResize"/>.
/// </summary>
/// <param name="Width">New width in pixels.</param>
/// <param name="Height">New height in pixels.</param>
public readonly record struct WindowResizeArgs(int Width, int Height);

/// <summary>Typed argument for <see cref="WindowEvents.OnMove"/>.</summary>
/// <param name="X">New X position in screen coordinates.</param>
/// <param name="Y">New Y position in screen coordinates.</param>
public readonly record struct WindowMoveArgs(int X, int Y);

/// <summary>Typed argument for <see cref="WindowEvents.OnStateChanged"/>.</summary>
/// <param name="State">The new window state as an integer (0=Normal, 1=Minimized, 2=Maximized, 3=Fullscreen).</param>
public readonly record struct WindowStateChangedArgs(int State);

/// <summary>Typed argument for <see cref="WindowEvents.OnFileDrop"/>.</summary>
/// <param name="Files">Array of absolute file paths that were dropped.</param>
public readonly record struct WindowFileDropArgs(string[] Files);
