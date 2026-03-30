// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.EventSystem;

public enum BaseEvents
{
    [EventArgs(typeof(Unit))]
    OnBeforeUpdate,
    [EventArgs(typeof(Unit))]
    OnAfterUpdate,
    [EventArgs(typeof(Unit))]
    OnBeforeLateUpdate,
    [EventArgs(typeof(Unit))]
    OnAfterLateUpdate,
}
