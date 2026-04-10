// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Resources;

using Prowl.EventSystem;

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Per-scene instance event domain for scene lifecycle events.
/// Each Scene instance has its own event manager accessible via <see cref="Scene.Events"/>.
/// </summary>
[EventDomain]
public partial class SceneEvents
{
    /// <summary>
    /// Fired when a GameObject is added to this scene.
    /// </summary>
    [EventArgs(typeof(GameObjectAddedArgs))]
    private static readonly EventKey _OnGameObjectAdded = new();

    /// <summary>
    /// Fired when a GameObject is removed from this scene.
    /// </summary>
    [EventArgs(typeof(GameObjectRemovedArgs))]
    private static readonly EventKey _OnGameObjectRemoved = new();

    /// <summary>
    /// Fired when this scene is enabled.
    /// </summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnSceneEnabled = new();

    /// <summary>
    /// Fired when this scene is disabled.
    /// </summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnSceneDisabled = new();

    /// <summary>
    /// Fired before this scene's Update cycle begins.
    /// </summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnBeforeUpdate = new();

    /// <summary>
    /// Fired after this scene's Update cycle completes.
    /// </summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnAfterUpdate = new();

    /// <summary>
    /// Fired before this scene's FixedUpdate cycle begins.
    /// </summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnBeforeFixedUpdate = new();

    /// <summary>
    /// Fired after this scene's FixedUpdate cycle completes.
    /// </summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnAfterFixedUpdate = new();

    /// <summary>
    /// Fired before this scene's rendering begins.
    /// </summary>
    [EventArgs(typeof(RenderingArgs))]
    private static readonly EventKey _OnBeforeRender = new();

    /// <summary>
    /// Fired after this scene's rendering completes.
    /// </summary>
    [EventArgs(typeof(RenderingArgs))]
    private static readonly EventKey _OnAfterRender = new();
}

/// <summary>
/// Arguments for GameObject addition events.
/// </summary>
public readonly record struct GameObjectAddedArgs(GameObject GameObject);

/// <summary>
/// Arguments for GameObject removal events.
/// </summary>
public readonly record struct GameObjectRemovedArgs(GameObject GameObject);

/// <summary>
/// Arguments for rendering events.
/// </summary>
public readonly record struct RenderingArgs(int CameraCount, bool Success);
