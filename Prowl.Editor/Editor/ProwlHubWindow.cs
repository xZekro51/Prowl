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

// Paper UI Components - Custom implementation for this project
public abstract class PaperElement
{
    public string? Id { get; set; }
    public string Width { get; set; } = "auto";
    public string Height { get; set; } = "auto";
    public Color BackgroundColor { get; set; } = Color.clear;
    public string Padding { get; set; } = "0";
    public string Margin { get; set; } = "0";
    public List<PaperElement> Children { get; set; } = new();
    public Action? OnClick { get; set; }
    public Action? OnDoubleClick { get; set; }
    public Action<bool>? OnHover { get; set; }

    public abstract void Render(Gui gui, string nodeId, int index);
}

public class PaperContainer : PaperElement
{
    public string FlexDirection { get; set; } = "column";
    public string JustifyContent { get; set; } = "flex-start";
    public string AlignItems { get; set; } = "flex-start";
    public string Gap { get; set; } = "0";

    public override void Render(Gui gui, string nodeId, int index)
    {
        var layoutType = FlexDirection == "row" ? LayoutType.Row : LayoutType.Column;
        
        using (gui.Node(nodeId, index).ExpandWidth().FitContentHeight().Layout(layoutType).Enter())
        {
            if (BackgroundColor != Color.clear)
                gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, BackgroundColor, (float)EditorStylePrefs.Instance.WindowRoundness);

            if (OnClick != null && gui.IsNodePressed())
                OnClick.Invoke();

            if (OnDoubleClick != null && gui.IsPointerDoubleClick(MouseButton.Left) && gui.IsNodeHovered())
                OnDoubleClick.Invoke();

            if (OnHover != null)
                OnHover.Invoke(gui.IsNodeHovered());

            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].Render(gui, $"{nodeId}_Child{i}", i);
            }
        }
    }
}

public class PaperText : PaperElement
{
    public string Content { get; set; } = "";
    public int FontSize { get; set; } = 16;
    public Color Color { get; set; } = Color.white;
    public string TextAlign { get; set; } = "left";

    public PaperText(string content) => Content = content;

    public override void Render(Gui gui, string nodeId, int index)
    {
        using (gui.Node(nodeId, index).ExpandWidth().Height(FontSize + 10).Enter())
        {
            if (BackgroundColor != Color.clear)
                gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, BackgroundColor);

            gui.Draw2D.DrawText(Content, FontSize, gui.CurrentNode.LayoutData.Rect, Color);
        }
    }
}

public class PaperButton : PaperElement
{
    public string Text { get; set; } = "";
    public Color TextColor { get; set; } = Color.white;
    public int FontSize { get; set; } = 16;
    public float BorderRadius { get; set; } = 0;

    public override void Render(Gui gui, string nodeId, int index)
    {
        double width = Width == "100%" ? gui.CurrentNode.LayoutData.Rect.width : 
                      Width.EndsWith("px") ? double.Parse(Width.Replace("px", "")) : 100;
        double height = Height == "100%" ? gui.CurrentNode.LayoutData.Rect.height : 
                       Height.EndsWith("px") ? double.Parse(Height.Replace("px", "")) : 35;

        using (gui.Node(nodeId, index).Width(width).Height(height).Enter())
        {
            bool isHovered = gui.IsNodeHovered();
            bool isPressed = gui.IsNodePressed();

            Color bgColor = BackgroundColor;
            if (isPressed && OnClick != null)
            {
                OnClick.Invoke();
                bgColor = Color.Lerp(BackgroundColor, Color.white, 0.3f);
            }
            else if (isHovered)
            {
                bgColor = Color.Lerp(BackgroundColor, Color.white, 0.1f);
            }

            if (bgColor != Color.clear)
                gui.Draw2D.DrawRectFilled(gui.CurrentNode.LayoutData.Rect, bgColor, BorderRadius);

            gui.Draw2D.DrawText(Text, FontSize, gui.CurrentNode.LayoutData.Rect, TextColor);

            OnHover?.Invoke(isHovered);
        }
    }
}

public class PaperTextInput : PaperElement
{
    private string _value = "";
    public string Value
    {
        get => _value;
        set => _value = value;
    }
    
    public string Placeholder { get; set; } = "";
    public Action<string>? OnValueChanged { get; set; }

    public override void Render(Gui gui, string nodeId, int index)
    {
        double width = Width == "100%" ? gui.CurrentNode.LayoutData.Rect.width : 
                      Width.EndsWith("px") ? double.Parse(Width.Replace("px", "")) : 200;
        double height = Height == "100%" ? gui.CurrentNode.LayoutData.Rect.height : 
                       Height.EndsWith("px") ? double.Parse(Height.Replace("px", "")) : 35;

        using (gui.Node(nodeId, index).Width(width).Height(height).Enter())
        {
            string originalValue = _value;
            if (gui.InputField(nodeId + "_input", ref _value, 255, Gui.InputFieldFlags.None, 
                0, 0, width, height, EditorGUI.InputStyle))
            {
                if (_value != originalValue)
                    OnValueChanged?.Invoke(_value);
            }
        }
    }
}

public class PaperScrollView : PaperElement
{
    public override void Render(Gui gui, string nodeId, int index)
    {
        using (gui.Node(nodeId, index).ExpandWidth().ExpandHeight().Clip().Scroll(inputstyle: EditorGUI.InputStyle).Enter())
        {
            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].Render(gui, $"{nodeId}_ScrollChild{i}", i);
            }
        }
    }
}

public class PaperCanvas
{
    private PaperElement? _root;

    public void SetRoot(PaperElement element) => _root = element;
    
    public PaperElement? FindById(string id) => FindElementById(_root, id);
    
    private PaperElement? FindElementById(PaperElement? element, string id)
    {
        if (element?.Id == id) return element;
        
        if (element?.Children != null)
        {
            foreach (var child in element.Children)
            {
                var found = FindElementById(child, id);
                if (found != null) return found;
            }
        }
        
        return null;
    }

    public void Render(Gui gui)
    {
        _root?.Render(gui, "PaperRoot", 0);
    }

    public void Invalidate() { /* For now, this does nothing but could trigger re-layout */ }
}

public class PaperContextMenu
{
    public List<PaperContextMenuItem> Items { get; set; } = new();
    
    public void Show()
    {
        // Implementation would use existing GUI popup system
    }
}

public class PaperContextMenuItem
{
    public string Text { get; set; }
    public Action OnClick { get; set; }

    public PaperContextMenuItem(string text, Action onClick)
    {
        Text = text;
        OnClick = onClick;
    }
}

public class ProwlHubWindow : EditorWindow
{
    public Project? SelectedProject;

    private string _searchText = "";
    private string _createName = "";

    private readonly (string, Func<PaperElement>)[] _tabs;
    private int _currentTab;
    private bool _createTabOpen;

    private FileDialog _dialog;
    private FileDialogContext _dialogContext;

    // Table sorting
    private enum SortBy { Name, Modified, EditorVersion }
    private SortBy _sortBy = SortBy.Modified;
    private bool _sortAscending = false;

    // Paper UI Canvas
    private PaperCanvas _canvas;

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
            (FontAwesome6.FolderOpen + "  Projects", () => CreateProjectsTab()),
            (FontAwesome6.Download + "  Installs", () => CreateInstallsTab()),
            (FontAwesome6.BookOpen + "  Learn", () => CreateLearnTab()),
            (FontAwesome6.Users + "  Community", () => CreateCommunityTab()),
            (FontAwesome6.Gear + "  Settings", () => CreateSettingsTab())
        ];

        InitializePaperUI();
    }

    private void InitializePaperUI()
    {
        _canvas = new PaperCanvas();
        var mainContainer = CreateMainContainer();
        _canvas.SetRoot(mainContainer);
    }

    private PaperElement CreateMainContainer()
    {
        return new PaperContainer()
        {
            Width = "100%",
            Height = "100%",
            FlexDirection = "column",
            Children = [
                CreateTitleBar(),
                CreateContentArea()
            ]
        };
    }

    private PaperElement CreateTitleBar()
    {
        return new PaperContainer()
        {
            Width = "100%",
            Height = "50px",
            BackgroundColor = EditorStylePrefs.Instance.Background,
            FlexDirection = "row",
            JustifyContent = "space-between",
            AlignItems = "center",
            Padding = "20px 10px",
            Children = [
                new PaperText(FontAwesome6.Book + " Hub")
                {
                    FontSize = 24,
                    Color = Color.white
                },
                CreateWindowControls()
            ]
        };
    }

    private PaperElement CreateWindowControls()
    {
        return new PaperContainer()
        {
            FlexDirection = "row",
            Gap = "5px",
            Children = [
                new PaperButton()
                {
                    Width = "30px",
                    Height = "30px",
                    BackgroundColor = Color.clear,
                    BorderRadius = (float)EditorStylePrefs.Instance.ButtonRoundness,
                    Text = FontAwesome6.Minus,
                    OnClick = () => { /* TODO: Minimize window */ }
                },
                new PaperButton()
                {
                    Width = "30px",
                    Height = "30px", 
                    BackgroundColor = Color.clear,
                    BorderRadius = (float)EditorStylePrefs.Instance.ButtonRoundness,
                    Text = FontAwesome6.Xmark,
                    OnClick = () => isOpened = false
                }
            ]
        };
    }

    private PaperElement CreateContentArea()
    {
        return new PaperContainer()
        {
            Width = "100%",
            Height = "calc(100% - 50px)",
            FlexDirection = "row",
            Children = [
                CreateSidebar(),
                CreateMainContent()
            ]
        };
    }

    private PaperElement CreateSidebar()
    {
        var sidebarButtons = new List<PaperElement>();
        
        for (int i = 0; i < _tabs.Length; i++)
        {
            int tabIndex = i; // Capture for closure
            bool isSelected = _currentTab == i;
            
            sidebarButtons.Add(new PaperButton()
            {
                Width = "100%",
                Height = "40px",
                BackgroundColor = isSelected ? EditorStylePrefs.Instance.Highlighted : Color.clear,
                BorderRadius = (float)EditorStylePrefs.Instance.ButtonRoundness,
                Text = _tabs[i].Item1,
                TextColor = isSelected ? Color.white : EditorStylePrefs.Instance.LesserText,
                FontSize = 16,
                OnClick = () => {
                    _currentTab = tabIndex;
                    UpdateMainContent();
                }
            });
        }

        return new PaperContainer()
        {
            Width = "200px",
            Height = "100%",
            BackgroundColor = EditorStylePrefs.Instance.WindowBGOne,
            FlexDirection = "column",
            Padding = "10px",
            Gap = "5px",
            Children = sidebarButtons
        };
    }

    private PaperElement CreateMainContent()
    {
        return new PaperContainer()
        {
            Id = "MainContent",
            Width = "calc(100% - 200px)",
            Height = "100%",
            BackgroundColor = EditorStylePrefs.Instance.WindowBGTwo,
            Children = [
                _tabs[_currentTab].Item2.Invoke()
            ]
        };
    }

    private void UpdateMainContent()
    {
        var mainContent = _canvas.FindById("MainContent") as PaperContainer;
        if (mainContent != null)
        {
            mainContent.Children = [_tabs[_currentTab].Item2.Invoke()];
            _canvas.Invalidate();
        }
    }

    private PaperElement CreateProjectsTab()
    {
        return new PaperContainer()
        {
            Width = "100%",
            Height = "100%",
            FlexDirection = "column",
            Padding = "20px",
            Gap = "10px",
            Children = [
                CreateProjectsHeader(),
                CreateProjectsTable()
            ]
        };
    }

    private PaperElement CreateProjectsHeader()
    {
        return new PaperContainer()
        {
            Width = "100%",
            Height = "50px",
            FlexDirection = "row",
            AlignItems = "center",
            Gap = "10px",
            Children = [
                new PaperText("Projects")
                {
                    FontSize = 32,
                    Color = Color.white
                },
                new PaperTextInput()
                {
                    Width = "300px",
                    Height = "100%",
                    Placeholder = "Search projects...",
                    Value = _searchText,
                    OnValueChanged = (value) => {
                        _searchText = value;
                        UpdateProjectsTable();
                    }
                },
                new PaperContainer() { Width = "1fr" }, // Spacer
                new PaperButton()
                {
                    Width = "100px",
                    Height = "35px",
                    BackgroundColor = EditorStylePrefs.Instance.WindowBGOne,
                    BorderRadius = (float)EditorStylePrefs.Instance.ButtonRoundness,
                    Text = "Add",
                    TextColor = Color.white,
                    OnClick = () => OpenDialog("Add Existing Project", (x) => ProjectCache.Instance.AddProject(new Project(new DirectoryInfo(x))))
                },
                new PaperButton()
                {
                    Width = "120px",
                    Height = "35px",
                    BackgroundColor = EditorStylePrefs.Blue,
                    BorderRadius = (float)EditorStylePrefs.Instance.ButtonRoundness,
                    Text = "New project",
                    TextColor = Color.white,
                    OnClick = () => {
                        _createTabOpen = !_createTabOpen;
                    }
                }
            ]
        };
    }

    private PaperElement CreateProjectsTable()
    {
        return new PaperContainer()
        {
            Width = "100%",
            Height = "calc(100% - 50px)",
            FlexDirection = "column",
            Children = [
                CreateTableHeader(),
                CreateTableContent()
            ]
        };
    }

    private PaperElement CreateTableHeader()
    {
        return new PaperContainer()
        {
            Width = "100%",
            Height = "40px",
            BackgroundColor = TableHeaderBG,
            FlexDirection = "row",
            AlignItems = "center",
            Children = [
                new PaperText(FontAwesome6.Star) { Width = "50px", Color = EditorStylePrefs.Instance.LesserText },
                new PaperText(FontAwesome6.Cloud) { Width = "50px", Color = EditorStylePrefs.Instance.LesserText },
                new PaperText(FontAwesome6.Cube) { Width = "50px", Color = EditorStylePrefs.Instance.LesserText },
                CreateSortableHeader("Name", SortBy.Name),
                CreateSortableHeader("Modified", SortBy.Modified),
                CreateSortableHeader("Editor version", SortBy.EditorVersion),
                new PaperContainer() { Width = "50px" } // Actions column
            ]
        };
    }

    private PaperElement CreateSortableHeader(string text, SortBy sortType)
    {
        string displayText = text;
        if (_sortBy == sortType)
        {
            displayText += _sortAscending ? " " + FontAwesome6.CaretUp : " " + FontAwesome6.CaretDown;
        }

        return new PaperButton()
        {
            Width = "150px",
            Height = "100%",
            BackgroundColor = Color.clear,
            Text = displayText,
            TextColor = EditorStylePrefs.Instance.LesserText,
            OnClick = () => {
                if (_sortBy == sortType)
                    _sortAscending = !_sortAscending;
                else
                {
                    _sortBy = sortType;
                    _sortAscending = true;
                }
                UpdateProjectsTable();
            }
        };
    }

    private PaperElement CreateTableContent()
    {
        var projects = GetSortedProjects();
        var projectRows = new List<PaperElement>();

        for (int i = 0; i < projects.Count; i++)
        {
            var project = projects[i];
            if (project == null) continue;

            if (!string.IsNullOrEmpty(_searchText) && 
                !project.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                continue;

            projectRows.Add(CreateProjectRow(project, i));
        }

        return new PaperScrollView()
        {
            Width = "100%",
            Height = "calc(100% - 40px)",
            Children = [
                new PaperContainer()
                {
                    Width = "100%",
                    FlexDirection = "column",
                    Children = projectRows
                }
            ]
        };
    }

    private PaperElement CreateProjectRow(Project project, int index)
    {
        bool isSelected = SelectedProject == project;
        Color bgColor = index % 2 == 0 ? Color.clear : TableRowAlternate;
        if (isSelected)
            bgColor = EditorStylePrefs.Instance.Highlighted * 0.3f;

        string path = project.ProjectPath;
        if (path.Length > 60)
            path = string.Concat("...", path.AsSpan(path.Length - 60));

        var nameColumn = new PaperContainer()
        {
            Width = "calc(100% - 370px)",
            Height = "100%",
            FlexDirection = "column",
            JustifyContent = "center",
            Padding = "10px",
            Children = [
                new PaperText(project.Name) { FontSize = 18, Color = Color.white },
                new PaperText(path) { FontSize = 14, Color = EditorStylePrefs.Instance.LesserText }
            ]
        };

        var versionColumn = new PaperContainer()
        {
            Width = "120px",
            Height = "100%",
            FlexDirection = "row",
            AlignItems = "center",
            Padding = "10px",
            Children = [
                new PaperText("Prowl 1.0") { Color = EditorStylePrefs.Instance.LesserText }
            ]
        };

        if (!project.IsValid())
        {
            versionColumn.Children.Add(new PaperText(FontAwesome6.TriangleExclamation)
            {
                Color = EditorStylePrefs.Yellow
            });
        }

        return new PaperContainer()
        {
            Width = "100%",
            Height = "60px",
            FlexDirection = "row",
            AlignItems = "center",
            BackgroundColor = bgColor,
            OnClick = () => {
                SelectedProject = project;
                UpdateProjectsTable();
            },
            OnDoubleClick = () => {
                Project.Open(project);
                isOpened = false;
            },
            Children = [
                new PaperText(FontAwesome6.Star) { Width = "50px", Color = EditorStylePrefs.Instance.LesserText },
                new PaperText(FontAwesome6.CloudArrowUp) { Width = "50px", Color = EditorStylePrefs.Instance.LesserText },
                new PaperText(FontAwesome6.Cube) { Width = "50px", Color = EditorStylePrefs.Instance.LesserText },
                nameColumn,
                new PaperText(GetFormattedLastModifiedTime(project.ProjectDirectory.LastWriteTime))
                {
                    Width = "150px",
                    Color = EditorStylePrefs.Instance.LesserText,
                    Padding = "10px"
                },
                versionColumn,
                new PaperButton()
                {
                    Width = "50px",
                    Height = "100%",
                    BackgroundColor = Color.clear,
                    Text = FontAwesome6.EllipsisVertical,
                    TextColor = EditorStylePrefs.Instance.LesserText,
                    OnClick = () => ShowProjectContextMenu(project)
                }
            ]
        };
    }

    private void ShowProjectContextMenu(Project project)
    {
        var contextMenu = new PaperContextMenu()
        {
            Items = [
                new PaperContextMenuItem("Open", () => {
                    Project.Open(project);
                    isOpened = false;
                }),
                new PaperContextMenuItem("Show in Explorer", () => {
                    AssetDatabase.OpenPath(project.ProjectDirectory, type: FileOpenType.FileExplorer);
                }),
                new PaperContextMenuItem("Remove from list", () => {
                    ProjectCache.Instance.RemoveProject(project);
                })
            ]
        };
        contextMenu.Show();
    }

    private PaperElement CreateInstallsTab()
    {
        return new PaperContainer()
        {
            Width = "100%",
            Height = "100%",
            Padding = "20px",
            Children = [
                new PaperText("Installs - Coming Soon")
                {
                    FontSize = 24,
                    Color = Color.white
                }
            ]
        };
    }

    private PaperElement CreateLearnTab()
    {
        return new PaperContainer()
        {
            Width = "100%",
            Height = "100%",
            Padding = "20px",
            Children = [
                new PaperText("Learn - Coming Soon")
                {
                    FontSize = 24,
                    Color = Color.white
                }
            ]
        };
    }

    private PaperElement CreateCommunityTab()
    {
        return new PaperContainer()
        {
            Width = "100%",
            Height = "100%",
            Padding = "20px",
            Children = [
                new PaperText("Community - Coming Soon")
                {
                    FontSize = 24,
                    Color = Color.white
                }
            ]
        };
    }

    private PaperElement CreateSettingsTab()
    {
        return new PaperContainer()
        {
            Width = "100%",
            Height = "100%",
            Padding = "20px",
            Children = [
                new PaperText("Settings - Coming Soon")
                {
                    FontSize = 24,
                    Color = Color.white
                }
            ]
        };
    }

    private void UpdateProjectsTable()
    {
        // For now, just invalidate the canvas
        _canvas.Invalidate();
    }

    protected override void Draw()
    {
        if (Project.HasProject)
            isOpened = false;

        // Fill parent (Window in this case).
        using (gui.CurrentNode.Left(0).Top(0).ExpandWidth().ExpandHeight().Enter())
        {
            // Render the Paper canvas
            _canvas.Render(gui);

            // Handle create project sidebar overlay
            if (_createTabOpen)
            {
                DrawCreateProjectSidebar();
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
