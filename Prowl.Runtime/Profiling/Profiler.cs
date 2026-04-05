// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Prowl.Runtime.Profiling;

/// <summary>
/// Lightweight hierarchical CPU profiler that collects per-frame timing data.
/// Use <see cref="BeginSection"/>/<see cref="EndSection"/> (or the disposable
/// <see cref="Section"/>) to instrument code paths. At the end of each frame
/// call <see cref="EndFrame"/> to swap the sample buffers.
/// <para>
/// The profiler is designed to work in both the editor (play mode) and
/// standalone builds. Overhead is kept low — timing uses
/// <see cref="Stopwatch.GetTimestamp"/> (high-resolution, no allocation).
/// </para>
/// <para>
/// Samples are sorted by <see cref="ProfilerSample.StartMs"/> in each completed
/// frame so that parent sections always precede their children. Each sample
/// stores a <see cref="ProfilerSample.ParentIndex"/> for O(1) call-chain
/// navigation (caller paths, "% Parent" calculations, hot-path tracing).
/// </para>
/// <para>
/// In release builds (without <c>PROWL_PROFILING</c> defined) all
/// instrumentation calls are compiled away via
/// <see cref="ConditionalAttribute"/> — zero runtime overhead.
/// </para>
/// </summary>
public static class Profiler
{
    // ── Configuration ────────────────────────────────────────────────

    /// <summary> Maximum number of history frames kept in the ring buffer. </summary>
    public const int MaxHistoryFrames = 300;

    /// <summary> Global on/off switch. When false, Begin/End calls are no-ops. </summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// Optional callback invoked at the end of every frame with the completed
    /// <see cref="ProfilerFrame"/>. Used by <c>ProfilerServer</c> to stream
    /// frame data to a connected editor for remote profiling.
    /// </summary>
    public static Action<ProfilerFrame>? OnFrameCompleted { get; set; }

    // ── Internal state ───────────────────────────────────────────────

    // Active (write) buffer — samples are appended here during a frame.
    private static readonly List<ProfilerSample> s_current = new(128);

    // Ring buffer of completed frames.
    private static readonly ProfilerFrame[] s_history = new ProfilerFrame[MaxHistoryFrames];
    private static int s_historyHead;   // next write index
    private static int s_historyCount;  // number of valid entries

    // Pending section stack — tracks nesting.
    private static readonly Stack<PendingSection> s_stack = new();
    private static long s_frameStartTick;

    // High-resolution tick frequency (cached once).
    private static readonly double s_ticksToMs = 1000.0 / Stopwatch.Frequency;

    // Registered section descriptions (category + description text).
    private static readonly Dictionary<string, SectionInfo> s_sectionInfos = new(StringComparer.Ordinal);

    // Reusable sort buffer to avoid per-frame allocation.
    private static readonly List<ProfilerSample> s_sortBuffer = new(128);

    // ── Public API ───────────────────────────────────────────────────

    /// <summary>
    /// Registers a description for a named section. This is optional —
    /// sections that are not registered will appear with empty descriptions.
    /// Call this once at startup for every known section.
    /// </summary>
    public static void RegisterSection(string name, string category, string description)
    {
        s_sectionInfos[name] = new SectionInfo(category, description);
    }

    /// <summary>
    /// Begins a new frame. Call at the very start of the game loop (before
    /// any Begin/End pairs).
    /// </summary>
    [Conditional("PROWL_PROFILING")]
    public static void BeginFrame()
    {
        if (!Enabled) return;
        s_current.Clear();
        s_stack.Clear();
        s_frameStartTick = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Opens a profiled section. Must be paired with <see cref="EndSection"/>.
    /// </summary>
    [Conditional("PROWL_PROFILING")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BeginSection(string name)
    {
        if (!Enabled) return;

        s_stack.Push(new PendingSection
        {
            Name = name,
            StartTick = Stopwatch.GetTimestamp(),
            Depth = s_stack.Count,
        });
    }

    /// <summary>
    /// Closes the most recently opened section and records the sample.
    /// </summary>
    [Conditional("PROWL_PROFILING")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EndSection()
    {
        if (!Enabled) return;
        if (s_stack.Count == 0) return;

        long endTick = Stopwatch.GetTimestamp();
        PendingSection pending = s_stack.Pop();

        s_sectionInfos.TryGetValue(pending.Name, out SectionInfo info);

        s_current.Add(new ProfilerSample
        {
            Name = pending.Name,
            Category = info.Category ?? string.Empty,
            Description = info.Description ?? string.Empty,
            Depth = pending.Depth,
            StartMs = (pending.StartTick - s_frameStartTick) * s_ticksToMs,
            DurationMs = (endTick - pending.StartTick) * s_ticksToMs,
            ParentIndex = -1, // will be resolved in EndFrame
        });
    }

    /// <summary>
    /// Returns a disposable handle that calls <see cref="EndSection"/> when
    /// disposed, enabling <c>using var _ = Profiler.Section("Name");</c>.
    /// <para>
    /// Note: Unlike the void methods, this returns a value and therefore
    /// cannot use <c>[Conditional]</c>. In release builds without
    /// <c>PROWL_PROFILING</c> the inner Begin/EndSection calls are stripped,
    /// so the scope is a harmless empty struct.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ProfilerScope Section(string name)
    {
        BeginSection(name);
        return new ProfilerScope();
    }

    /// <summary>
    /// Completes the current frame: sorts samples by start time (so parents
    /// precede children), computes <see cref="ProfilerSample.ParentIndex"/>
    /// for each sample, copies them into the history ring buffer, and invokes
    /// <see cref="OnFrameCompleted"/> if set.
    /// </summary>
    [Conditional("PROWL_PROFILING")]
    public static void EndFrame()
    {
        if (!Enabled) return;

        // Flush any un-closed sections (shouldn't happen, but be safe).
        while (s_stack.Count > 0)
            EndSection();

        long endTick = Stopwatch.GetTimestamp();
        double totalMs = (endTick - s_frameStartTick) * s_ticksToMs;

        // Sort samples by StartMs so parents (which start first) precede
        // their children. This enables correct hierarchical display.
        s_sortBuffer.Clear();
        s_sortBuffer.AddRange(s_current);
        s_sortBuffer.Sort(s_startMsComparison);

        // Compute ParentIndex for each sample using a depth-tracking stack.
        // After sorting by StartMs, the parent of sample[i] is the most
        // recent sample with depth == sample[i].Depth - 1 that is still
        // "open" (its time span covers sample[i].StartMs).
        ProfilerSample[] sorted = new ProfilerSample[s_sortBuffer.Count];
        // Stack of (sampleIndex, depth) tracking open ancestors.
        // We maintain the invariant: stack entries are in strictly increasing
        // depth order (bottom = depth 0, top = deepest open parent).
        int ancestorTop = -1;               // index into ancestorIndices
        Span<int> ancestorIndices = s_sortBuffer.Count <= 256
            ? stackalloc int[256]
            : new int[s_sortBuffer.Count];

        for (int i = 0; i < s_sortBuffer.Count; i++)
        {
            ProfilerSample s = s_sortBuffer[i];

            // Pop ancestors whose depth >= this sample's depth (they're
            // siblings or shallower — no longer potential parents).
            while (ancestorTop >= 0 && sorted[ancestorIndices[ancestorTop]].Depth >= s.Depth)
                ancestorTop--;

            int parentIdx = ancestorTop >= 0 ? ancestorIndices[ancestorTop] : -1;

            sorted[i] = new ProfilerSample
            {
                Name = s.Name,
                Category = s.Category,
                Description = s.Description,
                Depth = s.Depth,
                StartMs = s.StartMs,
                DurationMs = s.DurationMs,
                ParentIndex = parentIdx,
            };

            // Push this sample as a potential parent for deeper samples.
            ancestorIndices[++ancestorTop] = i;
        }

        ProfilerFrame frame = new ProfilerFrame
        {
            Samples = sorted,
            TotalMs = totalMs,
        };

        s_history[s_historyHead] = frame;

        s_historyHead = (s_historyHead + 1) % MaxHistoryFrames;
        if (s_historyCount < MaxHistoryFrames)
            s_historyCount++;

        s_current.Clear();

        // Notify listeners (e.g. ProfilerServer for remote profiling).
        OnFrameCompleted?.Invoke(frame);
    }

    // ── Query API ────────────────────────────────────────────────────

    /// <summary> Number of completed frames available in the history. </summary>
    public static int FrameCount => s_historyCount;

    /// <summary>
    /// Returns the profiler frame at the given age (0 = most recent).
    /// Returns null if the index is out of range.
    /// </summary>
    public static ProfilerFrame? GetFrame(int age)
    {
        if (age < 0 || age >= s_historyCount) return null;
        int idx = (s_historyHead - 1 - age + MaxHistoryFrames * 2) % MaxHistoryFrames;
        return s_history[idx];
    }

    /// <summary> Clears all history. </summary>
    public static void Clear()
    {
        s_historyCount = 0;
        s_historyHead = 0;
        s_current.Clear();
        s_stack.Clear();
    }

    // ── Internal types ───────────────────────────────────────────────

    private struct PendingSection
    {
        public string Name;
        public long StartTick;
        public int Depth;
    }

    private readonly record struct SectionInfo(string Category, string Description);

    private static readonly Comparison<ProfilerSample> s_startMsComparison =
        static (a, b) =>
        {
            int cmp = a.StartMs.CompareTo(b.StartMs);
            return cmp != 0 ? cmp : a.Depth.CompareTo(b.Depth);
        };
}

/// <summary>
/// A completed profiler frame containing all recorded samples and the total
/// frame duration.
/// </summary>
public sealed class ProfilerFrame
{
    public ProfilerSample[] Samples { get; init; } = [];
    public double TotalMs { get; init; }
}

/// <summary>
/// Disposable scope returned by <see cref="Profiler.Section"/> for use
/// with the <c>using</c> statement.
/// </summary>
public readonly struct ProfilerScope : IDisposable
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose() => Profiler.EndSection();
}
