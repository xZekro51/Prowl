// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Linq;

using ImGuiNET;

using Prowl.Runtime;
using Prowl.UI;

namespace Prowl.ImGuiIntegration;

/// <summary>
/// Manages the Dear ImGui lifecycle (Graphite-based renderer creation, per-frame
/// update, rendering, and disposal). Extracted from <see cref="Game"/> to keep
/// the main class focused on frame orchestration and virtual hooks.
/// <para>
/// Uses <see cref="ImGuiRendererGraphite"/> (Graphite abstraction) so ImGui works
/// on both OpenGL and Vulkan backends.
/// </para>
/// </summary>
public sealed class ImGuiManager : IOverlayManager
{
    private ImGuiRendererGraphite? _graphiteRenderer;
    private ImGuiInputHandler? _inputHandler;
    private ImGuiUIRenderer? _renderer;

    /// <summary>Whether the ImGui controller has been initialised and is ready for use.</summary>
    public bool IsReady { get; private set; }

    /// <summary>The <see cref="IUIRenderer"/> created during initialisation.</summary>
    public IUIRenderer? Renderer => _renderer;

    /// <summary>
    /// Optional path to an icon font (e.g. Phosphor Icons) that will be merged into
    /// every font size during atlas construction. Set before <see cref="Initialize"/> is called.
    /// </summary>
    public string? IconFontPath { get; set; }

    /// <summary>First Unicode codepoint in the icon font glyph range.</summary>
    public int IconGlyphRangeMin { get; set; }

    /// <summary>Last Unicode codepoint in the icon font glyph range.</summary>
    public int IconGlyphRangeMax { get; set; }

    /// <summary>
    /// Initialises the Dear ImGui context, Graphite-based renderer, and input handler.
    /// Should be called during the window Load event, after <see cref="Graphics"/>
    /// and the Silk.NET window/input are available.
    /// </summary>
    public void Initialize()
    {
        if (!Graphics.IsGraphiteReady)
        {
            Debug.LogWarning("[ImGuiManager] Graphite device is not ready. Skipping ImGui initialization.");
            return;
        }

        // Create ImGui context
        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

        // Load fonts
        string? systemFont = FindSystemFont();
        if (systemFont != null)
        {
            int baseFontSize = (int)MathF.Round(15 * DpiManager.Scale);
            io.Fonts.AddFontFromFileTTF(systemFont, baseFontSize);

            // Merge icon font into the base/default font if configured
            if (!string.IsNullOrEmpty(IconFontPath) && IconGlyphRangeMin > 0 && IconGlyphRangeMax > 0)
                MergeIconFontUnsafe(io, baseFontSize);

            ImGuiUIRenderer.LoadFonts(systemFont, DpiManager.Scale,
                IconFontPath, IconGlyphRangeMin, IconGlyphRangeMax);
        }

        // Initialise the Graphite-based renderer (uploads font atlas, creates pipeline)
        int fbW = Window.InternalWindow.FramebufferSize.X;
        int fbH = Window.InternalWindow.FramebufferSize.Y;
        _graphiteRenderer = new ImGuiRendererGraphite();
        _graphiteRenderer.Initialize(fbW, fbH);

        // Initialise the backend-agnostic input handler
        _inputHandler = new ImGuiInputHandler(Window.InternalInput);

        _renderer = new ImGuiUIRenderer();
        IsReady = true;
    }

    /// <summary>Updates ImGui input and begins a new frame.</summary>
    public void Update(float delta)
    {
        if (!IsReady) return;

        int fbW = Window.InternalWindow.FramebufferSize.X;
        int fbH = Window.InternalWindow.FramebufferSize.Y;

        _inputHandler?.Update(delta, fbW, fbH);
        _graphiteRenderer?.NewFrame(fbW, fbH);
        ImGui.NewFrame();
    }

    /// <summary>Begins an ImGui frame via the renderer.</summary>
    public void BeginFrame()
    {
        _renderer?.BeginFrame();
    }

    /// <summary>Renders the ImGui draw data via the Graphite backend.</summary>
    public void Render()
    {
        StatsMonitor.Draw();
        ImGui.Render();
        _graphiteRenderer?.RenderDrawData(ImGui.GetDrawData());
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
        ImGuiTextureRegistry.Clear();
        _inputHandler?.Dispose();
        _graphiteRenderer?.Dispose();

        if (IsReady)
            ImGui.DestroyContext();

        IsReady = false;
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

    /// <summary>
    /// Merges icon font glyphs into the most recently added font (the base/default font).
    /// </summary>
    private unsafe void MergeIconFontUnsafe(ImGuiIOPtr io, int pixelSize)
    {
        ImFontConfigPtr config = ImGuiNative.ImFontConfig_ImFontConfig();
        config.MergeMode = true;
        config.PixelSnapH = true;
        config.GlyphMinAdvanceX = pixelSize;
        ushort[] ranges = [(ushort)IconGlyphRangeMin, (ushort)IconGlyphRangeMax, 0];
        fixed (ushort* pRanges = ranges)
        {
            io.Fonts.AddFontFromFileTTF(IconFontPath!, pixelSize, config, (nint)pRanges);
        }
        config.Destroy();
    }
}
