// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.EventSystem;

namespace Prowl.Editor.Prefabs;

/// <summary>
/// Per-instance event domain for <see cref="PrefabEditMode"/>.
/// Subscribe via <c>prefabEditMode.Events.SubscribeOnXxx(...)</c>.
/// </summary>
[EventDomain]
public partial class PrefabEditModeEvents
{
    /// <summary>Raised when prefab edit mode is entered or exited.</summary>
    [EventArgs(typeof(PrefabModeChangedArgs))]
    private static readonly EventKey _OnModeChanged = new();
}

/// <summary>Typed argument for <see cref="PrefabEditModeEvents.OnModeChanged"/>.</summary>
/// <param name="IsActive">True when entering prefab edit mode, false when exiting.</param>
public readonly record struct PrefabModeChangedArgs(bool IsActive);
