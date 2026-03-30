// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Resources;

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Events raised by <see cref="Prowl.Runtime.SceneManager"/> during
/// additive scene load/unload operations.
/// </summary>
public enum SceneManagerEvents
{
    /// <summary>Raised after a scene is loaded additively.</summary>
    OnSceneLoaded,
    /// <summary>Raised after a scene is unloaded.</summary>
    OnSceneUnloaded,
}

/// <summary>
/// Typed argument for <see cref="SceneManagerEvents"/>.
/// </summary>
public readonly record struct SceneEventArgs(Scene Scene);
