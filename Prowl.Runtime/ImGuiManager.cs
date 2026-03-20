// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Linq;

using ImGuiNET;

using Prowl.UI;

using SilkImGui = Silk.NET.OpenGL.Extensions.ImGui;

namespace Prowl.Runtime;

/// <summary>
/// Manages the Dear ImGui lifecycle (controller creation, per-frame update,
/// rendering, and disposal). Extracted from <see cref="Game"/> to keep the
/// main class focused on frame orchestration and virtual hooks.
/// </summary>
public sealed class ImGuiManager : IDisposable
{
    private SilkImGui.ImGuiController? _controller;
    private ImGuiUIRenderer? _renderer;

    /// <summary>Whether the ImGui controller has been initialised and is ready for use.</summary>
    public bool IsReady { get; private set; }

    /// <summary>The <see cref="ImGuiUIRenderer"/> created during initialisation.</summary>
    public ImGuiUIRenderer? Renderer => _renderer;

    /// <summary>
    /// Initialises the Dear ImGui controller and renderer.
    /// Should be called during the window Load event, after <see cref="Graphics"/>
    /// and the Silk.NET window/input are available.
    /// </summary>
    public void Initialize()
    {
        string? systemFont = FindSystemFont();
        int baseFontSize = (int)MathF.Round(14 * DpiManager.Scale);
        _controller = new SilkImGui.ImGuiController(
            Graphics.GL,
            Window.InternalWindow,
            Window.InternalInput,
            systemFont != null ? new SilkImGui.ImGuiFontConfig(systemFont, baseFontSize) : null,
            () =>
            {
                var io = ImGui.GetIO();
                io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
                if (systemFont != null)
                    ImGuiUIRenderer.LoadFonts(systemFont, DpiManager.Scale);
            });
        _renderer = new ImGuiUIRenderer();
        IsReady = true;
    }

    /// <summary>Updates the ImGui controller for the current frame.</summary>
    public void Update(float delta)
    {
        _controller?.Update(delta);
    }

    /// <summary>Begins an ImGui frame via the renderer.</summary>
    public void BeginFrame()
    {
        _renderer?.BeginFrame();
    }

    /// <summary>Renders the ImGui draw data.</summary>
    public void Render()
    {
        _controller?.Render();
    }

    /// <summary>
    /// Adjusts ImGui font rendering to match a new DPI scale without
    /// rebuilding the font atlas.
    /// </summary>
    public void OnDpiChanged(float newScale)
    {
        var io = ImGui.GetIO();
        io.FontGlobalScale = newScale / DpiManager.BaseFontScale;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _controller?.Dispose();
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
