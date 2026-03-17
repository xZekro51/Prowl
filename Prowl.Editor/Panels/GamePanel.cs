// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Prowl.Editor.Core;
using Prowl.Editor.Docking;
using Prowl.Editor.Services;

namespace Prowl.Editor.Panels;

/// <summary>
/// Game View panel — displays the game camera's render texture via
/// ImGui.Image() so it renders within the window's clipping rectangle
/// and respects z-order with other panels and popups.
/// </summary>
public sealed class GamePanel : EditorPanel
{
    /// <summary> Viewport bounds in screen pixels (set from ImGui window position). </summary>
    public Rect ViewportRect { get; private set; }

    // Resolution presets: name, width, height (0x0 = use panel size)
    private static readonly (string Name, int W, int H)[] ResolutionPresets =
    [
        ("Free (Panel Size)", 0, 0),
        ("1280 × 720 (720p)", 1280, 720),
        ("1920 × 1080 (1080p)", 1920, 1080),
        ("2560 × 1440 (1440p)", 2560, 1440),
        ("3840 × 2160 (4K)", 3840, 2160),
        ("800 × 600", 800, 600),
        ("1024 × 768", 1024, 768),
    ];

    private int _selectedResolution;
    private int _customW = 1280;
    private int _customH = 720;

    // Stats overlay
    private bool _showStats;
    private float _smoothedFps;
    private float _smoothedFrameTime;
    private const float FpsSmoothingFactor = 0.1f;

    /// <summary> The current game view render resolution. </summary>
    public (int Width, int Height) RenderResolution
    {
        get
        {
            if (_selectedResolution >= 0 && _selectedResolution < ResolutionPresets.Length)
            {
                var preset = ResolutionPresets[_selectedResolution];
                if (preset.W > 0 && preset.H > 0) return (preset.W, preset.H);
            }
            // Free mode — will be set from panel size
            return (0, 0);
        }
    }

    public GamePanel() : base("Game") { }

    public override Vector2 GetPanelPadding() => Vector2.Zero;
    protected override void DrawContent()
    {
        // ── Resolution toolbar ─────────────────────────────────
        DrawResolutionToolbar();
        ImGui.Separator();

        // Capture viewport rect from ImGui window content area
        Vector2 regionAvail = ImGui.GetContentRegionAvail();
        if (regionAvail.X < 1 || regionAvail.Y < 1)
        {
            ViewportRect = default;
            return;
        }

        Vector2 cursorScreen = ImGui.GetCursorScreenPos();

        bool isPlaying = EditorServices.TryGet<IEditorTime>(out var time) && time!.IsPlaying;

        // Compute the effective render size
        var (renderW, renderH) = RenderResolution;
        if (renderW <= 0 || renderH <= 0)
        {
            renderW = (int)regionAvail.X;
            renderH = (int)regionAvail.Y;
        }

        // Compute letterboxed display rect (fit render target into available area)
        float panelAspect = regionAvail.X / regionAvail.Y;
        float renderAspect = (float)renderW / Math.Max(renderH, 1);

        float displayW, displayH;
        if (renderAspect > panelAspect)
        {
            displayW = regionAvail.X;
            displayH = regionAvail.X / renderAspect;
        }
        else
        {
            displayH = regionAvail.Y;
            displayW = regionAvail.Y * renderAspect;
        }

        float offsetX = (regionAvail.X - displayW) * 0.5f;
        float offsetY = (regionAvail.Y - displayH) * 0.5f;

        // Draw game RT as an ImGui image (show the latest frame even when paused)
        bool drewImage = false;
        if (EditorServices.TryGet<IEditorRendering>(out var rendering))
        {
            var rt = rendering!.GameViewRT;
            if (rt != null && rt.MainTexture != null)
            {
                nint texId = (nint)rt.MainTexture.Handle.Handle;
                ImGui.SetCursorScreenPos(new Vector2(cursorScreen.X + offsetX, cursorScreen.Y + offsetY));
                ImGui.Image(texId, new Vector2(displayW, displayH), new Vector2(0, 1), new Vector2(1, 0));
                drewImage = true;
            }
        }

        if (!drewImage)
        {
            // Claim the area with an invisible button
            ImGui.InvisibleButton("##GameViewport", regionAvail);
        }

        ViewportRect = new Rect(
            cursorScreen.X, cursorScreen.Y,
            cursorScreen.X + regionAvail.X,
            cursorScreen.Y + regionAvail.Y);

        // Overlay text
        var drawList = ImGui.GetWindowDrawList();

        if (!isPlaying)
        {
            // Stopped overlay — centered hint text
            string stopped = "Press Play to start the game";
            Vector2 textSize = ImGui.CalcTextSize(stopped);
            float cx = cursorScreen.X + (regionAvail.X - textSize.X) * 0.5f;
            float cy = cursorScreen.Y + (regionAvail.Y - textSize.Y) * 0.5f;
            drawList.AddText(new Vector2(cx, cy),
                ImGui.GetColorU32(new Vector4(0.40f, 0.40f, 0.40f, 1f)), stopped);
        }
        else
        {
            // Playing overlay — top-left status
            string status = time!.IsPaused
                ? $"PAUSED - Frame {time.FrameCount}"
                : $"Playing - T:{time.SimulationTime:F1}s  Frame:{time.FrameCount}";

            drawList.AddText(new Vector2(cursorScreen.X + 6 * Game.DpiScale, cursorScreen.Y + 4 * Game.DpiScale),
                ImGui.GetColorU32(new Vector4(0.70f, 0.86f, 0.70f, 0.78f)), status);
        }

        // Show current render resolution
        string resInfo = $"{renderW}\u00D7{renderH}";
        float resInfoW = ImGui.CalcTextSize(resInfo).X;
        drawList.AddText(
            new Vector2(cursorScreen.X + regionAvail.X - resInfoW - 6 * Game.DpiScale, cursorScreen.Y + 4 * Game.DpiScale),
            ImGui.GetColorU32(new Vector4(0.55f, 0.55f, 0.55f, 0.70f)), resInfo);

        // ── Stats overlay ──────────────────────────────────────
        if (_showStats)
            DrawStatsOverlay(cursorScreen, regionAvail);
    }

    private void DrawStatsOverlay(Vector2 origin, Vector2 regionAvail)
    {
        // Compute smoothed FPS / frame-time
        float dt = Time.UnscaledDeltaTime;
        if (dt > 0)
        {
            float instantFps = 1f / dt;
            _smoothedFps += (instantFps - _smoothedFps) * FpsSmoothingFactor;
            _smoothedFrameTime += (dt * 1000f - _smoothedFrameTime) * FpsSmoothingFactor;
        }

        var stats = RenderStats.Instance;

        string[] lines =
        [
            $"FPS: {_smoothedFps:F1}",
            $"Frame: {_smoothedFrameTime:F2} ms",
            $"Draw Calls: {stats.DrawCalls}",
            $"Batches: {stats.Batches}",
            $"Triangles: {stats.Triangles}",
            $"Vertices: {stats.Vertices}",
        ];

        float dpi = Game.DpiScale;
        float lineH = ImGui.GetTextLineHeightWithSpacing();
        float padX = 8 * dpi;
        float padY = 6 * dpi;
        float maxW = 0;
        foreach (var line in lines)
        {
            float w = ImGui.CalcTextSize(line).X;
            if (w > maxW) maxW = w;
        }

        float boxW = maxW + padX * 2;
        float boxH = lineH * lines.Length + padY * 2;

        // Position at top-right of the game viewport
        float boxX = origin.X + regionAvail.X - boxW - 4 * dpi;
        float boxY = origin.Y + 20 * dpi;

        var drawList = ImGui.GetWindowDrawList();
        uint bgCol = ImGui.GetColorU32(new Vector4(0.0f, 0.0f, 0.0f, 0.55f));
        drawList.AddRectFilled(new Vector2(boxX, boxY), new Vector2(boxX + boxW, boxY + boxH), bgCol, 4f * dpi);

        uint textCol = ImGui.GetColorU32(new Vector4(0.85f, 0.92f, 0.85f, 0.95f));
        float y = boxY + padY;
        foreach (var line in lines)
        {
            drawList.AddText(new Vector2(boxX + padX, y), textCol, line);
            y += lineH;
        }
    }

    private void DrawResolutionToolbar()
    {
        ImGui.Text("Resolution:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200 * Game.DpiScale);

        string[] names = new string[ResolutionPresets.Length];
        for (int i = 0; i < ResolutionPresets.Length; i++)
            names[i] = ResolutionPresets[i].Name;

        ImGui.Combo("##ResPreset", ref _selectedResolution, names, names.Length);

        ImGui.SameLine();
        if (ImGui.Button(_showStats ? "Stats \u2713" : "Stats"))
            _showStats = !_showStats;
    }
}
