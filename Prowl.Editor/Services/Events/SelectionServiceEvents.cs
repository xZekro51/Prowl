// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.EventSystem;

namespace Prowl.Editor.Services;

/// <summary>
/// Per-instance event domain for <see cref="ISelectionService"/>.
/// Subscribe via <c>selectionService.Events.SubscribeOnXxx(...)</c>.
/// </summary>
[EventDomain]
public partial class SelectionServiceEvents
{
    /// <summary>Raised whenever the selection changes (either ActiveObject or SelectedAsset).</summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnSelectionChanged = new();
}
