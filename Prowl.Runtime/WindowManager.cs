// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Runtime.Graphite;

namespace Prowl.Runtime;

/// <summary>
/// Manages the Silk.NET window lifecycle and DPI-aware sizing.
/// Extracted from <see cref="Game"/> to keep the main class focused on
/// frame orchestration and virtual hooks.
/// </summary>
public sealed class WindowManager
{
    private int _logicalWidth;
    private int _logicalHeight;

    /// <summary>The DPI-scaled width used when the window was first created.</summary>
    public int InitialScaledWidth { get; private set; }

    /// <summary>The DPI-scaled height used when the window was first created.</summary>
    public int InitialScaledHeight { get; private set; }

    /// <summary>
    /// Creates the Silk.NET window with DPI-aware sizing.
    /// Returns the system-level DPI scale used for the initial size calculation.
    /// </summary>
    public float CreateWindow(string title, int logicalWidth, int logicalHeight, GraphicsBackendType backend = GraphicsBackendType.OpenGL)
    {
        _logicalWidth = logicalWidth;
        _logicalHeight = logicalHeight;

        DpiManager.EnsureProcessDpiAware();
        float systemScale = DpiManager.GetSystemScale();
        DpiManager.Initialize(systemScale);

        InitialScaledWidth = (int)MathF.Round(logicalWidth * systemScale);
        InitialScaledHeight = (int)MathF.Round(logicalHeight * systemScale);

        Window.InitWindow(title, InitialScaledWidth, InitialScaledHeight,
            Silk.NET.Windowing.WindowState.Normal, false, backend);

        return systemScale;
    }

    /// <summary>
    /// Refines DPI using per-window detection after the window is visible.
    /// Handles multi-monitor setups where the window's monitor may differ
    /// from the primary monitor used for system-level DPI.
    /// Should be called during the Load event.
    /// </summary>
    public void RefineWindowDpi(float systemScale)
    {
        float windowScale = DpiManager.GetWindowScale();
        DpiManager.Initialize(windowScale);
        if (MathF.Abs(windowScale - systemScale) > 0.01f)
        {
            int newW = (int)MathF.Round(_logicalWidth * windowScale);
            int newH = (int)MathF.Round(_logicalHeight * windowScale);
            Window.InternalWindow.Size = new Silk.NET.Maths.Vector2D<int>(newW, newH);
        }
    }

    /// <summary>
    /// Resizes the window to maintain the same logical size based on the current
    /// monitor DPI. Called when the DPI changes at runtime (e.g., window moved
    /// to another monitor).
    /// </summary>
    public void ResizeForDpi()
    {
        int newW = (int)MathF.Round(_logicalWidth * DpiManager.MonitorScale);
        int newH = (int)MathF.Round(_logicalHeight * DpiManager.MonitorScale);
        Window.InternalWindow.Size = new Silk.NET.Maths.Vector2D<int>(newW, newH);
    }
}
