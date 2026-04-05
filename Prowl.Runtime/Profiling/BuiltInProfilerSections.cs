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

        // ── Physics detail ───────────────────────────────────
        Profiler.RegisterSection("PhysicsStep",
            "Physics",
            "A single physics world step including constraint solving, " +
            "broadphase/narrowphase collision detection, and integration.");

        Profiler.RegisterSection("PhysicsSyncTransforms",
            "Physics",
            "Synchronizes Jitter2 rigid body transforms back to " +
            "the engine's Transform components after the physics step.");

        // ── Scene management ─────────────────────────────────
        Profiler.RegisterSection("SceneLoad",
            "Core",
            "Deserializes and initializes a scene from disk, including " +
            "asset reference resolution and component instantiation.");

        Profiler.RegisterSection("SceneUnload",
            "Core",
            "Tears down a scene: disposes GameObjects and components, " +
            "releases held resources.");

        // ── Asset pipeline ───────────────────────────────────
        Profiler.RegisterSection("AssetImport",
            "Core",
            "Imports external files into the project asset database: " +
            "copies files, generates .meta entries, and triggers refresh.");

        Profiler.RegisterSection("AssetRefresh",
            "Core",
            "Re-scans the asset folder to detect new, modified, or " +
            "deleted files and updates the in-memory asset database.");

        // ── Script compilation ───────────────────────────────
        Profiler.RegisterSection("ScriptCompile",
            "Scripts",
            "Compiles user C# scripts into a hot-reloadable assembly " +
            "using the Roslyn compiler.");

        Profiler.RegisterSection("ScriptReload",
            "Scripts",
            "Loads the freshly compiled script assembly and resolves " +
            "previously-missing MonoBehaviour components.");

        // ── Global Illumination ──────────────────────────────────
        Profiler.RegisterSection("VoxelGI.Voxelize",
            "GI",
            "Rasterizes opaque geometry into a 3D voxel grid using " +
            "dominant-axis projection and imageStore.");

        Profiler.RegisterSection("VoxelGI.InjectLight",
            "GI",
            "Injects direct illumination from the primary directional " +
            "light into occupied voxels via compute shader.");

        Profiler.RegisterSection("VoxelGI.Mipmap",
            "GI",
            "Generates the anisotropic mipmap chain of the voxel radiance " +
            "volume for pre-filtered cone tracing.");

        Profiler.RegisterSection("VoxelGI.ConeTrace",
            "GI",
            "Traces diffuse and specular cones through the voxel mipmap chain " +
            "to compute per-pixel indirect illumination.");

        Profiler.RegisterSection("SDFGI.UpdateSDF",
            "GI",
            "Merges per-object signed distance fields into global SDF cascades " +
            "centered around the camera.");

        Profiler.RegisterSection("SDFGI.UpdateProbes",
            "GI",
            "Incrementally updates irradiance probes by tracing rays through " +
            "the global SDF and accumulating spherical harmonics.");

        Profiler.RegisterSection("SDFGI.TraceGI",
            "GI",
            "Fullscreen pass that trilinearly interpolates the probe grid to " +
            "compute per-pixel indirect diffuse illumination.");

        // ── Render Pipeline detail ──────────────────────────────
        Profiler.RegisterSection("Pipeline.Render",
            "Rendering",
            "Full per-camera render pipeline execution including culling, " +
            "GBuffer, lighting, composition, transparents, post-process, " +
            "and final blit.");

        Profiler.RegisterSection("Pipeline.Cull",
            "Rendering",
            "Frustum and layer-mask culling of scene renderables to " +
            "determine which objects are visible to this camera.");

        Profiler.RegisterSection("Pipeline.GBuffer",
            "Rendering",
            "Creates and fills the GBuffer render targets (albedo, normal, " +
            "PBR parameters, custom data) using deferred geometry passes.");

        Profiler.RegisterSection("Pipeline.Lighting",
            "Rendering",
            "Deferred lighting pass: iterates all visible lights and " +
            "accumulates their contributions using the GBuffer data.");

        Profiler.RegisterSection("Pipeline.Compose",
            "Rendering",
            "Combines the light accumulation buffer with GBuffer albedo, " +
            "applies ambient lighting, fog, and emissive terms.");

        Profiler.RegisterSection("Pipeline.Transparents",
            "Rendering",
            "Forward-renders transparent geometry back-to-front on top " +
            "of the composed opaque result.");

        // ── Physics detail ───────────────────────────────────────
        Profiler.RegisterSection("Physics.Configure",
            "Physics",
            "Applies physics world settings (gravity, solver iterations, " +
            "substeps, sleep policy) before the simulation step.");

        Profiler.RegisterSection("Physics.Step",
            "Physics",
            "Executes the Jitter2 World.Step: broadphase, narrowphase, " +
            "constraint solving, integration, and island management.");
    }
}
