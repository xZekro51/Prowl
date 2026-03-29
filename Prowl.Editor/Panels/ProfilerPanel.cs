// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ImGuiNET;

using Prowl.Editor.Docking;
using Prowl.Editor.Profiling;
using Prowl.Runtime;
using Prowl.Runtime.Profiling;

namespace Prowl.Editor.Panels;

/// <summary>
/// Profiling target — either the editor process itself or a remote debug
/// player connected via TCP.
/// </summary>
public enum ProfilerTarget
{
    Editor,
    RemotePlayer,
}

/// <summary>
/// Dockable profiler window that displays hierarchical per-frame CPU timing
/// data collected by <see cref="Profiler"/>. Supports both local editor
/// profiling and remote profiling of a debug player build over TCP,
/// similar to Unity's Profiler window.
/// <para>
/// Features:
/// <list type="bullet">
///   <item>Target selector (Editor / Remote Player)</item>
///   <item>Frame time graph (click to inspect a specific frame)</item>
///   <item>Hierarchical sample list with click-to-expand drill-down</item>
///   <item>Category colour coding (Physics, Rendering, Scripts, UI, …)</item>
///   <item>Detailed description tooltip for every registered section</item>
///   <item>Pause / resume recording</item>
/// </list>
/// </para>
/// </summary>
public sealed class ProfilerPanel : EditorPanel
{
    // ── State ────────────────────────────────────────────────────

    /// <summary> Index into the history ring (0 = most recent). </summary>
    private int _selectedFrameAge;

    /// <summary> When true the profiler keeps recording. </summary>
    private bool _recording = true;

    /// <summary> When true the profiler is globally enabled. </summary>
    private bool _enabled;

    /// <summary> Current profiling target. </summary>
    private ProfilerTarget _target = ProfilerTarget.Editor;

    // Remote connection
    private ProfilerClient? _remoteClient;
    private string _remoteAddress = "127.0.0.1";
    private int _remotePort = ProfilerProtocol.DefaultPort;

    // Drill-down state: set of expanded section names (by identity = name + depth)
    private readonly HashSet<string> _expandedSections = new(StringComparer.Ordinal);

    // Category → colour mapping
    private static readonly Dictionary<string, Vector4> CategoryColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Core"]      = new(0.60f, 0.60f, 0.60f, 1f),
        ["Physics"]   = new(0.30f, 0.75f, 0.45f, 1f),
        ["Scripts"]   = new(0.40f, 0.65f, 1.00f, 1f),
        ["Rendering"] = new(0.95f, 0.65f, 0.25f, 1f),
        ["UI"]        = new(0.85f, 0.45f, 0.85f, 1f),
        ["Audio"]     = new(0.55f, 0.80f, 0.85f, 1f),
        ["Editor"]    = new(0.75f, 0.55f, 0.40f, 1f),
    };

    private static readonly Vector4 DefaultCategoryColor = new(0.65f, 0.65f, 0.65f, 1f);

    // Graph hover state
    private const int GraphHeight = 60;
    private const float TargetFrameMs = 16.667f; // 60 fps

    public ProfilerPanel() : base("Profiler")
    {
        IsOpen = false; // hidden by default; toggled from menu
    }

    protected override void DrawContent()
    {
        // ── Toolbar ──────────────────────────────────────────────
        DrawToolbar();
        ImGui.Separator();

        // ── Connection bar (when targeting remote) ───────────────
        if (_target == ProfilerTarget.RemotePlayer)
        {
            DrawConnectionBar();
            ImGui.Separator();
        }

        if (!_enabled)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f),
                "Profiler is disabled. Click \"Enable\" to start recording.");
            return;
        }

        int frameCount = GetFrameCount();
        if (frameCount == 0)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f),
                "No frames recorded yet.");
            return;
        }

        // ── Frame-time graph ─────────────────────────────────────
        DrawFrameGraph();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Selected frame detail ────────────────────────────────
        var frame = GetFrame(_selectedFrameAge);
        if (frame == null)
        {
            ImGui.Text("Frame not available.");
            return;
        }

        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f),
            $"Frame #{frameCount - _selectedFrameAge}  —  " +
            $"{frame.TotalMs:F2} ms  ({1000.0 / frame.TotalMs:F0} fps)  —  " +
            $"{frame.Samples.Length} section(s)");

        ImGui.Spacing();

        DrawSampleTable(frame);
    }

    // ── Data source helpers ──────────────────────────────────────

    private int GetFrameCount()
    {
        return _target == ProfilerTarget.RemotePlayer && _remoteClient != null
            ? _remoteClient.FrameCount
            : Profiler.FrameCount;
    }

    private ProfilerFrame? GetFrame(int age)
    {
        return _target == ProfilerTarget.RemotePlayer && _remoteClient != null
            ? _remoteClient.GetFrame(age)
            : Profiler.GetFrame(age);
    }

    private void ClearData()
    {
        if (_target == ProfilerTarget.RemotePlayer && _remoteClient != null)
            _remoteClient.Clear();
        else
            Profiler.Clear();
    }

    // ── Toolbar ──────────────────────────────────────────────────

    private void DrawToolbar()
    {
        // ── Target selector ────────────────────────────────────
        ImGui.SetNextItemWidth(140 * Game.DpiScale);
        int targetIdx = (int)_target;
        if (ImGui.Combo("##ProfilerTarget", ref targetIdx, "Editor\0Remote Player\0"))
        {
            var newTarget = (ProfilerTarget)targetIdx;
            if (newTarget != _target)
            {
                _target = newTarget;
                _selectedFrameAge = 0;
                _expandedSections.Clear();
            }
        }

        ImGui.SameLine();

        // Enable / Disable
        if (_enabled)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.55f, 0.20f, 1f));
            if (ImGui.SmallButton("Enabled"))
            {
                _enabled = false;
                if (_target == ProfilerTarget.Editor)
                    Profiler.Enabled = false;
            }
            ImGui.PopStyleColor();
        }
        else
        {
            if (ImGui.SmallButton("Enable"))
            {
                _enabled = true;
                if (_target == ProfilerTarget.Editor)
                    Profiler.Enabled = true;
            }
        }

        ImGui.SameLine();

        // Record / Pause
        if (_enabled)
        {
            if (_recording)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.70f, 0.25f, 0.25f, 1f));
                if (ImGui.SmallButton("Pause"))
                    _recording = false;
                ImGui.PopStyleColor();
            }
            else
            {
                if (ImGui.SmallButton("Resume"))
                    _recording = true;
            }

            // When paused, stop the editor profiler so no new data overwrites
            // the frame the user is inspecting.
            if (_target == ProfilerTarget.Editor)
                Profiler.Enabled = _recording;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
        {
            ClearData();
            _selectedFrameAge = 0;
            _expandedSections.Clear();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Collapse All"))
        {
            _expandedSections.Clear();
        }
    }

    // ── Connection bar for remote profiling ──────────────────────

    private void DrawConnectionBar()
    {
        bool isConnected = _remoteClient?.IsConnected == true;

        ImGui.Text("Remote:");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(140 * Game.DpiScale);
        ImGui.InputText("##Address", ref _remoteAddress, 256);
        ImGui.SameLine();

        ImGui.SetNextItemWidth(70 * Game.DpiScale);
        ImGui.InputInt("##Port", ref _remotePort, 0, 0);
        ImGui.SameLine();

        if (isConnected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.55f, 0.20f, 1f));
            if (ImGui.SmallButton("Disconnect"))
            {
                _remoteClient?.Disconnect();
            }
            ImGui.PopStyleColor();

            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.3f, 1f), "● Connected");
        }
        else
        {
            if (ImGui.SmallButton("Connect"))
            {
                _remoteClient ??= new ProfilerClient();
                _remoteClient.Connect(_remoteAddress, _remotePort);
            }

            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "○ Disconnected");
        }

        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
            "(Debug builds only — player must be built in Debug configuration)");
    }

    // ── Frame-time graph ─────────────────────────────────────────

    private void DrawFrameGraph()
    {
        int frameCount = GetFrameCount();
        if (frameCount == 0) return;

        int visibleFrames = Math.Min(frameCount, Profiler.MaxHistoryFrames);
        float avail = ImGui.GetContentRegionAvail().X;
        float barW = Math.Max(1f, avail / visibleFrames);

        var drawList = ImGui.GetWindowDrawList();
        Vector2 cursor = ImGui.GetCursorScreenPos();
        float graphH = GraphHeight * Game.DpiScale;

        // Background
        drawList.AddRectFilled(cursor, cursor + new Vector2(avail, graphH),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.08f, 0.08f, 1f)));

        // 16.67 ms target line
        float targetY = cursor.Y + graphH - (TargetFrameMs / 33.333f) * graphH;
        drawList.AddLine(
            new Vector2(cursor.X, targetY),
            new Vector2(cursor.X + avail, targetY),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.60f, 0.35f, 0.50f)));

        // Bars
        for (int i = 0; i < visibleFrames; i++)
        {
            var f = GetFrame(visibleFrames - 1 - i);
            if (f == null) continue;

            float ratio = (float)(f.TotalMs / 33.333); // scale: 0ms..33ms maps to 0..1
            ratio = Math.Clamp(ratio, 0f, 1f);
            float h = ratio * graphH;

            Vector2 barMin = new(cursor.X + i * barW, cursor.Y + graphH - h);
            Vector2 barMax = new(cursor.X + (i + 1) * barW - 1, cursor.Y + graphH);

            Vector4 color = f.TotalMs > TargetFrameMs
                ? new Vector4(0.90f, 0.35f, 0.25f, 0.85f)
                : new Vector4(0.30f, 0.65f, 0.90f, 0.85f);

            bool isSelected = (visibleFrames - 1 - i) == _selectedFrameAge;
            if (isSelected)
                color = new Vector4(1f, 1f, 0.30f, 1f);

            drawList.AddRectFilled(barMin, barMax, ImGui.ColorConvertFloat4ToU32(color));
        }

        // Invisible button for click detection
        ImGui.InvisibleButton("##graph", new Vector2(avail, graphH));
        if (ImGui.IsItemHovered())
        {
            float mouseX = ImGui.GetMousePos().X - cursor.X;
            int hoverIdx = (int)(mouseX / barW);
            int age = visibleFrames - 1 - hoverIdx;
            if (age >= 0 && age < visibleFrames)
            {
                var hf = GetFrame(age);
                if (hf != null)
                    ImGui.SetTooltip($"Frame: {hf.TotalMs:F2} ms ({1000.0 / hf.TotalMs:F0} fps)");

                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    _selectedFrameAge = age;
            }
        }

        // Always stick to most-recent when recording
        if (_recording)
            _selectedFrameAge = 0;
    }

    // ── Sample table with drill-down ─────────────────────────────

    private void DrawSampleTable(ProfilerFrame frame)
    {
        ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                                ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;

        float tableH = ImGui.GetContentRegionAvail().Y;

        if (!ImGui.BeginTable("##ProfilerSamples", 4, flags, new Vector2(0, tableH)))
            return;

        ImGui.TableSetupColumn("Section",   ImGuiTableColumnFlags.WidthStretch, 0.45f);
        ImGui.TableSetupColumn("Category",  ImGuiTableColumnFlags.WidthFixed, 80 * Game.DpiScale);
        ImGui.TableSetupColumn("Time (ms)", ImGuiTableColumnFlags.WidthFixed, 80 * Game.DpiScale);
        ImGui.TableSetupColumn("% Frame",   ImGuiTableColumnFlags.WidthFixed, 100 * Game.DpiScale);
        ImGui.TableHeadersRow();

        // Build a parent-child index so we can implement drill-down.
        // Each sample knows its depth; children of sample[i] are the
        // contiguous samples after i whose depth > sample[i].Depth,
        // up until the next sample at the same or lesser depth.
        var samples = frame.Samples;
        int i = 0;
        while (i < samples.Length)
        {
            i = DrawSampleRow(frame, samples, i, expandAll: false);
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// Draws a single sample row and, if the section is expanded, recursively
    /// draws its children. Returns the index of the next sibling sample.
    /// </summary>
    private int DrawSampleRow(ProfilerFrame frame, ProfilerSample[] samples, int index, bool expandAll)
    {
        var sample = samples[index];
        int parentDepth = sample.Depth;

        // Determine if this sample has children (next sample has greater depth).
        bool hasChildren = (index + 1 < samples.Length) && (samples[index + 1].Depth > parentDepth);

        // Unique key for expand state: name + depth + start time.
        string expandKey = $"{sample.Name}#{sample.Depth}#{sample.StartMs:F4}";
        bool isExpanded = _expandedSections.Contains(expandKey);

        ImGui.TableNextRow();

        // ── Name (indented by depth, with tree node arrow) ───────
        ImGui.TableSetColumnIndex(0);

        float indent = sample.Depth * 20f * Game.DpiScale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);

        Vector4 catColor = GetCategoryColor(sample.Category);

        if (hasChildren)
        {
            // Clickable tree node arrow
            ImGui.PushStyleColor(ImGuiCol.Text, catColor);
            string arrow = isExpanded ? "▼" : "▶";
            if (ImGui.Selectable($"{arrow} {sample.Name}###{expandKey}", false,
                    ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap))
            {
                if (isExpanded)
                    _expandedSections.Remove(expandKey);
                else
                    _expandedSections.Add(expandKey);
                isExpanded = !isExpanded;
            }
            ImGui.PopStyleColor();
        }
        else
        {
            // Leaf node — no expand arrow
            ImGui.PushStyleColor(ImGuiCol.Text, catColor);
            ImGui.Selectable($"    {sample.Name}###{expandKey}", false,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap);
            ImGui.PopStyleColor();
        }

        // Tooltip with detailed description
        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(sample.Description))
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(350 * Game.DpiScale);
            ImGui.TextColored(catColor, sample.Name);
            ImGui.Separator();
            ImGui.TextUnformatted(sample.Description);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        // ── Category ──────────────────────────────────────────
        ImGui.TableSetColumnIndex(1);
        if (!string.IsNullOrEmpty(sample.Category))
        {
            ImGui.TextColored(catColor, sample.Category);
        }

        // ── Duration ──────────────────────────────────────────
        ImGui.TableSetColumnIndex(2);
        string durationText = sample.DurationMs < 0.01
            ? "<0.01"
            : sample.DurationMs.ToString("F2");
        ImGui.TextUnformatted(durationText);

        // ── Percentage bar ────────────────────────────────────
        ImGui.TableSetColumnIndex(3);
        float pct = frame.TotalMs > 0 ? (float)(sample.DurationMs / frame.TotalMs) : 0f;
        pct = Math.Clamp(pct, 0f, 1f);

        // Draw a small inline bar
        float barMaxW = ImGui.GetContentRegionAvail().X - 40 * Game.DpiScale;
        if (barMaxW < 10) barMaxW = 10;
        float barH = ImGui.GetTextLineHeight();
        Vector2 barPos = ImGui.GetCursorScreenPos();

        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(barPos, barPos + new Vector2(barMaxW * pct, barH),
            ImGui.ColorConvertFloat4ToU32(catColor with { W = 0.35f }));

        ImGui.TextUnformatted($"{pct * 100f:F1}%%");

        // ── Children ──────────────────────────────────────────
        int next = index + 1;

        if (hasChildren)
        {
            if (isExpanded)
            {
                // Draw children recursively
                while (next < samples.Length && samples[next].Depth > parentDepth)
                {
                    next = DrawSampleRow(frame, samples, next, expandAll);
                }
            }
            else
            {
                // Skip children
                while (next < samples.Length && samples[next].Depth > parentDepth)
                    next++;
            }
        }

        return next;
    }

    private static Vector4 GetCategoryColor(string category)
    {
        if (string.IsNullOrEmpty(category))
            return DefaultCategoryColor;
        return CategoryColors.TryGetValue(category, out var c) ? c : DefaultCategoryColor;
    }
}
