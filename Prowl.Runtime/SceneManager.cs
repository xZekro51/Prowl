// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Runtime.Resources;

namespace Prowl.Runtime;

/// <summary>
/// Manages multiple simultaneously loaded scenes (additive scene loading).
/// <para>
/// The single-scene workflow (<see cref="Scene.Load"/>/<see cref="Scene.Current"/>)
/// continues to work unchanged. <see cref="SceneManager"/> adds support for:
/// <list type="bullet">
///   <item>Additive scene loading (streaming open-world levels)</item>
///   <item>Multiple active scenes for server-side simulation</item>
///   <item>Editor previews (main scene + prefab preview simultaneously)</item>
/// </list>
/// </para>
/// <para>
/// The <see cref="ActiveScene"/> is the primary scene — new GameObjects are
/// added to it by default. All loaded scenes receive lifecycle calls
/// (Update, FixedUpdate, Render, OnGui, DrawGizmos) each frame.
/// </para>
/// </summary>
public static class SceneManager
{
    private static readonly List<Scene> s_loadedScenes = [];
    private static readonly object s_lock = new();

    /// <summary>
    /// Raised after a scene is loaded additively via <see cref="LoadSceneAdditive"/>.
    /// </summary>
    public static event Action<Scene>? SceneLoaded;

    /// <summary>
    /// Raised after a scene is unloaded via <see cref="UnloadScene"/>.
    /// </summary>
    public static event Action<Scene>? SceneUnloaded;

    /// <summary>
    /// The primary active scene. New GameObjects are added to this scene by default.
    /// This is the same scene as <see cref="Scene.Current"/> — both accessors
    /// are kept in sync.
    /// </summary>
    public static Scene? ActiveScene
    {
        get => Scene.Current;
        set
        {
            if (value != null && !s_loadedScenes.Contains(value))
                throw new InvalidOperationException(
                    "Cannot set ActiveScene to a scene that has not been loaded. " +
                    "Use LoadSceneAdditive() or Scene.Load() first.");
            Scene.SetCurrentDirect(value);
        }
    }

    /// <summary>
    /// Returns a read-only snapshot of all currently loaded scenes.
    /// </summary>
    public static IReadOnlyList<Scene> LoadedScenes
    {
        get
        {
            lock (s_lock)
            {
                return [.. s_loadedScenes];
            }
        }
    }

    /// <summary>
    /// The number of currently loaded scenes.
    /// </summary>
    public static int LoadedSceneCount
    {
        get { lock (s_lock) { return s_loadedScenes.Count; } }
    }

    /// <summary>
    /// Loads a scene additively without unloading any existing scenes.
    /// The scene is enabled and starts receiving lifecycle calls immediately.
    /// <see cref="ActiveScene"/> is not changed — call <see cref="SetActiveScene"/>
    /// to switch the primary scene.
    /// </summary>
    /// <param name="scene">The scene to load additively.</param>
    /// <exception cref="ArgumentNullException"/>
    /// <exception cref="InvalidOperationException">If the scene is already loaded.</exception>
    public static void LoadSceneAdditive(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        lock (s_lock)
        {
            if (s_loadedScenes.Contains(scene))
                throw new InvalidOperationException("Scene is already loaded.");
            s_loadedScenes.Add(scene);
        }

        if (!scene.IsActive)
            scene.Enable();

        // If no active scene is set, make this the active one.
        if (Scene.Current == null)
            Scene.SetCurrentDirect(scene);

        SceneLoaded?.Invoke(scene);
        Debug.Log($"[SceneManager] Additively loaded scene: {scene.Name ?? "(unnamed)"}");
    }

    /// <summary>
    /// Unloads a specific scene, disabling and disposing it.
    /// If the unloaded scene was the <see cref="ActiveScene"/>,
    /// the active scene falls back to the first remaining loaded scene (or null).
    /// </summary>
    /// <param name="scene">The scene to unload.</param>
    /// <returns><c>true</c> if the scene was found and unloaded; <c>false</c> otherwise.</returns>
    public static bool UnloadScene(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        bool removed;
        lock (s_lock)
        {
            removed = s_loadedScenes.Remove(scene);
        }

        if (!removed)
            return false;

        if (scene.IsActive)
            scene.Disable();
        scene.Dispose();

        // If this was the active scene, fall back
        if (Scene.Current == scene)
        {
            lock (s_lock)
            {
                Scene.SetCurrentDirect(s_loadedScenes.Count > 0 ? s_loadedScenes[0] : null);
            }
        }

        SceneUnloaded?.Invoke(scene);
        Debug.Log($"[SceneManager] Unloaded scene: {scene.Name ?? "(unnamed)"}");
        return true;
    }

    /// <summary>
    /// Sets which loaded scene is the active (primary) scene.
    /// The active scene is the one that <see cref="Scene.Current"/> points to.
    /// </summary>
    /// <param name="scene">Must be a currently loaded scene.</param>
    public static void SetActiveScene(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        lock (s_lock)
        {
            if (!s_loadedScenes.Contains(scene))
                throw new InvalidOperationException(
                    "Cannot set active scene to one that is not loaded.");
        }
        Scene.SetCurrentDirect(scene);
    }

    /// <summary>
    /// Unloads all scenes except the specified one.
    /// If <paramref name="except"/> is null, all scenes are unloaded.
    /// </summary>
    public static void UnloadAllExcept(Scene? except = null)
    {
        List<Scene> toUnload;
        lock (s_lock)
        {
            toUnload = [.. s_loadedScenes];
        }

        foreach (var scene in toUnload)
        {
            if (scene == except)
                continue;
            UnloadScene(scene);
        }
    }

    /// <summary>
    /// Checks whether a scene is currently loaded.
    /// </summary>
    public static bool IsSceneLoaded(Scene scene)
    {
        lock (s_lock)
        {
            return s_loadedScenes.Contains(scene);
        }
    }

    /// <summary>
    /// Updates all loaded scenes. Called from the game loop.
    /// </summary>
    internal static void UpdateAll()
    {
        List<Scene> scenes;
        lock (s_lock) { scenes = [.. s_loadedScenes]; }
        foreach (var scene in scenes)
        {
            if (scene.IsActive)
                scene.Update();
        }
    }

    /// <summary>
    /// Runs FixedUpdate on all loaded scenes. Called from the game loop.
    /// </summary>
    internal static void FixedUpdateAll()
    {
        List<Scene> scenes;
        lock (s_lock) { scenes = [.. s_loadedScenes]; }
        foreach (var scene in scenes)
        {
            if (scene.IsActive)
                scene.FixedUpdate();
        }
    }

    /// <summary>
    /// Renders all loaded scenes. Called from the game loop.
    /// </summary>
    /// <param name="target">Optional render target.</param>
    /// <returns><c>true</c> if any camera in any scene rendered.</returns>
    internal static bool RenderAll(RenderTexture? target = null)
    {
        bool anyRendered = false;
        List<Scene> scenes;
        lock (s_lock) { scenes = [.. s_loadedScenes]; }
        foreach (var scene in scenes)
        {
            if (scene.IsActive && scene.Render(target))
                anyRendered = true;
        }
        return anyRendered;
    }

    /// <summary>
    /// Draws gizmos for all loaded scenes.
    /// </summary>
    internal static void DrawGizmosAll()
    {
        List<Scene> scenes;
        lock (s_lock) { scenes = [.. s_loadedScenes]; }
        foreach (var scene in scenes)
        {
            if (scene.IsActive)
                scene.DrawGizmos();
        }
    }

    /// <summary>
    /// Called when <see cref="Scene.Load"/> replaces the primary scene.
    /// Ensures the SceneManager's internal list stays in sync.
    /// </summary>
    internal static void OnPrimarySceneLoaded(Scene? oldScene, Scene newScene)
    {
        lock (s_lock)
        {
            // Remove the old scene from the loaded list
            if (oldScene != null)
                s_loadedScenes.Remove(oldScene);

            // Add the new scene if not already tracked
            if (!s_loadedScenes.Contains(newScene))
                s_loadedScenes.Add(newScene);
        }
    }

    /// <summary>
    /// Called when <see cref="Scene.Unload"/> removes the primary scene.
    /// Ensures the SceneManager's internal list stays in sync.
    /// </summary>
    internal static void OnPrimarySceneUnloaded(Scene scene)
    {
        lock (s_lock)
        {
            s_loadedScenes.Remove(scene);
        }
    }

    /// <summary>
    /// Removes all tracked scenes without disposing them. Used during shutdown.
    /// </summary>
    internal static void Clear()
    {
        lock (s_lock)
        {
            s_loadedScenes.Clear();
        }
    }
}
