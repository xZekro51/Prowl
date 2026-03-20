// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using System.Text;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.Editor.Docking;
using Prowl.Editor.Services;
using Prowl.Editor.Utilities;

namespace Prowl.Editor.Panels;

/// <summary>
/// Professional dockable console window that displays log messages from the
/// engine's <see cref="Debug"/> class. Features include severity filtering,
/// message collapsing, search, auto-scroll, inline timestamps, alternating
/// row backgrounds, right-click context menus, clipboard support, keyboard
/// navigation, log export, "Clear on Play", and "Error Pause" integration.
/// </summary>
public sealed class ConsolePanel : EditorPanel
{
    private string _searchFilter = string.Empty;
    private bool _autoScroll = true;
    private bool _showTimestamps;
    private int _selectedIndex = -1;

    // ── Color palette ──────────────────────────────────────────────
    private static readonly Vector4 InfoColor    = new(0.78f, 0.78f, 0.78f, 1f);
    private static readonly Vector4 SuccessColor = new(0.40f, 0.85f, 0.40f, 1f);
    private static readonly Vector4 WarningColor = new(0.95f, 0.80f, 0.25f, 1f);
    private static readonly Vector4 ErrorColor   = new(0.95f, 0.30f, 0.30f, 1f);

    // Alternating row backgrounds
    private static readonly Vector4 RowEvenBg    = new(0f, 0f, 0f, 0f);
    private static readonly Vector4 RowOddBg     = new(1f, 1f, 1f, 0.03f);
    private static readonly Vector4 RowSelectedBg = new(0.20f, 0.36f, 0.58f, 0.65f);

    // Severity strip (thin left border accent)
    private static readonly Vector4 StripInfo    = new(0.45f, 0.45f, 0.45f, 0.40f);
    private static readonly Vector4 StripSuccess = new(0.30f, 0.70f, 0.30f, 0.60f);
    private static readonly Vector4 StripWarning = new(0.85f, 0.70f, 0.15f, 0.60f);
    private static readonly Vector4 StripError   = new(0.85f, 0.20f, 0.20f, 0.70f);

    // Clickable stack frame colors
    private static readonly Vector4 LinkColor = new(0.45f, 0.65f, 1.0f, 1f);
    private static readonly Vector4 DimColor  = new(0.50f, 0.50f, 0.50f, 1f);

    public ConsolePanel() : base("Console")
    {
        IsOpen = true;
    }

    protected override void DrawContent()
    {
        DrawToolbar();
        ImGui.Separator();

        // ── Main log list ──────────────────────────────────────────
        float footerHeight = _selectedIndex >= 0 ? 150 * Game.DpiScale : 0;
        ImGui.BeginChild("##LogList", new Vector2(0, -footerHeight), ImGuiChildFlags.None);

        var entries = EditorConsoleLogger.GetEntries();
        bool hasSearch = !string.IsNullOrWhiteSpace(_searchFilter);
        bool collapse = EditorConsoleLogger.Collapse;

        HashSet<string>? seenMessages = collapse ? new(StringComparer.Ordinal) : null;

        int visibleRow = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            if (!ShouldShow(entry.Severity)) continue;

            if (hasSearch && !entry.Message.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (collapse && seenMessages != null)
            {
                string key = $"{(int)entry.Severity}:{entry.Message}";
                if (!seenMessages.Add(key))
                    continue;
            }

            DrawLogEntry(i, entry, visibleRow);
            visibleRow++;
        }

        if (visibleRow == 0)
        {
            ImGui.Spacing();
            float indent = 12 * Game.DpiScale;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);
            ImGui.TextColored(new Vector4(0.40f, 0.40f, 0.40f, 1f), "No log entries.");
        }

        // Auto-scroll to bottom when near the end
        if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 10)
            ImGui.SetScrollHereY(1.0f);

        // Context menu on empty area
        if (ImGui.BeginPopupContextWindow("##ConsoleCtxEmpty",
                ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
        {
            DrawContextMenu(null);
            ImGui.EndPopup();
        }

        // Keyboard navigation
        HandleKeyboardInput(entries);

        ImGui.EndChild();

        // ── Detail pane for selected entry ─────────────────────────
        if (_selectedIndex >= 0 && _selectedIndex < entries.Count)
        {
            ImGui.Separator();
            DrawDetailPane(entries[_selectedIndex]);
        }
    }

    // ── Toolbar ────────────────────────────────────────────────────

    private void DrawToolbar()
    {
        // ── Row 1: Clear + toggle options ──
        if (ImGui.SmallButton("Clear"))
        {
            EditorConsoleLogger.Clear();
            EditorConsoleLogger.ResetCounts();
            _selectedIndex = -1;
        }

        ImGui.SameLine();

        bool clearOnPlay = EditorConsoleLogger.ClearOnPlay;
        if (DrawToggle("Clear on Play", clearOnPlay))
            EditorConsoleLogger.ClearOnPlay = !clearOnPlay;

        ImGui.SameLine();

        bool errorPause = EditorConsoleLogger.ErrorPause;
        if (DrawToggle("Error Pause", errorPause))
            EditorConsoleLogger.ErrorPause = !errorPause;

        ImGui.SameLine();

        bool collapse = EditorConsoleLogger.Collapse;
        if (DrawToggle("Collapse", collapse))
            EditorConsoleLogger.Collapse = !collapse;

        ImGui.SameLine();

        if (DrawToggle("Timestamps", _showTimestamps))
            _showTimestamps = !_showTimestamps;

        ImGui.SameLine();

        if (DrawToggle("Auto-scroll", _autoScroll))
            _autoScroll = !_autoScroll;

        // ── Row 2: Severity filters + search ──
        EditorConsoleLogger.ShowInfo    = DrawFilterButton("Info",  EditorConsoleLogger.ShowInfo,    InfoColor,    EditorConsoleLogger.InfoCount);
        ImGui.SameLine();
        EditorConsoleLogger.ShowWarning = DrawFilterButton("Warn",  EditorConsoleLogger.ShowWarning, WarningColor, EditorConsoleLogger.WarningCount);
        ImGui.SameLine();
        EditorConsoleLogger.ShowError   = DrawFilterButton("Error", EditorConsoleLogger.ShowError,   ErrorColor,   EditorConsoleLogger.ErrorCount);

        ImGui.SameLine();

        // Search bar (fills remaining width)
        float remainingW = ImGui.GetContentRegionAvail().X;
        if (remainingW > 40 * Game.DpiScale)
        {
            ImGui.SetNextItemWidth(remainingW);
            ImGui.InputTextWithHint("##ConsoleSearch", "Search...", ref _searchFilter, 256);
        }
    }

    /// <summary> Small toggle button that highlights when active. </summary>
    private static bool DrawToggle(string label, bool active)
    {
        Vector4 bg    = active ? new(0.22f, 0.40f, 0.68f, 0.80f) : new(0.20f, 0.20f, 0.20f, 1f);
        Vector4 hover = active ? new(0.26f, 0.46f, 0.76f, 0.90f) : new(0.26f, 0.26f, 0.26f, 1f);

        ImGui.PushStyleColor(ImGuiCol.Button, bg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hover);
        bool clicked = ImGui.SmallButton(label);
        ImGui.PopStyleColor(2);
        return clicked;
    }

    /// <summary> Severity filter button with tinted background and count badge. </summary>
    private static bool DrawFilterButton(string label, bool enabled, Vector4 color, int count)
    {
        Vector4 bg = enabled
            ? new(color.X * 0.3f, color.Y * 0.3f, color.Z * 0.3f, 0.80f)
            : new(0.20f, 0.20f, 0.20f, 1f);

        ImGui.PushStyleColor(ImGuiCol.Button, bg);
        string text = $"{label} ({count})";
        bool clicked = ImGui.SmallButton(text);
        ImGui.PopStyleColor();
        if (clicked) enabled = !enabled;
        return enabled;
    }

    // ── Log entries ────────────────────────────────────────────────

    private void DrawLogEntry(int index, LogEntry entry, int visibleRow)
    {
        bool isSelected = _selectedIndex == index;
        Vector4 textColor = GetSeverityColor(entry.Severity);
        string prefix = GetSeverityPrefix(entry.Severity);

        float lineHeight = ImGui.GetTextLineHeightWithSpacing() + 2 * Game.DpiScale;
        Vector2 cursorPos = ImGui.GetCursorScreenPos();
        float fullWidth = ImGui.GetContentRegionAvail().X;

        // ── Row background (alternating) ──
        var drawList = ImGui.GetWindowDrawList();
        Vector4 rowBg = isSelected ? RowSelectedBg : (visibleRow % 2 == 0 ? RowEvenBg : RowOddBg);
        drawList.AddRectFilled(
            cursorPos,
            new Vector2(cursorPos.X + fullWidth, cursorPos.Y + lineHeight),
            ImGui.ColorConvertFloat4ToU32(rowBg));

        // ── Severity color strip (thin left accent) ──
        float stripW = 3 * Game.DpiScale;
        drawList.AddRectFilled(
            cursorPos,
            new Vector2(cursorPos.X + stripW, cursorPos.Y + lineHeight),
            ImGui.ColorConvertFloat4ToU32(GetStripColor(entry.Severity)));

        // ── Build display label ──
        var sb = new StringBuilder(256);
        sb.Append("  ");   // indent past strip
        sb.Append(prefix);
        sb.Append(' ');

        if (_showTimestamps)
            sb.Append('[').Append(entry.Timestamp.ToString("HH:mm:ss")).Append("] ");

        string displayMsg = entry.Message;
        if (displayMsg.Length > 200)
            displayMsg = displayMsg[..200] + "...";
        displayMsg = displayMsg.Replace('\n', ' ').Replace('\r', ' ');
        sb.Append(displayMsg);

        if (entry.RepeatCount > 1)
            sb.Append("  x").Append(entry.RepeatCount);

        // ── Selectable ──
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);
        ImGui.PushStyleColor(ImGuiCol.Header, RowSelectedBg);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.22f, 0.32f, 0.50f, 0.45f));

        if (ImGui.Selectable($"{sb}##log_{index}", isSelected,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick,
                new Vector2(0, lineHeight)))
        {
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                _selectedIndex = index;
                OpenFirstStackFrame(entry);
            }
            else
            {
                _selectedIndex = isSelected ? -1 : index;
            }
        }

        ImGui.PopStyleColor(3);

        // ── Right-click context menu ──
        if (ImGui.BeginPopupContextItem($"##ctx_{index}"))
        {
            DrawContextMenu(entry);
            ImGui.EndPopup();
        }

        // ── Tooltip (detailed) ──
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(textColor,
                $"[{entry.Timestamp:HH:mm:ss.fff}]  Frame #{entry.FrameNumber}");
            ImGui.Separator();
            string tipMsg = entry.Message.Length > 500
                ? entry.Message[..500] + "..."
                : entry.Message;
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 40.0f);
            ImGui.TextUnformatted(tipMsg);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        // ── Frame number (right-aligned, subtle) ──
        if (_showTimestamps)
        {
            string frameText = $"#{entry.FrameNumber}";
            float frameTw = ImGui.CalcTextSize(frameText).X;
            float rightEdge = cursorPos.X + fullWidth - 8 * Game.DpiScale;
            float mainTw = ImGui.CalcTextSize(sb.ToString()).X;

            if (rightEdge - frameTw > cursorPos.X + mainTw + 20 * Game.DpiScale)
            {
                drawList.AddText(
                    new Vector2(rightEdge - frameTw,
                                cursorPos.Y + (lineHeight - ImGui.GetTextLineHeight()) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0.38f, 0.38f, 0.38f, 1f)),
                    frameText);
            }
        }
    }

    // ── Context menu ───────────────────────────────────────────────

    private void DrawContextMenu(LogEntry? entry)
    {
        if (entry != null)
        {
            if (ImGui.MenuItem("Copy Message"))
                ImGui.SetClipboardText(entry.Message);

            if (!string.IsNullOrEmpty(entry.StackTrace))
            {
                if (ImGui.MenuItem("Copy Stack Trace"))
                    ImGui.SetClipboardText(entry.StackTrace!);
            }

            if (ImGui.MenuItem("Copy Message + Stack"))
            {
                string full = entry.Message;
                if (!string.IsNullOrEmpty(entry.StackTrace))
                    full += "\n\n" + entry.StackTrace;
                ImGui.SetClipboardText(full);
            }

            ImGui.Separator();
        }

        if (ImGui.MenuItem("Copy All Visible"))
            ImGui.SetClipboardText(EditorConsoleLogger.FormatAllAsText());

        if (ImGui.MenuItem("Export to File..."))
            ExportLog();

        ImGui.Separator();

        if (ImGui.MenuItem("Clear All"))
        {
            EditorConsoleLogger.Clear();
            EditorConsoleLogger.ResetCounts();
            _selectedIndex = -1;
        }
    }

    // ── Detail pane ────────────────────────────────────────────────

    private void DrawDetailPane(LogEntry entry)
    {
        // Action buttons row
        if (ImGui.SmallButton("Copy Message##dtl"))
            ImGui.SetClipboardText(entry.Message);

        ImGui.SameLine();

        bool hasStack = !string.IsNullOrEmpty(entry.StackTrace);
        if (hasStack)
        {
            if (ImGui.SmallButton("Copy Stack##dtl"))
                ImGui.SetClipboardText(entry.StackTrace!);
            ImGui.SameLine();
        }

        if (ImGui.SmallButton("Export##dtl"))
            ExportLog();

        // Right-aligned metadata
        ImGui.SameLine();
        string meta = $"[{entry.Timestamp:HH:mm:ss.fff}]  Frame #{entry.FrameNumber}  {GetSeverityLabel(entry.Severity)}";
        float metaW = ImGui.CalcTextSize(meta).X;
        float availX = ImGui.GetContentRegionAvail().X;
        if (availX > metaW + 8)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availX - metaW);
            ImGui.TextColored(GetSeverityColor(entry.Severity), meta);
        }

        ImGui.Separator();

        // Scrollable detail body
        ImGui.BeginChild("##LogDetail", Vector2.Zero, ImGuiChildFlags.Border);

        // Message (wrapped, printf-safe)
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(entry.Message);
        ImGui.PopTextWrapPos();

        // Clickable stack frames
        if (entry.StackFrames is { StackFrames.Length: > 0 })
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(DimColor, "Stack Trace:  (click a frame to open in editor)");
            ImGui.Spacing();

            for (int i = 0; i < entry.StackFrames.StackFrames.Length; i++)
            {
                var frame = entry.StackFrames.StackFrames[i];
                string frameText = frame.ToString();
                bool canOpen = !string.IsNullOrEmpty(frame.FileName) && frame.Line.HasValue;

                if (canOpen)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, LinkColor);
                    if (ImGui.Selectable($"  {frameText}##frame_{i}"))
                        OpenFileAtLine(frame.FileName!, frame.Line!.Value, frame.Column ?? 1);
                    ImGui.PopStyleColor();

                    if (ImGui.IsItemHovered())
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                }
                else
                {
                    ImGui.TextColored(DimColor, $"  {frameText}");
                }
            }
        }
        else if (hasStack)
        {
            // Fallback: raw string when structured frames are not available
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(DimColor, "Stack Trace:");
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(entry.StackTrace!);
            ImGui.PopTextWrapPos();
        }

        ImGui.EndChild();
    }

    // ── Keyboard handling ──────────────────────────────────────────

    private void HandleKeyboardInput(List<LogEntry> entries)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows))
            return;

        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow) && _selectedIndex < entries.Count - 1)
        {
            _selectedIndex++;
            _autoScroll = false;
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow) && _selectedIndex > 0)
        {
            _selectedIndex--;
            _autoScroll = false;
        }

        if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.C)
            && _selectedIndex >= 0 && _selectedIndex < entries.Count)
        {
            ImGui.SetClipboardText(entries[_selectedIndex].Message);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            _selectedIndex = -1;
    }

    // ── File opening helpers ──────────────────────────────────────

    /// <summary>
    /// Opens the first stack frame that has a valid source file.
    /// Called on double-click of a log entry in the list.
    /// </summary>
    private static void OpenFirstStackFrame(LogEntry entry)
    {
        if (entry.StackFrames is not { StackFrames.Length: > 0 }) return;

        foreach (var frame in entry.StackFrames.StackFrames)
        {
            if (!string.IsNullOrEmpty(frame.FileName) && frame.Line.HasValue)
            {
                OpenFileAtLine(frame.FileName!, frame.Line!.Value, frame.Column ?? 1);
                return;
            }
        }
    }

    /// <summary>
    /// Opens a source file at the given line in the configured external editor.
    /// Delegates to <see cref="ExternalEditorUtility"/> which handles per-IDE
    /// arguments and COM automation for Visual Studio line-navigation.
    /// </summary>
    private static void OpenFileAtLine(string filePath, int line, int column)
    {
        ExternalEditorUtility.OpenFileAtLine(PreferencesPanel.ExternalEditor, filePath, line, column);
    }

    // ── Export helper ──────────────────────────────────────────────

    private static void ExportLog()
    {
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        Directory.CreateDirectory(dir);
        string fileName = $"ConsoleLog_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        string path = Path.Combine(dir, fileName);

        try
        {
            EditorConsoleLogger.ExportToFile(path);
            Debug.LogSuccess($"Console log exported to: {path}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to export log: {ex.Message}");
        }
    }

    // ── Severity helpers ───────────────────────────────────────────

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

    private static Vector4 GetStripColor(LogSeverity severity) => severity switch
    {
        LogSeverity.Success => StripSuccess,
        LogSeverity.Warning => StripWarning,
        LogSeverity.Error or LogSeverity.Exception => StripError,
        _ => StripInfo,
    };

    private static string GetSeverityPrefix(LogSeverity severity) => severity switch
    {
        LogSeverity.Success   => "[+]",
        LogSeverity.Warning   => "[!]",
        LogSeverity.Error     => "[x]",
        LogSeverity.Exception => "[!!]",
        _                     => "[-]",
    };

    private static string GetSeverityLabel(LogSeverity severity) => severity switch
    {
        LogSeverity.Success   => "SUCCESS",
        LogSeverity.Warning   => "WARNING",
        LogSeverity.Error     => "ERROR",
        LogSeverity.Exception => "EXCEPTION",
        _                     => "INFO",
    };
}
