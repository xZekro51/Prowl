// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Editor.Scripting;
using Prowl.Editor.Widgets;
using Prowl.PaperUI;

namespace Prowl.Editor;

[ProjectSettings("Hotloading", EditorIcons.BoltLightning, order: 50, exportToBuild: false)]
public class HotloadSettings : ProjectSettingsBase
{
    /// <summary>Logging verbosity (0 = silent, 1 = summary, 2 = detailed, 3 = trace).</summary>
    public int LogVerbosity { get; set; } = 2;

    /// <summary>Debounce delay in milliseconds before processing file changes.</summary>
    public int DebounceMs { get; set; } = 300;

    /// <summary>Whether automatic hotloading on file save is enabled.</summary>
    public bool AutoHotload { get; set; } = true;

    public override void Apply()
    {
        HotloadLogger.Verbosity = LogVerbosity;
    }

    public override void ResetToDefaults()
    {
        LogVerbosity = 2;
        DebounceMs = 300;
        AutoHotload = true;
        Apply();
    }

    public override void OnGUI(Paper paper, float width)
    {
        EditorGUI.Header(paper, "hl_header", $"{EditorIcons.BoltLightning}  Hotloading");
        EditorGUI.Separator(paper, "hl_sep");

        EditorGUI.IntSlider(paper, "hl_verbosity", "Log Verbosity", LogVerbosity, 0, 3)
            .OnValueChanged(v => { LogVerbosity = v; Apply(); ProjectSettingsRegistry.SaveAll(); });

        EditorGUI.IntField(paper, "hl_debounce", DebounceMs, "Debounce (ms)")
            .OnValueChanged(v => { DebounceMs = Math.Max(50, v); ProjectSettingsRegistry.SaveAll(); });

        EditorGUI.Toggle(paper, "hl_auto", "Auto Hotload on Save", AutoHotload)
            .OnValueChanged(v => { AutoHotload = v; ProjectSettingsRegistry.SaveAll(); });

        EditorGUI.Separator(paper, "hl_sep2");
        EditorGUI.Header(paper, "hl_stats_header", $"{EditorIcons.Gauge}  Last Hotload Stats");

        var stats = HotloadPipeline.LastStats;
        if (stats.TotalTimeMs > 0)
        {
            EditorGUI.Label(paper, "hl_stats_type", $"Type: {stats.Type}");
            EditorGUI.Label(paper, "hl_stats_time", $"Total Time: {stats.TotalTimeMs:F0}ms");
            EditorGUI.Label(paper, "hl_stats_scene",
                $"Scene: {(stats.SceneSerialized ? "Serialized" : "—")} → {(stats.SceneRestored ? "Restored" : "—")}");

            if (stats.Migration != null)
            {
                EditorGUI.Label(paper, "hl_stats_migrated", $"Components Migrated: {stats.Migration.ComponentsMigrated}");
                EditorGUI.Label(paper, "hl_stats_recreated", $"Components Recreated: {stats.Migration.ComponentsRecreated}");
                EditorGUI.Label(paper, "hl_stats_failed", $"Components Failed: {stats.Migration.ComponentsFailed}");
                EditorGUI.Label(paper, "hl_stats_removed", $"Types Removed: {stats.Migration.TypesRemoved}");
            }
        }
        else
        {
            EditorGUI.Label(paper, "hl_stats_none", "No hotload performed yet.");
        }

        EditorGUI.Separator(paper, "hl_sep3");
        EditorGUI.Header(paper, "hl_actions_header", $"{EditorIcons.Wrench}  Actions");

        EditorGUI.Button(paper, "hl_force", $"{EditorIcons.Bolt}  Force Full Hotload")
            .OnValueChanged(v =>
            {
                if (v) HotloadPipeline.ForceFullHotload();
            });
    }
}
