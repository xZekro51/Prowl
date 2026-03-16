// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using ImGuiNET;
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

    // ── Lifecycle ────────────────────────────────────────────────────

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

    private static void DrawSidebar(float width)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.10f, 0.10f, 0.10f, 1.0f));
        ImGui.BeginChild("##Sidebar", new Vector2(width, 0));

        ImGui.Spacing(); ImGui.Spacing();

        // Logo
        if (ImGuiUIRenderer.Fonts.TryGetValue(20, out var bigFont))
            ImGui.PushFont(bigFont);

        ImGui.SetCursorPosX(S(20));
        ImGui.TextColored(new Vector4(0.30f, 0.56f, 1.00f, 1.00f), "Prowl Engine");

        if (ImGuiUIRenderer.Fonts.TryGetValue(20, out _))
            ImGui.PopFont();

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

        // Navigation
        ImGui.SetCursorPosX(S(8));
        ImGui.Selectable("  \uf07c  Projects", true, ImGuiSelectableFlags.None, new Vector2(width - S(16), S(28)));

        ImGui.Spacing();
        ImGui.SetCursorPosX(S(8));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.40f, 0.40f, 0.40f, 1f));
        ImGui.Selectable("  \uf19d  Learn", false, ImGuiSelectableFlags.Disabled, new Vector2(width - S(16), S(28)));
        ImGui.Selectable("  \uf0c0  Community", false, ImGuiSelectableFlags.Disabled, new Vector2(width - S(16), S(28)));
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

        if (ImGuiUIRenderer.Fonts.TryGetValue(18, out var headFont))
            ImGui.PushFont(headFont);
        ImGui.Text("Projects");
        if (ImGuiUIRenderer.Fonts.TryGetValue(18, out _))
            ImGui.PopFont();

        // Right-aligned action buttons
        float buttonAreaWidth = S(260);
        ImGui.SameLine(ImGui.GetWindowWidth() - buttonAreaWidth);

        // ▸ New Project
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.28f, 0.56f, 1.00f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.36f, 0.64f, 1.00f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.20f, 0.48f, 0.92f, 1.00f));
        if (ImGui.Button("New Project", S(100, 28)))
        {
            _newProjectName = "MyProject";
            _showNewProjectModal = true;
        }
        ImGui.PopStyleColor(3);

        ImGui.SameLine();

        // ▸ Add existing
        if (ImGui.Button("Add", S(60, 28)))
        {
            _browsePath = "";
            _showBrowseModal = true;
        }

        ImGui.SameLine();

        // ▸ Open selected
        bool canOpen = _selectedIndex >= 0 && _selectedIndex < _projectManager.Projects.Count
                       && Directory.Exists(_projectManager.Projects[_selectedIndex].Path);
        if (!canOpen) ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.28f, 0.56f, 1.00f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.36f, 0.64f, 1.00f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.20f, 0.48f, 0.92f, 1.00f));
        if (ImGui.Button("Open", S(60, 28)))
        {
            LaunchEditor(_projectManager.Projects[_selectedIndex]);
        }
        ImGui.PopStyleColor(3);
        if (!canOpen) ImGui.EndDisabled();

        ImGui.Spacing();

        // ── Search / filter bar ──
        ImGui.SetCursorPosX(indent);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - S(16));
        ImGui.InputTextWithHint("##ProjectSearch", "\ud83d\udd0d Search projects...", ref _searchFilter, 256);

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
            ImGui.Spacing(); ImGui.Spacing();
            ImGui.SetCursorPosX(S(40));
            ImGui.TextColored(new Vector4(0.50f, 0.50f, 0.50f, 1f),
                "No projects yet.  Click \"New Project\" or \"Add\" to get started.");
        }
        else
        {
            int visibleCount = 0;
            for (int i = 0; i < projects.Count; i++)
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

        // Project initial letter on thumbnail
        string initial = project.Name.Length > 0 ? project.Name[..1].ToUpper() : "?";
        var initialSize = ImGui.CalcTextSize(initial);
        dl.AddText(
            new Vector2(thumbX + (thumbSize - initialSize.X) * 0.5f,
                        thumbY + (thumbSize - initialSize.Y) * 0.5f),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.90f)), initial);

        // Text area start
        float textX = thumbX + thumbSize + padding;
        float textY = cardMin.Y + S(12);

        // Project name (larger if font available)
        uint nameCol = exists
            ? ImGui.GetColorU32(new Vector4(0.92f, 0.92f, 0.92f, 1f))
            : ImGui.GetColorU32(new Vector4(0.75f, 0.25f, 0.25f, 1f));
        string nameText = exists ? project.Name : $"{project.Name}  (missing)";

        if (ImGuiUIRenderer.Fonts.TryGetValue(16, out var nameFont))
        {
            dl.AddText(nameFont, 16 * Game.DpiScale,
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
            if (exists && ImGui.MenuItem("Open"))
                LaunchEditor(project);
            ImGui.Separator();
            if (ImGui.MenuItem("Remove from list"))
            {
                _selectedIndex = index;
                _showDeleteModal = true;
            }
            if (exists && ImGui.MenuItem("Delete from disk"))
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
        style.FrameRounding = 4;
        style.GrabRounding = 2;
        style.TabRounding = 4;
        style.ScrollbarRounding = 4;
        style.FramePadding = new Vector2(8, 4);
        style.ItemSpacing = new Vector2(8, 6);

        var c = style.Colors;
        c[(int)ImGuiCol.WindowBg]       = new Vector4(0.12f, 0.12f, 0.12f, 1f);
        c[(int)ImGuiCol.ChildBg]        = new Vector4(0.14f, 0.14f, 0.14f, 1f);
        c[(int)ImGuiCol.PopupBg]        = new Vector4(0.14f, 0.14f, 0.14f, 0.96f);
        c[(int)ImGuiCol.Header]         = new Vector4(0.22f, 0.22f, 0.22f, 1f);
        c[(int)ImGuiCol.HeaderHovered]  = new Vector4(0.28f, 0.56f, 1.00f, 0.30f);
        c[(int)ImGuiCol.HeaderActive]   = new Vector4(0.28f, 0.56f, 1.00f, 0.50f);
        c[(int)ImGuiCol.Separator]      = new Vector4(0.22f, 0.22f, 0.22f, 1f);
        c[(int)ImGuiCol.FrameBg]        = new Vector4(0.18f, 0.18f, 0.18f, 1f);
        c[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.24f, 0.24f, 0.24f, 1f);
        c[(int)ImGuiCol.FrameBgActive]  = new Vector4(0.28f, 0.28f, 0.28f, 1f);

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
}
