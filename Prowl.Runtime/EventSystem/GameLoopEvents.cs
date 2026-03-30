// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Events raised during the main game loop lifecycle.
/// Subscribe via <c>Game.GameLoopEventManager.AddNewDelegate(...)</c> or
/// listen globally with <see cref="EventManager{T}.GlobalInvokeEvent"/>.
/// </summary>
public enum GameLoopEvents
{
    /// <summary>Raised once after the engine and window have been fully initialized.</summary>
    [EventArgs(typeof(Unit))]
    OnInitialized,

    /// <summary>Raised at the very beginning of each frame, before input processing.</summary>
    [EventArgs(typeof(Unit))]
    OnFrameBegin,

    /// <summary>Raised after all update logic has completed for the frame.</summary>
    [EventArgs(typeof(Unit))]
    OnFrameEnd,

    /// <summary>Raised after rendering is complete (after Profiler.EndFrame).</summary>
    [EventArgs(typeof(Unit))]
    OnRenderComplete,

    /// <summary>Raised when the application window is closing.</summary>
    [EventArgs(typeof(Unit))]
    OnClosing,
}
