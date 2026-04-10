// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.EventSystem;
using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Resources;

namespace Prowl.Editor.Services;

/// <summary>
/// Per-instance event domain for <see cref="ISceneService"/>.
/// Subscribe via <c>sceneService.Events.SubscribeOnXxx(...)</c>.
/// </summary>
[EventDomain]
public partial class SceneServiceEvents
{
    /// <summary>Raised when the scene's dirty state changes.</summary>
    [EventArgs(typeof(DirtyStateChangedArgs))]
    private static readonly EventKey _OnDirtyStateChanged = new();

    /// <summary>Raised after a scene has been loaded or created and set as current.</summary>
    [EventArgs(typeof(SceneLoadedArgs))]
    private static readonly EventKey _OnSceneLoaded = new();
}

/// <summary>Typed argument for <see cref="SceneServiceEvents.OnDirtyStateChanged"/>.</summary>
/// <param name="IsDirty">True when the scene has unsaved modifications, false when cleared.</param>
public readonly record struct DirtyStateChangedArgs(bool IsDirty);

/// <summary>Typed argument for <see cref="SceneServiceEvents.OnSceneLoaded"/>.</summary>
/// <param name="Scene">The scene that was just loaded or created.</param>
public readonly record struct SceneLoadedArgs(Scene Scene);
