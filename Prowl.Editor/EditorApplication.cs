// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using System.Timers;

using ImGuiNET;

using Prowl.Editor.Core;
using Prowl.Editor.Docking;
using Prowl.Editor.Icons;
using Prowl.Editor.Panels;
using Prowl.Editor.Project;
using Prowl.Editor.Rendering;
using Prowl.Editor.Services;
using Prowl.Editor.Toolbar;
using Prowl.Editor.Undo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.UI;
using Prowl.Vector;

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

    // Script compilation
    private ProjectAssemblyManager? _assemblyManager;

    // Panel references
    private HierarchyPanel? _hierarchyPanel;
    private InspectorPanel? _inspectorPanel;
    private ScenePanel? _scenePanel;
    private ProjectPanel? _projectPanel;
    private GamePanel? _gamePanel;
    private PreferencesPanel? _preferencesPanel;
    private ConsolePanel? _consolePanel;

    // Maximize state for scene panel
    private bool _sceneMaximized;
    private bool[] _savedOpenStates = new bool[6]; // hierarchy, inspector, project, game, prefs, console

    /// <summary> The project folder path passed via --project, or null. </summary>
    public static string? ProjectPath { get; private set; }

    /// <summary>
    /// The project script assembly manager. Use this to trigger recompilation
    /// or subscribe to assembly-change events. Null when no project is open.
    /// </summary>
    public static ProjectAssemblyManager? ScriptAssemblyManager { get; private set; }

    private bool _themeApplied;
    private float _lastAppliedUserScale = 1.0f;
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

    // Per-project session state
    private ProjectSessionState? _sessionState;

    public EditorApplication(string? projectPath = null)
    {
        ProjectPath = projectPath;
    }

    public override void WindowUpdate(float delta)
    {
        try
        {
            UpdatePaperInput();

            Prowl.Runtime.Audio.AudioContext.Update();

            time.Update();
            Time.TimeStack.Clear();
            Time.TimeStack.Push(time);

            Input.UpdateActions(delta);

            BeginUpdate();

            Scene? currentScene = Scene.Current;

            // Fixed update loop
            fixedTimeAccumulator += delta;
            int count = 0;
            while (fixedTimeAccumulator >= Time.FixedDeltaTime && count++ < 10)
            {
                currentScene?.FixedUpdate();
                fixedTimeAccumulator -= Time.FixedDeltaTime;
            }

            if (_playMode.State == PlayModeState.Playing)
            {
                currentScene?.Update();
            }
            else
            {
                // Update only the cameras
                currentScene?.UpdateCameras();
            }

            if (DrawGizmos)
            {
                currentScene?.DrawGizmos();
            }

            EndUpdate();

            if (frameCounter++ % 60 == 0)
            {
                Console.Title = $"{WindowTitle} - {Window.InternalWindow.FramebufferSize.X}x{Window.InternalWindow.FramebufferSize.Y} - FPS: {1.0 / Time.DeltaTime}";
            }

        }
        catch (Exception e)
        {
            Debug.LogError("An exception occurred during the Update loop:");
            Debug.LogError(e.ToString());
            throw;
        }
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

        Prowl.Echo.Serializer.OnResolveCustomType += Serializer_OnResolveCustomType;

        // Initialize the editor console logger (hooks into Debug.OnLog)
        EditorConsoleLogger.Initialize();

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

        // ── Script compilation ─────────────────────────────────
        // Compile user scripts BEFORE loading scenes so that custom
        // MonoBehaviour types are available during deserialization.
        if (!string.IsNullOrEmpty(ProjectPath))
        {
            _assemblyManager = new ProjectAssemblyManager(ProjectPath);
            ProjectAssembly.Register(_assemblyManager);

            ScriptAssemblyManager = _assemblyManager;
            _assemblyManager.OnAssemblyChanged += OnScriptAssemblyChanged;
            _assemblyManager.CompileAndLoad();
            _assemblyManager.StartWatching();

            // Generate IDE solution (.sln + .csproj) so external editors
            // get IntelliSense for user scripts — similar to Unity's workflow.
            ProjectSolutionGenerator.GenerateSolution(ProjectPath);
        }

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

        // Disable physics during edit mode
        _playMode.InitEditMode();

        // Create panels
        _hierarchyPanel = new HierarchyPanel();
        _inspectorPanel = new InspectorPanel();
        _scenePanel = new ScenePanel();
        _projectPanel = new ProjectPanel();
        _gamePanel = new GamePanel();
        _preferencesPanel = new PreferencesPanel();
        _consolePanel = new ConsolePanel();

        // Register ProjectPanel so other panels can find it for cross-panel features
        EditorServices.Register<ProjectPanel>(_projectPanel);

        // Menu bar panel toggles
        _menuBar.OnToggleHierarchy = () => _hierarchyPanel.IsOpen = !_hierarchyPanel.IsOpen;
        _menuBar.OnToggleInspector = () => _inspectorPanel.IsOpen = !_inspectorPanel.IsOpen;
        _menuBar.OnToggleSceneView = () => _scenePanel.IsOpen = !_scenePanel.IsOpen;
        _menuBar.OnToggleProjectBrowser = () => _projectPanel.IsOpen = !_projectPanel.IsOpen;
        _menuBar.OnToggleGameView = () => _gamePanel.IsOpen = !_gamePanel.IsOpen;
        _menuBar.OnTogglePreferences = () => _preferencesPanel.IsOpen = !_preferencesPanel.IsOpen;
        _menuBar.OnToggleConsole = () => _consolePanel.IsOpen = !_consolePanel.IsOpen;

        // Initialise the icon system (registers all built-in icons)
        IconManager.Load();

        // ── Per-project session state ──────────────────────────
        if (!string.IsNullOrEmpty(ProjectPath))
        {
            _sessionState = ProjectSessionState.Load(ProjectPath);

            // Restore last scene
            if (!sceneLoaded && _sessionState.LastScenePath != null)
            {
                string absScene = Path.Combine(ProjectPath, _sessionState.LastScenePath);
                if (File.Exists(absScene) &&
                    EditorServices.TryGet<ISceneSerializer>(out var ser))
                {
                    var s = ser!.Load(absScene);
                    if (s != null)
                    {
                        EditorServices.Get<ISceneService>().SetScene(s);
                        EditorServices.Get<ISceneService>().SceneFilePath = absScene;
                    }
                }
            }

            // Restore game view resolution
            if (_gamePanel != null)
                _gamePanel.SelectedResolutionIndex = _sessionState.GameViewResolutionIndex;

            // Restore panel open states
            RestorePanelStates(_sessionState);
        }

        Debug.LogSuccess("Editor initialized.");
    }

    private void Serializer_OnResolveCustomType(string typeName, ref Type type)
    {
        if (type == null) type = ProjectAssembly.GetType(typeName);
    }

    public override void BeginUpdate()
    {
        _playMode.Update(Time.UnscaledDeltaTime);
        _preferencesPanel?.Tick(Time.UnscaledDeltaTime);
        _assemblyManager?.ProcessPendingRecompile();
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

    /// <summary>
    /// Called when the user-script assembly is (re)compiled and loaded.
    /// Walks every GameObject in the current scene and attempts to resolve
    /// <see cref="MissingMonobehaviour"/> placeholders whose types may now
    /// be available in the freshly loaded assembly.
    /// </summary>
    private void OnScriptAssemblyChanged()
    {
        if (!EditorServices.TryGet<ISceneService>(out var sceneSvc))
            return;

        var scene = sceneSvc!.CurrentScene;
        if (scene == null)
            return;

        int totalResolved = 0;
        foreach (var go in scene.AllObjects)
            totalResolved += go.TryResolveMissingComponents();

        if (totalResolved > 0)
            Debug.LogSuccess($"[Scripts] Recovered {totalResolved} previously-missing component(s).");
    }

    private static void HandleKeyboardShortcuts()
    {
        bool ctrl = Input.GetKey(KeyCode.ControlLeft) || Input.GetKey(KeyCode.ControlRight);

        if (ctrl && Input.GetKeyDown(KeyCode.S))
        {
            EditorMenuBar.OnSaveScene();
        }
        else if (ctrl && Input.GetKeyDown(KeyCode.B))
        {
            EditorApplication.ScriptAssemblyManager?.CompileAndLoad();
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
                    w, h, _scenePanel.ViewMode);

                // Selection outline (rendered onto the scene RT as a post-process)
                var selService = EditorServices.Get<ISelectionService>();
                if (selService.ActiveObject is GameObject selectedGo)
                {
                    rendering.RenderSelectionOutline([selectedGo]);
                }
            }
        }

        // Render game view always — in edit mode this provides a live preview
        // from the scene's highest-priority camera (sorted by Camera.Depth).
        if (_gamePanel != null && _gamePanel.IsOpen)
        {
            Rect gvp = _gamePanel.ViewportRect;
            var (rw, rh) = _gamePanel.RenderResolution;
            int gw = rw > 0 ? rw : (int)gvp.Size.X;
            int gh = rh > 0 ? rh : (int)gvp.Size.Y;

            if (gw > 0 && gh > 0)
                rendering.RenderGameView(gw, gh);
        }
    }

    public override void BeginImGui(IUIRenderer ui)
    {
        if (!_themeApplied || MathF.Abs(DpiManager.UserScale - _lastAppliedUserScale) > 0.001f)
        {
            ApplyEditorTheme();
            _themeApplied = true;
            _lastAppliedUserScale = DpiManager.UserScale;
        }

        // Auto-clear drag-drop state when the mouse button is released
        EditorDragDrop.Update();

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
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.MenuBar |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar(3);

        // ── Main menu bar (inside the host window) ──
        _menuBar.Draw();

        // ── Fixed toolbar (above dockspace, not dockable) ──
        _playToolbar?.Draw();

        // ── Dockspace (leave room at the bottom for the status bar) ──
        float statusBarHeight = 24 * Game.DpiScale;
        Vector2 avail = ImGui.GetContentRegionAvail();
        Vector2 dockSize = new(avail.X, avail.Y - statusBarHeight);

        // Remove item spacing so dockspace + status bar fit exactly in the available region
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

        uint dockspaceId = ImGui.GetID("EditorDockSpace");
        ImGui.DockSpace(dockspaceId, dockSize, ImGuiDockNodeFlags.None);

        // On the first frame, if no saved layout was loaded, build the default layout
        if (_firstFrame)
        {
            _firstFrame = false;
            if (!_layoutInitialised)
                BuildDefaultLayout(dockspaceId, viewport.WorkSize);
        }

        // ── Status bar (fixed at the bottom, outside the dockspace) ──
        DrawStatusBar(statusBarHeight);

        ImGui.PopStyleVar(); // ItemSpacing

        ImGui.End();

        // ── All dockable panel windows ──
        // Handle scene maximize: hide/show other panels
        if (_scenePanel != null)
        {
            bool wantMax = _scenePanel.IsMaximized;
            if (wantMax && !_sceneMaximized)
            {
                // Entering maximized mode — save panel states and hide others
                _sceneMaximized = true;
                _savedOpenStates[0] = _hierarchyPanel?.IsOpen ?? false;
                _savedOpenStates[1] = _inspectorPanel?.IsOpen ?? false;
                _savedOpenStates[2] = _projectPanel?.IsOpen ?? false;
                _savedOpenStates[3] = _gamePanel?.IsOpen ?? false;
                _savedOpenStates[4] = _preferencesPanel?.IsOpen ?? false;
                _savedOpenStates[5] = _consolePanel?.IsOpen ?? false;

                if (_hierarchyPanel != null) _hierarchyPanel.IsOpen = false;
                if (_inspectorPanel != null) _inspectorPanel.IsOpen = false;
                if (_projectPanel != null) _projectPanel.IsOpen = false;
                if (_gamePanel != null) _gamePanel.IsOpen = false;
                if (_preferencesPanel != null) _preferencesPanel.IsOpen = false;
                if (_consolePanel != null) _consolePanel.IsOpen = false;
            }
            else if (!wantMax && _sceneMaximized)
            {
                // Exiting maximized mode — restore saved panel states
                _sceneMaximized = false;
                if (_hierarchyPanel != null) _hierarchyPanel.IsOpen = _savedOpenStates[0];
                if (_inspectorPanel != null) _inspectorPanel.IsOpen = _savedOpenStates[1];
                if (_projectPanel != null) _projectPanel.IsOpen = _savedOpenStates[2];
                if (_gamePanel != null) _gamePanel.IsOpen = _savedOpenStates[3];
                if (_preferencesPanel != null) _preferencesPanel.IsOpen = _savedOpenStates[4];
                if (_consolePanel != null) _consolePanel.IsOpen = _savedOpenStates[5];
            }
        }

        _hierarchyPanel?.Draw();
        _inspectorPanel?.Draw();
        _scenePanel?.Draw();
        _projectPanel?.Draw();
        _gamePanel?.Draw();
        _preferencesPanel?.Draw();
        _consolePanel?.Draw();

        // Persist layout whenever ImGui marks it dirty
        if (ImGui.GetIO().WantSaveIniSettings)
        {
            ImGui.SaveIniSettingsToDisk(_iniFilePath);
            ImGui.GetIO().WantSaveIniSettings = false;
        }
    }

    /// <summary>
    /// Draws a thin status bar at the bottom of the host window showing
    /// the most recent log message. Clicking it opens the Console panel.
    /// </summary>
    private void DrawStatusBar(float height)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.11f, 0.11f, 0.11f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);

        ImGui.BeginChild("##StatusBar", new Vector2(0, height), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        float yPad = (height - ImGui.GetFontSize()) * 0.5f;
        ImGui.SetCursorPosY(yPad);
        ImGui.SetCursorPosX(6 * Game.DpiScale);

        var lastEntry = EditorConsoleLogger.GetLastEntry();
        if (lastEntry != null)
        {
            Vector4 color = lastEntry.Severity switch
            {
                LogSeverity.Success => new Vector4(0.40f, 0.85f, 0.40f, 1f),
                LogSeverity.Warning => new Vector4(0.95f, 0.80f, 0.25f, 1f),
                LogSeverity.Error or LogSeverity.Exception => new Vector4(0.95f, 0.30f, 0.30f, 1f),
                _ => new Vector4(0.65f, 0.65f, 0.65f, 1f),
            };

            string prefix = lastEntry.Severity switch
            {
                LogSeverity.Success => "\u2714 ",
                LogSeverity.Warning => "\u26A0 ",
                LogSeverity.Error => "\u2716 ",
                LogSeverity.Exception => "\u2716 ",
                _ => "\u25CF ",
            };

            // Truncate long messages
            string msg = lastEntry.Message.Replace('\n', ' ').Replace('\r', ' ');
            float maxWidth = ImGui.GetContentRegionAvail().X - 12 * Game.DpiScale;
            if (ImGui.CalcTextSize(prefix + msg).X > maxWidth && msg.Length > 120)
                msg = msg[..120] + "...";

            ImGui.TextColored(color, prefix + msg);
        }
        else
        {
            ImGui.TextColored(new Vector4(0.45f, 0.45f, 0.45f, 1f), "Ready.");
        }

        // Click anywhere on the status bar to open/focus the Console
        if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (_consolePanel != null)
                _consolePanel.IsOpen = true;
            ImGui.SetWindowFocus("Console");
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
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
        ImGuiDockBuilder.DockWindow("Console", bottomId); // tabbed with Project

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

        // Scale all style dimensions by the combined DPI × user scale factor
        s.ScaleAllSizes(Game.DpiScale);

        // Keep ImGui font rendering in sync with the combined scale
        var io = ImGui.GetIO();
        io.FontGlobalScale = Game.DpiScale / DpiManager.BaseFontScale;
    }

    private void SavePanelStates(ProjectSessionState state)
    {
        state.OpenPanels.Clear();
        if (_hierarchyPanel != null) state.OpenPanels["Hierarchy"] = _hierarchyPanel.IsOpen;
        if (_inspectorPanel != null) state.OpenPanels["Inspector"] = _inspectorPanel.IsOpen;
        if (_scenePanel != null) state.OpenPanels["Scene"] = _scenePanel.IsOpen;
        if (_projectPanel != null) state.OpenPanels["Project"] = _projectPanel.IsOpen;
        if (_gamePanel != null) state.OpenPanels["Game"] = _gamePanel.IsOpen;
        if (_preferencesPanel != null) state.OpenPanels["Preferences"] = _preferencesPanel.IsOpen;
        if (_consolePanel != null) state.OpenPanels["Console"] = _consolePanel.IsOpen;
    }

    private void RestorePanelStates(ProjectSessionState state)
    {
        if (state.OpenPanels.Count == 0) return;
        if (state.OpenPanels.TryGetValue("Hierarchy", out var h) && _hierarchyPanel != null) _hierarchyPanel.IsOpen = h;
        if (state.OpenPanels.TryGetValue("Inspector", out var i) && _inspectorPanel != null) _inspectorPanel.IsOpen = i;
        if (state.OpenPanels.TryGetValue("Scene", out var s) && _scenePanel != null) _scenePanel.IsOpen = s;
        if (state.OpenPanels.TryGetValue("Project", out var p) && _projectPanel != null) _projectPanel.IsOpen = p;
        if (state.OpenPanels.TryGetValue("Game", out var g) && _gamePanel != null) _gamePanel.IsOpen = g;
        if (state.OpenPanels.TryGetValue("Preferences", out var pr) && _preferencesPanel != null) _preferencesPanel.IsOpen = pr;
        if (state.OpenPanels.TryGetValue("Console", out var c) && _consolePanel != null) _consolePanel.IsOpen = c;
    }

    public override void Closing()
    {
        // Save the dock layout one final time before shutdown
        ImGui.SaveIniSettingsToDisk(_iniFilePath);

        // ── Save per-project session state ─────────────────────
        if (!string.IsNullOrEmpty(ProjectPath))
        {
            _sessionState ??= new ProjectSessionState();

            // Save last opened scene
            var sceneFilePath = EditorServices.Get<ISceneService>().SceneFilePath;
            if (sceneFilePath != null && ProjectPath != null)
            {
                try { _sessionState.LastScenePath = Path.GetRelativePath(ProjectPath, sceneFilePath); }
                catch { _sessionState.LastScenePath = sceneFilePath; }
            }

            // Save game view resolution
            if (_gamePanel != null)
                _sessionState.GameViewResolutionIndex = _gamePanel.SelectedResolutionIndex;

            // Save panel open states
            SavePanelStates(_sessionState);

            _sessionState.Save(ProjectPath);
        }

        // Dispose script compilation system
        _assemblyManager?.Dispose();
        ScriptAssemblyManager = null;

        // Dispose icon textures
        IconManager.Dispose();
        EditorIcons.Dispose();

        if (EditorServices.TryGet<IEditorRendering>(out var rendering))
            rendering!.Dispose();

        EditorConsoleLogger.Shutdown();
        EditorServices.Clear();
        Debug.Log("Editor shutting down.");
    }
}
