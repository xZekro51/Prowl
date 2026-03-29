// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Linq;
using System.Numerics;
using ImGuiNET;
using Prowl.ImGuiIntegration;
using Prowl.Runtime;
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

    // ── State ────────────────────────────────────────────────────────
    private ProjectManager _projectManager = null!;
    private int _selectedIndex = -1;
    private bool _themeApplied;
    private string _searchFilter = string.Empty;

    /// <summary>
    /// Resets the theme flag when DPI changes so that the launcher theme
    /// (including <c>ScaleAllSizes</c>) is reapplied on the next frame.
    /// </summary>
    public override void OnDpiChanged(float oldScale, float newScale)
    {
        _themeApplied = false;
    }

    // Modal: New Project
    private bool _showNewProjectModal;
    private string _newProjectName = "MyProject";
    private string _newProjectLocation = "";

    // Modal: Browse
    private bool _showBrowseModal;
    private string _browsePath = "";

    // Modal: Confirm Delete
    private bool _showDeleteModal;

    // Sort
    private enum SortMode { LastModified, Name, Path }
    private SortMode _sortMode = SortMode.LastModified;
    private bool _sortDescending = true;

    // ── Lifecycle ────────────────────────────────────────────────────

    protected override IOverlayManager? CreateOverlayManager()
    {
        var mgr = new ImGuiManager();

        // Extract the embedded Phosphor Icons font for the launcher
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
    }

    public override void BeginImGui(IUIRenderer ui)
    {
        if (!_themeApplied) { ApplyTheme(); _themeApplied = true; }

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
        ImGui.EndChild();

        ImGui.End();

        // ── Popup modals ──
        DrawNewProjectPopup();
        DrawBrowsePopup();
        DrawDeletePopup();
    }

    // ── Sidebar ──────────────────────────────────────────────────────

    private void DrawSidebar(float width)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.09f, 1.0f));
        ImGui.BeginChild("##Sidebar", new Vector2(width, 0));

        ImGui.Spacing(); ImGui.Spacing();

        // Logo
        if (ImGuiUIRenderer.Fonts.TryGetValue(21, out var bigFont))
            ImGui.PushFont(bigFont);

        ImGui.SetCursorPosX(S(20));
        ImGui.TextColored(new Vector4(0.30f, 0.56f, 1.00f, 1.00f), "\ue1da  Prowl");

        if (ImGuiUIRenderer.Fonts.TryGetValue(21, out _))
            ImGui.PopFont();

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

        // Navigation
        ImGui.SetCursorPosX(S(8));
        ImGui.Selectable("  \ue24a  Projects", true, ImGuiSelectableFlags.None, new Vector2(width - S(16), S(28)));

        ImGui.Spacing();
        ImGui.SetCursorPosX(S(8));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.40f, 0.40f, 0.40f, 1f));
        ImGui.Selectable("  \ue914  Templates", false, ImGuiSelectableFlags.Disabled, new Vector2(width - S(16), S(28)));
        ImGui.Selectable("  \ue272  Settings", false, ImGuiSelectableFlags.Disabled, new Vector2(width - S(16), S(28)));
        ImGui.PopStyleColor();

        // Version at bottom
        float bottom = ImGui.GetWindowHeight() - S(30);
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

        // ▸ New Project
        PushAccentButton();
        if (ImGui.Button("\ue3d4  New Project", S(120, 28)))
        {
            _newProjectName = "MyProject";
            _showNewProjectModal = true;
        }
        PopAccentButton();

        ImGui.SameLine();

        // ▸ Add existing
        if (ImGui.Button("\ue24a  Add", S(70, 28)))
        {
            _browsePath = "";
            _showBrowseModal = true;
        }

        ImGui.SameLine();

        // ▸ Open selected
        bool canOpen = _selectedIndex >= 0 && _selectedIndex < _projectManager.Projects.Count
                       && Directory.Exists(_projectManager.Projects[_selectedIndex].Path);
        if (!canOpen) ImGui.BeginDisabled();
        PushAccentButton();
        if (ImGui.Button("\ue256  Open", S(70, 28)))
        {
            LaunchEditor(_projectManager.Projects[_selectedIndex]);
        }
        PopAccentButton();
        if (!canOpen) ImGui.EndDisabled();

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

        // Filter projects by search
        bool hasFilter = !string.IsNullOrWhiteSpace(_searchFilter);

        if (projects.Count == 0)
        {
            // Empty state with icon
            float centerY = ImGui.GetContentRegionAvail().Y * 0.35f;
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
            string emptyMsg = "No projects yet";
            var msgSize = ImGui.CalcTextSize(emptyMsg);
            ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - msgSize.X) * 0.5f);
            ImGui.TextColored(new Vector4(0.45f, 0.45f, 0.45f, 1f), emptyMsg);

            string subMsg = "Click \"New Project\" or \"Add\" to get started.";
            var subSize = ImGui.CalcTextSize(subMsg);
            ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - subSize.X) * 0.5f);
            ImGui.TextColored(new Vector4(0.35f, 0.35f, 0.35f, 1f), subMsg);
        }
        else
        {
            // Build a sorted index list
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
                // Apply search filter
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

    private void DrawProjectCard(ProjectInfo project, int index)
    {
        bool isSelected = _selectedIndex == index;
        bool exists = Directory.Exists(project.Path);

        float cardHeight = S(80);
        float padding = S(12);
        float cardMarginH = S(8);

        // Card background
        var dl = ImGui.GetWindowDrawList();
        Vector2 cursorStart = ImGui.GetCursorScreenPos();
        float availWidth = ImGui.GetContentRegionAvail().X;

        Vector2 cardMin = new(cursorStart.X + cardMarginH, cursorStart.Y);
        Vector2 cardMax = new(cursorStart.X + availWidth - cardMarginH, cursorStart.Y + cardHeight);

        // Invisible button for interaction
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + cardMarginH);
        if (ImGui.InvisibleButton("##card", new Vector2(availWidth - cardMarginH * 2, cardHeight)))
        {
            _selectedIndex = index;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && exists)
                LaunchEditor(project);
        }

        bool isHovered = ImGui.IsItemHovered();

        // Card background color
        Vector4 bgColor;
        if (isSelected)
            bgColor = new Vector4(0.20f, 0.36f, 0.60f, 0.50f);
        else if (isHovered)
            bgColor = new Vector4(0.20f, 0.20f, 0.22f, 1.0f);
        else
            bgColor = new Vector4(0.16f, 0.16f, 0.16f, 1.0f);

        // Draw card rounded rect
        uint bgCol = ImGui.GetColorU32(bgColor);
        dl.AddRectFilled(cardMin, cardMax, bgCol, S(6));

        // Left accent bar (thin color strip)
        uint accentCol = exists
            ? ImGui.GetColorU32(new Vector4(0.28f, 0.56f, 1.00f, isSelected ? 1.0f : 0.60f))
            : ImGui.GetColorU32(new Vector4(0.75f, 0.25f, 0.25f, 0.80f));
        dl.AddRectFilled(cardMin, new Vector2(cardMin.X + S(4), cardMax.Y), accentCol, S(6),
            ImDrawFlags.RoundCornersLeft);

        // Placeholder thumbnail (colored square)
        float thumbSize = S(48);
        float thumbX = cardMin.X + padding + S(4);
        float thumbY = cardMin.Y + (cardHeight - thumbSize) * 0.5f;
        uint thumbColor = GenerateProjectColor(project.Name);
        dl.AddRectFilled(new Vector2(thumbX, thumbY),
            new Vector2(thumbX + thumbSize, thumbY + thumbSize), thumbColor, S(4));

        // Project icon glyph on thumbnail (Phosphor folder icon, fallback to initial)
        string iconGlyph = exists ? "\ue24a" : "\ue4e0"; // FolderSimple or Warning
        var initialSize = ImGui.CalcTextSize(iconGlyph);
        dl.AddText(
            new Vector2(thumbX + (thumbSize - initialSize.X) * 0.5f,
                        thumbY + (thumbSize - initialSize.Y) * 0.5f),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.90f)), iconGlyph);

        // Text area start
        float textX = thumbX + thumbSize + padding;
        float textY = cardMin.Y + S(12);

        // Project name (larger if font available)
        uint nameCol = exists
            ? ImGui.GetColorU32(new Vector4(0.92f, 0.92f, 0.92f, 1f))
            : ImGui.GetColorU32(new Vector4(0.75f, 0.25f, 0.25f, 1f));
        string nameText = exists ? project.Name : $"{project.Name}  (missing)";

        if (ImGuiUIRenderer.Fonts.TryGetValue(17, out var nameFont))
        {
            dl.AddText(nameFont, 17 * Game.DpiScale,
                new Vector2(textX, textY), nameCol, nameText);
        }
        else
        {
            dl.AddText(new Vector2(textX, textY), nameCol, nameText);
        }

        // Path (smaller, dimmer)
        dl.AddText(new Vector2(textX, textY + S(22)),
            ImGui.GetColorU32(new Vector4(0.45f, 0.45f, 0.45f, 1f)), project.Path);

        // Last modified (right-aligned)
        string dateStr = project.LastModified.ToString("yyyy-MM-dd  HH:mm");
        var dateSz = ImGui.CalcTextSize(dateStr);
        dl.AddText(new Vector2(cardMax.X - dateSz.X - padding, cardMin.Y + S(12)),
            ImGui.GetColorU32(new Vector4(0.40f, 0.40f, 0.40f, 1f)), dateStr);

        // Right-click context menu
        if (ImGui.BeginPopupContextItem())
        {
            if (exists && ImGui.MenuItem("\ue256  Open"))
                LaunchEditor(project);
            if (exists && ImGui.MenuItem("\ue24a  Show in Explorer"))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = project.Path,
                        UseShellExecute = true,
                    });
                }
                catch { /* ignore platform errors */ }
            }
            ImGui.Separator();
            if (ImGui.MenuItem("\ue4f6  Remove from list"))
            {
                _selectedIndex = index;
                _showDeleteModal = true;
            }
            if (exists && ImGui.MenuItem("\ue4a6  Delete from disk"))
            {
                _projectManager.DeleteProject(project);
                if (_selectedIndex >= _projectManager.Projects.Count)
                    _selectedIndex = _projectManager.Projects.Count - 1;
            }
            ImGui.EndPopup();
        }

        // Spacing between cards
        ImGui.Spacing();
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

        // HSV to RGB
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

    // ── Modal: New Project ───────────────────────────────────────────

    private void DrawNewProjectPopup()
    {
        if (_showNewProjectModal)
            ImGui.OpenPopup("New Project");

        ImGui.SetNextWindowSize(S(480, 230), ImGuiCond.Always);
        if (ImGui.BeginPopupModal("New Project", ref _showNewProjectModal, ImGuiWindowFlags.NoResize))
        {
            ImGui.Spacing();
            ImGui.Text("Project Name:");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##ProjName", ref _newProjectName, 256);

            ImGui.Spacing();
            ImGui.Text("Location:");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##ProjLoc", ref _newProjectLocation, 1024);

            ImGui.Spacing(); ImGui.Spacing();

            PushAccentButton();
            if (ImGui.Button("Create", S(120, 32)))
            {
                if (!string.IsNullOrWhiteSpace(_newProjectName))
                {
                    _projectManager.CreateProject(_newProjectLocation, _newProjectName.Trim());
                    _selectedIndex = 0;
                    _showNewProjectModal = false;
                    ImGui.CloseCurrentPopup();
                }
            }
            PopAccentButton();

            ImGui.SameLine();
            if (ImGui.Button("Cancel", S(120, 32)))
            {
                _showNewProjectModal = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    // ── Modal: Browse ────────────────────────────────────────────────

    private void DrawBrowsePopup()
    {
        if (_showBrowseModal)
            ImGui.OpenPopup("Add Existing Project");

        ImGui.SetNextWindowSize(S(480, 170), ImGuiCond.Always);
        if (ImGui.BeginPopupModal("Add Existing Project", ref _showBrowseModal, ImGuiWindowFlags.NoResize))
        {
            ImGui.Spacing();
            ImGui.Text("Project folder path:");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##BrowsePath", ref _browsePath, 1024);

            ImGui.Spacing(); ImGui.Spacing();

            PushAccentButton();
            if (ImGui.Button("Add", S(120, 32)))
            {
                if (!string.IsNullOrWhiteSpace(_browsePath))
                {
                    var info = _projectManager.AddExistingProject(_browsePath.Trim());
                    if (info != null)
                    {
                        _selectedIndex = 0;
                        _showBrowseModal = false;
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
            PopAccentButton();

            ImGui.SameLine();
            if (ImGui.Button("Cancel", S(120, 32)))
            {
                _showBrowseModal = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    // ── Modal: Confirm Delete ────────────────────────────────────────

    private void DrawDeletePopup()
    {
        if (_showDeleteModal)
            ImGui.OpenPopup("Remove Project");

        ImGui.SetNextWindowSize(S(460, 190), ImGuiCond.Always);
        if (ImGui.BeginPopupModal("Remove Project", ref _showDeleteModal, ImGuiWindowFlags.NoResize))
        {
            string name = (_selectedIndex >= 0 && _selectedIndex < _projectManager.Projects.Count)
                ? _projectManager.Projects[_selectedIndex].Name : "";

            ImGui.Spacing();
            ImGui.Text($"Remove \"{name}\" from the list?");
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "The project folder will NOT be deleted.");

            ImGui.Spacing(); ImGui.Spacing();

            // Remove from list
            PushDangerButton();
            if (ImGui.Button("Remove from list", S(140, 32)))
            {
                if (_selectedIndex >= 0 && _selectedIndex < _projectManager.Projects.Count)
                {
                    _projectManager.RemoveFromList(_projectManager.Projects[_selectedIndex]);
                    _selectedIndex = -1;
                }
                _showDeleteModal = false;
                ImGui.CloseCurrentPopup();
            }
            PopDangerButton();

            ImGui.SameLine();

            PushDangerButton();
            if (ImGui.Button("Delete from disk", S(140, 32)))
            {
                if (_selectedIndex >= 0 && _selectedIndex < _projectManager.Projects.Count)
                {
                    _projectManager.DeleteProject(_projectManager.Projects[_selectedIndex]);
                    _selectedIndex = -1;
                }
                _showDeleteModal = false;
                ImGui.CloseCurrentPopup();
            }
            PopDangerButton();

            ImGui.SameLine();
            if (ImGui.Button("Cancel", S(80, 32)))
            {
                _showDeleteModal = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
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

        // Scale all style dimensions by the monitor's DPI factor
        style.ScaleAllSizes(Game.DpiScale);
    }

    // ── Editor Launch ────────────────────────────────────────────────

    private static void LaunchEditor(ProjectInfo project)
    {
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
                Debug.Log($"[Launcher] Launched editor via dotnet run for: {project.Name}");
                return;
            }

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
        Debug.Log($"[Launcher] Launched editor for project: {project.Name}");
    }

    // ── Phosphor Icons constants (subset used in launcher) ───────────
    // Range covers the full Phosphor Regular PUA block.
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
