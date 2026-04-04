// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;

using ImGuiNET;

using Prowl.Editor.Build;
using Prowl.Editor.Docking;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

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
        "Lighting",
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
                case 2: DrawLightingPage(); break;
                case 3: DrawScriptingDefinesPage(); break;
            }
        }
        ImGui.EndChild();
    }

    // ── Lighting Page ────────────────────────────────────────────────

    private void DrawLightingPage()
    {
        var scene = Scene.Current;
        if (scene == null)
        {
            ImGui.TextDisabled("No active scene");
            return;
        }

        ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "Global Illumination");
        ImGui.Separator();
        ImGui.Spacing();

        // GI Mode dropdown
        Scene.GlobalIlluminationParams gi = scene.GlobalIllumination;
        string[] modeNames = Enum.GetNames<Scene.GlobalIlluminationParams.GIMode>();
        int modeIndex = (int)gi.Mode;
        ImGui.Text("GI Mode");
        ImGui.SetNextItemWidth(200 * Game.DpiScale);
        if (ImGui.Combo("##GIMode", ref modeIndex, modeNames, modeNames.Length))
            gi.Mode = (Scene.GlobalIlluminationParams.GIMode)modeIndex;

        ImGui.Spacing();

        // Shared settings
        float intensity = gi.Intensity;
        ImGui.SetNextItemWidth(200 * Game.DpiScale);
        if (ImGui.SliderFloat("Intensity", ref intensity, 0f, 5f))
            gi.Intensity = intensity;

        float distance = gi.Distance;
        ImGui.SetNextItemWidth(200 * Game.DpiScale);
        if (ImGui.SliderFloat("Distance", ref distance, 10f, 500f))
            gi.Distance = distance;

        int bounces = gi.BounceCount;
        ImGui.SetNextItemWidth(200 * Game.DpiScale);
        if (ImGui.SliderInt("Bounces", ref bounces, 1, 4))
            gi.BounceCount = bounces;

        float resScale = gi.ResolutionScale;
        ImGui.SetNextItemWidth(200 * Game.DpiScale);
        if (ImGui.SliderFloat("Resolution Scale", ref resScale, 0.25f, 1.0f))
            gi.ResolutionScale = resScale;

        // VoxelGI-specific
        if (gi.Mode == Scene.GlobalIlluminationParams.GIMode.VoxelGI)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "Voxel Cone Tracing");
            ImGui.Separator();

            int[] resOptions = [64, 128, 256, 512];
            int resIdx = Array.IndexOf(resOptions, gi.VoxelResolution);
            if (resIdx < 0) resIdx = 2;
            string[] resLabels = ["64", "128", "256", "512"];
            ImGui.SetNextItemWidth(200 * Game.DpiScale);
            if (ImGui.Combo("Voxel Resolution", ref resIdx, resLabels, resLabels.Length))
                gi.VoxelResolution = resOptions[resIdx];

            int coneCount = gi.ConeCount;
            ImGui.SetNextItemWidth(200 * Game.DpiScale);
            if (ImGui.SliderInt("Cone Count", ref coneCount, 4, 16))
                gi.ConeCount = coneCount;

            float coneAngle = gi.ConeAngle;
            ImGui.SetNextItemWidth(200 * Game.DpiScale);
            if (ImGui.SliderFloat("Cone Angle", ref coneAngle, 0.1f, 1.0f))
                gi.ConeAngle = coneAngle;

            bool voxelAO = gi.VoxelAO;
            if (ImGui.Checkbox("Voxel AO", ref voxelAO))
                gi.VoxelAO = voxelAO;
        }

        // SDFGI-specific
        if (gi.Mode == Scene.GlobalIlluminationParams.GIMode.SDFGI)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "SDF Global Illumination");
            ImGui.Separator();

            int[] cascadeOptions = [2, 3, 4, 6];
            int cascIdx = Array.IndexOf(cascadeOptions, gi.SDFCascadeCount);
            if (cascIdx < 0) cascIdx = 2;
            string[] cascLabels = ["2", "3", "4", "6"];
            ImGui.SetNextItemWidth(200 * Game.DpiScale);
            if (ImGui.Combo("Cascade Count", ref cascIdx, cascLabels, cascLabels.Length))
                gi.SDFCascadeCount = cascadeOptions[cascIdx];

            int[] probeOptions = [4, 8, 16];
            int probeIdx = Array.IndexOf(probeOptions, gi.SDFProbeResolution);
            if (probeIdx < 0) probeIdx = 1;
            string[] probeLabels = ["4", "8", "16"];
            ImGui.SetNextItemWidth(200 * Game.DpiScale);
            if (ImGui.Combo("Probe Resolution", ref probeIdx, probeLabels, probeLabels.Length))
                gi.SDFProbeResolution = probeOptions[probeIdx];

            float cascadeScale = gi.SDFCascadeScale;
            ImGui.SetNextItemWidth(200 * Game.DpiScale);
            if (ImGui.SliderFloat("Cascade Scale", ref cascadeScale, 1.5f, 4.0f))
                gi.SDFCascadeScale = cascadeScale;

            float occBias = gi.SDFOcclusionBias;
            ImGui.SetNextItemWidth(200 * Game.DpiScale);
            if (ImGui.SliderFloat("Occlusion Bias", ref occBias, 0.001f, 0.1f))
                gi.SDFOcclusionBias = occBias;
        }

        scene.GlobalIllumination = gi;
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
