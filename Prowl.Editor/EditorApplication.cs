// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Prowl.UI;
using Prowl.Editor.Services;
using Prowl.Editor.Docking;
using Prowl.Editor.Panels;
using Prowl.Editor.Rendering;
using Prowl.Editor.Core;
using Prowl.Editor.Toolbar;
using Prowl.Editor.Undo;

namespace Prowl.Editor;

/// <summary>
/// The main editor application — a Unity-like interface built with Dear ImGui.
/// Each panel lives in its own dockable ImGui window arranged around a central
/// dockspace, giving the user full control over layout.
/// </summary>
public sealed class EditorApplication : Game
{
    private readonly EditorMenuBar _menuBar = new();

    // Play mode
    private readonly EditorPlayMode _playMode = new();
    private PlayModeToolbar? _playToolbar;

    // Panel references
    private HierarchyPanel? _hierarchyPanel;
    private InspectorPanel? _inspectorPanel;
    private ScenePanel? _scenePanel;
    private ProjectPanel? _projectPanel;
    private GamePanel? _gamePanel;
    private PreferencesPanel? _preferencesPanel;

    /// <summary> The project folder path passed via --project, or null. </summary>
    public static string? ProjectPath { get; private set; }

    private bool _themeApplied;
    private bool _firstFrame = true;

    /// <summary>
    /// Resets the theme flag when DPI changes so that the editor theme
    /// (including <c>ScaleAllSizes</c>) is reapplied on the next frame.
    /// </summary>
    public override void OnDpiChanged(float oldScale, float newScale)
    {
        _themeApplied = false;
    }

    // Layout persistence
    private string _iniFilePath = "imgui.ini";
    private bool _layoutInitialised;

    public EditorApplication(string? projectPath = null)
    {
        ProjectPath = projectPath;
    }

    public override void Initialize()
    {
        // ── Layout persistence ──
        // Store the layout .ini in the project's settings folder (or next to the exe as fallback).
        if (!string.IsNullOrEmpty(ProjectPath))
        {
            string settingsDir = Path.Combine(ProjectPath, "ProjectSettings");
            Directory.CreateDirectory(settingsDir);
            _iniFilePath = Path.Combine(settingsDir, "EditorLayout.ini");
        }
        else
        {
            _iniFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EditorLayout.ini");
        }

        // Tell ImGui where to save/load the layout.
        // A null IniFilename disables auto-save; we will call SaveIniSettingsToDisk manually.
        var io = ImGui.GetIO();
        _layoutInitialised = File.Exists(_iniFilePath);
        unsafe
        {
            io.NativePtr->IniFilename = null; // disable automatic save; we manage it ourselves
        }

        // If a saved layout exists, load it now
        if (_layoutInitialised)
            ImGui.LoadIniSettingsFromDisk(_iniFilePath);

        // Register core services
        EditorServices.Register<ISceneService>(new DefaultSceneService());
        EditorServices.Register<ISelectionService>(new DefaultSelectionService());
        EditorServices.Register<IEditorInput>(new DefaultEditorInput());
        EditorServices.Register<IEditorRendering>(new StubEditorRendering());
        EditorServices.Register<IEditorTime>(new EditorTime());
        EditorServices.Register<ISceneSerializer>(new JsonSceneSerializer());
        EditorServices.Register<UndoRedoService>(new UndoRedoService());

        // Asset database
        var assetDb = new FileSystemAssetDatabase();
        if (!string.IsNullOrEmpty(ProjectPath) && Directory.Exists(ProjectPath))
        {
            assetDb.SetAssetRoot(Path.Combine(ProjectPath, "Assets"));
        }
        else if (!string.IsNullOrEmpty(ProjectPath))
        {
            Directory.CreateDirectory(ProjectPath);
            Directory.CreateDirectory(Path.Combine(ProjectPath, "Assets"));
            Directory.CreateDirectory(Path.Combine(ProjectPath, "ProjectSettings"));
            assetDb.SetAssetRoot(Path.Combine(ProjectPath, "Assets"));
        }
        else
        {
            assetDb.SetAssetRoot(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets"));
        }
        EditorServices.Register<IAssetService>(assetDb);

        // Try to load the project's default scene; otherwise create an empty one
        bool sceneLoaded = false;
        if (!string.IsNullOrEmpty(ProjectPath) && assetDb.HasProject)
        {
            string defaultScenePath = Path.Combine(ProjectPath, "Assets", "DefaultScene.scene");
            if (File.Exists(defaultScenePath) &&
                EditorServices.TryGet<ISceneSerializer>(out var serializer))
            {
                var loadedScene = serializer!.Load(defaultScenePath);
                if (loadedScene != null)
                {
                    EditorServices.Get<ISceneService>().SetScene(loadedScene);
                    sceneLoaded = true;
                }
            }
        }
        if (!sceneLoaded)
        {
            EditorServices.Get<ISceneService>().CreateNewScene("Untitled");
        }

        // Play mode toolbar
        _playToolbar = new PlayModeToolbar(_playMode);

        // Create panels
        _hierarchyPanel = new HierarchyPanel();
        _inspectorPanel = new InspectorPanel();
        _scenePanel = new ScenePanel();
        _projectPanel = new ProjectPanel();
        _gamePanel = new GamePanel();
        _preferencesPanel = new PreferencesPanel();

        // Menu bar panel toggles
        _menuBar.OnToggleHierarchy = () => _hierarchyPanel.IsOpen = !_hierarchyPanel.IsOpen;
        _menuBar.OnToggleInspector = () => _inspectorPanel.IsOpen = !_inspectorPanel.IsOpen;
        _menuBar.OnToggleSceneView = () => _scenePanel.IsOpen = !_scenePanel.IsOpen;
        _menuBar.OnToggleProjectBrowser = () => _projectPanel.IsOpen = !_projectPanel.IsOpen;
        _menuBar.OnToggleGameView = () => _gamePanel.IsOpen = !_gamePanel.IsOpen;
        _menuBar.OnTogglePreferences = () => _preferencesPanel.IsOpen = !_preferencesPanel.IsOpen;

        Debug.LogSuccess("Editor initialized.");
    }

    public override void BeginUpdate()
    {
        _playMode.Update(Time.UnscaledDeltaTime);
        _preferencesPanel?.Tick(Time.UnscaledDeltaTime);
        HandleKeyboardShortcuts();
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        var sceneSvc = EditorServices.Get<ISceneService>();
        string sceneName = sceneSvc.CurrentScene?.Name ?? "Untitled";
        string dirty = sceneSvc.IsDirty ? " *" : "";
        string title = $"Prowl Editor — {sceneName}{dirty}";
        Window.InternalWindow.Title = title;
    }

    private static void HandleKeyboardShortcuts()
    {
        bool ctrl = Input.GetKey(KeyCode.ControlLeft) || Input.GetKey(KeyCode.ControlRight);

        if (ctrl && Input.GetKeyDown(KeyCode.S))
        {
            EditorMenuBar.OnSaveScene();
        }
        else if (ctrl && Input.GetKeyDown(KeyCode.Z))
        {
            if (EditorServices.TryGet<UndoRedoService>(out var undo) && undo!.CanUndo)
                undo.Undo();
        }
        else if (ctrl && Input.GetKeyDown(KeyCode.Y))
        {
            if (EditorServices.TryGet<UndoRedoService>(out var undo) && undo!.CanRedo)
                undo.Redo();
        }
    }

    public override void BeginRender()
    {
        var rendering = EditorServices.Get<IEditorRendering>();

        if (_scenePanel != null && _scenePanel.IsOpen)
        {
            Rect vp = _scenePanel.ViewportRect;
            int w = (int)vp.Size.X;
            int h = (int)vp.Size.Y;

            if (w > 0 && h > 0)
            {
                var cam = _scenePanel.Camera;
                rendering.RenderSceneView(
                    cam.GetPosition(), cam.GetRotation(),
                    cam.FieldOfView, cam.NearClip, cam.FarClip,
                    w, h);
            }
        }

        if (_gamePanel != null && _gamePanel.IsOpen && _playMode.State != PlayModeState.Stopped)
        {
            Rect gvp = _gamePanel.ViewportRect;
            int gw = (int)gvp.Size.X;
            int gh = (int)gvp.Size.Y;

            if (gw > 0 && gh > 0)
                rendering.RenderGameView(gw, gh);
        }
    }

    public override void BeginImGui(IUIRenderer ui)
    {
        if (!_themeApplied) { ApplyEditorTheme(); _themeApplied = true; }

        // ── Full-screen host window for dockspace ──
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        ImGui.Begin("##EditorDockHost",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.MenuBar);
        ImGui.PopStyleVar(3);

        // ── Dockspace ──
        uint dockspaceId = ImGui.GetID("EditorDockSpace");
        ImGui.DockSpace(dockspaceId, Vector2.Zero, ImGuiDockNodeFlags.None);

        // On the first frame, if no saved layout was loaded, build the default layout
        if (_firstFrame)
        {
            _firstFrame = false;
            if (!_layoutInitialised)
                BuildDefaultLayout(dockspaceId, viewport.WorkSize);
        }

        // ── Main menu bar (inside the host window) ──
        _menuBar.Draw();

        ImGui.End();

        // ── Toolbar (small standalone window) ──
        _playToolbar?.Draw();

        // ── All dockable panel windows ──
        _hierarchyPanel?.Draw();
        _inspectorPanel?.Draw();
        _scenePanel?.Draw();
        _projectPanel?.Draw();
        _gamePanel?.Draw();
        _preferencesPanel?.Draw();

        // Persist layout whenever ImGui marks it dirty
        if (ImGui.GetIO().WantSaveIniSettings)
        {
            ImGui.SaveIniSettingsToDisk(_iniFilePath);
            ImGui.GetIO().WantSaveIniSettings = false;
        }
    }

    /// <summary>
    /// Builds a Unity-like default dock layout using the cimgui DockBuilder API:
    /// <code>
    /// ┌────────────┬───────────────────┬───────────┐
    /// │            │                   │           │
    /// │  Hierarchy │   Scene / Game    │ Inspector │
    /// │    18%     │      57%          │    25%    │
    /// │            │                   │           │
    /// │            ├───────────────────┤           │
    /// │            │    Project        │           │
    /// │            │     28%           │           │
    /// └────────────┴───────────────────┴───────────┘
    /// </code>
    /// </summary>
    private void BuildDefaultLayout(uint dockspaceId, Vector2 size)
    {
        // Clear any existing layout in this dockspace
        ImGuiDockBuilder.RemoveNode(dockspaceId);
        // ImGuiDockNodeFlags_DockSpace = 1 << 10 (internal flag, not in public enum)
        ImGuiDockBuilder.AddNode(dockspaceId, 1 << 10);
        ImGuiDockBuilder.SetNodeSize(dockspaceId, size);

        // 1) Split left panel (Hierarchy) from the rest
        ImGuiDockBuilder.SplitNode(dockspaceId, (int)ImGuiDir.Left, 0.18f,
            out uint leftId, out uint remainingId);

        // 2) Split right panel (Inspector) from center
        ImGuiDockBuilder.SplitNode(remainingId, (int)ImGuiDir.Right, 0.30f,
            out uint rightId, out uint centerId);

        // 3) Split center into top (Scene/Game) and bottom (Project)
        ImGuiDockBuilder.SplitNode(centerId, (int)ImGuiDir.Down, 0.28f,
            out uint bottomId, out uint topId);

        // Dock the panel windows into the nodes
        ImGuiDockBuilder.DockWindow("Hierarchy", leftId);
        ImGuiDockBuilder.DockWindow("Scene", topId);
        ImGuiDockBuilder.DockWindow("Game", topId);       // tabbed with Scene
        ImGuiDockBuilder.DockWindow("Inspector", rightId);
        ImGuiDockBuilder.DockWindow("Project", bottomId);

        ImGuiDockBuilder.Finish(dockspaceId);
    }

    // ── Theme ────────────────────────────────────────────────────────

    private static void ApplyEditorTheme()
    {
        ImGui.StyleColorsDark();
        var s = ImGui.GetStyle();
        s.WindowRounding    = 0;
        s.ChildRounding     = 0;
        s.FrameRounding     = 2;
        s.GrabRounding      = 2;
        s.TabRounding       = 4;
        s.ScrollbarRounding = 2;
        s.WindowBorderSize  = 1;
        s.FrameBorderSize   = 0;
        s.WindowPadding     = new Vector2(8, 8);
        s.FramePadding      = new Vector2(4, 3);
        s.ItemSpacing       = new Vector2(8, 4);
        s.ItemInnerSpacing  = new Vector2(4, 4);
        s.IndentSpacing     = 20;

        var c = s.Colors;
        c[(int)ImGuiCol.Text]                 = new Vector4(0.86f, 0.86f, 0.86f, 1.00f);
        c[(int)ImGuiCol.TextDisabled]         = new Vector4(0.50f, 0.50f, 0.50f, 1.00f);
        c[(int)ImGuiCol.WindowBg]             = new Vector4(0.13f, 0.13f, 0.13f, 1.00f);
        c[(int)ImGuiCol.ChildBg]              = new Vector4(0.13f, 0.13f, 0.13f, 0.00f);
        c[(int)ImGuiCol.PopupBg]              = new Vector4(0.14f, 0.14f, 0.14f, 0.96f);
        c[(int)ImGuiCol.Border]               = new Vector4(0.25f, 0.25f, 0.25f, 0.50f);
        c[(int)ImGuiCol.FrameBg]              = new Vector4(0.20f, 0.20f, 0.20f, 1.00f);
        c[(int)ImGuiCol.FrameBgHovered]       = new Vector4(0.26f, 0.26f, 0.26f, 1.00f);
        c[(int)ImGuiCol.FrameBgActive]        = new Vector4(0.30f, 0.30f, 0.30f, 1.00f);
        c[(int)ImGuiCol.TitleBg]              = new Vector4(0.10f, 0.10f, 0.10f, 1.00f);
        c[(int)ImGuiCol.TitleBgActive]        = new Vector4(0.15f, 0.15f, 0.15f, 1.00f);
        c[(int)ImGuiCol.TitleBgCollapsed]     = new Vector4(0.10f, 0.10f, 0.10f, 0.75f);
        c[(int)ImGuiCol.MenuBarBg]            = new Vector4(0.15f, 0.15f, 0.15f, 1.00f);
        c[(int)ImGuiCol.ScrollbarBg]          = new Vector4(0.12f, 0.12f, 0.12f, 0.53f);
        c[(int)ImGuiCol.ScrollbarGrab]        = new Vector4(0.31f, 0.31f, 0.31f, 1.00f);
        c[(int)ImGuiCol.ScrollbarGrabHovered] = new Vector4(0.41f, 0.41f, 0.41f, 1.00f);
        c[(int)ImGuiCol.ScrollbarGrabActive]  = new Vector4(0.51f, 0.51f, 0.51f, 1.00f);
        c[(int)ImGuiCol.CheckMark]            = new Vector4(0.28f, 0.56f, 1.00f, 1.00f);
        c[(int)ImGuiCol.SliderGrab]           = new Vector4(0.28f, 0.56f, 1.00f, 0.78f);
        c[(int)ImGuiCol.SliderGrabActive]     = new Vector4(0.28f, 0.56f, 1.00f, 1.00f);
        c[(int)ImGuiCol.Button]               = new Vector4(0.22f, 0.22f, 0.22f, 1.00f);
        c[(int)ImGuiCol.ButtonHovered]        = new Vector4(0.28f, 0.56f, 1.00f, 0.60f);
        c[(int)ImGuiCol.ButtonActive]         = new Vector4(0.28f, 0.56f, 1.00f, 1.00f);
        c[(int)ImGuiCol.Header]               = new Vector4(0.22f, 0.22f, 0.22f, 1.00f);
        c[(int)ImGuiCol.HeaderHovered]        = new Vector4(0.28f, 0.56f, 1.00f, 0.40f);
        c[(int)ImGuiCol.HeaderActive]         = new Vector4(0.28f, 0.56f, 1.00f, 0.60f);
        c[(int)ImGuiCol.Separator]            = new Vector4(0.25f, 0.25f, 0.25f, 0.50f);
        c[(int)ImGuiCol.SeparatorHovered]     = new Vector4(0.28f, 0.56f, 1.00f, 0.78f);
        c[(int)ImGuiCol.SeparatorActive]      = new Vector4(0.28f, 0.56f, 1.00f, 1.00f);
        c[(int)ImGuiCol.ResizeGrip]           = new Vector4(0.28f, 0.56f, 1.00f, 0.25f);
        c[(int)ImGuiCol.ResizeGripHovered]    = new Vector4(0.28f, 0.56f, 1.00f, 0.67f);
        c[(int)ImGuiCol.ResizeGripActive]     = new Vector4(0.28f, 0.56f, 1.00f, 0.95f);
        c[(int)ImGuiCol.Tab]                  = new Vector4(0.15f, 0.15f, 0.15f, 1.00f);
        c[(int)ImGuiCol.TabHovered]           = new Vector4(0.28f, 0.56f, 1.00f, 0.40f);
        c[(int)ImGuiCol.TabActive]            = new Vector4(0.20f, 0.40f, 0.75f, 1.00f);
        c[(int)ImGuiCol.TabUnfocused]         = new Vector4(0.15f, 0.15f, 0.15f, 1.00f);
        c[(int)ImGuiCol.TabUnfocusedActive]   = new Vector4(0.18f, 0.18f, 0.18f, 1.00f);
        c[(int)ImGuiCol.DockingPreview]       = new Vector4(0.28f, 0.56f, 1.00f, 0.70f);
        c[(int)ImGuiCol.DockingEmptyBg]       = new Vector4(0.13f, 0.13f, 0.13f, 1.00f);

        // Scale all style dimensions by the monitor's DPI factor
        s.ScaleAllSizes(Game.DpiScale);
    }

    public override void Closing()
    {
        // Save the dock layout one final time before shutdown
        ImGui.SaveIniSettingsToDisk(_iniFilePath);

        // Dispose icon textures
        EditorIcons.Dispose();

        if (EditorServices.TryGet<IEditorRendering>(out var rendering))
            rendering!.Dispose();

        EditorServices.Clear();
        Debug.Log("Editor shutting down.");
    }
}
