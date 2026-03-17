// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using ImGuiNET;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Editor.Services;
using Prowl.Editor.Undo;

namespace Prowl.Editor;

/// <summary>
/// Draws the main menu bar (File, Edit, View, Help) using ImGui's native
/// menu bar API. Each entry triggers editor actions such as scene
/// management, undo/redo, and panel toggling.
/// </summary>
public sealed class EditorMenuBar
{
    // Public action hooks wired up by EditorApplication during init
    public Action? OnToggleHierarchy { get; set; }
    public Action? OnToggleInspector { get; set; }
    public Action? OnToggleSceneView { get; set; }
    public Action? OnToggleProjectBrowser { get; set; }
    public Action? OnToggleGameView { get; set; }
    public Action? OnToggleConsole { get; set; }
    public Action? OnTogglePreferences { get; set; }

    // Cached scene file list for the "Load Scene" popup
    private string[] _sceneFiles = [];
    private bool _loadPopupRequested;

    public void Draw()
    {
        if (!ImGui.BeginMenuBar()) return;

        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("New Scene"))            OnNewScene();
            if (ImGui.MenuItem("Save Scene", "Ctrl+S")) OnSaveScene();
            if (ImGui.MenuItem("Load Scene"))           OnRequestLoadScene();
            ImGui.Separator();
            if (ImGui.MenuItem("Exit"))                 OnExit();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Edit"))
        {
            string undoLabel = "Undo";
            string redoLabel = "Redo";

            if (EditorServices.TryGet<UndoRedoService>(out var undoSvc))
            {
                if (undoSvc!.CanUndo) undoLabel = $"Undo  {undoSvc.UndoDescription}";
                if (undoSvc.CanRedo)  redoLabel = $"Redo  {undoSvc.RedoDescription}";
            }

            if (ImGui.MenuItem(undoLabel, "Ctrl+Z")) OnUndo();
            if (ImGui.MenuItem(redoLabel, "Ctrl+Y")) OnRedo();
            ImGui.Separator();
            if (ImGui.MenuItem("Preferences..."))    OnTogglePreferences?.Invoke();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("Hierarchy"))       OnToggleHierarchy?.Invoke();
            if (ImGui.MenuItem("Inspector"))       OnToggleInspector?.Invoke();
            if (ImGui.MenuItem("Scene View"))      OnToggleSceneView?.Invoke();
            if (ImGui.MenuItem("Project Browser")) OnToggleProjectBrowser?.Invoke();
            if (ImGui.MenuItem("Game View"))       OnToggleGameView?.Invoke();
            if (ImGui.MenuItem("Console"))         OnToggleConsole?.Invoke();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Help"))
        {
            if (ImGui.MenuItem("About Prowl")) OnAbout();
            ImGui.EndMenu();
        }

        ImGui.EndMenuBar();

        // ── Load Scene popup ──────────────────────────────────
        DrawLoadScenePopup();
    }

    // ── Menu Actions ─────────────────────────────────────────────────

    private static void OnNewScene()
    {
        var sceneSvc = EditorServices.Get<ISceneService>();
        if (sceneSvc.IsDirty)
            Debug.LogWarning("[Menu] Unsaved changes were discarded.");
        sceneSvc.CreateNewScene();
        Debug.Log("[Menu] New Scene created.");
    }

    internal static void OnSaveScene()
    {
        if (!EditorServices.TryGet<ISceneSerializer>(out var serializer)) return;
        var sceneSvc = EditorServices.Get<ISceneService>();
        var scene = sceneSvc.CurrentScene;
        if (scene == null) return;

        // Determine the save path: reuse the current file path, or default to Assets/
        string? path = sceneSvc.SceneFilePath;
        if (string.IsNullOrEmpty(path))
        {
            var assets = EditorServices.Get<IAssetService>();
            string dir = assets.HasProject ? assets.AssetRootPath : ".";
            string name = (scene.Name ?? "Untitled").Replace(" ", "_");
            path = Path.Combine(dir, $"{name}{serializer!.FileExtension}");
        }

        serializer!.Save(scene, path);
        sceneSvc.SceneFilePath = path;
        sceneSvc.ClearDirty();
        Debug.Log($"[Menu] Scene saved: {path}");
    }

    private void OnRequestLoadScene()
    {
        var assets = EditorServices.Get<IAssetService>();
        string dir = assets.HasProject ? assets.AssetRootPath : ".";

        string ext = ".scene";
        if (EditorServices.TryGet<ISceneSerializer>(out var serializer))
            ext = serializer!.FileExtension;

        _sceneFiles = Directory.Exists(dir)
            ? Directory.GetFiles(dir, $"*{ext}", SearchOption.AllDirectories)
            : [];

        if (_sceneFiles.Length == 0)
        {
            Debug.LogWarning("[Menu] No scene files found to load.");
            return;
        }

        if (_sceneFiles.Length == 1)
        {
            LoadSceneFromFile(_sceneFiles[0]);
        }
        else
        {
            _loadPopupRequested = true;
        }
    }

    private void DrawLoadScenePopup()
    {
        if (_loadPopupRequested)
        {
            ImGui.OpenPopup("##LoadScenePicker");
            _loadPopupRequested = false;
        }

        if (ImGui.BeginPopup("##LoadScenePicker"))
        {
            ImGui.Text("Select a scene to load:");
            ImGui.Separator();

            foreach (var filePath in _sceneFiles)
            {
                string displayName = Path.GetFileNameWithoutExtension(filePath);
                if (ImGui.MenuItem(displayName))
                {
                    LoadSceneFromFile(filePath);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(filePath);
            }
            ImGui.EndPopup();
        }
    }

    internal static void LoadSceneFromFile(string filePath)
    {
        if (!EditorServices.TryGet<ISceneSerializer>(out var serializer)) {
            Debug.LogError($"Failed to load Scene at path: {filePath}");
            return;
        }

        var scene = serializer!.Load(filePath);
        if (scene != null)
        {
            var sceneSvc = EditorServices.Get<ISceneService>();
            sceneSvc.SetScene(scene);
            sceneSvc.SceneFilePath = filePath;
            sceneSvc.ClearDirty();
            Debug.Log($"[Menu] Loaded scene: {filePath}");
        }
    }

    private static void OnExit() => Game.Quit();

    private static void OnUndo()
    {
        if (EditorServices.TryGet<UndoRedoService>(out var undo) && undo!.CanUndo)
            undo.Undo();
    }

    private static void OnRedo()
    {
        if (EditorServices.TryGet<UndoRedoService>(out var undo) && undo!.CanRedo)
            undo.Redo();
    }

    private static void OnAbout()
    {
        Debug.Log("[Menu] Prowl Editor v0.1 — Built on Prowl Engine (Standalone).");
    }
}
