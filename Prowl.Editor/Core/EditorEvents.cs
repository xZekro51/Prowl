// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.EventSystem;

namespace Prowl.Editor.Core;

/// <summary>
/// Events raised by the editor for play mode, assembly reloads, and error logging.
/// Subscribe via <c>EditorApplication.EditorEventManager.AddNewDelegate(...)</c>.
/// </summary>
public enum EditorEvents
{
    /// <summary>Raised when the play mode state changes (play, pause, stop).</summary>
    [EventArgs(typeof(PlayModeChangedArgs))]
    OnPlayModeStateChanged,

    /// <summary>Raised after the user-script assembly has been recompiled and reloaded.</summary>
    [EventArgs(typeof(Unit))]
    OnAssemblyChanged,

    /// <summary>Raised when an error or exception is logged. Used to pause play mode.</summary>
    [EventArgs(typeof(Unit))]
    OnErrorLogged,
}

/// <summary>
/// Typed argument for <see cref="EditorEvents.OnPlayModeStateChanged"/>.
/// </summary>
public readonly record struct PlayModeChangedArgs(PlayModeState State);
