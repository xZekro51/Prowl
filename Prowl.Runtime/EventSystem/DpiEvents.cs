// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.EventSystem;

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Events raised by <see cref="Prowl.Runtime.DpiManager"/> when the display
/// scaling factor changes at runtime.
/// </summary>
[EventDomain]
public static partial class DpiEvents
{
    /// <summary>
    /// Raised when the DPI scale changes (e.g., window dragged to a monitor
    /// with a different scaling setting).
    /// </summary>
    [EventArgs(typeof(DpiChangedArgs))]
    private static readonly EventKey _OnDpiChanged = new();
}

/// <summary>
/// Typed argument for <see cref="DpiEvents.OnDpiChanged"/>.
/// </summary>
public readonly record struct DpiChangedArgs(float OldScale, float NewScale);
