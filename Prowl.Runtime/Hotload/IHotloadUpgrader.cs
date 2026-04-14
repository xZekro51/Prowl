// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Runtime;

/// <summary>
/// Implement this interface on a MonoBehaviour to provide custom field migration
/// logic during a full structural hotload. If a component implements this interface,
/// the migration engine will call <see cref="OnHotloadUpgrade"/> instead of performing
/// automatic reflection-based field copying.
/// </summary>
public interface IHotloadUpgrader
{
    /// <summary>
    /// Called during a full hotload to migrate state from the old serialized snapshot
    /// to this newly-constructed instance.
    /// </summary>
    /// <param name="snapshot">
    /// A dictionary of field-name → value pairs captured from the old component instance
    /// before the assembly was unloaded. Values are serialized EchoObjects.
    /// </param>
    void OnHotloadUpgrade(Echo.EchoObject snapshot);
}
