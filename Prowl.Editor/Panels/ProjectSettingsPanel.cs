// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;

using ImGuiNET;

using Prowl.Editor.Build;
using Prowl.Editor.Docking;
using Prowl.Runtime;

namespace Prowl.Editor.Panels;

/// <summary>
/// Project-wide settings panel with a vertical tab bar on the left and
/// content pages on the right.  Each tab page can contain multiple
/// sections.  Settings are persisted to <c>ProjectSettings/</c>.
/// </summary>
public sealed class ProjectSettingsPanel : EditorPanel
{
    private int _selectedTab;
    private BuildSettings? _buildSettings;
    private bool _loaded;

    // Build settings UI state
    private int _selectedPlatformIndex;
    private string _newDefineInput = string.Empty;

    private static readonly string[] TabNames =
    [
        "Player",
        "Rendering",
        "Scripting Defines",
    ];

    public ProjectSettingsPanel() : base("Project Settings")
    {
        IsOpen = false;
    }

    protected override void DrawContent()
    {
        EnsureLoaded();

        // ── Vertical tab layout: left column = tabs, right column = content ──
        float tabWidth = 140 * Game.DpiScale;
        Vector2 avail = ImGui.GetContentRegionAvail();

        // Left column — tab list
        ImGui.BeginChild("##SettingsTabs", new Vector2(tabWidth, avail.Y), ImGuiChildFlags.Border);
        {
            ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "Settings");
            ImGui.Separator();
            ImGui.Spacing();

            for (int i = 0; i < TabNames.Length; i++)
            {
                bool selected = _selectedTab == i;
                if (selected)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.28f, 0.56f, 1.00f, 0.40f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.28f, 0.56f, 1.00f, 0.60f));
                }

                if (ImGui.Button(TabNames[i], new Vector2(tabWidth - 16 * Game.DpiScale, 0)))
                    _selectedTab = i;

                if (selected)
                    ImGui.PopStyleColor(2);
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // Right column — page content
        ImGui.BeginChild("##SettingsContent", new Vector2(0, avail.Y), ImGuiChildFlags.Border);
        {
            switch (_selectedTab)
            {
                case 0: DrawPlayerPage(); break;
                case 1: DrawRenderingPage(); break;
                case 2: DrawScriptingDefinesPage(); break;
            }
        }
        ImGui.EndChild();
    }

    // ── Scripting Defines Page ─────────────────────────────────────

    private void DrawScriptingDefinesPage()
    {
        if (_buildSettings == null) return;

        ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "Scripting Define Symbols");
        ImGui.Separator();
        ImGui.Spacing();

        // ── Platform selector ─────────────────────────────────
        ImGui.Text("Platform");
        string[] platformNames = Enum.GetNames<BuildTarget>();
        ImGui.SetNextItemWidth(200 * Game.DpiScale);
        ImGui.Combo("##Platform", ref _selectedPlatformIndex, platformNames, platformNames.Length);
        ImGui.Spacing();

        BuildTarget selectedTarget = (BuildTarget)_selectedPlatformIndex;

        // ── Scripting Define Symbols ──────────────────────────
        ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), $"Defines ({selectedTarget})");
        ImGui.Separator();
        ImGui.Spacing();

        var profile = _buildSettings.GetProfile(selectedTarget);

        // List existing defines
        int removeIndex = -1;
        for (int i = 0; i < profile.ScriptingDefineSymbols.Count; i++)
        {
            ImGui.PushID(i);
            ImGui.BulletText(profile.ScriptingDefineSymbols[i]);
            ImGui.SameLine();
            if (ImGui.SmallButton("X"))
                removeIndex = i;
            ImGui.PopID();
        }

        if (removeIndex >= 0)
        {
            profile.ScriptingDefineSymbols.RemoveAt(removeIndex);
            SaveBuildSettings();
        }

        // Add new define
        ImGui.SetNextItemWidth(200 * Game.DpiScale);
        ImGui.InputText("##NewDefine", ref _newDefineInput, 256);
        ImGui.SameLine();
        bool canAdd = !string.IsNullOrWhiteSpace(_newDefineInput) &&
                      !profile.ScriptingDefineSymbols.Contains(_newDefineInput.Trim());
        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button("Add Symbol"))
        {
            profile.ScriptingDefineSymbols.Add(_newDefineInput.Trim());
            _newDefineInput = string.Empty;
            SaveBuildSettings();
        }
        if (!canAdd) ImGui.EndDisabled();
    }

    // ── Player Page ─────────────────────────────────────────────────

    private void DrawPlayerPage()
    {
        if (_buildSettings == null) return;

        ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "Player Settings");
        ImGui.Separator();
        ImGui.Spacing();

        // Product name
        ImGui.Text("Product Name");
        string productName = _buildSettings.ProductName;
        ImGui.SetNextItemWidth(300 * Game.DpiScale);
        if (ImGui.InputText("##ProductName", ref productName, 256))
        {
            _buildSettings.ProductName = productName;
            SaveBuildSettings();
        }
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
            "The product name is used as the executable name and window title.");
    }

    // ── Rendering Page ──────────────────────────────────────────────

    private void DrawRenderingPage()
    {
        if (_buildSettings == null) return;

        ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "Rendering Settings");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("Rendering Backend");
        string[] backendNames = Enum.GetNames<Runtime.Graphite.GraphicsBackendType>();
        int backendIndex = (int)_buildSettings.RenderingBackend;
        ImGui.SetNextItemWidth(200 * Game.DpiScale);
        if (ImGui.Combo("##RenderingBackend", ref backendIndex, backendNames, backendNames.Length))
        {
            _buildSettings.RenderingBackend = (Runtime.Graphite.GraphicsBackendType)backendIndex;
            SaveBuildSettings();
        }
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
            "The rendering backend used for scene/game views and the built player.");
        /*ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
            "Editor UI always uses OpenGL. Currently only OpenGL is fully implemented.");
        ImGui.Spacing();

        if (_buildSettings.RenderingBackend != Runtime.Graphite.GraphicsBackendType.OpenGL)
        {
            ImGui.TextColored(new Vector4(0.95f, 0.80f, 0.25f, 1f),
                "⚠ Warning: Only OpenGL is currently supported. Selecting another backend may cause errors.");
        }*/
    }

    // ── Load / Save ─────────────────────────────────────────────────

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        if (!string.IsNullOrEmpty(EditorApplication.ProjectPath))
            _buildSettings = BuildSettings.Load(EditorApplication.ProjectPath);
        else
            _buildSettings = new BuildSettings();
    }

    private void SaveBuildSettings()
    {
        if (_buildSettings == null) return;
        if (string.IsNullOrEmpty(EditorApplication.ProjectPath)) return;
        _buildSettings.Save(EditorApplication.ProjectPath);
    }
}
