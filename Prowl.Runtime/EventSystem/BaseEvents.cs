// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.EventSystem;

namespace Prowl.Runtime.EventSystem;

[EventDomain]
public static partial class BaseEvents
{
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnBeforeUpdate = new();

    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnAfterUpdate = new();

    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnBeforeLateUpdate = new();

    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnAfterLateUpdate = new();
}
