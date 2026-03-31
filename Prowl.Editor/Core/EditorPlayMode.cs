// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Resources;
using Prowl.Editor.Services;

namespace Prowl.Editor.Core;

/// <summary>
/// Possible states for the editor play mode.
/// </summary>
public enum PlayModeState
{
    /// <summary> Editor is in edit mode — no simulation running. </summary>
    Stopped,

    /// <summary> Simulation is running normally. </summary>
    Playing,

    /// <summary> Simulation is active but frozen (can be single-stepped). </summary>
    Paused,
}

/// <summary>
/// Manages the editor's play/pause/stop lifecycle.
/// On Play: snapshots the scene via serialization, enables simulation.
/// On Pause/Step: freezes/advances the simulation.
/// On Stop: restores the pre-play scene state from the snapshot, disables simulation.
/// </summary>
public sealed class EditorPlayMode
{
    private object? _sceneSnapshot;

    /// <summary> Current play mode state. </summary>
    public PlayModeState State { get; private set; } = PlayModeState.Stopped;

    /// <summary>
    /// Ensures physics and gameplay are disabled in edit mode at startup.
    /// Call this once during editor initialization.
    /// </summary>
    public void InitEditMode()
    {
        Scene.SimulatePhysics = false;
        Scene.IsPlayMode = false;
    }

    /// <summary>
    /// Toggle play mode. If stopped → play. If playing/paused → stop.
    /// </summary>
    public void TogglePlay()
    {
        if (State == PlayModeState.Stopped)
            EnterPlayMode();
        else
            ExitPlayMode();
    }

    /// <summary>
    /// Toggle pause. Only meaningful while playing.
    /// </summary>
    public void TogglePause()
    {
        if (State == PlayModeState.Playing)
        {
            State = PlayModeState.Paused;
            EditorServices.Get<IEditorTime>().Pause();
            EditorEvents.InvokeOnPlayModeStateChanged(new PlayModeChangedArgs(State));
        }
        else if (State == PlayModeState.Paused)
        {
            State = PlayModeState.Playing;
            EditorServices.Get<IEditorTime>().Play();
            EditorEvents.InvokeOnPlayModeStateChanged(new PlayModeChangedArgs(State));
        }
    }

    /// <summary>
    /// Advance one frame while paused.
    /// </summary>
    public void StepFrame()
    {
        if (State == PlayModeState.Paused)
        {
            EditorServices.Get<IEditorTime>().Step();
        }
    }

    /// <summary>
    /// Called each editor frame to manage simulation time.
    /// Scene updates are driven by the main loop; this only ticks the clock.
    /// </summary>
    public void Update(float realDelta)
    {
        if (State == PlayModeState.Stopped) return;

        var time = EditorServices.Get<IEditorTime>();
        time.Tick(realDelta);
    }

    private void EnterPlayMode()
    {
        // Prevent entering play mode while editing a prefab
        if (EditorServices.TryGet<Prefabs.PrefabEditMode>(out var prefabMode)
            && prefabMode!.IsActive)
        {
            Debug.LogWarning("[PlayMode] Cannot enter play mode while editing a prefab. Close the prefab first.");
            return;
        }

        var sceneService = EditorServices.Get<ISceneService>();
        var time = EditorServices.Get<IEditorTime>();

        // Snapshot the current scene for later restoration (full serialization)
        _sceneSnapshot = sceneService.SnapshotScene();

        // Enable gameplay simulation
        Scene.IsPlayMode = true;
        Scene.SimulatePhysics = true;

        // Start the simulation clock
        time.Play();

        State = PlayModeState.Playing;
        EditorEvents.InvokeOnPlayModeStateChanged(new PlayModeChangedArgs(State));

        Debug.Log("[PlayMode] Entered play mode.");
    }

    private void ExitPlayMode()
    {
        var sceneService = EditorServices.Get<ISceneService>();
        var time = EditorServices.Get<IEditorTime>();

        // Stop the simulation clock
        time.Stop();

        // Disable gameplay simulation
        Scene.IsPlayMode = false;
        Scene.SimulatePhysics = false;

        // Restore the original scene state from the serialized snapshot
        sceneService.RestoreScene(_sceneSnapshot);
        _sceneSnapshot = null;

        State = PlayModeState.Stopped;
        EditorEvents.InvokeOnPlayModeStateChanged(new PlayModeChangedArgs(State));

        Debug.Log("[PlayMode] Exited play mode. Scene restored.");
    }
}
