// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;

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
    private static readonly Dictionary<UInt128, PipelineState> s_cache = new();

#if DEBUG
    // In debug mode, store the full descriptor alongside the pipeline to detect hash collisions.
    private static readonly Dictionary<UInt128, PipelineCacheKey> s_debugKeys = new();
#endif

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
        UInt128 hash = ComputeHash(program, vertexLayout, rasterizerState, topology, renderPassLayout, bindGroupLayouts);

#if DEBUG
        var newKey = new PipelineCacheKey(program, vertexLayout, rasterizerState, topology, renderPassLayout, bindGroupLayouts);
        if (s_debugKeys.TryGetValue(hash, out var existingKey))
        {
            Debug.Assert(existingKey.Equals(newKey),
                $"[PipelineStateCache] Hash collision detected! Two different pipeline configurations produced hash {hash:X16}. " +
                "This will cause rendering artifacts. Consider expanding the hash to 128-bit.");
        }
        else
        {
            s_debugKeys[hash] = newKey;
        }
#endif

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
#if DEBUG
        s_debugKeys.Clear();
#endif
    }

    /// <summary>
    /// Pre-creates pipeline states for the given configurations to avoid
    /// first-use stutter during rendering. Configurations already present
    /// in the cache are skipped cheaply (hash lookup only).
    /// </summary>
    public static void WarmUp(ReadOnlySpan<PipelineWarmUpEntry> entries)
    {
        foreach (ref readonly PipelineWarmUpEntry entry in entries)
        {
            GetOrCreate(entry.Program, entry.VertexLayout, entry.RasterizerState,
                entry.Topology, entry.RenderPassLayout, entry.BindGroupLayouts);
        }
    }

    private static UInt128 ComputeHash(
        GraphicsProgram program,
        VertexLayoutDescriptor vertexLayout,
        RasterizerState rasterizerState,
        Topology topology,
        RenderPassLayout renderPassLayout,
        BindGroupLayout[]? bindGroupLayouts)
    {
        // Dual FNV-1a 64-bit hashes with different seeds → combined into UInt128
        ulong lo = 14695981039346656037UL;  // Standard FNV-1a offset basis
        ulong hi = 0x6C62272E07BB0142UL;    // Alternative seed

        void Mix(ulong value)
        {
            lo = FnvMix(lo, value);
            hi = FnvMix(hi, value ^ 0x9E3779B97F4A7C15UL);
        }

        // Hash shader program identity (unique monotonic ID)
        Mix((ulong)program.ID);

        // Hash vertex layout
        if (vertexLayout.Buffers != null)
        {
            Mix((ulong)vertexLayout.Buffers.Length);
            foreach (var buf in vertexLayout.Buffers)
            {
                Mix((ulong)buf.Stride);
                Mix((ulong)buf.StepMode);
                if (buf.Attributes != null)
                {
                    Mix((ulong)buf.Attributes.Length);
                    foreach (var attr in buf.Attributes)
                    {
                        Mix((ulong)attr.Location);
                        Mix((ulong)attr.Format);
                        Mix((ulong)attr.Offset);
                    }
                }
            }
        }

        // Hash rasterizer state fields
        Mix(rasterizerState.DepthTest ? 1UL : 0UL);
        Mix(rasterizerState.DepthWrite ? 1UL : 0UL);
        Mix((ulong)rasterizerState.Depth);
        Mix(rasterizerState.DoBlend ? 1UL : 0UL);
        Mix((ulong)rasterizerState.BlendSrc);
        Mix((ulong)rasterizerState.BlendDst);
        Mix((ulong)rasterizerState.Blend);
        Mix((ulong)rasterizerState.CullFace);
        Mix((ulong)rasterizerState.Winding);

        // Hash topology
        Mix((ulong)topology);

        // Hash render pass layout
        if (renderPassLayout.ColorFormats != null)
        {
            foreach (var fmt in renderPassLayout.ColorFormats)
                Mix((ulong)fmt);
        }
        if (renderPassLayout.DepthStencilFormat.HasValue)
            Mix((ulong)renderPassLayout.DepthStencilFormat.Value);
        Mix((ulong)renderPassLayout.SampleCount);

        // Hash bind group layouts by identity — different layout objects
        // produce different Vulkan pipeline layouts and are NOT interchangeable.
        if (bindGroupLayouts != null)
        {
            Mix((ulong)bindGroupLayouts.Length);
            foreach (var bgl in bindGroupLayouts)
                Mix((ulong)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(bgl));
        }

        return new UInt128(hi, lo);
    }

    private static ulong FnvMix(ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
        return hash;
    }

#if DEBUG
    /// <summary>
    /// Full descriptor key used in debug builds to detect hash collisions.
    /// Uses exact values (not lossy hashes) so equality is collision-free.
    /// </summary>
    private readonly struct PipelineCacheKey : IEquatable<PipelineCacheKey>
    {
        private readonly int _programId;
        private readonly VertexLayoutDescriptor _vertexLayout;
        private readonly RasterizerState _rasterizerState;
        private readonly Topology _topology;
        private readonly RenderPassLayout _renderPassLayout;
        private readonly BindGroupLayout[]? _bindGroupLayouts;

        public PipelineCacheKey(
            GraphicsProgram program,
            VertexLayoutDescriptor vertexLayout,
            RasterizerState rasterizerState,
            Topology topology,
            RenderPassLayout renderPassLayout,
            BindGroupLayout[]? bindGroupLayouts)
        {
            _programId = program.ID;
            _vertexLayout = vertexLayout;
            _rasterizerState = rasterizerState;
            _topology = topology;
            _renderPassLayout = renderPassLayout;
            _bindGroupLayouts = bindGroupLayouts;
        }

        public bool Equals(PipelineCacheKey other)
        {
            if (_programId != other._programId ||
                !_rasterizerState.Equals(other._rasterizerState) ||
                _topology != other._topology)
                return false;

            if (!VertexLayoutEquals(in _vertexLayout, in other._vertexLayout))
                return false;

            // Compare RenderPassLayout by content — default struct Equals
            // compares the ColorFormats array by reference, which incorrectly
            // reports different array instances with the same elements as unequal.
            if (!RenderPassLayoutEquals(in _renderPassLayout, in other._renderPassLayout))
                return false;

            if (!ArrayReferenceEquals(_bindGroupLayouts, other._bindGroupLayouts))
                return false;

            return true;
        }

        private static bool RenderPassLayoutEquals(in RenderPassLayout a, in RenderPassLayout b)
        {
            if (a.DepthStencilFormat != b.DepthStencilFormat || a.SampleCount != b.SampleCount)
                return false;

            if (a.ColorFormats == null && b.ColorFormats == null)
                return true;
            if (a.ColorFormats == null || b.ColorFormats == null)
                return false;
            if (a.ColorFormats.Length != b.ColorFormats.Length)
                return false;
            for (int i = 0; i < a.ColorFormats.Length; i++)
            {
                if (a.ColorFormats[i] != b.ColorFormats[i])
                    return false;
            }
            return true;
        }

        private static bool ArrayReferenceEquals(BindGroupLayout[]? a, BindGroupLayout[]? b)
        {
            if (a == null && b == null)
                return true;
            if (a == null || b == null)
                return false;
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!ReferenceEquals(a[i], b[i]))
                    return false;
            }
            return true;
        }

        private static bool VertexLayoutEquals(in VertexLayoutDescriptor a, in VertexLayoutDescriptor b)
        {
            if (a.Buffers == null && b.Buffers == null)
                return true;
            if (a.Buffers == null || b.Buffers == null)
                return false;
            if (a.Buffers.Length != b.Buffers.Length)
                return false;
            for (int i = 0; i < a.Buffers.Length; i++)
            {
                if (a.Buffers[i].Stride != b.Buffers[i].Stride ||
                    a.Buffers[i].StepMode != b.Buffers[i].StepMode)
                    return false;
                var aa = a.Buffers[i].Attributes;
                var ba = b.Buffers[i].Attributes;
                if (aa == null && ba == null) continue;
                if (aa == null || ba == null) return false;
                if (aa.Length != ba.Length) return false;
                for (int j = 0; j < aa.Length; j++)
                {
                    if (aa[j].Location != ba[j].Location ||
                        aa[j].Format != ba[j].Format ||
                        aa[j].Offset != ba[j].Offset)
                        return false;
                }
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is PipelineCacheKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(_programId, _topology);
    }
#endif
}

/// <summary>
/// Describes a single pipeline configuration to pre-create via
/// <see cref="PipelineStateCache.WarmUp"/>.
/// </summary>
internal struct PipelineWarmUpEntry
{
    public GraphicsProgram Program;
    public VertexLayoutDescriptor VertexLayout;
    public RasterizerState RasterizerState;
    public Topology Topology;
    public RenderPassLayout RenderPassLayout;
    public BindGroupLayout[]? BindGroupLayouts;
}
