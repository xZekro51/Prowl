// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Runtime.Graphite;
using Prowl.Runtime.Rendering.Shaders;

namespace Prowl.Runtime.Rendering;

/// <summary>
/// Caches Graphite <see cref="PipelineState"/> objects to avoid recreating them
/// every frame. Pipeline states are keyed by a hash of (shader modules, vertex layout,
/// rasterizer state, topology, render pass layout).
/// </summary>
internal static class PipelineStateCache
{
    private static readonly Dictionary<ulong, PipelineState> s_cache = new();

    /// <summary>
    /// Gets or creates a <see cref="PipelineState"/> for the given configuration.
    /// The pipeline is cached so that repeated calls with the same parameters return
    /// the same object without hitting the GPU driver.
    /// </summary>
    public static PipelineState GetOrCreate(
        GraphicsProgram program,
        VertexLayoutDescriptor vertexLayout,
        RasterizerState rasterizerState,
        Topology topology,
        RenderPassLayout renderPassLayout,
        BindGroupLayout[]? bindGroupLayouts = null)
    {
        ulong hash = ComputeHash(program, rasterizerState, topology, renderPassLayout, bindGroupLayouts);

        if (s_cache.TryGetValue(hash, out var cached))
            return cached;

        var descriptor = new PipelineStateDescriptor
        {
            VertexShader = program.GraphiteVertexModule,
            FragmentShader = program.GraphiteFragmentModule,
            GeometryShader = program.GraphiteGeometryModule,
            VertexLayout = vertexLayout,
            Topology = GraphiteFormatMapper.MapTopology(topology),
            RasterizerState = GraphiteFormatMapper.MapRasterizerState(rasterizerState),
            DepthStencilState = GraphiteFormatMapper.MapDepthStencilState(rasterizerState),
            BlendState = GraphiteFormatMapper.MapBlendState(rasterizerState),
            RenderPassLayout = renderPassLayout,
            BindGroupLayouts = bindGroupLayouts,
        };

        var pipeline = Graphics.Graphite.CreatePipelineState(in descriptor);
        s_cache[hash] = pipeline;
        return pipeline;
    }

    /// <summary>
    /// Disposes all cached pipeline states. Call during shutdown or when the
    /// device is being recreated.
    /// </summary>
    public static void Clear()
    {
        foreach (var pipeline in s_cache.Values)
            pipeline?.Dispose();
        s_cache.Clear();
    }

    private static ulong ComputeHash(
        GraphicsProgram program,
        RasterizerState rasterizerState,
        Topology topology,
        RenderPassLayout renderPassLayout,
        BindGroupLayout[]? bindGroupLayouts)
    {
        // FNV-1a 64-bit hash
        ulong hash = 14695981039346656037UL;

        // Hash shader program identity
        hash = FnvMix(hash, (ulong)program.GetHashCode());

        // Hash rasterizer state fields
        hash = FnvMix(hash, rasterizerState.DepthTest ? 1UL : 0UL);
        hash = FnvMix(hash, rasterizerState.DepthWrite ? 1UL : 0UL);
        hash = FnvMix(hash, (ulong)rasterizerState.Depth);
        hash = FnvMix(hash, rasterizerState.DoBlend ? 1UL : 0UL);
        hash = FnvMix(hash, (ulong)rasterizerState.BlendSrc);
        hash = FnvMix(hash, (ulong)rasterizerState.BlendDst);
        hash = FnvMix(hash, (ulong)rasterizerState.Blend);
        hash = FnvMix(hash, (ulong)rasterizerState.CullFace);
        hash = FnvMix(hash, (ulong)rasterizerState.Winding);

        // Hash topology
        hash = FnvMix(hash, (ulong)topology);

        // Hash render pass layout
        if (renderPassLayout.ColorFormats != null)
        {
            foreach (var fmt in renderPassLayout.ColorFormats)
                hash = FnvMix(hash, (ulong)fmt);
        }
        if (renderPassLayout.DepthStencilFormat.HasValue)
            hash = FnvMix(hash, (ulong)renderPassLayout.DepthStencilFormat.Value);
        hash = FnvMix(hash, (ulong)renderPassLayout.SampleCount);

        // Hash bind group layouts by identity — different layout objects
        // produce different Vulkan pipeline layouts and are NOT interchangeable.
        if (bindGroupLayouts != null)
        {
            hash = FnvMix(hash, (ulong)bindGroupLayouts.Length);
            foreach (var bgl in bindGroupLayouts)
                hash = FnvMix(hash, (ulong)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bgl));
        }

        return hash;
    }

    private static ulong FnvMix(ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
        return hash;
    }
}
