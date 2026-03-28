// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Quill;
using Prowl.Scribe;
using Prowl.Vector;

using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;

namespace Prowl.UI;

/// <summary>
/// A Paper-based performance stats overlay that displays FPS, FPS graph,
/// draw calls, triangles, and memory utilization. Toggle visibility with F8.
/// Works in standalone game builds without ImGui.
/// </summary>
public static class PaperStatsMonitor
{
    private static bool _visible;

    private const int HistorySize = 120;
    private static readonly float[] _fpsHistory = new float[HistorySize];
    private static int _historyIndex;
    private static float _updateTimer;
    private const float UpdateInterval = 0.05f;

    private static FontFile? _font;
    private static bool _fontLoadAttempted;

    /// <summary>Whether the stats overlay is currently visible.</summary>
    public static bool IsVisible
    {
        get => _visible;
        set => _visible = value;
    }

    /// <summary>
    /// Call once per frame during the Paper render phase to handle the F8
    /// toggle and render the stats overlay when visible.
    /// </summary>
    public static void Draw(Paper paper)
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

        if (!EnsureFont())
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
        if (minFps == float.MaxValue) minFps = 0;

        float scale = Game.DpiScale;
        float windowWidth = 260f * scale;
        float padding = 10f * scale;
        float lineHeight = 18f * scale;
        float graphHeight = 40f * scale;
        float sectionPad = 6f * scale;
        float innerPad = 4f * scale;
        float fontSize = 13f * scale;
        float smallFontSize = 11f * scale;

        var bgColor = new Color(0.1f, 0.1f, 0.1f, 0.80f);
        var separatorColor = new Color(0.3f, 0.3f, 0.3f, 1f);
        var dimTextColor = new Color(0.6f, 0.6f, 0.6f, 1f);
        var whiteTextColor = new Color(0.9f, 0.9f, 0.9f, 1f);

        Color fpsColor = fps >= 60f
            ? new Color(0.4f, 1f, 0.4f, 1f)
            : fps >= 30f
                ? new Color(1f, 1f, 0.4f, 1f)
                : new Color(1f, 0.4f, 0.4f, 1f);

        var stats = RenderStats.Instance;

        string fpsText = $"FPS: {fps:F0}";
        string msText = $"({dt * 1000f:F1} ms)";
        string minMaxAvg = $"Min: {minFps:F0}  Avg: {avgFps:F0}  Max: {maxFps:F0}";
        string drawCalls = $"Draw Calls: {stats.DrawCalls}";
        string triangles = $"Triangles:  {stats.Triangles:N0}";
        string vertices = $"Vertices:   {stats.Vertices:N0}";
        string batches = $"Batches:    {stats.Batches}";
        long gcMemory = GC.GetTotalMemory(false);
        long workingSet = Environment.WorkingSet;
        string gcText = $"GC Memory:  {FormatBytes(gcMemory)}";
        string procText = $"Process:    {FormatBytes(workingSet)}";

        FontFile font = _font!;

        // Position the panel in the top-right corner using SelfDirected positioning
        using (paper.Column("StatsPanel")
            .PositionType(PositionType.SelfDirected)
            .Right(padding)
            .Top(padding)
            .Width(windowWidth)
            .BackgroundColor(bgColor)
            .Rounded(6f * scale)
            .ChildLeft(innerPad)
            .ChildRight(innerPad)
            .ChildTop(innerPad)
            .ChildBottom(innerPad)
            .ColBetween(2f * scale)
            .Enter())
        {
            // FPS line
            using (paper.Box("FpsLine").Width(UnitValue.Stretch(1)).Height(lineHeight).Enter())
            {
                paper.AddActionElement((Canvas canvas, Rect rect) =>
                {
                    canvas.DrawText(fpsText, rect.Min.X, rect.Min.Y, (Color32)fpsColor, fontSize, font);
                    var fpsSize = canvas.MeasureText(fpsText, fontSize, font);
                    canvas.DrawText(msText, rect.Min.X + fpsSize.X + 8f * scale, rect.Min.Y, (Color32)dimTextColor, smallFontSize, font);
                });
            }

            // FPS Graph
            using (paper.Box("FpsGraph").Width(UnitValue.Stretch(1)).Height(graphHeight).BackgroundColor(new Color(0.05f, 0.05f, 0.05f, 1f)).Enter())
            {
                paper.AddActionElement((Canvas canvas, Rect rect) =>
                {
                    float gMax = MathF.Max(maxFps * 1.1f, 1f);
                    float barWidth = rect.Size.X / HistorySize;
                    var barColor = new Color(0.3f, 0.7f, 1f, 0.8f);

                    for (int i = 0; i < HistorySize; i++)
                    {
                        float v = _fpsHistory[(_historyIndex + i) % HistorySize];
                        float normalized = MathF.Min(v / gMax, 1f);
                        float barH = normalized * rect.Size.Y;
                        float x = rect.Min.X + i * barWidth;
                        float y = rect.Max.Y - barH;

                        canvas.RectFilled(x, y, barWidth, barH, (Color32)barColor);
                    }
                });
            }

            // Min/Avg/Max
            using (paper.Box("MinMaxAvg").Width(UnitValue.Stretch(1)).Height(lineHeight).Enter())
            {
                paper.AddActionElement((Canvas canvas, Rect rect) =>
                {
                    canvas.DrawText(minMaxAvg, rect.Min.X, rect.Min.Y, (Color32)dimTextColor, smallFontSize, font);
                });
            }

            // Separator
            using (paper.Box("Sep1").Width(UnitValue.Stretch(1)).Height(1f).BackgroundColor(separatorColor).Enter()) { }

            // Render stats
            DrawTextLine(paper, "StatDC", drawCalls, font, fontSize, whiteTextColor, lineHeight, scale);
            DrawTextLine(paper, "StatTri", triangles, font, fontSize, whiteTextColor, lineHeight, scale);
            DrawTextLine(paper, "StatVert", vertices, font, fontSize, whiteTextColor, lineHeight, scale);
            DrawTextLine(paper, "StatBatch", batches, font, fontSize, whiteTextColor, lineHeight, scale);

            // Separator
            using (paper.Box("Sep2").Width(UnitValue.Stretch(1)).Height(1f).BackgroundColor(separatorColor).Enter()) { }

            // Memory stats
            DrawTextLine(paper, "MemGC", gcText, font, fontSize, whiteTextColor, lineHeight, scale);
            DrawTextLine(paper, "MemProc", procText, font, fontSize, whiteTextColor, lineHeight, scale);
        }
    }

    private static void DrawTextLine(Paper paper, string id, string text, FontFile font, float fontSize, Color color, float height, float scale)
    {
        using (paper.Box(id).Width(UnitValue.Stretch(1)).Height(height).Enter())
        {
            paper.AddActionElement((Canvas canvas, Rect rect) =>
            {
                canvas.DrawText(text, rect.Min.X, rect.Min.Y, (Color32)color, fontSize, font);
            });
        }
    }

    private static bool EnsureFont()
    {
        if (_font != null)
            return true;

        if (_fontLoadAttempted)
            return false;

        _fontLoadAttempted = true;

        try
        {
            using Stream stream = EmbeddedResources.GetStream("Inter-Regular.ttf");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            byte[] data = ms.ToArray();
            _font = new FontFile(data);
            Debug.Log($"[PaperStatsMonitor] Font loaded successfully ({data.Length} bytes)");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PaperStatsMonitor] Failed to load font: {ex.Message}");
            return false;
        }
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
