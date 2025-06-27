// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Assets;
using Prowl.Editor.Preferences;
using Prowl.Icons;
using Prowl.Runtime;
using Prowl.Runtime.GUI;
using Prowl.Runtime.GUI.Layout;

namespace Prowl.Editor;

public class ProwlHubWindow : EditorWindow
{
    public Project? SelectedProject;

    private string _searchText = "";
    private string _createName = "";

    private readonly (string, Action)[] _tabs;
    private int _currentTab;
    private bool _createTabOpen;

    private FileDialog _dialog;
    private FileDialogContext _dialogContext;

    // Table sorting
    private enum SortBy { Name, Modified, EditorVersion }
    private SortBy _sortBy = SortBy.Modified;
    private bool _sortAscending = false;

    protected override bool Center { get; } = true;
    protected override double Width { get; } = 1200;
    protected override double Height { get; } = 800;
    protected override bool BackgroundFade { get; } = true;
    protected override bool TitleBar { get; } = false;
    protected override bool RoundCorners => false;
    protected override bool LockSize => true;
    protected override double Padding => 0;

    static readonly Color DarkBG = new Color(0.1f, 0.1f, 0.1f, 1.0f);
    static readonly Color SidebarBG = new Color(0.08f, 0.08f, 0.08f, 1.0f);
    static readonly Color TableHeaderBG = new Color(0.14f, 0.14f, 0.16f, 1.0f);
    static readonly Color TableRowAlternate = new Color(0.08f, 0.08f, 0.1f, 1.0f);
    static readonly Color HubBlue = new Color(0.2f, 0.5f, 1.0f, 1.0f);

    public ProwlHubWindow()
    {
        Title = FontAwesome6.Cube + " Hub";

        _tabs = [
            (FontAwesome6.FolderOpen + "  Projects", DrawProjectsTab),
            (FontAwesome6.Download + "  Installs", DrawInstallsTab),
            (FontAwesome6.BookOpen + "  Learn", DrawLearnTab),
            (FontAwesome6.Users + "  Community", DrawCommunityTab),
            (FontAwesome6.Gear + "  Settings", DrawSettingsTab)
        ];
    }

    protected override void Draw()
    {
        if (Project.HasProject)
            isOpened = false;

        // Main container
        using (gui.CurrentNode.Left(0).Top(0).ExpandWidth().ExpandHeight().Enter())
        {
            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, DarkBG);

            // Title bar
            //DrawTitleBar(); we don't need this

            // Content area
            using (gui.Node("Content").Top(0).ExpandWidth().ExpandHeight(-50).Layout(LayoutType.Row).Enter())
            {
                // Sidebar
                DrawSidebar();

                // Main content area
                DrawMainContent();
            }
        }
    }

    private void DrawTitleBar()
    {
        using (gui.Node("TitleBar").ExpandWidth().Height(50).Layout(LayoutType.Row).Enter())
        {
            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, SidebarBG);

            // Hub logo and title
            using (gui.Node("HubTitle").Width(200).ExpandHeight().Padding(20, 0).Layout(LayoutType.Row).Spacing(10).Enter())
            {
                using (gui.Node("Logo").Width(30).ExpandHeight().Enter())
                {
                    gui.Draw2D.DrawText(FontAwesome6.Cube, 24, gui.CurrentNode.LayoutData.Rect, Color.white);
                }

                using (gui.Node("Title").ExpandWidth().ExpandHeight().Enter())
                {
                    gui.Draw2D.DrawText("Hub", 24, gui.CurrentNode.LayoutData.Rect, Color.white);
                }
            }

            // Spacer
            using (gui.Node("Spacer").ExpandWidth().ExpandHeight().Enter()) { }

            // Window controls (top right)
            using (gui.Node("WindowControls").Width(100).ExpandHeight().Layout(LayoutType.Row).Enter())
            {
                using (gui.Node("MinimizeBtn").Width(50).ExpandHeight().Enter())
                {
                    if (gui.IsNodeHovered())
                        gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, Color.white * 0.1f);

                    if (gui.IsNodePressed())
                    {
                        // TODO: Minimize window
                    }

                    gui.Draw2D.DrawText(FontAwesome6.Minus, gui.CurrentNode.LayoutData.Rect, Color.white);
                }

                using (gui.Node("CloseBtn").Width(50).ExpandHeight().Enter())
                {
                    if (gui.IsNodeHovered())
                        gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Red * 0.8f);

                    if (gui.IsNodePressed())
                        isOpened = false;

                    gui.Draw2D.DrawText(FontAwesome6.Xmark, gui.CurrentNode.LayoutData.Rect, Color.white);
                }
            }
        }
    }

    private void DrawSidebar()
    {
        using (gui.Node("Sidebar").Width(250).ExpandHeight().Layout(LayoutType.Column).Spacing(5).Padding(20).Enter())
        {
            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, SidebarBG);

            for (int i = 0; i < _tabs.Length; i++)
            {
                using (gui.Node($"Tab_{i}").ExpandWidth().Height(45).Enter())
                {
                    bool isSelected = _currentTab == i;
                    bool isHovered = gui.IsNodeHovered();

                    if (isSelected)
                    {
                        gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, Color.white * 0.15f, (float)EditorStylePrefs.Instance.ButtonRoundness);
                    }
                    else if (isHovered)
                    {
                        gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, Color.white * 0.08f, (float)EditorStylePrefs.Instance.ButtonRoundness);
                    }

                    if (gui.IsNodePressed())
                        _currentTab = i;

                    Color textColor = isSelected ? Color.white : Color.white * 0.7f;
                    var rect = gui.CurrentNode.LayoutData.Rect;
                    rect.x += 15;
                    gui.Draw2D.DrawText(_tabs[i].Item1, 16, rect, textColor);
                }
            }
        }
    }

    private void DrawMainContent()
    {
        using (gui.Node("MainContent").ExpandWidth().ExpandHeight().Enter())
        {
            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, DarkBG);

            _tabs[_currentTab].Item2.Invoke();
        }
    }

    private void DrawProjectsTab()
    {
        using (gui.Node("ProjectsTab").ExpandWidth().ExpandHeight().Layout(LayoutType.Column).Padding(30).Enter())
        {
            // Header with search and buttons
            DrawProjectsHeader();

            // Projects table
            DrawProjectsTable();
        }

        // Create project overlay
        if (_createTabOpen)
        {
            DrawCreateProjectOverlay();
        }
    }

    private void DrawProjectsHeader()
    {
        using (gui.Node("Header").ExpandWidth().Height(60).Layout(LayoutType.Row).Spacing(20).Enter())
        {
            // Projects title
            using (gui.Node("Title").FitContentWidth().ExpandHeight().Enter())
            {
                gui.Draw2D.DrawText("Projects", 32, gui.CurrentNode.LayoutData.Rect, Color.white);
            }

            // Search box
            using (gui.Node("SearchContainer").Width(300).Height(35).Top(12).Enter())
            {
                gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, Color.black * 0.3f, 6);
                gui.Draw2D.DrawRect(gui.CurrentNode.LayoutData.Rect, Color.white * 0.2f, 1, 6);

                var searchRect = gui.CurrentNode.LayoutData.Rect;
                searchRect.x += 40;
                searchRect.width -= 50;
                searchRect.y += 2;
                searchRect.height -= 4;

                // Search icon
                var iconRect = gui.CurrentNode.LayoutData.Rect;
                iconRect.x += 10;
                iconRect.width = 20;
                gui.Draw2D.DrawText(FontAwesome6.MagnifyingGlass, 14, iconRect, Color.white * 0.5f);

                gui.InputField("SearchInput", ref _searchText, 255, Gui.InputFieldFlags.None, 
                    searchRect.x, searchRect.y, searchRect.width, searchRect.height, EditorGUI.InputStyle);
            }

            // Spacer
            using (gui.Node("Spacer").ExpandWidth().ExpandHeight().Enter()) { }

            // Add button
            using (gui.Node("AddBtn").Width(80).Height(35).Top(12).Enter())
            {
                Color btnColor = Color.white * 0.2f;
                if (gui.IsNodeHovered())
                    btnColor = Color.white * 0.3f;
                if (gui.IsNodePressed())
                {
                    btnColor = Color.white * 0.4f;
                    OpenDialog("Add Existing Project", (x) => ProjectCache.Instance.AddProject(new Project(new DirectoryInfo(x))));
                }

                gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, btnColor, 6);
                gui.Draw2D.DrawText("Add", gui.CurrentNode.LayoutData.Rect, Color.white);
            }

            // New project button
            using (gui.Node("NewBtn").Width(120).Height(35).Top(12).Enter())
            {
                Color btnColor = HubBlue;
                if (gui.IsNodeHovered())
                    btnColor = HubBlue * 1.2f;
                if (gui.IsNodePressed())
                {
                    btnColor = HubBlue * 0.8f;
                    _createTabOpen = !_createTabOpen;
                }

                gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, btnColor, 6);
                gui.Draw2D.DrawText("New project", gui.CurrentNode.LayoutData.Rect, Color.white);
            }
        }
    }

    private void DrawProjectsTable()
    {
        using (gui.Node("Table").Top(80).ExpandWidth().ExpandHeight(-80).Layout(LayoutType.Column).Enter())
        {
            // Table header
            DrawTableHeader();

            // Table content
            DrawTableContent();
        }
    }

    private void DrawTableHeader()
    {
        using (gui.Node("TableHeader").ExpandWidth().Height(40).Layout(LayoutType.Row).Enter())
        {
            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, TableHeaderBG);

            // Icon columns
            DrawHeaderIcon(FontAwesome6.Star, 50);
            DrawHeaderIcon(FontAwesome6.Link, 50);
            DrawHeaderIcon(FontAwesome6.Cube, 50);

            // Sortable columns
            DrawSortableHeader("Name", SortBy.Name, 400);
            DrawSortableHeader("Modified", SortBy.Modified, 150);
            DrawSortableHeader("Editor version", SortBy.EditorVersion, 150);

            // Actions column
            using (gui.Node("ActionsHeader").Width(50).ExpandHeight().Enter())
            {
                // Empty header for actions column
            }
        }
    }

    private void DrawHeaderIcon(string icon, double width)
    {
        using (gui.Node($"Icon_{icon}").Width(width).ExpandHeight().Enter())
        {
            gui.Draw2D.DrawText(icon, gui.CurrentNode.LayoutData.Rect, Color.white * 0.5f);
        }
    }

    private void DrawSortableHeader(string text, SortBy sortType, double width)
    {
        using (gui.Node($"Header_{text}").Width(width).ExpandHeight().Enter())
        {
            bool isHovered = gui.IsNodeHovered();
            if (isHovered)
                gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, Color.white * 0.1f);

            if (gui.IsNodePressed())
            {
                if (_sortBy == sortType)
                    _sortAscending = !_sortAscending;
                else
                {
                    _sortBy = sortType;
                    _sortAscending = true;
                }
            }

            string displayText = text;
            if (_sortBy == sortType)
            {
                displayText += _sortAscending ? " " + FontAwesome6.CaretUp : " " + FontAwesome6.CaretDown;
            }

            var rect = gui.CurrentNode.LayoutData.Rect;
            rect.x += 10;
            gui.Draw2D.DrawText(displayText, 14, rect, Color.white * 0.7f);
        }
    }

    private void DrawTableContent()
    {
        using (gui.Node("TableContent").ExpandWidth().ExpandHeight().Clip().Scroll(inputstyle: EditorGUI.InputStyle).Enter())
        {
            using (gui.Node("TableRows").ExpandWidth().FitContentHeight().Layout(LayoutType.Column).Enter())
            {
                var projects = GetSortedProjects();
                
                for (int i = 0; i < projects.Count; i++)
                {
                    var project = projects[i];
                    if (project == null) continue;

                    if (!string.IsNullOrEmpty(_searchText) && 
                        !project.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                        continue;

                    DrawProjectRow(project, i);
                }
            }
        }
    }

    private void DrawProjectRow(Project project, int index)
    {
        using (gui.Node($"ProjectRow_{project.Name}_{index}").ExpandWidth().Height(65).Layout(LayoutType.Row).Enter())
        {
            bool isSelected = SelectedProject == project;
            bool isHovered = gui.IsNodeHovered();

            // Background
            Color bgColor = index % 2 == 0 ? Color.clear : TableRowAlternate;
            if (isSelected)
                bgColor = HubBlue * 0.3f;
            else if (isHovered)
                bgColor = Color.white * 0.05f;

            if (bgColor.a > 0)
                gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, bgColor);

            // Handle clicks
            if (gui.IsNodePressed())
                SelectedProject = project;

            if (gui.IsPointerDoubleClick(MouseButton.Left) && isHovered)
            {
                Project.Open(project);
                isOpened = false;
            }

            // Star icon
            DrawRowIcon(FontAwesome6.Star, 50, Color.white * 0.3f);

            // Cloud icon
            DrawRowIcon(FontAwesome6.CloudArrowUp, 50, Color.white * 0.3f);

            // Platform icon
            DrawRowIcon(FontAwesome6.Cube, 50, Color.white * 0.3f);

            // Name column
            using (gui.Node("NameColumn").Width(400).ExpandHeight().Padding(15, 10).Layout(LayoutType.Column).Enter())
            {
                using (gui.Node("ProjectName").ExpandWidth().Height(25).Enter())
                {
                    gui.Draw2D.DrawText(project.Name, 18, gui.CurrentNode.LayoutData.Rect, Color.white);
                }

                using (gui.Node("ProjectPath").ExpandWidth().Height(20).Enter())
                {
                    string path = project.ProjectPath;
                    if (path.Length > 60)
                        path = "..." + path.Substring(path.Length - 60);

                    gui.Draw2D.DrawText(path, 14, gui.CurrentNode.LayoutData.Rect, Color.white * 0.6f);
                }
            }

            // Modified column
            using (gui.Node("ModifiedColumn").Width(150).ExpandHeight().Padding(10).Enter())
            {
                string timeAgo = GetFormattedLastModifiedTime(project.ProjectDirectory.LastWriteTime);
                gui.Draw2D.DrawText(timeAgo, 14, gui.CurrentNode.LayoutData.Rect, Color.white * 0.6f);
            }

            // Editor version column
            using (gui.Node("VersionColumn").Width(150).ExpandHeight().Padding(10).Layout(LayoutType.Row).Spacing(10).Enter())
            {
                using (gui.Node("VersionText").FitContentWidth().ExpandHeight().Enter())
                {
                    gui.Draw2D.DrawText("2022.3.51f1", 14, gui.CurrentNode.LayoutData.Rect, Color.white * 0.6f);
                }

                if (!project.IsValid())
                {
                    using (gui.Node("WarningIcon").Width(20).ExpandHeight().Enter())
                    {
                        gui.Draw2D.DrawText(FontAwesome6.TriangleExclamation, gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Yellow);
                    }
                }
            }

            // Actions column
            using (gui.Node("ActionsColumn").Width(50).ExpandHeight().Enter())
            {
                if (isHovered)
                {
                    using (gui.Node("MenuBtn").Width(30).Height(30).Enter())
                    {
                        gui.CurrentNode.Left(10).Top(17.5);

                        if (gui.IsNodeHovered())
                            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, Color.white * 0.1f, 4);

                        if (gui.IsNodePressed())
                            ShowProjectContextMenu(project);

                        gui.Draw2D.DrawText(FontAwesome6.EllipsisVertical, gui.CurrentNode.LayoutData.Rect, Color.white * 0.7f);
                    }
                }
            }
        }
    }

    private void DrawRowIcon(string icon, double width, Color color)
    {
        using (gui.Node($"RowIcon_{icon}").Width(width).ExpandHeight().Enter())
        {
            gui.Draw2D.DrawText(icon, gui.CurrentNode.LayoutData.Rect, color);
        }
    }

    private void DrawCreateProjectOverlay()
    {
        // Overlay background
        using (gui.Node("CreateOverlay").Left(0).Top(0).ExpandWidth().ExpandHeight().Enter())
        {
            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, Color.black * 0.5f);

            // Create project panel
            using (gui.Node("CreatePanel").Width(400).Height(500).Enter())
            {
                gui.CurrentNode.Left(Offset.Percentage(0.5f, -200)).Top(Offset.Percentage(0.5f, -250));

                gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, SidebarBG, 10);
                gui.Draw2D.DrawRect(gui.CurrentNode.LayoutData.Rect, Color.white * 0.2f, 1, 10);

                DrawCreateProjectContent();
            }
        }
    }

    private void DrawCreateProjectContent()
    {
        using (gui.Node("CreateContent").ExpandWidth().ExpandHeight().Layout(LayoutType.Column).Padding(30).Spacing(20).Enter())
        {
            // Header
            using (gui.Node("CreateHeader").ExpandWidth().Height(40).Layout(LayoutType.Row).Enter())
            {
                using (gui.Node("CreateTitle").ExpandWidth().ExpandHeight().Enter())
                {
                    gui.Draw2D.DrawText("Create project", 24, gui.CurrentNode.LayoutData.Rect, Color.white);
                }

                using (gui.Node("CloseBtn").Width(30).Height(30).Enter())
                {
                    if (gui.IsNodeHovered())
                        gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Red * 0.8f, 4);

                    if (gui.IsNodePressed())
                        _createTabOpen = false;

                    gui.Draw2D.DrawText(FontAwesome6.Xmark, gui.CurrentNode.LayoutData.Rect, Color.white);
                }
            }

            // Template preview
            using (gui.Node("TemplatePreview").ExpandWidth().Height(150).Enter())
            {
                gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, HubBlue * 0.3f, 8);
                gui.Draw2D.DrawRect(gui.CurrentNode.LayoutData.Rect, HubBlue * 0.7f, 2, 8);
                gui.Draw2D.DrawText(FontAwesome6.PuzzlePiece, 60, gui.CurrentNode.LayoutData.Rect, Color.white);
            }

            // Project name input
            using (gui.Node("NameSection").ExpandWidth().Height(70).Layout(LayoutType.Column).Spacing(10).Enter())
            {
                using (gui.Node("NameLabel").ExpandWidth().Height(20).Enter())
                {
                    gui.Draw2D.DrawText("Project name", 16, gui.CurrentNode.LayoutData.Rect, Color.white * 0.8f);
                }

                using (gui.Node("NameInput").ExpandWidth().Height(40).Enter())
                {
                    gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, Color.black * 0.3f, 6);
                    gui.Draw2D.DrawRect(gui.CurrentNode.LayoutData.Rect, Color.white * 0.2f, 1, 6);

                    var inputRect = gui.CurrentNode.LayoutData.Rect;
                    inputRect.x += 10;
                    inputRect.width -= 20;

                    gui.InputField("ProjectNameInput", ref _createName, 255, Gui.InputFieldFlags.None,
                        inputRect.x, inputRect.y, inputRect.width, inputRect.height, EditorGUI.InputStyle);
                }
            }

            // Location section
            using (gui.Node("LocationSection").ExpandWidth().Height(70).Layout(LayoutType.Column).Spacing(10).Enter())
            {
                using (gui.Node("LocationLabel").ExpandWidth().Height(20).Enter())
                {
                    gui.Draw2D.DrawText("Location", 16, gui.CurrentNode.LayoutData.Rect, Color.white * 0.8f);
                }

                using (gui.Node("LocationRow").ExpandWidth().Height(40).Layout(LayoutType.Row).Spacing(10).Enter())
                {
                    using (gui.Node("LocationDisplay").ExpandWidth().ExpandHeight().Enter())
                    {
                        gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, Color.black * 0.3f, 6);
                        gui.Draw2D.DrawRect(gui.CurrentNode.LayoutData.Rect, Color.white * 0.2f, 1, 6);

                        string path = ProjectCache.Instance.SavedProjectsFolder;
                        if (path.Length > 35)
                            path = "..." + path.Substring(path.Length - 35);

                        var textRect = gui.CurrentNode.LayoutData.Rect;
                        textRect.x += 10;
                        gui.Draw2D.DrawText(path, 14, textRect, Color.white * 0.8f);
                    }

                    using (gui.Node("BrowseBtn").Width(40).ExpandHeight().Enter())
                    {
                        Color btnColor = Color.white * 0.2f;
                        if (gui.IsNodeHovered())
                            btnColor = Color.white * 0.3f;
                        if (gui.IsNodePressed())
                        {
                            btnColor = Color.white * 0.4f;
                            OpenDialog("Select Folder", (x) => ProjectCache.Instance.SavedProjectsFolder = x);
                        }

                        gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, btnColor, 6);
                        gui.Draw2D.DrawText(FontAwesome6.FolderOpen, gui.CurrentNode.LayoutData.Rect, Color.white);
                    }
                }
            }

            // Spacer
            using (gui.Node("Spacer").ExpandWidth().ExpandHeight().Enter()) { }

            // Create button
            using (gui.Node("CreateButton").ExpandWidth().Height(40).Enter())
            {
                bool canCreate = !string.IsNullOrEmpty(_createName) && 
                               Directory.Exists(ProjectCache.Instance.SavedProjectsFolder) && 
                               !Path.Exists(Path.Combine(ProjectCache.Instance.SavedProjectsFolder, _createName));

                Color btnColor = canCreate ? HubBlue : Color.white * 0.3f;
                if (canCreate && gui.IsNodeHovered())
                    btnColor = HubBlue * 1.2f;

                if (gui.IsNodePressed() && canCreate)
                {
                    Project project = Project.CreateNew(new DirectoryInfo(Path.Join(ProjectCache.Instance.SavedProjectsFolder, _createName)));
                    ProjectCache.Instance.AddProject(project);
                    _createTabOpen = false;
                    _createName = "";
                }

                gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, btnColor, 6);
                gui.Draw2D.DrawText("Create project", gui.CurrentNode.LayoutData.Rect, Color.white);
            }
        }
    }

    private void ShowProjectContextMenu(Project project)
    {
        // TODO: Implement context menu using Prowl's popup system
        // For now, just implement basic functionality
        if (gui.IsPointerClick(MouseButton.Right))
        {
            // Basic context menu actions without popup for now
        }
    }

    private void DrawInstallsTab()
    {
        using (gui.Node("InstallsTab").ExpandWidth().ExpandHeight().Padding(30).Enter())
        {
            gui.Draw2D.DrawText("Installs", 32, gui.CurrentNode.LayoutData.Rect, Color.white);
            
            var rect = gui.CurrentNode.LayoutData.Rect;
            rect.y += 60;
            gui.Draw2D.DrawText("Coming soon...", 18, rect, Color.white * 0.7f);
        }
    }

    private void DrawLearnTab()
    {
        using (gui.Node("LearnTab").ExpandWidth().ExpandHeight().Padding(30).Enter())
        {
            gui.Draw2D.DrawText("Learn", 32, gui.CurrentNode.LayoutData.Rect, Color.white);
            
            var rect = gui.CurrentNode.LayoutData.Rect;
            rect.y += 60;
            gui.Draw2D.DrawText("Coming soon...", 18, rect, Color.white * 0.7f);
        }
    }

    private void DrawCommunityTab()
    {
        using (gui.Node("CommunityTab").ExpandWidth().ExpandHeight().Padding(30).Enter())
        {
            gui.Draw2D.DrawText("Community", 32, gui.CurrentNode.LayoutData.Rect, Color.white);
            
            var rect = gui.CurrentNode.LayoutData.Rect;
            rect.y += 60;
            gui.Draw2D.DrawText("Coming soon...", 18, rect, Color.white * 0.7f);
        }
    }

    private void DrawSettingsTab()
    {
        using (gui.Node("SettingsTab").ExpandWidth().ExpandHeight().Padding(30).Enter())
        {
            gui.Draw2D.DrawText("Settings", 32, gui.CurrentNode.LayoutData.Rect, Color.white);
            
            var rect = gui.CurrentNode.LayoutData.Rect;
            rect.y += 60;
            gui.Draw2D.DrawText("Coming soon...", 18, rect, Color.white * 0.7f);
        }
    }

    private List<Project> GetSortedProjects()
    {
        var projects = new List<Project>();
        for (int i = 0; i < ProjectCache.Instance.ProjectsCount; i++)
        {
            var project = ProjectCache.Instance.GetProject(i);
            if (project != null)
                projects.Add(project);
        }

        return _sortBy switch
        {
            SortBy.Name => _sortAscending ? 
                projects.OrderBy(p => p.Name).ToList() : 
                projects.OrderByDescending(p => p.Name).ToList(),
            SortBy.Modified => _sortAscending ? 
                projects.OrderBy(p => p.ProjectDirectory.LastWriteTime).ToList() : 
                projects.OrderByDescending(p => p.ProjectDirectory.LastWriteTime).ToList(),
            SortBy.EditorVersion => _sortAscending ? 
                projects.OrderBy(p => "2022.3.51f1").ToList() : 
                projects.OrderByDescending(p => "2022.3.51f1").ToList(),
            _ => projects
        };
    }

    private void OpenDialog(string title, Action<string> onComplete)
    {
        _dialogContext ??= new();

        _dialogContext.title = title;
        _dialogContext.parentDirectory = new DirectoryInfo(ProjectCache.Instance.SavedProjectsFolder);
        _dialogContext.OnComplete = onComplete;
        _dialogContext.OnCancel = () => _dialogContext.OnComplete = (x) => { };

        EditorGuiManager.Remove(_dialog);
        _dialog = new FileDialog(_dialogContext);
        EditorGuiManager.FocusWindow(_dialog);
    }

    private static string GetFormattedLastModifiedTime(DateTime lastModified)
    {
        TimeSpan timeSinceLastModified = DateTime.Now - lastModified;

        if (timeSinceLastModified.TotalMinutes < 1)
            return "Just now";
        else if (timeSinceLastModified.TotalMinutes < 60)
            return $"{(int)timeSinceLastModified.TotalMinutes} minutes ago";
        else if (timeSinceLastModified.TotalHours < 24)
            return $"{(int)timeSinceLastModified.TotalHours} hours ago";
        else if (timeSinceLastModified.TotalDays < 30)
            return $"{(int)timeSinceLastModified.TotalDays} days ago";
        else if (timeSinceLastModified.TotalDays < 365)
            return $"{(int)(timeSinceLastModified.TotalDays / 30)} months ago";
        else
            return $"{(int)(timeSinceLastModified.TotalDays / 365)} years ago";
    }
}
