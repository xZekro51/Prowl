// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
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
/// On Play: snapshots the scene, starts simulation time.
/// On Pause/Step: freezes/advances the simulation.
/// On Stop: restores the pre-play scene state.
/// </summary>
public sealed class EditorPlayMode
{
    private object? _sceneSnapshot;
    private Scene? _playScene;

    /// <summary> Current play mode state. </summary>
    public PlayModeState State { get; private set; } = PlayModeState.Stopped;

    /// <summary> Fires when the play mode state changes. </summary>
    public event Action<PlayModeState>? StateChanged;

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
            StateChanged?.Invoke(State);
        }
        else if (State == PlayModeState.Paused)
        {
            State = PlayModeState.Playing;
            EditorServices.Get<IEditorTime>().Play();
            StateChanged?.Invoke(State);
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
    /// Called each editor frame to advance the simulation if playing.
    /// </summary>
    public void Update(float realDelta)
    {
        if (State == PlayModeState.Stopped) return;

        var time = EditorServices.Get<IEditorTime>();
        if (time.Tick(realDelta))
        {
            // Advance the play-mode scene simulation
            _playScene?.Update();
        }
    }

    private void EnterPlayMode()
    {
        var sceneService = EditorServices.Get<ISceneService>();
        var time = EditorServices.Get<IEditorTime>();

        // Snapshot the current scene for later restoration
        _sceneSnapshot = sceneService.SnapshotScene();

        // Clone the scene for play-mode simulation
        _playScene = sceneService.CloneCurrentScene();

        // Start the simulation clock
        time.Play();

        State = PlayModeState.Playing;
        StateChanged?.Invoke(State);

        Debug.Log("[PlayMode] Entered play mode.");
    }

    private void ExitPlayMode()
    {
        var sceneService = EditorServices.Get<ISceneService>();
        var time = EditorServices.Get<IEditorTime>();

        // Stop the simulation clock
        time.Stop();

        // Dispose the play scene
        if (_playScene != null)
        {
            _playScene.Dispose();
            _playScene = null;
        }

        // Restore the original scene state
        sceneService.RestoreScene(_sceneSnapshot);
        _sceneSnapshot = null;

        State = PlayModeState.Stopped;
        StateChanged?.Invoke(State);

        Debug.Log("[PlayMode] Exited play mode. Scene restored.");
    }
}
