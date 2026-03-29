// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.Profiling;

/// <summary>
/// Registers all built-in profiler section names with human-readable
/// descriptions. Called once during engine initialization.
/// </summary>
public static class BuiltInProfilerSections
{
    private static bool s_registered;

    /// <summary>
    /// Registers every well-known engine section so that the profiler
    /// window can display meaningful category tags and tooltips.
    /// </summary>
    public static void Register()
    {
        if (s_registered) return;
        s_registered = true;

        // ── Update loop ──────────────────────────────────────────
        Profiler.RegisterSection("Input",
            "Core",
            "Processes raw input events (keyboard, mouse, gamepad) and " +
            "updates action mappings for the current frame.");

        Profiler.RegisterSection("Audio",
            "Audio",
            "Updates the audio context: advances playing sources, " +
            "reclaims finished buffers, and submits new audio data.");

        Profiler.RegisterSection("BeginUpdate",
            "Core",
            "Editor/game pre-update hook. In the editor this handles " +
            "play-mode state, preferences tick, script recompilation " +
            "polling, keyboard shortcuts, and auto-save.");

        Profiler.RegisterSection("FixedUpdate",
            "Physics",
            "Runs the fixed-timestep loop: physics simulation steps, " +
            "MonoBehaviour.FixedUpdate callbacks, and constraint solving.");

        Profiler.RegisterSection("Update",
            "Scripts",
            "Per-frame MonoBehaviour.Update callbacks and scene-graph " +
            "traversal for all active GameObjects.");

        Profiler.RegisterSection("Gizmos",
            "Editor",
            "Draws debug gizmos (lines, spheres, boxes) requested by " +
            "components via Debug.Draw* during the frame.");

        Profiler.RegisterSection("EndUpdate",
            "Core",
            "Post-update hook. Reserved for custom game logic that " +
            "must run after all Update callbacks.");

        // ── Rendering loop ───────────────────────────────────────
        Profiler.RegisterSection("Shadows",
            "Rendering",
            "Initializes and clears the shadow atlas for the frame. " +
            "Shadow-casting lights will later render depth into atlas tiles.");

        Profiler.RegisterSection("BeginRender",
            "Rendering",
            "Editor-specific pre-render phase: renders scene and game " +
            "views into off-screen render textures via the Graphite " +
            "abstraction layer.");

        Profiler.RegisterSection("RenderScenes",
            "Rendering",
            "Executes the render pipeline for every active camera: " +
            "culling, geometry pass, lighting, post-processing, and " +
            "final composite.");

        Profiler.RegisterSection("EndRender",
            "Rendering",
            "Post-render cleanup: render-texture pool housekeeping and " +
            "stats collection.");

        Profiler.RegisterSection("PaperUI",
            "UI",
            "Paper UI frame: layout, painting, and GPU submission for " +
            "in-game UI elements (Canvas, WorldCanvas).");

        Profiler.RegisterSection("ImGui",
            "UI",
            "Dear ImGui overlay frame: editor panels, docking, menus, " +
            "and the full editor chrome.");
    }
}
