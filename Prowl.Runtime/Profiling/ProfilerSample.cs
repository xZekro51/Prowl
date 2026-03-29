// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.Profiling;

/// <summary>
/// A single captured timing sample from a profiler section.
/// </summary>
public readonly struct ProfilerSample
{
    /// <summary> Name of the profiled section. </summary>
    public string Name { get; init; }

    /// <summary> Category tag (e.g. "Physics", "Rendering", "Scripts"). </summary>
    public string Category { get; init; }

    /// <summary> Human-readable description of what this section measures. </summary>
    public string Description { get; init; }

    /// <summary> Nesting depth (0 = top-level). </summary>
    public int Depth { get; init; }

    /// <summary> Duration of this section in milliseconds. </summary>
    public double DurationMs { get; init; }

    /// <summary> Absolute start time relative to frame start, in milliseconds. </summary>
    public double StartMs { get; init; }
}
