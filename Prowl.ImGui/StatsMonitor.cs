// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Numerics;

using ImGuiNET;

using Prowl.Runtime;
using Prowl.Runtime.Rendering;

namespace Prowl.UI;

/// <summary>
/// An ImGui-based performance stats overlay that displays FPS, FPS graph,
/// draw calls, triangles, and memory utilization. Toggle visibility with F8.
/// </summary>
public static class StatsMonitor
{
    private static bool _visible;

    private const int HistorySize = 120;
    private static readonly float[] _fpsHistory = new float[HistorySize];
    private static int _historyIndex;
    private static float _updateTimer;
    private const float UpdateInterval = 0.05f;

    /// <summary>Whether the stats overlay is currently visible.</summary>
    public static bool IsVisible
    {
        get => _visible;
        set => _visible = value;
    }

    /// <summary>
    /// Call once per frame during the ImGui render phase to handle the F8
    /// toggle and render the stats overlay window when visible.
    /// </summary>
    public static void Draw()
    {
        try
        {
            if (Input.GetKeyDown(KeyCode.F8))
                _visible = !_visible;
        }
        catch
        {
            // Input handler may not be ready yet.
        }

        if (!_visible)
            return;

        float dt = Time.UnscaledDeltaTime;
        float fps = dt > 0f ? 1f / dt : 0f;

        _updateTimer += dt;
        if (_updateTimer >= UpdateInterval)
        {
            _updateTimer -= UpdateInterval;
            _fpsHistory[_historyIndex] = fps;
            _historyIndex = (_historyIndex + 1) % HistorySize;
        }

        float minFps = float.MaxValue;
        float maxFps = 0f;
        float avgFps = 0f;
        for (int i = 0; i < HistorySize; i++)
        {
            float v = _fpsHistory[i];
            if (v < minFps) minFps = v;
            if (v > maxFps) maxFps = v;
            avgFps += v;
        }
        avgFps /= HistorySize;
        if (minFps == float.MaxValue) minFps = 0f;

        float scale = Game.DpiScale;
        float windowWidth = 280f * scale;
        float padding = 10f * scale;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            new Vector2(
                viewport.WorkPos.X + viewport.WorkSize.X - windowWidth - padding,
                viewport.WorkPos.Y + padding),
            ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(windowWidth, 0), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.75f);

        if (ImGui.Begin("Stats##ProwlStatsMonitor",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.AlwaysAutoResize))
        {
            Vector4 color = fps >= 60f
                ? new Vector4(0.4f, 1f, 0.4f, 1f)
                : fps >= 30f
                    ? new Vector4(1f, 1f, 0.4f, 1f)
                    : new Vector4(1f, 0.4f, 0.4f, 1f);
            ImGui.TextColored(color, $"FPS: {fps:F0}");
            ImGui.SameLine();
            ImGui.TextDisabled($"({dt * 1000f:F1} ms)");

            float[] ordered = new float[HistorySize];
            for (int i = 0; i < HistorySize; i++)
                ordered[i] = _fpsHistory[(_historyIndex + i) % HistorySize];

            float graphMax = MathF.Max(maxFps * 1.1f, 1f);
            ImGui.PlotLines("##FPSGraph", ref ordered[0], HistorySize,
                0, null, 0f, graphMax, new Vector2(-1, 40f * scale));

            ImGui.TextDisabled($"Min: {minFps:F0}  Avg: {avgFps:F0}  Max: {maxFps:F0}");

            ImGui.Separator();

            var stats = RenderStats.Instance;
            ImGui.Text($"Draw Calls: {stats.DrawCalls}");
            ImGui.Text($"Triangles:  {stats.Triangles:N0}");
            ImGui.Text($"Vertices:   {stats.Vertices:N0}");
            ImGui.Text($"Batches:    {stats.Batches}");

            ImGui.Separator();

            long gcMemory = GC.GetTotalMemory(false);
            ImGui.Text($"GC Memory:  {FormatBytes(gcMemory)}");

            long workingSet = Environment.WorkingSet;
            ImGui.Text($"Process:    {FormatBytes(workingSet)}");
        }

        ImGui.End();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        if (bytes >= 1024L * 1024L)
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
