// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Xml.Linq;

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

    static readonly Color GrayAlpha = new Color(0, 0, 0, 0.5f);
    static readonly Color TableHeaderBG = new Color(35, 35, 40);
    static readonly Color TableRowAlternate = new Color(20, 20, 25);

    public ProwlHubWindow()
    {
        Title = FontAwesome6.Book + " Prowl Hub";

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

        // Fill parent (Window in this case).
        using (gui.CurrentNode.Left(0).Top(0).ExpandWidth().ExpandHeight().Enter())
        {
            DrawTitleBar();

            using (gui.Node("MainContent").Top(50).ExpandWidth().ExpandHeight(-50).Layout(LayoutType.Row).ScaleChildren().Enter())
            {
                DrawSidebar();
                DrawMainContent();
            }
        }
    }

    private void DrawTitleBar()
    {
        using (gui.Node("TitleBar").ExpandWidth().Height(50).Enter())
        {
            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Instance.Background);

            using (gui.Node("Logo").Left(20).Top(10).Scale(100, 30).Enter())
            {
                gui.Draw2D.DrawText(Font.DefaultFont, FontAwesome6.Book + " Hub", 24, gui.CurrentNode.LayoutData.Rect, Color.white);
            }

            // Window controls
            using (gui.Node("WindowControls").Left(Offset.Percentage(1f, -110)).Top(10).Scale(100, 30).Layout(LayoutType.Row).Spacing(5).Enter())
            {
                // Minimize button
                using (gui.Node("Minimize").Scale(30, 30).Enter())
                {
                    if (gui.IsNodePressed())
                    {
                        // TODO: Minimize window
                    }
                    gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, 
                        gui.IsNodeHovered() ? EditorStylePrefs.Instance.Hovering : new Color(0, 0, 0, 0), 
                        (float)EditorStylePrefs.Instance.ButtonRoundness);
                    gui.Draw2D.DrawText(FontAwesome6.Minus, gui.CurrentNode.LayoutData.Rect);
                }

                // Close button
                using (gui.Node("Close").Scale(30, 30).Enter())
                {
                    if (gui.IsNodePressed())
                        isOpened = false;
                    gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, 
                        gui.IsNodeHovered() ? EditorStylePrefs.Red : new Color(0, 0, 0, 0), 
                        (float)EditorStylePrefs.Instance.ButtonRoundness);
                    gui.Draw2D.DrawText(FontAwesome6.Xmark, gui.CurrentNode.LayoutData.Rect);
                }
            }
        }
    }

    private void DrawSidebar()
    {
        using (gui.Node("Sidebar").Width(200).ExpandHeight().Enter())
        {
            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Instance.WindowBGOne);

            using (gui.Node("SidebarContent").Expand().Padding(10).Layout(LayoutType.Column).Spacing(5).Enter())
            {
                for (int i = 0; i < _tabs.Length; i++)
                {
                    using (gui.Node($"Tab{i}").ExpandWidth().Height(40).Enter())
                    {
                        bool isSelected = _currentTab == i;
                        bool isHovered = gui.IsNodeHovered();

                        if (gui.IsNodePressed())
                            _currentTab = i;

                        Color bgColor = isSelected ? EditorStylePrefs.Instance.Highlighted :
                                       isHovered ? EditorStylePrefs.Instance.Hovering : new Color(0, 0, 0, 0);

                        gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, bgColor, 
                            (float)EditorStylePrefs.Instance.ButtonRoundness);

                        Color textColor = isSelected ? Color.white : EditorStylePrefs.Instance.LesserText;
                        gui.Draw2D.DrawText(_tabs[i].Item1, 16, gui.CurrentNode.LayoutData.Rect, textColor);
                    }
                }
            }
        }
    }

    private void DrawMainContent()
    {
        using (gui.Node("MainContent").ExpandWidth().ExpandHeight().Enter())
        {
            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Instance.WindowBGTwo);

            gui.PushID((ulong)_currentTab);
            _tabs[_currentTab].Item2.Invoke();
            gui.PopID();
        }
    }

    private void DrawProjectsTab()
    {
        using (gui.Node("ProjectsTab").Expand().Padding(20).Layout(LayoutType.Column).Spacing(10).Enter())
        {
            DrawProjectsHeader();
            DrawProjectsTable();
        }
    }

    private void DrawProjectsHeader()
    {
        using (gui.Node("Header").ExpandWidth().Height(50).Layout(LayoutType.Row).Spacing(10).Enter())
        {
            // Title
            using (gui.Node("Title").Width(200).ExpandHeight().Enter())
            {
                gui.Draw2D.DrawText("Projects", 32, gui.CurrentNode.LayoutData.Rect, Color.white);
            }

            // Search bar
            using (gui.Node("Search").Width(300).ExpandHeight().Enter())
            {
                gui.Search("SearchInput", ref _searchText, 10, 15, 280, null, EditorGUI.InputFieldStyle);
            }

            // Spacer
            using (gui.Node("Spacer").ExpandWidth().ExpandHeight().Enter()) { }

            // Add button
            using (gui.Node("AddButton").Width(100).Height(35).Enter())
            {
                if (gui.IsNodePressed())
                {
                    OpenDialog("Add Existing Project", (x) => ProjectCache.Instance.AddProject(new Project(new DirectoryInfo(x))));
                }

                Color buttonColor = gui.IsNodeHovered() ? EditorStylePrefs.Instance.Hovering : EditorStylePrefs.Instance.WindowBGOne;
                gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, buttonColor, 
                    (float)EditorStylePrefs.Instance.ButtonRoundness);
                gui.Draw2D.DrawText("Add", gui.CurrentNode.LayoutData.Rect, Color.white);
            }

            // New project button
            using (gui.Node("NewButton").Width(120).Height(35).Enter())
            {
                if (gui.IsNodePressed())
                {
                    _createTabOpen = !_createTabOpen;
                }

                Color buttonColor = gui.IsNodeHovered() ? EditorStylePrefs.Blue * 1.2f : EditorStylePrefs.Blue;
                gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, buttonColor, 
                    (float)EditorStylePrefs.Instance.ButtonRoundness);
                gui.Draw2D.DrawText("New project", gui.CurrentNode.LayoutData.Rect, Color.white);
            }
        }
    }

    private void DrawProjectsTable()
    {
        using (gui.Node("Table").ExpandWidth().ExpandHeight().Layout(LayoutType.Column).Enter())
        {
            DrawTableHeader();
            DrawTableContent();
        }

        if (_createTabOpen)
        {
            DrawCreateProjectSidebar();
        }
    }

    private void DrawTableHeader()
    {
        using (gui.Node("TableHeader").ExpandWidth().Height(40).Layout(LayoutType.Row).Enter())
        {
            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, TableHeaderBG);

            // Star column
            using (gui.Node("StarHeader").Width(50).ExpandHeight().Enter())
            {
                gui.Draw2D.DrawText(FontAwesome6.Star, gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Instance.LesserText);
            }

            // Cloud column
            using (gui.Node("CloudHeader").Width(50).ExpandHeight().Enter())
            {
                gui.Draw2D.DrawText(FontAwesome6.Cloud, gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Instance.LesserText);
            }

            // Project type column
            using (gui.Node("TypeHeader").Width(50).ExpandHeight().Enter())
            {
                gui.Draw2D.DrawText(FontAwesome6.Cube, gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Instance.LesserText);
            }

            // Name column
            using (gui.Node("NameHeader").ExpandWidth().ExpandHeight().Enter())
            {
                DrawSortableHeader("Name", SortBy.Name);
            }

            // Modified column
            using (gui.Node("ModifiedHeader").Width(150).ExpandHeight().Enter())
            {
                DrawSortableHeader("Modified", SortBy.Modified);
            }

            // Editor version column
            using (gui.Node("VersionHeader").Width(120).ExpandHeight().Enter())
            {
                DrawSortableHeader("Editor version", SortBy.EditorVersion);
            }

            // Actions column
            using (gui.Node("ActionsHeader").Width(50).ExpandHeight().Enter())
            {
                // Empty for actions
            }
        }
    }

    private void DrawSortableHeader(string text, SortBy sortType)
    {
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

        Color textColor = gui.IsNodeHovered() ? Color.white : EditorStylePrefs.Instance.LesserText;
        string displayText = text;
        
        if (_sortBy == sortType)
        {
            displayText += _sortAscending ? " " + FontAwesome6.CaretUp : " " + FontAwesome6.CaretDown;
        }

        gui.Draw2D.DrawText(displayText, gui.CurrentNode.LayoutData.Rect, textColor);
    }

    private void DrawTableContent()
    {
        using (gui.Node("TableContent").ExpandWidth().ExpandHeight().Clip().Scroll(inputstyle: EditorGUI.InputStyle).Enter())
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
                projects.OrderBy(p => "Prowl 1.0").ToList() : 
                projects.OrderByDescending(p => "Prowl 1.0").ToList(),
            _ => projects
        };
    }

    private void DrawProjectRow(Project project, int index)
    {
        using (gui.Node($"ProjectRow{index}").ExpandWidth().Height(60).Layout(LayoutType.Row).Enter())
        {
            bool isSelected = SelectedProject == project;
            bool isHovered = gui.IsNodeHovered();
            
            if (gui.IsNodePressed())
            {
                SelectedProject = project;
            }

            if (gui.IsPointerDoubleClick(MouseButton.Left) && isHovered)
            {
                Project.Open(project);
                isOpened = false;
            }

            // Alternate row colors
            Color bgColor = index % 2 == 0 ? new Color(0, 0, 0, 0) : TableRowAlternate;
            if (isSelected)
                bgColor = EditorStylePrefs.Instance.Highlighted * 0.3f;
            else if (isHovered)
                bgColor = EditorStylePrefs.Instance.Hovering * 0.3f;

            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, bgColor);

            // Star column
            using (gui.Node("Star").Width(50).ExpandHeight().Enter())
            {
                string starIcon = FontAwesome6.Star; // Could be filled/empty based on starred status
                gui.Draw2D.DrawText(starIcon, gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Instance.LesserText);
            }

            // Cloud column
            using (gui.Node("Cloud").Width(50).ExpandHeight().Enter())
            {
                gui.Draw2D.DrawText(FontAwesome6.CloudArrowUp, gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Instance.LesserText);
            }

            // Project type column
            using (gui.Node("Type").Width(50).ExpandHeight().Enter())
            {
                gui.Draw2D.DrawText(FontAwesome6.Cube, gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Instance.LesserText);
            }

            // Name column
            using (gui.Node("Name").ExpandWidth().ExpandHeight().Padding(10).Enter())
            {
                var rect = gui.CurrentNode.LayoutData.InnerRect;
                
                // Project name
                gui.Draw2D.DrawText(project.Name, 18, rect.Position, Color.white);
                
                // Project path (smaller, gray text)
                string path = project.ProjectPath;
                if (path.Length > 60)
                    path = string.Concat("...", path.AsSpan(path.Length - 60));
                
                gui.Draw2D.DrawText(path, 14, rect.Position + new Vector2(0, 25), EditorStylePrefs.Instance.LesserText);
            }

            // Modified column
            using (gui.Node("Modified").Width(150).ExpandHeight().Padding(10).Enter())
            {
                string modifiedText = GetFormattedLastModifiedTime(project.ProjectDirectory.LastWriteTime);
                gui.Draw2D.DrawText(modifiedText, gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Instance.LesserText);
            }

            // Editor version column
            using (gui.Node("Version").Width(120).ExpandHeight().Padding(10).Enter())
            {
                gui.Draw2D.DrawText("Prowl 1.0", gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Instance.LesserText);
                
                // Warning icon for invalid projects
                if (!project.IsValid())
                {
                    gui.Draw2D.DrawText(FontAwesome6.TriangleExclamation, 
                        gui.CurrentNode.LayoutData.Rect.Position + new Vector2(80, 0), 
                        EditorStylePrefs.Yellow);
                }
            }

            // Actions column
            using (gui.Node("Actions").Width(50).ExpandHeight().Enter())
            {
                if (gui.IsNodePressed())
                {
                    gui.OpenPopup("ProjectContextMenu" + index, null, gui.CurrentNode);
                }

                gui.Draw2D.DrawText(FontAwesome6.EllipsisVertical, gui.CurrentNode.LayoutData.Rect, 
                    gui.IsNodeHovered() ? Color.white : EditorStylePrefs.Instance.LesserText);

                DrawProjectContextMenu(project, index);
            }
        }
    }

    private void DrawProjectContextMenu(Project project, int index)
    {
        if (gui.BeginPopup("ProjectContextMenu" + index, out LayoutNode? popupHolder, false, EditorGUI.InputStyle) && popupHolder != null)
        {
            using (popupHolder.Width(200).Padding(10).Layout(LayoutType.Column).Spacing(5).FitContentHeight().Enter())
            {
                if (EditorGUI.StyledButton("Open"))
                {
                    Project.Open(project);
                    isOpened = false;
                    gui.CloseAllPopups();
                }

                if (EditorGUI.StyledButton("Show in Explorer"))
                {
                    AssetDatabase.OpenPath(project.ProjectDirectory, type: FileOpenType.FileExplorer);
                    gui.CloseAllPopups();
                }

                if (EditorGUI.StyledButton("Remove from list"))
                {
                    ProjectCache.Instance.RemoveProject(project);
                    gui.CloseAllPopups();
                }
            }
        }
    }

    private void DrawCreateProjectSidebar()
    {
        using (gui.Node("CreateSidebar").Left(Offset.Percentage(1f, -350)).Top(0).Width(350).ExpandHeight().Enter())
        {
            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Instance.WindowBGOne);

            using (gui.Node("CreateContent").Expand().Padding(20).Layout(LayoutType.Column).Spacing(15).Enter())
            {
                // Header
                using (gui.Node("CreateHeader").ExpandWidth().Height(40).Layout(LayoutType.Row).Enter())
                {
                    using (gui.Node("Title").ExpandWidth().ExpandHeight().Enter())
                    {
                        gui.Draw2D.DrawText("Create project", 24, gui.CurrentNode.LayoutData.Rect, Color.white);
                    }

                    using (gui.Node("CloseBtn").Width(30).Height(30).Enter())
                    {
                        if (gui.IsNodePressed())
                            _createTabOpen = false;

                        Color closeColor = gui.IsNodeHovered() ? EditorStylePrefs.Red : EditorStylePrefs.Instance.LesserText;
                        gui.Draw2D.DrawText(FontAwesome6.Xmark, gui.CurrentNode.LayoutData.Rect, closeColor);
                    }
                }

                // Project template preview
                using (gui.Node("TemplatePreview").ExpandWidth().Height(150).Enter())
                {
                    gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Instance.Highlighted, 
                        (float)EditorStylePrefs.Instance.WindowRoundness);
                    gui.Draw2D.DrawText(FontAwesome6.PuzzlePiece, 50, gui.CurrentNode.LayoutData.Rect, Color.white);
                }

                // Project name input
                using (gui.Node("NameSection").ExpandWidth().Height(60).Layout(LayoutType.Column).Spacing(5).Enter())
                {
                    gui.Draw2D.DrawText("Project name", gui.CurrentNode.LayoutData.Rect.Position, EditorStylePrefs.Instance.LesserText);
                    
                    using (gui.Node("NameInput").ExpandWidth().Height(35).Top(25).Enter())
                    {
                        gui.InputField("ProjectNameInput", ref _createName, 0x100, Gui.InputFieldFlags.None, 
                            0, 0, gui.CurrentNode.LayoutData.Rect.width, 35, EditorGUI.InputStyle);
                    }
                }

                // Location section
                using (gui.Node("LocationSection").ExpandWidth().Height(80).Layout(LayoutType.Column).Spacing(5).Enter())
                {
                    gui.Draw2D.DrawText("Location", gui.CurrentNode.LayoutData.Rect.Position, EditorStylePrefs.Instance.LesserText);
                    
                    string path = ProjectCache.Instance.SavedProjectsFolder;
                    if (path.Length > 35)
                        path = string.Concat("...", path.AsSpan(path.Length - 35));
                    
                    using (gui.Node("LocationDisplay").ExpandWidth().Height(35).Top(25).Layout(LayoutType.Row).Enter())
                    {
                        using (gui.Node("PathText").ExpandWidth().ExpandHeight().Padding(10).Enter())
                        {
                            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, EditorStylePrefs.Instance.WindowBGTwo, 
                                (float)EditorStylePrefs.Instance.ButtonRoundness);
                            gui.Draw2D.DrawText(path, gui.CurrentNode.LayoutData.Rect, Color.white);
                        }

                        using (gui.Node("BrowseBtn").Width(35).ExpandHeight().Left(5).Enter())
                        {
                            if (gui.IsNodePressed())
                            {
                                OpenDialog("Select Folder", (x) => ProjectCache.Instance.SavedProjectsFolder = x);
                            }

                            gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, 
                                gui.IsNodeHovered() ? EditorStylePrefs.Instance.Hovering : EditorStylePrefs.Instance.WindowBGTwo, 
                                (float)EditorStylePrefs.Instance.ButtonRoundness);
                            gui.Draw2D.DrawText(FontAwesome6.FolderOpen, gui.CurrentNode.LayoutData.Rect);
                        }
                    }
                }

                // Spacer
                using (gui.Node("Spacer").ExpandWidth().ExpandHeight().Enter()) { }

                // Create button
                using (gui.Node("CreateBtn").ExpandWidth().Height(40).Enter())
                {
                    bool canCreate = !string.IsNullOrEmpty(_createName) && 
                                   Directory.Exists(ProjectCache.Instance.SavedProjectsFolder) && 
                                   !Path.Exists(Path.Combine(ProjectCache.Instance.SavedProjectsFolder, _createName));

                    if (gui.IsNodePressed() && canCreate)
                    {
                        Project project = Project.CreateNew(new DirectoryInfo(Path.Join(ProjectCache.Instance.SavedProjectsFolder, _createName)));
                        ProjectCache.Instance.AddProject(project);
                        _createTabOpen = false;
                        _createName = "";
                    }

                    Color createColor = canCreate ? 
                        (gui.IsNodeHovered() ? EditorStylePrefs.Blue * 1.2f : EditorStylePrefs.Blue) :
                        EditorStylePrefs.Instance.LesserText;

                    gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, createColor, 
                        (float)EditorStylePrefs.Instance.ButtonRoundness);
                    gui.Draw2D.DrawText("Create project", gui.CurrentNode.LayoutData.Rect, Color.white);
                }
            }
        }
    }

    private void DrawInstallsTab()
    {
        using (gui.Node("InstallsTab").Expand().Padding(20).Enter())
        {
            gui.Draw2D.DrawText("Installs - Coming Soon", 24, gui.CurrentNode.LayoutData.Rect, Color.white);
        }
    }

    private void DrawLearnTab()
    {
        using (gui.Node("LearnTab").Expand().Padding(20).Enter())
        {
            gui.Draw2D.DrawText("Learn - Coming Soon", 24, gui.CurrentNode.LayoutData.Rect, Color.white);
        }
    }

    private void DrawCommunityTab()
    {
        using (gui.Node("CommunityTab").Expand().Padding(20).Enter())
        {
            gui.Draw2D.DrawText("Community - Coming Soon", 24, gui.CurrentNode.LayoutData.Rect, Color.white);
        }
    }

    private void DrawSettingsTab()
    {
        using (gui.Node("SettingsTab").Expand().Padding(20).Enter())
        {
            gui.Draw2D.DrawText("Settings - Coming Soon", 24, gui.CurrentNode.LayoutData.Rect, Color.white);
        }
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
        else
            return $"{(int)timeSinceLastModified.TotalDays} days ago";
    }
}
