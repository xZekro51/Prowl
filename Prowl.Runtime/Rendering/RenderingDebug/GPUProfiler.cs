// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Graphite;

namespace Prowl.Runtime.Rendering;

/// <summary>
/// Static GPU profiler that subscribes to per-stage rendering events and inserts
/// GPU timestamp queries at stage boundaries. Results are read back 1–2 frames later
/// (after the GPU fence is signaled) and exposed via <see cref="LastFrameResults"/>.
/// <para>
/// On backends that do not support GPU timestamp queries (e.g. OpenGL), all
/// operations are no-ops and <see cref="LastFrameResults"/> returns an empty list.
/// </para>
/// </summary>
internal static class GPUProfiler
{
    private static GpuTimingResult[] s_lastFrameResults = [];

    /// <summary>
    /// GPU timing results from the most recently completed frame.
    /// Results are 1–2 frames behind due to GPU/CPU asynchrony.
    /// </summary>
    public static IReadOnlyList<GpuTimingResult> LastFrameResults => s_lastFrameResults;

    /// <summary>
    /// Total GPU time in milliseconds for the most recently completed frame.
    /// Computed as the sum of all individual timing results.
    /// </summary>
    public static float TotalGpuTimeMs
    {
        get
        {
            double total = 0;
            GpuTimingResult[] results = s_lastFrameResults;
            for (int i = 0; i < results.Length; i++)
                total += results[i].DurationMs;
            return (float)total;
        }
    }

    /// <summary>
    /// Registers event subscriptions for GPU profiling. Called once during
    /// <see cref="Graphics.InitializeEventSubscriptions"/>.
    /// </summary>
    internal static void InitializeEventSubscriptions()
    {
        // ── Insert timestamp queries at stage boundaries ──
        // Begin queries run at priority -10 (before stage work)
        // End queries run at priority 10 (after stage work)

        RenderingEvents.SubscribeOnGBufferPassBegin(args =>
        {
            CommandList? cmd = args.CommandBuffer?.CommandList;
            if (cmd != null)
                Graphics.Graphite.BeginGpuTimerQuery(cmd, "GBuffer");
        }, priority: -10);

        RenderingEvents.SubscribeOnGBufferPassEnd(args =>
        {
            CommandList? cmd = args.CommandBuffer?.CommandList;
            if (cmd != null)
                Graphics.Graphite.EndGpuTimerQuery(cmd, "GBuffer");
        }, priority: 10);

        RenderingEvents.SubscribeOnLightingPassBegin(args =>
        {
            CommandList? cmd = args.CommandBuffer?.CommandList;
            if (cmd != null)
                Graphics.Graphite.BeginGpuTimerQuery(cmd, "Lighting");
        }, priority: -10);

        RenderingEvents.SubscribeOnLightingPassEnd(args =>
        {
            CommandList? cmd = args.CommandBuffer?.CommandList;
            if (cmd != null)
                Graphics.Graphite.EndGpuTimerQuery(cmd, "Lighting");
        }, priority: 10);

        RenderingEvents.SubscribeOnCompositionComplete(args =>
        {
            CommandList? cmd = args.CommandBuffer?.CommandList;
            if (cmd != null)
                Graphics.Graphite.EndGpuTimerQuery(cmd, "Composition");
        }, priority: 10);

        RenderingEvents.SubscribeOnTransparentPassBegin(args =>
        {
            CommandList? cmd = args.CommandBuffer?.CommandList;
            if (cmd != null)
                Graphics.Graphite.BeginGpuTimerQuery(cmd, "Transparent");
        }, priority: -10);

        // ── Full-frame timer: begin at camera start, end at camera end ──

        RenderingEvents.SubscribeOnCameraRenderBegin(args =>
        {
            CommandList? cmd = args.CommandBuffer?.CommandList;
            if (cmd != null)
            {
                Graphics.Graphite.BeginGpuTimerQuery(cmd, "FullFrame");
                Graphics.Graphite.BeginGpuTimerQuery(cmd, "Composition");
            }
        }, priority: -10);

        RenderingEvents.SubscribeOnCameraRenderEnd(args =>
        {
            CommandList? cmd = args.CommandBuffer?.CommandList;
            if (cmd != null)
            {
                Graphics.Graphite.EndGpuTimerQuery(cmd, "Transparent");
                Graphics.Graphite.EndGpuTimerQuery(cmd, "FullFrame");
            }
        }, priority: 10);

        // ── Read back results at frame begin (after fence wait) ──
        // Priority 100: runs after stats swap (-90) so results are fresh for this frame's display
        GraphiteDeviceEvents.SubscribeOnGpuFrameBegin(_ =>
        {
            if (Graphics.IsGraphiteReady)
                s_lastFrameResults = Graphics.Graphite.GetGpuTimingResults();
        }, priority: 100);
    }
}
