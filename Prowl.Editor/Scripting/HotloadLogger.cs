// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;

using Prowl.Runtime;

namespace Prowl.Editor.Scripting;

/// <summary>
/// Hotload-specific logging with configurable verbosity levels.
/// Level 0 = silent, 1 = summary, 2 = detailed, 3 = trace (very verbose).
/// </summary>
public static class HotloadLogger
{
    /// <summary>Current verbosity level (0–3).</summary>
    public static int Verbosity { get; set; } = 2;

    /// <summary>Stopwatch for timing hotload phases.</summary>
    private static readonly Stopwatch s_timer = new();

    /// <summary>Start timing a hotload operation.</summary>
    public static void StartTimer() => s_timer.Restart();

    /// <summary>Get elapsed milliseconds since last <see cref="StartTimer"/>.</summary>
    public static double ElapsedMs => s_timer.Elapsed.TotalMilliseconds;

    /// <summary>Log a summary-level message (verbosity >= 1).</summary>
    public static void Log(string message)
    {
        if (Verbosity >= 1)
            Runtime.Debug.Log($"[Hotload] {message}");
    }

    /// <summary>Log a detailed message (verbosity >= 2).</summary>
    public static void LogDetail(string message)
    {
        if (Verbosity >= 2)
            Runtime.Debug.Log($"[Hotload] {message}");
    }

    /// <summary>Log a trace-level message (verbosity >= 3).</summary>
    public static void LogTrace(string message)
    {
        if (Verbosity >= 3)
            Runtime.Debug.Log($"[Hotload:Trace] {message}");
    }

    /// <summary>Log a warning at any verbosity level.</summary>
    public static void LogWarning(string message)
    {
        Runtime.Debug.LogWarning($"[Hotload] {message}");
    }

    /// <summary>Log an error at any verbosity level.</summary>
    public static void LogError(string message)
    {
        Runtime.Debug.LogError($"[Hotload] {message}");
    }

    /// <summary>Log a timing result with description.</summary>
    public static void LogTiming(string phase)
    {
        if (Verbosity >= 1)
            Runtime.Debug.Log($"[Hotload] {phase} completed in {s_timer.Elapsed.TotalMilliseconds:F1}ms");
    }

    /// <summary>Log a timing result and restart the timer for the next phase.</summary>
    public static void LogTimingAndRestart(string phase)
    {
        LogTiming(phase);
        s_timer.Restart();
    }
}
