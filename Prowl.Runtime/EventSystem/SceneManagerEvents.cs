// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Resources;

using Prowl.EventSystem;

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Events raised by <see cref="Prowl.Runtime.SceneManager"/> during
/// additive scene load/unload operations.
/// </summary>
[EventDomain]
public static partial class SceneManagerEvents
{
    /// <summary>Raised after a scene is loaded additively.</summary>
    [EventArgs(typeof(SceneEventArgs))]
    private static readonly EventKey _OnSceneLoaded = new();

    /// <summary>Raised after a scene is unloaded.</summary>
    [EventArgs(typeof(SceneEventArgs))]
    private static readonly EventKey _OnSceneUnloaded = new();
}

/// <summary>
/// Typed argument for <see cref="SceneManagerEvents"/>.
/// </summary>
public readonly record struct SceneEventArgs(Scene Scene);
