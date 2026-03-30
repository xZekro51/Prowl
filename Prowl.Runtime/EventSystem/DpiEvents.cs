// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Events raised by <see cref="Prowl.Runtime.DpiManager"/> when the display
/// scaling factor changes at runtime.
/// </summary>
public enum DpiEvents
{
    /// <summary>
    /// Raised when the DPI scale changes (e.g., window dragged to a monitor
    /// with a different scaling setting).
    /// </summary>
    OnDpiChanged,
}

/// <summary>
/// Typed argument for <see cref="DpiEvents.OnDpiChanged"/>.
/// </summary>
public readonly record struct DpiChangedArgs(float OldScale, float NewScale);
