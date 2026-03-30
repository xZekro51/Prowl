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
/// Controls which detail view mode is shown below the frame graph.
/// </summary>
public enum ProfilerViewMode
{
    /// <summary>Hierarchical tree with drill-down (default).</summary>
    Hierarchy,
    /// <summary>Flat list aggregated by section name with total/avg/max/count.</summary>
    Flat,
    /// <summary>Visual timeline showing section bars over the frame duration.</summary>
    Timeline,
}

/// <summary>
/// Aggregated stats for a single section name across a frame.
/// </summary>
internal sealed class AggregatedSection
{
    public string Name;
    public string Category;
    public string Description;
    public double TotalMs;
    public double MaxMs;
    public double SelfMs;
    public int Count;
    public double AvgMs => Count > 0 ? TotalMs / Count : 0;
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
///   <item>Flat/aggregated view with total, average, max, and call count</item>
///   <item>Visual timeline view showing colored section bars</item>
///   <item>Self time column (exclusive of children)</item>
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

    /// <summary> Current detail view mode. </summary>
    private ProfilerViewMode _viewMode = ProfilerViewMode.Hierarchy;

    // Remote connection
    private ProfilerClient? _remoteClient;
    private string _remoteAddress = "127.0.0.1";
    private int _remotePort = ProfilerProtocol.DefaultPort;

    // Drill-down state: set of expanded section names (by identity = name + depth)
    private readonly HashSet<string> _expandedSections = new(StringComparer.Ordinal);

    // Flat view sort
    private enum SortColumn { Name, Total, Self, Avg, Max, Count }
    private SortColumn _flatSortColumn = SortColumn.Total;
    private bool _flatSortDescending = true;

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

    // Timeline constants
    private const float TimelineRowHeight = 20f;
    private const float TimelineHeaderHeight = 24f;

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

        switch (_viewMode)
        {
            case ProfilerViewMode.Hierarchy:
                DrawSampleTable(frame);
                break;
            case ProfilerViewMode.Flat:
                DrawFlatView(frame);
                break;
            case ProfilerViewMode.Timeline:
                DrawTimeline(frame);
                break;
        }
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

        ImGui.SameLine();
        ImGui.Dummy(new Vector2(12 * Game.DpiScale, 0));
        ImGui.SameLine();

        // ── View mode selector ──────────────────────────────────
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "View:");
        ImGui.SameLine();

        bool isHierarchy = _viewMode == ProfilerViewMode.Hierarchy;
        bool isFlat = _viewMode == ProfilerViewMode.Flat;
        bool isTimeline = _viewMode == ProfilerViewMode.Timeline;

        if (isHierarchy) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.28f, 0.56f, 1.00f, 0.60f));
        if (ImGui.SmallButton("Hierarchy")) _viewMode = ProfilerViewMode.Hierarchy;
        if (isHierarchy) ImGui.PopStyleColor();

        ImGui.SameLine();

        if (isFlat) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.28f, 0.56f, 1.00f, 0.60f));
        if (ImGui.SmallButton("Flat")) _viewMode = ProfilerViewMode.Flat;
        if (isFlat) ImGui.PopStyleColor();

        ImGui.SameLine();

        if (isTimeline) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.28f, 0.56f, 1.00f, 0.60f));
        if (ImGui.SmallButton("Timeline")) _viewMode = ProfilerViewMode.Timeline;
        if (isTimeline) ImGui.PopStyleColor();
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

    // ── Sample table with drill-down (Hierarchy view) ────────────

    private void DrawSampleTable(ProfilerFrame frame)
    {
        ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                                ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;

        float tableH = ImGui.GetContentRegionAvail().Y;

        if (!ImGui.BeginTable("##ProfilerSamples", 5, flags, new Vector2(0, tableH)))
            return;

        ImGui.TableSetupColumn("Section",     ImGuiTableColumnFlags.WidthStretch, 0.40f);
        ImGui.TableSetupColumn("Category",    ImGuiTableColumnFlags.WidthFixed, 80 * Game.DpiScale);
        ImGui.TableSetupColumn("Time (ms)",   ImGuiTableColumnFlags.WidthFixed, 70 * Game.DpiScale);
        ImGui.TableSetupColumn("Self (ms)",   ImGuiTableColumnFlags.WidthFixed, 70 * Game.DpiScale);
        ImGui.TableSetupColumn("% Frame",     ImGuiTableColumnFlags.WidthFixed, 100 * Game.DpiScale);
        ImGui.TableHeadersRow();

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

        // Calculate self time (total minus direct children)
        double selfMs = CalculateSelfTime(samples, index);

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

        // ── Self Time ─────────────────────────────────────────
        ImGui.TableSetColumnIndex(3);
        string selfText = selfMs < 0.01 ? "<0.01" : selfMs.ToString("F2");
        ImGui.TextUnformatted(selfText);

        // ── Percentage bar ────────────────────────────────────
        ImGui.TableSetColumnIndex(4);
        float pct = frame.TotalMs > 0 ? (float)(sample.DurationMs / frame.TotalMs) : 0f;
        pct = Math.Clamp(pct, 0f, 1f);

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
                while (next < samples.Length && samples[next].Depth > parentDepth)
                {
                    next = DrawSampleRow(frame, samples, next, expandAll);
                }
            }
            else
            {
                while (next < samples.Length && samples[next].Depth > parentDepth)
                    next++;
            }
        }

        return next;
    }

    // ── Flat / Aggregated View ───────────────────────────────────

    private void DrawFlatView(ProfilerFrame frame)
    {
        var aggregated = BuildAggregatedSections(frame);

        // Sort
        aggregated = _flatSortColumn switch
        {
            SortColumn.Name  => _flatSortDescending ? aggregated.OrderByDescending(a => a.Name).ToList() : aggregated.OrderBy(a => a.Name).ToList(),
            SortColumn.Total => _flatSortDescending ? aggregated.OrderByDescending(a => a.TotalMs).ToList() : aggregated.OrderBy(a => a.TotalMs).ToList(),
            SortColumn.Self  => _flatSortDescending ? aggregated.OrderByDescending(a => a.SelfMs).ToList() : aggregated.OrderBy(a => a.SelfMs).ToList(),
            SortColumn.Avg   => _flatSortDescending ? aggregated.OrderByDescending(a => a.AvgMs).ToList() : aggregated.OrderBy(a => a.AvgMs).ToList(),
            SortColumn.Max   => _flatSortDescending ? aggregated.OrderByDescending(a => a.MaxMs).ToList() : aggregated.OrderBy(a => a.MaxMs).ToList(),
            SortColumn.Count => _flatSortDescending ? aggregated.OrderByDescending(a => a.Count).ToList() : aggregated.OrderBy(a => a.Count).ToList(),
            _ => aggregated,
        };

        ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                                ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY |
                                ImGuiTableFlags.Sortable;

        float tableH = ImGui.GetContentRegionAvail().Y;

        if (!ImGui.BeginTable("##ProfilerFlat", 7, flags, new Vector2(0, tableH)))
            return;

        ImGui.TableSetupColumn("Section",    ImGuiTableColumnFlags.WidthStretch, 0.35f);
        ImGui.TableSetupColumn("Category",   ImGuiTableColumnFlags.WidthFixed, 80 * Game.DpiScale);
        ImGui.TableSetupColumn("Total (ms)", ImGuiTableColumnFlags.WidthFixed, 80 * Game.DpiScale);
        ImGui.TableSetupColumn("Self (ms)",  ImGuiTableColumnFlags.WidthFixed, 70 * Game.DpiScale);
        ImGui.TableSetupColumn("Avg (ms)",   ImGuiTableColumnFlags.WidthFixed, 70 * Game.DpiScale);
        ImGui.TableSetupColumn("Max (ms)",   ImGuiTableColumnFlags.WidthFixed, 70 * Game.DpiScale);
        ImGui.TableSetupColumn("Calls",      ImGuiTableColumnFlags.WidthFixed, 50 * Game.DpiScale);
        ImGui.TableHeadersRow();

        // Handle sorting clicks
        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsDirty)
        {
            unsafe
            {
                if (sortSpecs.SpecsCount > 0)
                {
                    var spec = sortSpecs.Specs;
                    _flatSortColumn = spec.ColumnIndex switch
                    {
                        0 => SortColumn.Name,
                        2 => SortColumn.Total,
                        3 => SortColumn.Self,
                        4 => SortColumn.Avg,
                        5 => SortColumn.Max,
                        6 => SortColumn.Count,
                        _ => SortColumn.Total,
                    };
                    _flatSortDescending = spec.SortDirection == ImGuiSortDirection.Descending;
                }
            }
            sortSpecs.SpecsDirty = false;
        }

        foreach (var section in aggregated)
        {
            ImGui.TableNextRow();
            Vector4 catColor = GetCategoryColor(section.Category);

            // Name
            ImGui.TableSetColumnIndex(0);
            ImGui.PushStyleColor(ImGuiCol.Text, catColor);
            ImGui.TextUnformatted(section.Name);
            ImGui.PopStyleColor();

            if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(section.Description))
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(350 * Game.DpiScale);
                ImGui.TextColored(catColor, section.Name);
                ImGui.Separator();
                ImGui.TextUnformatted(section.Description);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }

            // Category
            ImGui.TableSetColumnIndex(1);
            if (!string.IsNullOrEmpty(section.Category))
                ImGui.TextColored(catColor, section.Category);

            // Total
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(section.TotalMs < 0.01 ? "<0.01" : section.TotalMs.ToString("F2"));

            // Self
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(section.SelfMs < 0.01 ? "<0.01" : section.SelfMs.ToString("F2"));

            // Avg
            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(section.AvgMs < 0.01 ? "<0.01" : section.AvgMs.ToString("F3"));

            // Max
            ImGui.TableSetColumnIndex(5);
            ImGui.TextUnformatted(section.MaxMs < 0.01 ? "<0.01" : section.MaxMs.ToString("F2"));

            // Calls
            ImGui.TableSetColumnIndex(6);
            ImGui.TextUnformatted(section.Count.ToString());

            // ── Percentage bar (overlaid on Total column background) ──
            // Draw a subtle inline bar behind the total column
            float pct = frame.TotalMs > 0 ? (float)(section.TotalMs / frame.TotalMs) : 0f;
            pct = Math.Clamp(pct, 0f, 1f);
        }

        ImGui.EndTable();
    }

    // ── Timeline View ────────────────────────────────────────────

    private void DrawTimeline(ProfilerFrame frame)
    {
        if (frame.Samples.Length == 0)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "No samples in this frame.");
            return;
        }

        float avail = ImGui.GetContentRegionAvail().X;
        float availH = ImGui.GetContentRegionAvail().Y;

        // Determine max depth for height calculation
        int maxDepth = 0;
        foreach (var s in frame.Samples)
            if (s.Depth > maxDepth) maxDepth = s.Depth;

        float rowH = TimelineRowHeight * Game.DpiScale;
        float headerH = TimelineHeaderHeight * Game.DpiScale;
        float totalH = headerH + (maxDepth + 1) * rowH;
        float timelineH = Math.Min(totalH, availH);

        ImGui.BeginChild("##TimelineChild", new Vector2(avail, timelineH), ImGuiChildFlags.Border,
            ImGuiWindowFlags.HorizontalScrollbar);

        var drawList = ImGui.GetWindowDrawList();
        Vector2 origin = ImGui.GetCursorScreenPos();
        float timelineW = Math.Max(avail, 600 * Game.DpiScale);
        double frameMs = frame.TotalMs;
        if (frameMs <= 0) frameMs = 1;

        // Background
        drawList.AddRectFilled(origin, origin + new Vector2(timelineW, totalH),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.08f, 0.08f, 1f)));

        // ── Time ruler (header) ──────────────────────────────
        float rulerY = origin.Y;
        int numTicks = Math.Max(2, (int)(timelineW / (80 * Game.DpiScale)));
        for (int t = 0; t <= numTicks; t++)
        {
            float x = origin.X + (t / (float)numTicks) * timelineW;
            double ms = (t / (float)numTicks) * frameMs;
            drawList.AddLine(new Vector2(x, rulerY), new Vector2(x, rulerY + headerH),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.30f, 0.30f, 0.30f, 0.70f)));
            drawList.AddText(new Vector2(x + 2, rulerY + 2), ImGui.GetColorU32(new Vector4(0.55f, 0.55f, 0.55f, 1f)),
                $"{ms:F1}ms");
        }

        // ── Section bars ─────────────────────────────────────
        float barAreaY = origin.Y + headerH;
        float padding = 1f * Game.DpiScale;

        for (int i = 0; i < frame.Samples.Length; i++)
        {
            var s = frame.Samples[i];
            float x0 = origin.X + (float)(s.StartMs / frameMs) * timelineW;
            float x1 = origin.X + (float)((s.StartMs + s.DurationMs) / frameMs) * timelineW;
            float y0 = barAreaY + s.Depth * rowH + padding;
            float y1 = y0 + rowH - padding * 2;

            // Ensure minimum width for visibility
            if (x1 - x0 < 2) x1 = x0 + 2;

            Vector4 catColor = GetCategoryColor(s.Category);

            drawList.AddRectFilled(new Vector2(x0, y0), new Vector2(x1, y1),
                ImGui.ColorConvertFloat4ToU32(catColor with { W = 0.80f }));
            drawList.AddRect(new Vector2(x0, y0), new Vector2(x1, y1),
                ImGui.ColorConvertFloat4ToU32(catColor with { W = 0.40f }));

            // Draw text label if the bar is wide enough
            float barWidth = x1 - x0;
            string label = $"{s.Name} ({s.DurationMs:F2}ms)";
            var textSize = ImGui.CalcTextSize(label);
            if (barWidth > textSize.X + 4)
            {
                drawList.AddText(new Vector2(x0 + 2, y0 + (rowH - textSize.Y) * 0.5f - padding),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.90f)), label);
            }
            else if (barWidth > ImGui.CalcTextSize(s.Name).X + 4)
            {
                drawList.AddText(new Vector2(x0 + 2, y0 + (rowH - ImGui.CalcTextSize(s.Name).Y) * 0.5f - padding),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.90f)), s.Name);
            }
        }

        // Invisible button for tooltip interaction
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##TimelineArea", new Vector2(timelineW, totalH));

        if (ImGui.IsItemHovered())
        {
            var mouse = ImGui.GetMousePos();
            // Find which sample the mouse is over
            for (int i = 0; i < frame.Samples.Length; i++)
            {
                var s = frame.Samples[i];
                float x0 = origin.X + (float)(s.StartMs / frameMs) * timelineW;
                float x1 = origin.X + (float)((s.StartMs + s.DurationMs) / frameMs) * timelineW;
                if (x1 - x0 < 2) x1 = x0 + 2;
                float y0 = barAreaY + s.Depth * rowH;
                float y1 = y0 + rowH;

                if (mouse.X >= x0 && mouse.X <= x1 && mouse.Y >= y0 && mouse.Y <= y1)
                {
                    Vector4 catColor = GetCategoryColor(s.Category);
                    ImGui.BeginTooltip();
                    ImGui.TextColored(catColor, s.Name);
                    if (!string.IsNullOrEmpty(s.Category))
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), $"[{s.Category}]");
                    }
                    ImGui.Separator();
                    ImGui.Text($"Duration: {s.DurationMs:F3} ms");
                    ImGui.Text($"Start:    {s.StartMs:F3} ms");
                    ImGui.Text($"Depth:    {s.Depth}");
                    double selfMs = CalculateSelfTimeForIndex(frame.Samples, i);
                    ImGui.Text($"Self:     {selfMs:F3} ms");
                    if (!string.IsNullOrEmpty(s.Description))
                    {
                        ImGui.Separator();
                        ImGui.PushTextWrapPos(350 * Game.DpiScale);
                        ImGui.TextUnformatted(s.Description);
                        ImGui.PopTextWrapPos();
                    }
                    ImGui.EndTooltip();
                    break;
                }
            }
        }

        ImGui.EndChild();
    }

    // ── Aggregation helpers ──────────────────────────────────────

    private static List<AggregatedSection> BuildAggregatedSections(ProfilerFrame frame)
    {
        var map = new Dictionary<string, AggregatedSection>(StringComparer.Ordinal);
        var samples = frame.Samples;

        for (int i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            double selfMs = CalculateSelfTimeForIndex(samples, i);

            if (!map.TryGetValue(s.Name, out var agg))
            {
                agg = new AggregatedSection
                {
                    Name = s.Name,
                    Category = s.Category,
                    Description = s.Description,
                };
                map[s.Name] = agg;
            }

            agg.TotalMs += s.DurationMs;
            agg.SelfMs += selfMs;
            agg.Count++;
            if (s.DurationMs > agg.MaxMs)
                agg.MaxMs = s.DurationMs;
        }

        return [.. map.Values];
    }

    /// <summary>
    /// Calculates the self (exclusive) time for the sample at the given index.
    /// Self time = total duration minus the sum of direct children durations.
    /// </summary>
    private static double CalculateSelfTime(ProfilerSample[] samples, int index)
    {
        return CalculateSelfTimeForIndex(samples, index);
    }

    private static double CalculateSelfTimeForIndex(ProfilerSample[] samples, int index)
    {
        var sample = samples[index];
        int parentDepth = sample.Depth;
        double childrenMs = 0;

        for (int j = index + 1; j < samples.Length; j++)
        {
            if (samples[j].Depth <= parentDepth)
                break; // No longer a descendant
            if (samples[j].Depth == parentDepth + 1)
                childrenMs += samples[j].DurationMs; // Direct child
        }

        return Math.Max(0, sample.DurationMs - childrenMs);
    }

    private static Vector4 GetCategoryColor(string category)
    {
        if (string.IsNullOrEmpty(category))
            return DefaultCategoryColor;
        return CategoryColors.TryGetValue(category, out var c) ? c : DefaultCategoryColor;
    }
}
