// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.Editor.Docking;
using Prowl.Editor.Services;

namespace Prowl.Editor.Panels;

/// <summary>
/// Dockable console window that displays log messages from the engine's
/// <see cref="Debug"/> class. Supports filtering by severity, collapsing
/// repeated messages, search, and auto-scroll.
/// </summary>
public sealed class ConsolePanel : EditorPanel
{
    private string _searchFilter = string.Empty;
    private bool _autoScroll = true;
    private int _selectedIndex = -1;

    // Detail pane buffer for selectable text
    private string _detailBuffer = string.Empty;
    private int _lastDetailIndex = -2;

    // Color palette
    private static readonly Vector4 InfoColor    = new(0.78f, 0.78f, 0.78f, 1f);
    private static readonly Vector4 SuccessColor = new(0.40f, 0.85f, 0.40f, 1f);
    private static readonly Vector4 WarningColor = new(0.95f, 0.80f, 0.25f, 1f);
    private static readonly Vector4 ErrorColor   = new(0.95f, 0.30f, 0.30f, 1f);

    public ConsolePanel() : base("Console")
    {
        IsOpen = true;
    }

    protected override void DrawContent()
    {
        DrawToolbar();
        ImGui.Separator();

        // Main log list
        float footerHeight = _selectedIndex >= 0 ? 120 * Game.DpiScale : 0;
        ImGui.BeginChild("##LogList", new Vector2(0, -footerHeight), ImGuiChildFlags.None);

        var entries = EditorConsoleLogger.GetEntries();
        bool hasSearch = !string.IsNullOrWhiteSpace(_searchFilter);
        bool collapse = EditorConsoleLogger.Collapse;

        // When collapsing, track unique messages to skip duplicates
        HashSet<string>? seenMessages = collapse ? new(StringComparer.Ordinal) : null;

        // Use clipper for efficient rendering of large lists
        int visibleCount = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            // Filter by severity
            if (!ShouldShow(entry.Severity)) continue;

            // Filter by search
            if (hasSearch && !entry.Message.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            // Collapse: skip if we've already shown this message+severity
            if (collapse && seenMessages != null)
            {
                string key = $"{(int)entry.Severity}:{entry.Message}";
                if (!seenMessages.Add(key))
                    continue;
            }

            visibleCount++;
            DrawLogEntry(i, entry);
        }

        if (visibleCount == 0)
        {
            ImGui.TextColored(new Vector4(0.45f, 0.45f, 0.45f, 1f), "  No log entries.");
        }

        // Auto-scroll to bottom
        if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 10)
            ImGui.SetScrollHereY(1.0f);

        ImGui.EndChild();

        // Detail pane for selected entry
        if (_selectedIndex >= 0 && _selectedIndex < entries.Count)
        {
            ImGui.Separator();
            DrawDetailPane(entries[_selectedIndex]);
        }
    }

    private void DrawToolbar()
    {
        // Clear button
        if (ImGui.Button("\ud83d\uddd1 Clear"))
        {
            EditorConsoleLogger.Clear();
            EditorConsoleLogger.ResetCounts();
            _selectedIndex = -1;
        }

        ImGui.SameLine();

        // Collapse toggle
        bool collapse = EditorConsoleLogger.Collapse;
        if (ImGui.Checkbox("Collapse", ref collapse))
            EditorConsoleLogger.Collapse = collapse;

        ImGui.SameLine();

        // Auto-scroll toggle
        ImGui.Checkbox("Auto-scroll", ref _autoScroll);

        ImGui.SameLine();
        ImGui.Spacing();
        ImGui.SameLine();

        // Severity filter buttons with counts
        EditorConsoleLogger.ShowInfo    = DrawFilterToggle("Info",  EditorConsoleLogger.ShowInfo,    InfoColor,    EditorConsoleLogger.InfoCount);
        ImGui.SameLine();
        EditorConsoleLogger.ShowWarning = DrawFilterToggle("Warn",  EditorConsoleLogger.ShowWarning, WarningColor, EditorConsoleLogger.WarningCount);
        ImGui.SameLine();
        EditorConsoleLogger.ShowError   = DrawFilterToggle("Error", EditorConsoleLogger.ShowError,   ErrorColor,   EditorConsoleLogger.ErrorCount);

        ImGui.SameLine();
        ImGui.Spacing();
        ImGui.SameLine();

        // Search bar
        float remainingW = ImGui.GetContentRegionAvail().X;
        if (remainingW > 80 * Game.DpiScale)
        {
            ImGui.SetNextItemWidth(remainingW);
            ImGui.InputTextWithHint("##ConsoleSearch", "\ud83d\udd0d Search...", ref _searchFilter, 256);
        }
    }

    private static bool DrawFilterToggle(string label, bool enabled, Vector4 color, int count)
    {
        if (enabled)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(color.X * 0.3f, color.Y * 0.3f, color.Z * 0.3f, 0.8f));
        else
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.20f, 0.20f, 1f));

        string text = $"{label} ({count})";
        if (ImGui.Button(text))
            enabled = !enabled;
        ImGui.PopStyleColor();
        return enabled;
    }

    private void DrawLogEntry(int index, LogEntry entry)
    {
        bool isSelected = _selectedIndex == index;
        Vector4 color = GetSeverityColor(entry.Severity);
        string prefix = GetSeverityPrefix(entry.Severity);

        // Truncate long messages for the list view
        string displayMsg = entry.Message;
        if (displayMsg.Length > 200)
            displayMsg = displayMsg[..200] + "...";

        // Remove newlines for single-line display
        displayMsg = displayMsg.Replace('\n', ' ').Replace('\r', ' ');

        string label = entry.RepeatCount > 1
            ? $"{prefix} {displayMsg}  [{entry.RepeatCount}]"
            : $"{prefix} {displayMsg}";

        ImGui.PushStyleColor(ImGuiCol.Text, color);

        if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.None))
        {
            _selectedIndex = isSelected ? -1 : index;
        }

        ImGui.PopStyleColor();

        // Tooltip with timestamp
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"[{entry.Timestamp:HH:mm:ss.fff}] {entry.Message}");
        }
    }

    private void DrawDetailPane(LogEntry entry)
    {
        ImGui.BeginChild("##LogDetail", Vector2.Zero, ImGuiChildFlags.Border);

        ImGui.TextColored(GetSeverityColor(entry.Severity),
            $"[{entry.Timestamp:HH:mm:ss.fff}] {GetSeverityPrefix(entry.Severity)}");
        ImGui.Separator();

        // Build selectable text buffer (rebuild when selection changes)
        if (_lastDetailIndex != _selectedIndex)
        {
            _lastDetailIndex = _selectedIndex;
            _detailBuffer = entry.Message;
            if (!string.IsNullOrEmpty(entry.StackTrace))
                _detailBuffer += "\n\n--- Stack Trace ---\n" + entry.StackTrace;
        }

        // Use InputTextMultiline with ReadOnly so the user can select & copy text
        Vector2 avail = ImGui.GetContentRegionAvail();
        ImGui.InputTextMultiline("##DetailText", ref _detailBuffer, (uint)(_detailBuffer.Length + 1),
            avail, ImGuiInputTextFlags.ReadOnly);

        ImGui.EndChild();
    }

    private static bool ShouldShow(LogSeverity severity) => severity switch
    {
        LogSeverity.Normal or LogSeverity.Success => EditorConsoleLogger.ShowInfo,
        LogSeverity.Warning => EditorConsoleLogger.ShowWarning,
        LogSeverity.Error or LogSeverity.Exception => EditorConsoleLogger.ShowError,
        _ => true,
    };

    private static Vector4 GetSeverityColor(LogSeverity severity) => severity switch
    {
        LogSeverity.Success => SuccessColor,
        LogSeverity.Warning => WarningColor,
        LogSeverity.Error or LogSeverity.Exception => ErrorColor,
        _ => InfoColor,
    };

    private static string GetSeverityPrefix(LogSeverity severity) => severity switch
    {
        LogSeverity.Success => "\u2714",     // ✔
        LogSeverity.Warning => "\u26A0",     // ⚠
        LogSeverity.Error => "\u2716",       // ✖
        LogSeverity.Exception => "\ud83d\udca5", // 💥
        _ => "\u25CF",                       // ●
    };
}
