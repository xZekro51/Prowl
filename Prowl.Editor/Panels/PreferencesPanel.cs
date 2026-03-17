// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.Editor.Docking;
using Prowl.Editor.Services;

namespace Prowl.Editor.Panels;

/// <summary>
/// Editor preferences panel — persists settings to a JSON file in ProjectSettings/.
/// </summary>
public sealed class PreferencesPanel : EditorPanel
{
    private const string FileName = "EditorPreferences.json";

    // ── Settings ────────────────────────────────────
    private float _autoSaveIntervalMinutes = 5f;
    private bool _autoSaveEnabled;
    private int _selectedTheme; // 0 = Dark, 1 = Light
    private string _externalScriptEditor = string.Empty;
    private int _undoHistorySize = 256;
    private float _uiScale = 1.0f;

    // Internal
    private float _autoSaveTimer;
    private bool _loaded;
    private string? _filePath;

    private static readonly string[] ThemeNames = ["Dark", "Light"];

    public PreferencesPanel() : base("Preferences")
    {
        IsOpen = false;
    }

    protected override void DrawContent()
    {
        EnsureLoaded();

        ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "Editor Preferences");
        ImGui.Separator();
        ImGui.Spacing();

        // ── Theme ──────────────────────────────────────
        ImGui.Text("Color Theme");
        if (ImGui.Combo("##Theme", ref _selectedTheme, ThemeNames, ThemeNames.Length))
            Save();
        ImGui.Spacing();

        // ── UI Scale ───────────────────────────────────
        ImGui.Text("UI Scale");
        ImGui.SetNextItemWidth(200 * Game.DpiScale);
        if (ImGui.SliderFloat("##UIScale", ref _uiScale, 0.5f, 3.0f, "%.2f"))
        {
            DpiManager.UserScale = _uiScale;
            Save();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset##UIScale"))
        {
            _uiScale = 1.0f;
            DpiManager.UserScale = 1.0f;
            Save();
        }
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
            $"Effective scale: {Game.DpiScale:F2}x  (Monitor: {DpiManager.MonitorScale:F2}x \u00d7 User: {_uiScale:F2}x)");
        ImGui.Spacing();

        // ── Auto-save ──────────────────────────────────
        ImGui.Text("Auto-Save");
        if (ImGui.Checkbox("Enable auto-save", ref _autoSaveEnabled))
            Save();
        if (_autoSaveEnabled)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100 * Game.DpiScale);
            if (ImGui.DragFloat("Interval (min)", ref _autoSaveIntervalMinutes, 0.5f, 1f, 60f, "%.1f"))
                Save();
        }
        ImGui.Spacing();

        // ── Undo History ───────────────────────────────
        ImGui.Text("Undo History Size");
        ImGui.SetNextItemWidth(120 * Game.DpiScale);
        if (ImGui.DragInt("##UndoSize", ref _undoHistorySize, 1, 16, 1024))
        {
            if (EditorServices.TryGet<Undo.UndoRedoService>(out var undoSvc))
                undoSvc!.MaxHistorySize = _undoHistorySize;
            Save();
        }
        ImGui.Spacing();

        // ── External Editor ────────────────────────────
        ImGui.Text("External Script Editor");
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.InputText("##ExtEditor", ref _externalScriptEditor, 512))
            Save();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Reset to Defaults"))
        {
            ResetDefaults();
            Save();
        }
    }

    /// <summary>
    /// Called each frame by the editor to handle auto-save timing.
    /// </summary>
    public void Tick(float deltaTime)
    {
        if (!_autoSaveEnabled) return;

        _autoSaveTimer += deltaTime;
        if (_autoSaveTimer >= _autoSaveIntervalMinutes * 60f)
        {
            _autoSaveTimer = 0f;
            if (EditorServices.TryGet<ISceneService>(out var sceneSvc) && sceneSvc!.IsDirty)
            {
                EditorMenuBar.OnSaveScene();
                Debug.Log("[Preferences] Auto-saved scene.");
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Load();
    }

    private string GetFilePath()
    {
        if (_filePath != null) return _filePath;

        string dir;
        if (!string.IsNullOrEmpty(EditorApplication.ProjectPath))
            dir = Path.Combine(EditorApplication.ProjectPath, "ProjectSettings");
        else
            dir = AppDomain.CurrentDomain.BaseDirectory;

        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, FileName);
        return _filePath;
    }

    private void Save()
    {
        try
        {
            var obj = new JsonObject
            {
                ["autoSaveEnabled"] = _autoSaveEnabled,
                ["autoSaveIntervalMinutes"] = _autoSaveIntervalMinutes,
                ["theme"] = _selectedTheme,
                ["externalScriptEditor"] = _externalScriptEditor,
                ["undoHistorySize"] = _undoHistorySize,
                ["uiScale"] = _uiScale,
            };
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(GetFilePath(), obj.ToJsonString(options));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Preferences] Failed to save: {ex.Message}");
        }
    }

    private void Load()
    {
        try
        {
            string path = GetFilePath();
            if (!File.Exists(path)) return;

            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root == null) return;

            _autoSaveEnabled = root["autoSaveEnabled"]?.GetValue<bool>() ?? false;
            _autoSaveIntervalMinutes = root["autoSaveIntervalMinutes"]?.GetValue<float>() ?? 5f;
            _selectedTheme = root["theme"]?.GetValue<int>() ?? 0;
            _externalScriptEditor = root["externalScriptEditor"]?.GetValue<string>() ?? string.Empty;
            _undoHistorySize = root["undoHistorySize"]?.GetValue<int>() ?? 256;
            _uiScale = root["uiScale"]?.GetValue<float>() ?? 1.0f;

            DpiManager.UserScale = _uiScale;

            if (EditorServices.TryGet<Undo.UndoRedoService>(out var undoSvc))
                undoSvc!.MaxHistorySize = _undoHistorySize;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Preferences] Failed to load: {ex.Message}");
        }
    }

    private void ResetDefaults()
    {
        _autoSaveEnabled = false;
        _autoSaveIntervalMinutes = 5f;
        _selectedTheme = 0;
        _externalScriptEditor = string.Empty;
        _undoHistorySize = 256;
        _uiScale = 1.0f;
        DpiManager.UserScale = 1.0f;
    }
}
