// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Events raised by the <see cref="Debug"/> logging system.
/// </summary>
[EventDomain]
public static partial class DebugEvents
{
    /// <summary>Raised whenever a message is logged via <see cref="Debug.Log"/> or related methods.</summary>
    [EventArgs(typeof(LogEventArgs))]
    private static readonly EventKey _OnLog = new();
}

/// <summary>
/// Typed argument for <see cref="DebugEvents.OnLog"/>.
/// </summary>
public readonly record struct LogEventArgs(string Message, DebugStackTrace? StackTrace, LogSeverity Severity);
