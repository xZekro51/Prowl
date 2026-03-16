// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Editor.Services;

/// <summary>
/// Abstracts editor time control so play-mode logic is decoupled from
/// the concrete engine time system.
/// </summary>
public interface IEditorTime
{
    /// <summary> Whether the simulation is currently running (play mode active and not paused). </summary>
    bool IsPlaying { get; }

    /// <summary> Whether the simulation is paused (play mode active but frozen). </summary>
    bool IsPaused { get; }

    /// <summary> The current simulation time scale (0..N). </summary>
    float TimeScale { get; set; }

    /// <summary> Total simulated time elapsed since play mode started. </summary>
    float SimulationTime { get; }

    /// <summary> Number of simulation frames advanced since play mode started. </summary>
    long FrameCount { get; }

    /// <summary> Delta time of the last simulation step. </summary>
    float DeltaTime { get; }

    /// <summary> Start or resume the simulation clock. </summary>
    void Play();

    /// <summary> Pause the simulation clock. </summary>
    void Pause();

    /// <summary> Advance exactly one simulation frame while paused. </summary>
    void Step();

    /// <summary> Stop the simulation clock and reset all counters. </summary>
    void Stop();

    /// <summary>
    /// Tick the simulation forward by <paramref name="realDelta"/> seconds (scaled by TimeScale).
    /// Called once per editor frame while playing. Returns false if paused/stopped.
    /// </summary>
    bool Tick(float realDelta);
}
