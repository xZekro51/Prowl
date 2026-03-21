// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Diagnostics;
using System.Numerics;
using System.Text;

using ImGuiNET;

using Prowl.Editor.Build;
using Prowl.Editor.Docking;
using Prowl.Editor.Icons;
using Prowl.Runtime;

namespace Prowl.Editor.Panels;

/// <summary>
/// Dedicated build window that manages building the game project.
/// Provides platform selection, output folder picker, build settings,
/// and an in-progress modal popup with console-style log output.
/// </summary>
public sealed class BuildPanel : EditorPanel
{
    private BuildSettings? _buildSettings;
    private bool _loaded;

    // Build settings UI state
    private int _selectedPlatformIndex;
    private string _outputDirectory = string.Empty;

    // Build progress state
    private BuildProgress? _activeProgress;
    private bool _buildModalOpen;
    private int _lastEntryCount;
    private bool _autoScroll = true;
    private int _selectedEntryIndex = -1;

    // Async folder picker state
    private Task<string?>? _pendingFolderPick;

    // ── Color palette (matches ConsolePanel) ──────────────────────
    private static readonly Vector4 InfoColor    = new(0.78f, 0.78f, 0.78f, 1f);
    private static readonly Vector4 SuccessColor = new(0.40f, 0.85f, 0.40f, 1f);
    private static readonly Vector4 WarningColor = new(0.95f, 0.80f, 0.25f, 1f);
    private static readonly Vector4 ErrorColor   = new(0.95f, 0.30f, 0.30f, 1f);
    private static readonly Vector4 DimColor     = new(0.40f, 0.40f, 0.40f, 1f);

    private static readonly Vector4 RowEvenBg    = new(0f, 0f, 0f, 0f);
    private static readonly Vector4 RowOddBg     = new(1f, 1f, 1f, 0.03f);
    private static readonly Vector4 RowSelectedBg = new(0.22f, 0.40f, 0.68f, 0.45f);

    private static readonly Vector4 StripInfo    = new(0.45f, 0.45f, 0.45f, 0.40f);
    private static readonly Vector4 StripSuccess = new(0.30f, 0.70f, 0.30f, 0.60f);
    private static readonly Vector4 StripWarning = new(0.85f, 0.70f, 0.15f, 0.60f);
    private static readonly Vector4 StripError   = new(0.85f, 0.20f, 0.20f, 0.70f);

    public BuildPanel() : base("Build")
    {
        IsOpen = false;
    }

    protected override void DrawContent()
    {
        EnsureLoaded();
        if (_buildSettings == null) return;

        // Poll async folder picker result
        PollFolderPicker();

        float scale = Game.DpiScale;

        ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "Build Game");
        ImGui.Separator();
        ImGui.Spacing();

        // ── Platform selector ─────────────────────────────────
        ImGui.Text("Target Platform");
        string[] platformNames = Enum.GetNames<BuildTarget>();
        ImGui.SetNextItemWidth(200 * scale);
        ImGui.Combo("##BuildTarget", ref _selectedPlatformIndex, platformNames, platformNames.Length);
        ImGui.Spacing();

        // ── Configuration ─────────────────────────────────────
        ImGui.Text("Configuration");
        int configIndex = _buildSettings.Configuration == "Debug" ? 0 : 1;
        string[] configs = ["Debug", "Release"];
        ImGui.SetNextItemWidth(200 * scale);
        if (ImGui.Combo("##BuildConfig", ref configIndex, configs, configs.Length))
        {
            _buildSettings.Configuration = configs[configIndex];
            SaveBuildSettings();
        }
        ImGui.Spacing();

        // ── Self-contained ────────────────────────────────────
        bool selfContained = _buildSettings.SelfContained;
        if (ImGui.Checkbox("Self-Contained", ref selfContained))
        {
            _buildSettings.SelfContained = selfContained;
            SaveBuildSettings();
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "(bundles .NET runtime)");
        ImGui.Spacing();

        // ── Show console window ───────────────────────────────
        bool showConsole = _buildSettings.ShowConsole;
        if (ImGui.Checkbox("Show Console Window", ref showConsole))
        {
            _buildSettings.ShowConsole = showConsole;
            SaveBuildSettings();
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "(opens a console alongside the game for log output)");
        ImGui.Spacing();

        // ── Product name ──────────────────────────────────────
        ImGui.Text("Product Name");
        string productName = _buildSettings.ProductName;
        ImGui.SetNextItemWidth(300 * scale);
        if (ImGui.InputText("##BuildProductName", ref productName, 256))
        {
            _buildSettings.ProductName = productName;
            SaveBuildSettings();
        }
        ImGui.Spacing();

        // ── Startup scene ─────────────────────────────────────
        ImGui.Text("Startup Scene");
        DrawStartupScenePicker(scale);
        ImGui.Spacing();

        ImGui.Separator();
        ImGui.Spacing();

        // ── Output directory ──────────────────────────────────
        ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "Output Directory");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 90 * scale);
        ImGui.InputText("##OutputDir", ref _outputDirectory, 1024);
        ImGui.SameLine();

        bool picking = _pendingFolderPick != null && !_pendingFolderPick.IsCompleted;
        if (picking) ImGui.BeginDisabled();
        if (ImGui.Button("Browse...", new Vector2(80 * scale, 0)))
        {
            _pendingFolderPick = PickFolderAsync();
        }
        if (picking) ImGui.EndDisabled();

        if (string.IsNullOrWhiteSpace(_outputDirectory))
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
                "Leave empty to use default: Builds/<Platform>/");
        }
        ImGui.Spacing();

        ImGui.Separator();
        ImGui.Spacing();

        // ── Build button ──────────────────────────────────────
        bool hasProject = !string.IsNullOrEmpty(EditorApplication.ProjectPath);
        bool isBusy = _activeProgress != null && !_activeProgress.IsComplete;

        if (!hasProject || isBusy)
            ImGui.BeginDisabled();

        BuildTarget selectedTarget = (BuildTarget)_selectedPlatformIndex;
        if (ImGui.Button($"Build {selectedTarget}", new Vector2(200 * scale, 34 * scale)))
        {
            StartBuild(selectedTarget);
        }

        if (!hasProject || isBusy)
            ImGui.EndDisabled();

        // ── Build progress modal ──────────────────────────────
        DrawBuildModal();
    }

    private void StartBuild(BuildTarget target)
    {
        string? outputDir = string.IsNullOrWhiteSpace(_outputDirectory) ? null : _outputDirectory;

        _lastEntryCount = 0;
        _autoScroll = true;
        _selectedEntryIndex = -1;
        _activeProgress = BuildManager.BuildAsync(
            EditorApplication.ProjectPath!, target, outputDir);
        _buildModalOpen = true;
        ImGui.OpenPopup("##BuildProgressModal");
    }

    // ── Build progress modal (console-style) ────────────────────────

    private void DrawBuildModal()
    {
        if (!_buildModalOpen)
            return;

        if (!ImGui.IsPopupOpen("##BuildProgressModal"))
            ImGui.OpenPopup("##BuildProgressModal");

        float scale = Game.DpiScale;
        ImGui.SetNextWindowSize(new Vector2(700 * scale, 500 * scale), ImGuiCond.Appearing);

        bool modalOpen = true;
        if (ImGui.BeginPopupModal("##BuildProgressModal", ref modalOpen,
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove))
        {
            bool isComplete = _activeProgress?.IsComplete ?? true;

            // Header
            if (!isComplete)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.8f, 0.3f, 1f), "Building...");
                ImGui.SameLine();
                int dots = ((int)(ImGui.GetTime() * 3)) % 4;
                ImGui.Text(new string('.', dots));
            }
            else
            {
                var result = _activeProgress?.Result;
                if (result?.Success == true)
                    ImGui.TextColored(SuccessColor, "Build Succeeded!");
                else
                    ImGui.TextColored(ErrorColor, "Build Failed");
            }

            ImGui.Separator();

            // Reserve space for footer
            float footerHeight = 40 * scale;
            float availableHeight = ImGui.GetContentRegionAvail().Y - footerHeight;

            // Split between log list and detail pane
            float detailHeight = _selectedEntryIndex >= 0 ? 140 * scale : 0;
            float logListHeight = availableHeight - detailHeight;

            // Scrolling log area (console-style)
            ImGui.BeginChild("##BuildLog",
                new Vector2(0, logListHeight),
                ImGuiChildFlags.Border);

            List<BuildLogEntry>? entries = null;
            if (_activeProgress != null)
            {
                entries = _activeProgress.GetEntries();
                for (int i = 0; i < entries.Count; i++)
                {
                    DrawBuildLogEntry(entries[i], i, i == _selectedEntryIndex);
                }

                if (entries.Count > _lastEntryCount && _autoScroll)
                {
                    ImGui.SetScrollHereY(1.0f);
                    _lastEntryCount = entries.Count;
                }
            }

            // Keyboard navigation
            if (ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) && entries != null)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.DownArrow) && _selectedEntryIndex < entries.Count - 1)
                    _selectedEntryIndex++;
                else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow) && _selectedEntryIndex > 0)
                    _selectedEntryIndex--;
                if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                    _selectedEntryIndex = -1;
                if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.C)
                    && _selectedEntryIndex >= 0 && _selectedEntryIndex < entries.Count)
                    ImGui.SetClipboardText(entries[_selectedEntryIndex].Message);
            }

            ImGui.EndChild();

            // ── Detail pane for selected entry ─────────────────────
            if (_selectedEntryIndex >= 0 && entries != null && _selectedEntryIndex < entries.Count)
            {
                ImGui.Separator();
                DrawBuildDetailPane(entries[_selectedEntryIndex]);
            }

            // Footer buttons
            ImGui.Spacing();
            if (isComplete)
            {
                if (ImGui.Button("Close", new Vector2(100 * scale, 0)))
                {
                    _buildModalOpen = false;
                    ImGui.CloseCurrentPopup();
                }

                var result = _activeProgress?.Result;
                if (result?.Success == true && !string.IsNullOrEmpty(result.OutputPath))
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Open Output Folder", new Vector2(160 * scale, 0)))
                    {
                        OpenFolder(result.OutputPath);
                    }
                }
            }
            else
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
                    "Please wait while the build completes...");
            }

            ImGui.EndPopup();
        }

        if (!modalOpen)
        {
            _buildModalOpen = false;
        }
    }

    // ── Console-style log entry rendering ───────────────────────────

    private void DrawBuildLogEntry(BuildLogEntry entry, int index, bool isSelected)
    {
        float scale = Game.DpiScale;
        Vector4 textColor = GetSeverityColor(entry.Severity);

        float lineHeight = ImGui.GetTextLineHeightWithSpacing() + 2 * scale;
        float iconSize = ImGui.GetTextLineHeight();
        Vector2 cursorPos = ImGui.GetCursorScreenPos();
        float fullWidth = ImGui.GetContentRegionAvail().X;

        // ── Row background (alternating, with selection highlight) ──
        var drawList = ImGui.GetWindowDrawList();
        Vector4 rowBg = isSelected ? RowSelectedBg : (index % 2 == 0 ? RowEvenBg : RowOddBg);
        drawList.AddRectFilled(
            cursorPos,
            new Vector2(cursorPos.X + fullWidth, cursorPos.Y + lineHeight),
            ImGui.ColorConvertFloat4ToU32(rowBg));

        // ── Severity color strip (thin left accent) ──
        float stripW = 3 * scale;
        drawList.AddRectFilled(
            cursorPos,
            new Vector2(cursorPos.X + stripW, cursorPos.Y + lineHeight),
            ImGui.ColorConvertFloat4ToU32(GetStripColor(entry.Severity)));

        // ── Build display label (with space for severity icon) ──
        var sb = new StringBuilder(256);
        sb.Append("  ");
        float spaceW = ImGui.CalcTextSize(" ").X;
        int iconSpaces = Math.Max(1, (int)MathF.Ceiling((iconSize + 4 * scale) / spaceW));
        sb.Append(' ', iconSpaces);

        sb.Append('[').Append(entry.Timestamp.ToString("HH:mm:ss")).Append("] ");

        string displayMsg = entry.Message;
        if (displayMsg.Length > 200)
            displayMsg = displayMsg[..200] + "...";
        displayMsg = displayMsg.Replace('\n', ' ').Replace('\r', ' ');
        sb.Append(displayMsg);

        // ── Selectable (click to select) ──
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);
        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0, 0.5f));

        if (ImGui.Selectable($"{sb}##buildlog_{index}",
            isSelected,
            ImGuiSelectableFlags.SpanAllColumns,
            new Vector2(0, lineHeight)))
        {
            _selectedEntryIndex = _selectedEntryIndex == index ? -1 : index;
        }

        ImGui.PopStyleVar();
        ImGui.PopStyleColor();

        // ── Tooltip on hover ──
        if (ImGui.IsItemHovered() && entry.Message.Length > 200)
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 40.0f);
            string tipMsg = entry.Message.Length > 500
                ? entry.Message[..500] + "..."
                : entry.Message;
            ImGui.TextUnformatted(tipMsg);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        // ── Draw severity icon ──
        nint iconTex = EditorIcons.Get(GetSeverityIconType(entry.Severity));
        if (iconTex != 0)
        {
            float iconY = cursorPos.Y + (lineHeight - iconSize) * 0.5f;
            float iconX = cursorPos.X + stripW + 4 * scale;
            drawList.AddImage(iconTex,
                new Vector2(iconX, iconY),
                new Vector2(iconX + iconSize, iconY + iconSize),
                new Vector2(0, 1), new Vector2(1, 0));
        }
    }

    // ── Detail pane ─────────────────────────────────────────────────

    private static void DrawBuildDetailPane(BuildLogEntry entry)
    {
        // Action buttons row
        if (ImGui.SmallButton("Copy Message##bdtl"))
            ImGui.SetClipboardText(entry.Message);

        // Right-aligned metadata
        ImGui.SameLine();
        string meta = $"[{entry.Timestamp:HH:mm:ss.fff}]  {GetSeverityLabel(entry.Severity)}";
        float metaW = ImGui.CalcTextSize(meta).X;
        float availX = ImGui.GetContentRegionAvail().X;
        if (availX > metaW + 8)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availX - metaW);
            ImGui.TextColored(GetSeverityColor(entry.Severity), meta);
        }

        ImGui.Separator();

        // Scrollable detail body — shows the full log message
        ImGui.BeginChild("##BuildLogDetail", Vector2.Zero, ImGuiChildFlags.Border);

        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(entry.Message);
        ImGui.PopTextWrapPos();

        ImGui.EndChild();
    }

    // ── Severity helpers ────────────────────────────────────────────

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

    private static EditorIconType GetSeverityIconType(LogSeverity severity) => severity switch
    {
        LogSeverity.Success => EditorIconType.Success,
        LogSeverity.Warning => EditorIconType.Warning,
        LogSeverity.Error or LogSeverity.Exception => EditorIconType.Error,
        _ => EditorIconType.Info,
    };

    private static string GetSeverityLabel(LogSeverity severity) => severity switch
    {
        LogSeverity.Success => "Success",
        LogSeverity.Warning => "Warning",
        LogSeverity.Error => "Error",
        LogSeverity.Exception => "Exception",
        _ => "Info",
    };

    // ── Folder picker (non-blocking, with platform fallbacks) ───────

    private void PollFolderPicker()
    {
        if (_pendingFolderPick == null || !_pendingFolderPick.IsCompleted)
            return;

        try
        {
            string? result = _pendingFolderPick.Result;
            if (!string.IsNullOrEmpty(result))
                _outputDirectory = result;
        }
        catch
        {
            // Silently fail — user can type the path manually
        }
        finally
        {
            _pendingFolderPick = null;
        }
    }

    private static Task<string?> PickFolderAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return PickFolderWindows();
                if (OperatingSystem.IsLinux())
                    return PickFolderLinux();
            }
            catch { }
            return null;
        });
    }

    private static string? PickFolderWindows()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; " +
                        "$f = New-Object System.Windows.Forms.FolderBrowserDialog; " +
                        "if ($f.ShowDialog() -eq 'OK') { $f.SelectedPath }\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc == null) return null;

        string output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        return !string.IsNullOrEmpty(output) ? output : null;
    }

    private static string? PickFolderLinux()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "zenity",
            Arguments = "--file-selection --directory --title=\"Select Output Folder\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc == null) return null;

        string output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        return proc.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
    }

    // ── Other utilities ─────────────────────────────────────────────

    private static void OpenFolder(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", path);
            else if (OperatingSystem.IsLinux())
                Process.Start("xdg-open", path);
        }
        catch { /* best effort */ }
    }

    // ── Startup scene picker ────────────────────────────────────────

    private string[]? _cachedSceneFiles;
    private string[]? _cachedSceneLabels;

    private void DrawStartupScenePicker(float scale)
    {
        if (_buildSettings == null) return;

        // Lazily discover .scene files under Assets/
        if (_cachedSceneFiles == null)
            RefreshSceneFileList();

        string current = _buildSettings.StartupScenePath;

        // Find current selection index (0 = "(none)")
        int selectedIndex = 0;
        if (!string.IsNullOrEmpty(current) && _cachedSceneFiles != null)
        {
            for (int i = 0; i < _cachedSceneFiles.Length; i++)
            {
                if (string.Equals(_cachedSceneFiles[i], current, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i + 1; // +1 because index 0 is "(none)"
                    break;
                }
            }
        }

        ImGui.SetNextItemWidth(300 * scale);
        if (ImGui.Combo("##StartupScene", ref selectedIndex, _cachedSceneLabels!, _cachedSceneLabels!.Length))
        {
            _buildSettings.StartupScenePath = selectedIndex == 0 ? "" : _cachedSceneFiles![selectedIndex - 1];
            SaveBuildSettings();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh##SceneRefresh"))
            RefreshSceneFileList();

        if (string.IsNullOrEmpty(_buildSettings.StartupScenePath))
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
                "No startup scene selected — the player will start with an empty scene.");
        }
    }

    private void RefreshSceneFileList()
    {
        var scenes = new List<string>();

        string? projectPath = EditorApplication.ProjectPath;
        if (!string.IsNullOrEmpty(projectPath))
        {
            string assetsDir = Path.Combine(projectPath, "Assets");
            if (Directory.Exists(assetsDir))
            {
                foreach (string file in Directory.GetFiles(assetsDir, "*.scene", SearchOption.AllDirectories))
                {
                    // Store relative path from Assets/ so it is portable
                    scenes.Add(Path.GetRelativePath(assetsDir, file));
                }
                scenes.Sort(StringComparer.OrdinalIgnoreCase);
            }
        }

        _cachedSceneFiles = scenes.ToArray();

        // Build labels array with "(none)" at index 0
        _cachedSceneLabels = new string[_cachedSceneFiles.Length + 1];
        _cachedSceneLabels[0] = "(none)";
        for (int i = 0; i < _cachedSceneFiles.Length; i++)
            _cachedSceneLabels[i + 1] = _cachedSceneFiles[i];
    }

    // ── Load / Save ─────────────────────────────────────────────────

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        if (!string.IsNullOrEmpty(EditorApplication.ProjectPath))
            _buildSettings = BuildSettings.Load(EditorApplication.ProjectPath);
        else
            _buildSettings = new BuildSettings();
    }

    private void SaveBuildSettings()
    {
        if (_buildSettings == null) return;
        if (string.IsNullOrEmpty(EditorApplication.ProjectPath)) return;
        _buildSettings.Save(EditorApplication.ProjectPath);
    }
}
