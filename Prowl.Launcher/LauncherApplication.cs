// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Linq;
using System.Numerics;

using ImGuiNET;

using Prowl.ImGuiIntegration;
using Prowl.Runtime;
using Prowl.Runtime.EventSystem;
using Prowl.UI;

namespace Prowl.Launcher;

/// <summary>
/// Unity Hub–like launcher that lists known projects and lets the user
/// create, open, browse, and remove them — built entirely with Dear ImGui.
/// </summary>
public sealed class LauncherApplication : Game
{
    // ── DPI helper ───────────────────────────────────────────────────
    private static float S(float v) => v * Game.DpiScale;
    private static Vector2 S(float x, float y) => new(x * Game.DpiScale, y * Game.DpiScale);

    // ── Core state ──────────────────────────────────────────────────
    private ProjectManager _projectManager = null!;
    private int _selectedIndex = -1;
    private bool _themeApplied;
    private string _searchFilter = string.Empty;

    // ── Native folder picker ────────────────────────────────────────
    private Task<string?>? _folderPickerTask;

    private enum PickerTarget { None, NewProjectLocation, AddExisting }
    private PickerTarget _folderPickerTarget = PickerTarget.None;

    // ── Toast notifications ─────────────────────────────────────────
    private string _toastMessage = string.Empty;
    private float _toastExpiry;
    private Vector4 _toastColor = new(0.28f, 0.56f, 1.00f, 1.00f);

    // ── Modal: New Project ──────────────────────────────────────────
    private bool _showNewProjectModal;
    private bool _newProjectFocusName;
    private string _newProjectName = "MyProject";
    private string _newProjectLocation = "";
    private string _newProjectError = string.Empty;

    // ── Modal: Confirm Remove ───────────────────────────────────────
    private bool _showRemoveModal;

    // ── Modal: Confirm Delete from Disk ─────────────────────────────
    private bool _showDeleteFromDiskModal;
    private string _deleteConfirmInput = string.Empty;

    // ── Sort ─────────────────────────────────────────────────────────
    private enum SortMode { LastModified, Name, Path }
    private SortMode _sortMode = SortMode.LastModified;
    private bool _sortDescending = true;

    // ── File drop subscription ──────────────────────────────────────
    private IDisposable? _fileDropSub;

    // ── Double-click tracking ───────────────────────────────────────
    private int _lastClickIndex = -1;
    private double _lastClickTime;
    private const double DoubleClickThreshold = 0.35;

    /// <summary>
    /// Resets the theme flag when DPI changes so that the launcher theme
    /// (including <c>ScaleAllSizes</c>) is reapplied on the next frame.
    /// </summary>
    public override void OnDpiChanged(float oldScale, float newScale)
    {
        _themeApplied = false;
    }

    // ── Lifecycle ────────────────────────────────────────────────────

    protected override IOverlayManager? CreateOverlayManager()
    {
        var mgr = new ImGuiManager();

        string? iconFontPath = ExtractEmbeddedFont();
        if (iconFontPath != null)
        {
            mgr.IconFontPath = iconFontPath;
            mgr.IconGlyphRangeMin = PhosphorGlyphRangeMin;
            mgr.IconGlyphRangeMax = PhosphorGlyphRangeMax;
        }

        return mgr;
    }

    public override void Initialize()
    {
        _projectManager = new ProjectManager();
        _newProjectLocation = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ProwlProjects");

        // Subscribe to OS file-drop events so users can drag project folders onto the window.
        _fileDropSub = WindowEvents.SubscribeOnFileDrop(args =>
        {
            int added = 0;
            foreach (string file in args.Files)
            {
                if (Directory.Exists(file))
                {
                    ProjectInfo? info = _projectManager.AddExistingProject(file);
                    if (info != null)
                        added++;
                }
            }

            if (added > 0)
            {
                _selectedIndex = 0;
                ShowToast($"Added {added} project{(added > 1 ? "s" : "")}.", new Vector4(0.20f, 0.72f, 0.40f, 1f));
            }
            else if (args.Files.Length > 0)
            {
                ShowToast("Dropped folder is not a valid Prowl project.", new Vector4(0.85f, 0.35f, 0.25f, 1f));
            }
        });
    }

    // ── Main render ─────────────────────────────────────────────────

    public override void BeginImGui(IUIRenderer ui)
    {
        if (!_themeApplied) { ApplyTheme(); _themeApplied = true; }

        // Process any completed folder picker results
        ProcessFolderPickerResult();

        // Full-screen host window (no decorations)
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.WorkPos);
        ImGui.SetNextWindowSize(vp.WorkSize);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        ImGui.Begin("##LauncherHost",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoDocking);
        ImGui.PopStyleVar(3);

        const float sidebarWidthBase = 200;
        float sidebarWidth = sidebarWidthBase * Game.DpiScale;

        // ── Left sidebar ──
        DrawSidebar(sidebarWidth);
        ImGui.SameLine();

        // ── Right main content ──
        ImGui.BeginChild("##MainContent", new Vector2(0, 0));

        DrawHeader();
        ImGui.Separator();
        DrawProjectList();

        // Handle global keyboard shortcuts (only when no popup is open)
        if (!ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopup))
            HandleKeyboardShortcuts();

        ImGui.EndChild();

        // ── Toast overlay ──
        DrawToast(vp);

        ImGui.End();

        // ── Popup modals ──
        DrawNewProjectPopup();
        DrawRemovePopup();
        DrawDeleteFromDiskPopup();
    }

    // ── Sidebar ──────────────────────────────────────────────────────

    private void DrawSidebar(float width)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.09f, 1.0f));
        ImGui.BeginChild("##Sidebar", new Vector2(width, 0));

        ImGui.Spacing(); ImGui.Spacing();

        if (ImGuiUIRenderer.Fonts.TryGetValue(21, out var bigFont))
            ImGui.PushFont(bigFont);

        ImGui.SetCursorPosX(S(20));
        ImGui.TextColored(new Vector4(0.30f, 0.56f, 1.00f, 1.00f), "\ue1da  Prowl");

        if (ImGuiUIRenderer.Fonts.TryGetValue(21, out _))
            ImGui.PopFont();

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

        ImGui.SetCursorPosX(S(8));
        ImGui.Selectable("  \ue24a  Projects", true, ImGuiSelectableFlags.None, new Vector2(width - S(16), S(28)));

        ImGui.Spacing();
        ImGui.SetCursorPosX(S(8));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.40f, 0.40f, 0.40f, 1f));
        ImGui.Selectable("  \ue914  Templates", false, ImGuiSelectableFlags.Disabled, new Vector2(width - S(16), S(28)));
        ImGui.Selectable("  \ue272  Settings", false, ImGuiSelectableFlags.Disabled, new Vector2(width - S(16), S(28)));
        ImGui.PopStyleColor();

        // Drag-and-drop hint at bottom of sidebar
        float bottom = ImGui.GetWindowHeight() - S(60);
        if (bottom > ImGui.GetCursorPosY())
        {
            ImGui.SetCursorPosY(bottom);
            ImGui.SetCursorPosX(S(12));
            ImGui.TextColored(new Vector4(0.28f, 0.28f, 0.30f, 1f), "Drag folders here");
            ImGui.SetCursorPosX(S(12));
            ImGui.TextColored(new Vector4(0.28f, 0.28f, 0.30f, 1f), "to add projects");
        }

        // Version
        bottom = ImGui.GetWindowHeight() - S(24);
        if (bottom > ImGui.GetCursorPosY())
        {
            ImGui.SetCursorPosY(bottom);
            ImGui.SetCursorPosX(S(12));
            ImGui.TextColored(new Vector4(0.35f, 0.35f, 0.35f, 1f), "v0.1.0");
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    // ── Header ───────────────────────────────────────────────────────

    private void DrawHeader()
    {
        ImGui.Spacing();
        float indent = S(16);
        ImGui.SetCursorPosX(indent);

        if (ImGuiUIRenderer.Fonts.TryGetValue(19, out var headFont))
            ImGui.PushFont(headFont);
        ImGui.Text("Projects");
        if (ImGuiUIRenderer.Fonts.TryGetValue(19, out _))
            ImGui.PopFont();

        // Right-aligned action buttons
        float buttonAreaWidth = S(370);
        ImGui.SameLine(ImGui.GetWindowWidth() - buttonAreaWidth);

        // ▸ New Project (Ctrl+N)
        PushAccentButton();
        if (ImGui.Button("\ue3d4  New Project", S(120, 28)))
            OpenNewProjectModal();
        PopAccentButton();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Create a new project (Ctrl+N)");

        ImGui.SameLine();

        // ▸ Add existing — directly opens native folder picker
        bool pickerBusy = _folderPickerTask != null;
        if (pickerBusy) ImGui.BeginDisabled();
        if (ImGui.Button(pickerBusy ? "\ue140  Browsing..." : "\ue24a  Add", S(pickerBusy ? 110 : 70, 28)))
            OpenAddExistingPicker();
        if (pickerBusy) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Add an existing project folder");

        ImGui.SameLine();

        // ▸ Open selected (Enter)
        bool canOpen = _selectedIndex >= 0 && _selectedIndex < _projectManager.Projects.Count
                       && Directory.Exists(_projectManager.Projects[_selectedIndex].Path);
        if (!canOpen) ImGui.BeginDisabled();
        PushAccentButton();
        if (ImGui.Button("\ue256  Open", S(70, 28)))
            LaunchEditor(_projectManager.Projects[_selectedIndex]);
        PopAccentButton();
        if (!canOpen) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(canOpen ? "Open selected project (Enter)" : "Select a project to open");

        ImGui.SameLine();

        // ▸ Sort dropdown
        string sortLabel = _sortMode switch
        {
            SortMode.Name => "\ue36c  Name",
            SortMode.Path => "\ue24a  Path",
            _ => "\ue140  Recent",
        };
        if (ImGui.Button(sortLabel, S(90, 28)))
            ImGui.OpenPopup("##SortPopup");

        if (ImGui.BeginPopup("##SortPopup"))
        {
            if (ImGui.Selectable("Recent", _sortMode == SortMode.LastModified))
            { _sortMode = SortMode.LastModified; _sortDescending = true; }
            if (ImGui.Selectable("Name", _sortMode == SortMode.Name))
            { _sortMode = SortMode.Name; _sortDescending = false; }
            if (ImGui.Selectable("Path", _sortMode == SortMode.Path))
            { _sortMode = SortMode.Path; _sortDescending = false; }
            ImGui.EndPopup();
        }

        ImGui.Spacing();

        // ── Search / filter bar ──
        ImGui.SetCursorPosX(indent);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - S(16));
        ImGui.InputTextWithHint("##ProjectSearch", "\ue30c  Search projects...", ref _searchFilter, 256);

        ImGui.Spacing();
    }

    // ── Project List ─────────────────────────────────────────────────

    private void DrawProjectList()
    {
        var projects = _projectManager.Projects;

        ImGui.BeginChild("##ProjectList");

        bool hasFilter = !string.IsNullOrWhiteSpace(_searchFilter);

        if (projects.Count == 0)
        {
            DrawEmptyState();
        }
        else
        {
            var indices = Enumerable.Range(0, projects.Count).ToList();

            indices.Sort((a, b) =>
            {
                int cmp = _sortMode switch
                {
                    SortMode.Name => string.Compare(projects[a].Name, projects[b].Name, StringComparison.OrdinalIgnoreCase),
                    SortMode.Path => string.Compare(projects[a].Path, projects[b].Path, StringComparison.OrdinalIgnoreCase),
                    _ => projects[a].LastModified.CompareTo(projects[b].LastModified),
                };
                return _sortDescending ? -cmp : cmp;
            });

            int visibleCount = 0;
            foreach (int i in indices)
            {
                if (hasFilter && !projects[i].Name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)
                    && !projects[i].Path.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                visibleCount++;
                ImGui.PushID(i);
                DrawProjectCard(projects[i], i);
                ImGui.PopID();
            }

            if (visibleCount == 0 && hasFilter)
            {
                ImGui.Spacing(); ImGui.Spacing();
                ImGui.SetCursorPosX(S(40));
                ImGui.TextColored(new Vector4(0.50f, 0.50f, 0.50f, 1f),
                    $"No projects match \"{_searchFilter}\".");
            }
        }

        ImGui.EndChild();
    }

    private void DrawEmptyState()
    {
        float centerY = ImGui.GetContentRegionAvail().Y * 0.30f;
        ImGui.SetCursorPosY(centerY);

        string emptyIcon = "\ue24a";
        if (ImGuiUIRenderer.Fonts.TryGetValue(21, out var bigFont))
            ImGui.PushFont(bigFont);
        var iconSize = ImGui.CalcTextSize(emptyIcon);
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - iconSize.X) * 0.5f);
        ImGui.TextColored(new Vector4(0.30f, 0.30f, 0.30f, 1f), emptyIcon);
        if (ImGuiUIRenderer.Fonts.TryGetValue(21, out _))
            ImGui.PopFont();

        ImGui.Spacing();

        CenterText("No projects yet", new Vector4(0.50f, 0.50f, 0.50f, 1f));
        ImGui.Spacing();
        CenterText("Create a new project, add an existing one,", new Vector4(0.38f, 0.38f, 0.38f, 1f));
        CenterText("or drag a project folder onto this window.", new Vector4(0.38f, 0.38f, 0.38f, 1f));

        ImGui.Spacing(); ImGui.Spacing(); ImGui.Spacing();

        // Center the action buttons
        float totalBtnWidth = S(130) + S(8) + S(130);
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - totalBtnWidth) * 0.5f);

        PushAccentButton();
        if (ImGui.Button("\ue3d4  New Project", S(130, 34)))
            OpenNewProjectModal();
        PopAccentButton();

        ImGui.SameLine();

        bool pickerBusy = _folderPickerTask != null;
        if (pickerBusy) ImGui.BeginDisabled();
        if (ImGui.Button("\ue24a  Add Existing", S(130, 34)))
            OpenAddExistingPicker();
        if (pickerBusy) ImGui.EndDisabled();
    }

    private static void CenterText(string text, Vector4 color)
    {
        var size = ImGui.CalcTextSize(text);
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - size.X) * 0.5f);
        ImGui.TextColored(color, text);
    }

    private void DrawProjectCard(ProjectInfo project, int index)
    {
        bool isSelected = _selectedIndex == index;
        bool exists = Directory.Exists(project.Path);

        float cardHeight = S(80);
        float padding = S(12);
        float cardMarginH = S(8);

        var dl = ImGui.GetWindowDrawList();
        Vector2 cursorStart = ImGui.GetCursorScreenPos();
        float availWidth = ImGui.GetContentRegionAvail().X;

        Vector2 cardMin = new(cursorStart.X + cardMarginH, cursorStart.Y);
        Vector2 cardMax = new(cursorStart.X + availWidth - cardMarginH, cursorStart.Y + cardHeight);

        // Invisible button for interaction
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + cardMarginH);
        bool clicked = ImGui.InvisibleButton("##card", new Vector2(availWidth - cardMarginH * 2, cardHeight));
        bool isHovered = ImGui.IsItemHovered();

        // Handle click and double-click with manual timing (InvisibleButton consumes
        // the first click of a double-click, so IsMouseDoubleClicked is unreliable).
        if (clicked)
        {
            double now = ImGui.GetTime();
            if (_lastClickIndex == index && (now - _lastClickTime) < DoubleClickThreshold)
            {
                // Double-click detected
                if (exists)
                    LaunchEditor(project);
                _lastClickIndex = -1;
            }
            else
            {
                _selectedIndex = index;
                _lastClickIndex = index;
                _lastClickTime = now;
            }
        }

        // Card background color
        Vector4 bgColor;
        if (isSelected)
            bgColor = new Vector4(0.20f, 0.36f, 0.60f, 0.50f);
        else if (isHovered)
            bgColor = new Vector4(0.20f, 0.20f, 0.22f, 1.0f);
        else
            bgColor = new Vector4(0.16f, 0.16f, 0.16f, 1.0f);

        uint bgCol = ImGui.GetColorU32(bgColor);
        dl.AddRectFilled(cardMin, cardMax, bgCol, S(6));

        // Left accent bar
        uint accentCol = exists
            ? ImGui.GetColorU32(new Vector4(0.28f, 0.56f, 1.00f, isSelected ? 1.0f : 0.60f))
            : ImGui.GetColorU32(new Vector4(0.75f, 0.25f, 0.25f, 0.80f));
        dl.AddRectFilled(cardMin, new Vector2(cardMin.X + S(4), cardMax.Y), accentCol, S(6),
            ImDrawFlags.RoundCornersLeft);

        // Thumbnail
        float thumbSize = S(48);
        float thumbX = cardMin.X + padding + S(4);
        float thumbY = cardMin.Y + (cardHeight - thumbSize) * 0.5f;
        uint thumbColor = GenerateProjectColor(project.Name);
        dl.AddRectFilled(new Vector2(thumbX, thumbY),
            new Vector2(thumbX + thumbSize, thumbY + thumbSize), thumbColor, S(4));

        string iconGlyph = exists ? "\ue24a" : "\ue4e0";
        var initialSize = ImGui.CalcTextSize(iconGlyph);
        dl.AddText(
            new Vector2(thumbX + (thumbSize - initialSize.X) * 0.5f,
                        thumbY + (thumbSize - initialSize.Y) * 0.5f),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.90f)), iconGlyph);

        // Text area
        float textX = thumbX + thumbSize + padding;
        float textY = cardMin.Y + S(12);

        uint nameCol = exists
            ? ImGui.GetColorU32(new Vector4(0.92f, 0.92f, 0.92f, 1f))
            : ImGui.GetColorU32(new Vector4(0.75f, 0.25f, 0.25f, 1f));
        string nameText = exists ? project.Name : $"{project.Name}  (missing)";

        if (ImGuiUIRenderer.Fonts.TryGetValue(17, out var nameFont))
            dl.AddText(nameFont, 17 * Game.DpiScale, new Vector2(textX, textY), nameCol, nameText);
        else
            dl.AddText(new Vector2(textX, textY), nameCol, nameText);

        // Path
        // Truncate path if too long for the available space
        float maxPathWidth = cardMax.X - textX - S(120); // Reserve space for date + action buttons
        string pathDisplay = project.Path;
        if (maxPathWidth > 0)
        {
            var pathSize = ImGui.CalcTextSize(pathDisplay);
            if (pathSize.X > maxPathWidth)
            {
                // Truncate from the left, show "...tail"
                while (pathDisplay.Length > 10 && ImGui.CalcTextSize("..." + pathDisplay).X > maxPathWidth)
                    pathDisplay = pathDisplay.Substring(1);
                pathDisplay = "..." + pathDisplay;
            }
        }

        dl.AddText(new Vector2(textX, textY + S(22)),
            ImGui.GetColorU32(new Vector4(0.45f, 0.45f, 0.45f, 1f)), pathDisplay);

        // Relative time (right-aligned)
        string dateStr = FormatRelativeTime(project.LastModified);
        var dateSz = ImGui.CalcTextSize(dateStr);
        dl.AddText(new Vector2(cardMax.X - dateSz.X - padding, cardMin.Y + S(12)),
            ImGui.GetColorU32(new Vector4(0.40f, 0.40f, 0.40f, 1f)), dateStr);

        // Hover action buttons (show in explorer) — right side of card
        if (isHovered && exists)
        {
            float btnSize = S(22);
            float btnY = cardMin.Y + (cardHeight - btnSize) * 0.5f;
            float btnX = cardMax.X - S(36);

            // "Reveal in explorer" icon button
            Vector2 btnMin = new(btnX, btnY);
            Vector2 btnMax = new(btnX + btnSize, btnY + btnSize);

            bool btnHovered = ImGui.GetMousePos().X >= btnMin.X && ImGui.GetMousePos().X <= btnMax.X
                           && ImGui.GetMousePos().Y >= btnMin.Y && ImGui.GetMousePos().Y <= btnMax.Y;

            uint btnBg = btnHovered
                ? ImGui.GetColorU32(new Vector4(0.30f, 0.30f, 0.32f, 1f))
                : ImGui.GetColorU32(new Vector4(0.22f, 0.22f, 0.24f, 1f));
            dl.AddRectFilled(btnMin, btnMax, btnBg, S(3));

            string revealIcon = "\ue24a";
            var revealSz = ImGui.CalcTextSize(revealIcon);
            dl.AddText(new Vector2(btnX + (btnSize - revealSz.X) * 0.5f, btnY + (btnSize - revealSz.Y) * 0.5f),
                ImGui.GetColorU32(new Vector4(0.70f, 0.70f, 0.70f, 1f)), revealIcon);

            if (btnHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                RevealInExplorer(project.Path);
        }

        // Full path tooltip
        if (isHovered)
            ImGui.SetTooltip(project.Path);

        // Right-click context menu
        if (ImGui.BeginPopupContextItem())
        {
            if (exists && ImGui.MenuItem("\ue256  Open project"))
                LaunchEditor(project);

            if (exists && ImGui.MenuItem("\ue24a  Reveal in file explorer"))
                RevealInExplorer(project.Path);

            ImGui.Separator();

            if (ImGui.MenuItem("\ue4f6  Remove from list"))
            {
                _selectedIndex = index;
                _showRemoveModal = true;
            }

            if (exists && ImGui.MenuItem("\ue4a6  Delete from disk..."))
            {
                _selectedIndex = index;
                _deleteConfirmInput = string.Empty;
                _showDeleteFromDiskModal = true;
            }

            ImGui.EndPopup();
        }

        ImGui.Spacing();
    }

    // ── Keyboard shortcuts ───────────────────────────────────────────

    private void HandleKeyboardShortcuts()
    {
        var io = ImGui.GetIO();

        // Ctrl+N → New Project
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.N))
            OpenNewProjectModal();

        // Enter → Open selected project
        if (ImGui.IsKeyPressed(ImGuiKey.Enter) && !ImGui.IsAnyItemActive())
        {
            if (_selectedIndex >= 0 && _selectedIndex < _projectManager.Projects.Count
                && Directory.Exists(_projectManager.Projects[_selectedIndex].Path))
            {
                LaunchEditor(_projectManager.Projects[_selectedIndex]);
            }
        }

        // Delete → Remove selected from list
        if (ImGui.IsKeyPressed(ImGuiKey.Delete) && !ImGui.IsAnyItemActive())
        {
            if (_selectedIndex >= 0 && _selectedIndex < _projectManager.Projects.Count)
                _showRemoveModal = true;
        }

        // Up/Down arrows → Navigate project list
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow) && !ImGui.IsAnyItemActive())
        {
            if (_selectedIndex > 0)
                _selectedIndex--;
            else if (_projectManager.Projects.Count > 0)
                _selectedIndex = 0;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow) && !ImGui.IsAnyItemActive())
        {
            if (_selectedIndex < _projectManager.Projects.Count - 1)
                _selectedIndex++;
        }

        // Escape → Clear selection
        if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !ImGui.IsAnyItemActive())
            _selectedIndex = -1;
    }

    // ── Modal: New Project ───────────────────────────────────────────

    private void OpenNewProjectModal()
    {
        _newProjectName = "MyProject";
        _newProjectError = string.Empty;
        _showNewProjectModal = true;
        _newProjectFocusName = true;
    }

    private void DrawNewProjectPopup()
    {
        if (_showNewProjectModal)
            ImGui.OpenPopup("New Project");

        ImGui.SetNextWindowSize(S(520, 280), ImGuiCond.Always);
        if (ImGui.BeginPopupModal("New Project", ref _showNewProjectModal, ImGuiWindowFlags.NoResize))
        {
            ImGui.Spacing();

            // Project Name
            ImGui.Text("Project Name:");
            ImGui.SetNextItemWidth(-1);
            if (_newProjectFocusName)
            {
                ImGui.SetKeyboardFocusHere();
                _newProjectFocusName = false;
            }
            if (ImGui.InputText("##ProjName", ref _newProjectName, 256,
                    ImGuiInputTextFlags.EnterReturnsTrue))
            {
                TryCreateProject();
            }

            ImGui.Spacing();

            // Location with Browse button
            ImGui.Text("Location:");
            float browseWidth = S(80);
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - browseWidth - S(4));
            ImGui.InputText("##ProjLoc", ref _newProjectLocation, 1024);

            ImGui.SameLine();
            bool pickerBusy = _folderPickerTask != null;
            if (pickerBusy) ImGui.BeginDisabled();
            if (ImGui.Button("Browse...", new Vector2(browseWidth, 0)))
            {
                _folderPickerTarget = PickerTarget.NewProjectLocation;
                _folderPickerTask = NativeFolderPicker.PickFolderAsync(
                    Directory.Exists(_newProjectLocation) ? _newProjectLocation : null);
            }
            if (pickerBusy) ImGui.EndDisabled();

            // Preview of the full project path
            if (!string.IsNullOrWhiteSpace(_newProjectName) && !string.IsNullOrWhiteSpace(_newProjectLocation))
            {
                string preview = Path.Combine(_newProjectLocation, _newProjectName.Trim());
                ImGui.TextColored(new Vector4(0.40f, 0.40f, 0.40f, 1f), $"Creates: {preview}");
            }

            // Validation error
            if (!string.IsNullOrEmpty(_newProjectError))
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.90f, 0.30f, 0.25f, 1f), $"\ue4e0  {_newProjectError}");
            }

            ImGui.Spacing(); ImGui.Spacing();

            PushAccentButton();
            if (ImGui.Button("Create", S(120, 32)))
                TryCreateProject();
            PopAccentButton();

            ImGui.SameLine();
            if (ImGui.Button("Cancel", S(120, 32)))
            {
                _showNewProjectModal = false;
                _newProjectError = string.Empty;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void TryCreateProject()
    {
        string name = _newProjectName.Trim();
        string location = _newProjectLocation.Trim();

        // Validate project name
        if (string.IsNullOrWhiteSpace(name))
        {
            _newProjectError = "Project name cannot be empty.";
            return;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(invalidChars) >= 0)
        {
            _newProjectError = "Project name contains invalid characters.";
            return;
        }

        // Validate location
        if (string.IsNullOrWhiteSpace(location))
        {
            _newProjectError = "Location cannot be empty.";
            return;
        }

        // Check if project folder already exists and is non-empty
        string fullPath = Path.Combine(location, name);
        if (Directory.Exists(fullPath) && Directory.EnumerateFileSystemEntries(fullPath).Any())
        {
            _newProjectError = "A non-empty folder already exists at this path.";
            return;
        }

        try
        {
            _projectManager.CreateProject(location, name);
            _selectedIndex = 0;
            _newProjectError = string.Empty;
            _showNewProjectModal = false;
            ShowToast($"Project \"{name}\" created.", new Vector4(0.20f, 0.72f, 0.40f, 1f));
            ImGui.CloseCurrentPopup();
        }
        catch (Exception ex)
        {
            _newProjectError = $"Failed to create project: {ex.Message}";
        }
    }

    // ── Add Existing (native folder picker) ──────────────────────────

    private void OpenAddExistingPicker()
    {
        if (_folderPickerTask != null)
            return;

        _folderPickerTarget = PickerTarget.AddExisting;
        string? defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _folderPickerTask = NativeFolderPicker.PickFolderAsync(defaultDir);
    }

    private void ProcessFolderPickerResult()
    {
        if (_folderPickerTask == null || !_folderPickerTask.IsCompleted)
            return;

        string? result = null;
        try
        {
            result = _folderPickerTask.Result;
        }
        catch
        {
            ShowToast("Folder picker failed to open.", new Vector4(0.85f, 0.35f, 0.25f, 1f));
        }

        PickerTarget target = _folderPickerTarget;
        _folderPickerTask = null;
        _folderPickerTarget = PickerTarget.None;

        if (string.IsNullOrEmpty(result))
            return;

        switch (target)
        {
            case PickerTarget.NewProjectLocation:
                _newProjectLocation = result;
                break;

            case PickerTarget.AddExisting:
                ProjectInfo? info = _projectManager.AddExistingProject(result);
                if (info != null)
                {
                    _selectedIndex = 0;
                    ShowToast($"Added \"{info.Name}\".", new Vector4(0.20f, 0.72f, 0.40f, 1f));
                }
                else
                {
                    ShowToast("Not a valid Prowl project. Expected an Assets folder or ProjectSettings.",
                        new Vector4(0.85f, 0.35f, 0.25f, 1f));
                }
                break;
        }
    }

    // ── Modal: Confirm Remove from List ──────────────────────────────

    private void DrawRemovePopup()
    {
        if (_showRemoveModal)
            ImGui.OpenPopup("Remove Project?");

        ImGui.SetNextWindowSize(S(440, 160), ImGuiCond.Always);
        if (ImGui.BeginPopupModal("Remove Project?", ref _showRemoveModal, ImGuiWindowFlags.NoResize))
        {
            string name = (_selectedIndex >= 0 && _selectedIndex < _projectManager.Projects.Count)
                ? _projectManager.Projects[_selectedIndex].Name : "";

            ImGui.Spacing();
            ImGui.Text($"Remove \"{name}\" from the project list?");
            ImGui.TextColored(new Vector4(0.50f, 0.50f, 0.50f, 1f),
                "The project folder will not be deleted from disk.");

            ImGui.Spacing(); ImGui.Spacing();

            PushDangerButton();
            if (ImGui.Button("Remove", S(100, 32)))
            {
                if (_selectedIndex >= 0 && _selectedIndex < _projectManager.Projects.Count)
                {
                    string removedName = _projectManager.Projects[_selectedIndex].Name;
                    _projectManager.RemoveFromList(_projectManager.Projects[_selectedIndex]);
                    ClampSelectedIndex();
                    ShowToast($"\"{removedName}\" removed from list.", new Vector4(0.50f, 0.50f, 0.50f, 1f));
                }
                _showRemoveModal = false;
                ImGui.CloseCurrentPopup();
            }
            PopDangerButton();

            ImGui.SameLine();
            if (ImGui.Button("Cancel", S(100, 32)))
            {
                _showRemoveModal = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    // ── Modal: Confirm Delete from Disk ──────────────────────────────

    private void DrawDeleteFromDiskPopup()
    {
        if (_showDeleteFromDiskModal)
            ImGui.OpenPopup("Delete Project from Disk");

        ImGui.SetNextWindowSize(S(500, 240), ImGuiCond.Always);
        if (ImGui.BeginPopupModal("Delete Project from Disk", ref _showDeleteFromDiskModal, ImGuiWindowFlags.NoResize))
        {
            string name = (_selectedIndex >= 0 && _selectedIndex < _projectManager.Projects.Count)
                ? _projectManager.Projects[_selectedIndex].Name : "";
            string path = (_selectedIndex >= 0 && _selectedIndex < _projectManager.Projects.Count)
                ? _projectManager.Projects[_selectedIndex].Path : "";

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.90f, 0.30f, 0.25f, 1f),
                "\ue4e0  This action is irreversible!");
            ImGui.Spacing();
            ImGui.TextWrapped($"This will permanently delete the project folder and all its contents:");
            ImGui.TextColored(new Vector4(0.45f, 0.45f, 0.45f, 1f), path);
            ImGui.Spacing();

            ImGui.Text($"Type \"{name}\" to confirm:");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##ConfirmDelete", ref _deleteConfirmInput, 256);

            ImGui.Spacing(); ImGui.Spacing();

            bool confirmed = string.Equals(_deleteConfirmInput.Trim(), name, StringComparison.Ordinal);
            if (!confirmed) ImGui.BeginDisabled();
            PushDangerButton();
            if (ImGui.Button("Delete permanently", S(160, 32)))
            {
                if (_selectedIndex >= 0 && _selectedIndex < _projectManager.Projects.Count)
                {
                    _projectManager.DeleteProject(_projectManager.Projects[_selectedIndex]);
                    ClampSelectedIndex();
                    ShowToast($"\"{name}\" deleted.", new Vector4(0.85f, 0.35f, 0.25f, 1f));
                }
                _showDeleteFromDiskModal = false;
                _deleteConfirmInput = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            PopDangerButton();
            if (!confirmed) ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Cancel", S(100, 32)))
            {
                _showDeleteFromDiskModal = false;
                _deleteConfirmInput = string.Empty;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    // ── Toast notifications ──────────────────────────────────────────

    private void ShowToast(string message, Vector4 color, float durationSeconds = 3.5f)
    {
        _toastMessage = message;
        _toastColor = color;
        _toastExpiry = (float)ImGui.GetTime() + durationSeconds;
    }

    private void DrawToast(ImGuiViewportPtr vp)
    {
        if (string.IsNullOrEmpty(_toastMessage))
            return;

        float now = (float)ImGui.GetTime();
        float remaining = _toastExpiry - now;
        if (remaining <= 0)
        {
            _toastMessage = string.Empty;
            return;
        }

        // Fade out during the last 0.5s
        float alpha = Math.Min(remaining / 0.5f, 1.0f);

        var textSize = ImGui.CalcTextSize(_toastMessage);
        float toastWidth = textSize.X + S(32);
        float toastHeight = S(36);
        float toastX = vp.WorkPos.X + (vp.WorkSize.X - toastWidth) * 0.5f;
        float toastY = vp.WorkPos.Y + vp.WorkSize.Y - toastHeight - S(16);

        var dl = ImGui.GetForegroundDrawList();

        // Background
        dl.AddRectFilled(
            new Vector2(toastX, toastY),
            new Vector2(toastX + toastWidth, toastY + toastHeight),
            ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.14f, 0.95f * alpha)),
            S(8));

        // Left accent bar
        dl.AddRectFilled(
            new Vector2(toastX, toastY),
            new Vector2(toastX + S(4), toastY + toastHeight),
            ImGui.GetColorU32(new Vector4(_toastColor.X, _toastColor.Y, _toastColor.Z, alpha)),
            S(8), ImDrawFlags.RoundCornersLeft);

        // Text
        dl.AddText(
            new Vector2(toastX + S(16), toastY + (toastHeight - textSize.Y) * 0.5f),
            ImGui.GetColorU32(new Vector4(0.90f, 0.90f, 0.90f, alpha)),
            _toastMessage);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void ClampSelectedIndex()
    {
        if (_selectedIndex >= _projectManager.Projects.Count)
            _selectedIndex = _projectManager.Projects.Count - 1;
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch { /* ignore platform errors */ }
    }

    /// <summary>
    /// Formats a timestamp as a human-readable relative time string.
    /// </summary>
    private static string FormatRelativeTime(DateTime timestamp)
    {
        TimeSpan elapsed = DateTime.Now - timestamp;

        if (elapsed.TotalMinutes < 1) return "Just now";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays}d ago";
        if (elapsed.TotalDays < 30) return $"{(int)(elapsed.TotalDays / 7)}w ago";
        if (elapsed.TotalDays < 365) return $"{(int)(elapsed.TotalDays / 30)}mo ago";

        return timestamp.ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// Generates a deterministic color from a project name for the thumbnail.
    /// </summary>
    private static uint GenerateProjectColor(string name)
    {
        int hash = 0;
        foreach (char c in name)
            hash = hash * 31 + c;

        float hue = (hash & 0x7FFFFFFF) % 360 / 360f;
        float sat = 0.45f;
        float val = 0.35f;

        float c2 = val * sat;
        float x = c2 * (1 - MathF.Abs(hue * 6 % 2 - 1));
        float m = val - c2;
        float r, g, b;
        int sector = (int)(hue * 6) % 6;
        (r, g, b) = sector switch
        {
            0 => (c2, x, 0f),
            1 => (x, c2, 0f),
            2 => (0f, c2, x),
            3 => (0f, x, c2),
            4 => (x, 0f, c2),
            _ => (c2, 0f, x),
        };

        return ImGui.GetColorU32(new Vector4(r + m, g + m, b + m, 1f));
    }

    // ── Style helpers ────────────────────────────────────────────────

    private static void PushAccentButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.28f, 0.56f, 1.00f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.36f, 0.64f, 1.00f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.20f, 0.48f, 0.92f, 1.00f));
    }

    private static void PopAccentButton() => ImGui.PopStyleColor(3);

    private static void PushDangerButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.70f, 0.23f, 0.23f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.80f, 0.30f, 0.30f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.60f, 0.18f, 0.18f, 1.00f));
    }

    private static void PopDangerButton() => ImGui.PopStyleColor(3);

    // ── Theme ────────────────────────────────────────────────────────

    private static void ApplyTheme()
    {
        ImGui.StyleColorsDark();
        var style = ImGui.GetStyle();
        style.WindowRounding = 0;
        style.FrameRounding = 6;
        style.GrabRounding = 4;
        style.TabRounding = 4;
        style.ScrollbarRounding = 6;
        style.PopupRounding = 6;
        style.ChildRounding = 4;
        style.FramePadding = new Vector2(10, 5);
        style.ItemSpacing = new Vector2(8, 6);
        style.ScrollbarSize = 12;

        var c = style.Colors;
        c[(int)ImGuiCol.WindowBg]       = new Vector4(0.11f, 0.11f, 0.12f, 1f);
        c[(int)ImGuiCol.ChildBg]        = new Vector4(0.13f, 0.13f, 0.14f, 1f);
        c[(int)ImGuiCol.PopupBg]        = new Vector4(0.13f, 0.13f, 0.14f, 0.97f);
        c[(int)ImGuiCol.Border]         = new Vector4(0.20f, 0.20f, 0.22f, 0.60f);
        c[(int)ImGuiCol.Header]         = new Vector4(0.20f, 0.20f, 0.22f, 1f);
        c[(int)ImGuiCol.HeaderHovered]  = new Vector4(0.28f, 0.56f, 1.00f, 0.30f);
        c[(int)ImGuiCol.HeaderActive]   = new Vector4(0.28f, 0.56f, 1.00f, 0.50f);
        c[(int)ImGuiCol.Separator]      = new Vector4(0.20f, 0.20f, 0.22f, 0.80f);
        c[(int)ImGuiCol.FrameBg]        = new Vector4(0.16f, 0.16f, 0.18f, 1f);
        c[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.22f, 0.22f, 0.24f, 1f);
        c[(int)ImGuiCol.FrameBgActive]  = new Vector4(0.26f, 0.26f, 0.28f, 1f);
        c[(int)ImGuiCol.Button]         = new Vector4(0.18f, 0.18f, 0.20f, 1f);
        c[(int)ImGuiCol.ButtonHovered]  = new Vector4(0.24f, 0.24f, 0.26f, 1f);
        c[(int)ImGuiCol.ButtonActive]   = new Vector4(0.28f, 0.28f, 0.30f, 1f);
        c[(int)ImGuiCol.ScrollbarBg]    = new Vector4(0.10f, 0.10f, 0.10f, 0.5f);
        c[(int)ImGuiCol.ScrollbarGrab]  = new Vector4(0.24f, 0.24f, 0.26f, 1f);
        c[(int)ImGuiCol.ScrollbarGrabHovered] = new Vector4(0.30f, 0.30f, 0.32f, 1f);
        c[(int)ImGuiCol.ScrollbarGrabActive]  = new Vector4(0.36f, 0.36f, 0.38f, 1f);

        style.ScaleAllSizes(Game.DpiScale);
    }

    // ── Editor Launch ────────────────────────────────────────────────

    private void LaunchEditor(ProjectInfo project)
    {
        // Update timestamp on launch
        project.LastModified = DateTime.Now;
        _projectManager.Save();

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        string[] candidates =
        [
            Path.Combine(baseDir, "Prowl.Editor.exe"),
            Path.Combine(baseDir, "Prowl.Editor"),
            Path.Combine(baseDir, "..", "Editor", "Debug", "Prowl.Editor.exe"),
            Path.Combine(baseDir, "..", "Editor", "Release", "Prowl.Editor.exe"),
            Path.Combine(baseDir, "..", "Editor", "Debug", "Prowl.Editor"),
            Path.Combine(baseDir, "..", "Editor", "Release", "Prowl.Editor"),
        ];

        string? editorPath = candidates.FirstOrDefault(File.Exists);

        if (editorPath == null)
        {
            string srcProject = Path.GetFullPath(
                Path.Combine(baseDir, "..", "..", "..", "..", "Prowl.Editor", "Prowl.Editor.csproj"));
            if (File.Exists(srcProject))
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"run --project \"{srcProject}\" -- --project \"{project.Path}\"",
                    UseShellExecute = false,
                };
                System.Diagnostics.Process.Start(psi);
                ShowToast($"Launching \"{project.Name}\" via dotnet run...", new Vector4(0.28f, 0.56f, 1.00f, 1f));
                return;
            }

            ShowToast("Could not find Prowl.Editor executable.", new Vector4(0.85f, 0.35f, 0.25f, 1f));
            Debug.LogError("[Launcher] Could not find Prowl.Editor executable.");
            return;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = editorPath,
            Arguments = $"--project \"{project.Path}\"",
            UseShellExecute = false,
        };

        System.Diagnostics.Process.Start(startInfo);
        ShowToast($"Launching \"{project.Name}\"...", new Vector4(0.28f, 0.56f, 1.00f, 1f));
        Debug.Log($"[Launcher] Launched editor for project: {project.Name}");
    }

    // ── Phosphor Icons constants ─────────────────────────────────────
    private const int PhosphorGlyphRangeMin = 0xE002;
    private const int PhosphorGlyphRangeMax = 0xED6E;

    /// <summary>
    /// Extracts the embedded Phosphor Icons TTF to a temp file so ImGui can load it.
    /// Returns the file path, or null on failure.
    /// </summary>
    private static string? ExtractEmbeddedFont()
    {
        try
        {
            var asm = typeof(LauncherApplication).Assembly;
            string? resName = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("Phosphor.ttf", StringComparison.OrdinalIgnoreCase));
            if (resName == null) return null;

            string tempPath = Path.Combine(Path.GetTempPath(), "Prowl_Phosphor.ttf");
            if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
            {
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream == null) return null;
                using var fs = File.Create(tempPath);
                stream.CopyTo(fs);
            }
            return tempPath;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Launcher] Failed to extract Phosphor icon font: {ex.Message}");
            return null;
        }
    }
}
