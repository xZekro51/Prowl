// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

using Prowl.Runtime.EventSystem;

namespace Prowl.Runtime;

/// <summary>
/// Central manager for DPI awareness and scaling.
/// Handles process DPI awareness declaration, scale detection (system-wide and per-window),
/// and dynamic DPI change notifications when moving between monitors.
/// <para>
/// On Windows, uses Win32 APIs (<c>SetProcessDpiAwarenessContext</c>,
/// <c>GetDpiForWindow</c>, <c>GetDpiForSystem</c>).
/// On other platforms, falls back to comparing framebuffer size to logical window size
/// (handles macOS Retina displays).
/// </para>
/// </summary>
public static class DpiManager
{
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4 (Windows 10 1703+)
    private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (nint)(-4);

    /// <summary>
    /// Current DPI scale factor.
    /// 1.0 = 96 DPI (100%), 1.25 = 120 DPI (125%), 1.5 = 144 DPI (150%), 2.0 = 192 DPI (200%).
    /// </summary>
    public static float MonitorScale { get; private set; } = 1.0f;

    /// <summary>
    /// Additional user-controlled UI scale multiplier (default 1.0).
    /// Set via the editor Preferences panel; persisted between sessions.
    /// </summary>
    public static float UserScale { get; set; } = 1.0f;

    /// <summary>
    /// Combined scale: <see cref="MonitorScale"/> × <see cref="UserScale"/>.
    /// This is the value UI code should use for all sizing calculations.
    /// </summary>
    public static float Scale => MonitorScale * UserScale;

    /// <summary>
    /// The DPI scale that was active when ImGui fonts were rasterised into the font atlas.
    /// Used to compute <c>io.FontGlobalScale</c> after a dynamic DPI change so that
    /// fonts visually match the new DPI without rebuilding the atlas.
    /// </summary>
    public static float BaseFontScale { get; private set; } = 1.0f;

    /// <summary>
    /// Event manager for DPI change notifications.
    /// </summary>
    [Obsolete("Use DpiEvents.Manager or the generated convenience methods instead.")]
    public static EventManager<DpiEvents.EventTypes> DpiEventManager
        => DpiEvents.Manager;

    // ── Process DPI Awareness ────────────────────────────────────────

    /// <summary>
    /// Declares the process as Per-Monitor DPI Aware V2 on Windows.
    /// <para>
    /// Must be called <b>before any window is created</b> (ideally as the first line in
    /// <c>Main()</c>). GLFW also sets this during <c>glfwInit()</c>, but calling it
    /// explicitly ensures the strongest awareness level is set regardless of the
    /// underlying windowing backend version.
    /// </para>
    /// Has no effect on non-Windows platforms. Safe to call multiple times.
    /// </summary>
    public static void EnsureProcessDpiAware()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // Per-Monitor V2 (Windows 10 1703+).
            // Returns false if already set by GLFW — that is expected and harmless.
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch
        {
            // Per-Monitor V1 (Windows 8.1+)
            try { SetProcessDpiAwareness(2); }
            catch
            {
                // Basic system-aware (Vista+)
                try { SetProcessDPIAware(); } catch { }
            }
        }
    }

    // ── Scale Detection ──────────────────────────────────────────────

    /// <summary>
    /// Returns the system-wide DPI scale (primary monitor at boot time).
    /// Can be called <b>before</b> any window is created.
    /// </summary>
    public static float GetSystemScale()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                uint dpi = GetDpiForSystem();
                if (dpi > 0)
                    return MathF.Max(1.0f, dpi / 96f);
            }
            catch { }
        }
        return 1.0f;
    }

    /// <summary>
    /// Returns the DPI scale for the monitor that contains the application window.
    /// Uses the Win32 HWND obtained from Silk.NET's native window interface.
    /// Falls back to <see cref="GetFramebufferScale"/> on non-Windows platforms.
    /// </summary>
    public static float GetWindowScale()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                nint hwnd = GetNativeHwnd();
                if (hwnd != nint.Zero)
                {
                    uint dpi = GetDpiForWindow(hwnd);
                    if (dpi > 0)
                        return MathF.Max(1.0f, dpi / 96f);
                }
            }
            catch { }
        }
        return GetFramebufferScale();
    }

    /// <summary>
    /// Retrieves the real Win32 HWND from Silk.NET's native window interface.
    /// <c>Window.Handle</c> returns the GLFW window pointer, which is NOT a valid
    /// HWND for Win32 DPI APIs. The actual HWND is exposed via
    /// <c>IWindow.Native.Win32.Hwnd</c>.
    /// </summary>
    private static nint GetNativeHwnd()
    {
        try
        {
            var native = Window.InternalWindow?.Native;
            if (native?.Win32 is { } win32)
                return win32.Hwnd;
        }
        catch { }
        return nint.Zero;
    }

    /// <summary>
    /// Detects DPI scale by comparing the framebuffer size to the logical window size.
    /// Works on macOS Retina displays and some Linux configurations where the framebuffer
    /// is larger than the logical window.
    /// </summary>
    public static float GetFramebufferScale()
    {
        var win = Window.InternalWindow;
        if (win == null) return 1.0f;

        var fb = win.FramebufferSize;
        var ws = win.Size;
        if (ws.X <= 0 || ws.Y <= 0) return 1.0f;

        float scale = MathF.Max((float)fb.X / ws.X, (float)fb.Y / ws.Y);
        return MathF.Max(1.0f, scale);
    }

    // ── Internal Helpers ─────────────────────────────────────────────

    /// <summary>
    /// Sets the initial DPI scale and records it as the base font scale.
    /// Called once during startup after per-window detection.
    /// </summary>
    internal static void Initialize(float scale)
    {
        MonitorScale = scale;
        BaseFontScale = scale;
    }

    /// <summary>
    /// Polls the current DPI for the application window and fires
    /// <see cref="DpiChanged"/> if the scale has changed.
    /// Called by <see cref="Game"/> on window-move and framebuffer-resize events.
    /// </summary>
    internal static void CheckForChange()
    {
        float newScale = GetWindowScale();
        if (MathF.Abs(newScale - MonitorScale) < 0.01f) return;

        float oldCombined = Scale;
        MonitorScale = newScale;
        DpiEvents.InvokeOnDpiChanged(new DpiChangedArgs(oldCombined, Scale));
    }

    // ── Win32 P/Invoke ───────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("shcore.dll", SetLastError = true)]
    private static extern int SetProcessDpiAwareness(int awareness);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
