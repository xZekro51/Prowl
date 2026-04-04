// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Runtime.Graphite;
using Prowl.Runtime.Rendering.Shaders;
using Prowl.Runtime.Resources;
using Prowl.Vector;

using Graphite = Prowl.Runtime.Graphite;

namespace Prowl.Runtime.Rendering;

/// <summary>
/// High-level command buffer that records rendering commands using the Graphite API.
/// Wraps a <see cref="CommandList"/> and provides engine-friendly abstractions
/// for render passes, pipeline state management, and draw commands.
/// <para>
/// During the bridge phase (Phases 2–3), this records Graphite commands using the
/// shadow resources created alongside legacy GL objects.  Once the legacy layer is
/// removed (Phase 4), this becomes the sole rendering interface.
/// </para>
/// </summary>
public sealed class RenderCommandBuffer : IDisposable
{
    private readonly CommandList _commandList;
    private readonly string? _debugName;
    private bool _disposed;
    private bool _submitted;
    private RenderPassLayout _currentRenderPassLayout;

    /// <summary>
    /// The underlying Graphite command list for advanced usage.
    /// </summary>
    public CommandList CommandList => _commandList;

    /// <summary>
    /// Whether a render pass is currently active.
    /// </summary>
    public bool InRenderPass => _commandList.InRenderPass;

    /// <summary>
    /// The <see cref="RenderPassLayout"/> of the currently active render pass.
    /// Only valid while <see cref="InRenderPass"/> is <c>true</c>.
    /// </summary>
    public RenderPassLayout CurrentRenderPassLayout => _currentRenderPassLayout;

    /// <summary>
    /// Creates a new command buffer and begins recording immediately.
    /// </summary>
    public RenderCommandBuffer(string? debugName = null)
    {
        if (!Graphics.IsGraphiteReady)
            throw new InvalidOperationException("Graphite device is not initialized.");

        _debugName = debugName;
        _commandList = Graphics.Graphite.CreateCommandList();
        _commandList.Begin();

        if (debugName != null)
            _commandList.PushDebugGroup(debugName);
    }

    #region Render Pass Management

    /// <summary>
    /// Begins a render pass targeting a <see cref="RenderTexture"/>.
    /// Uses the Graphite shadow textures from Phase 2 to build the render pass descriptor.
    /// </summary>
    /// <param name="target">The render texture to render into.</param>
    /// <param name="colorLoadOp">How to handle existing color data (Clear, Load, DontCare).</param>
    /// <param name="clearColor">Clear color when <paramref name="colorLoadOp"/> is <see cref="LoadOp.Clear"/>.</param>
    /// <param name="clearDepth">Whether to clear the depth buffer.</param>
    public void BeginRenderPass(RenderTexture target, LoadOp colorLoadOp = LoadOp.Clear, Float4? clearColor = null, bool clearDepth = true)
    {
        var desc = BuildRenderPassDescriptor(target, colorLoadOp, clearColor ?? Float4.Zero, clearDepth);
        _currentRenderPassLayout = GraphiteFormatMapper.MapRenderPassLayout(target);
        _commandList.BeginRenderPass(in desc);
    }

    /// <summary>
    /// Begins a render pass with an explicit descriptor.
    /// The caller is responsible for setting <see cref="_currentRenderPassLayout"/>
    /// or calling the overload that accepts a <see cref="RenderPassLayout"/>.
    /// </summary>
    public void BeginRenderPass(in RenderPassDescriptor descriptor, RenderPassLayout layout)
    {
        _currentRenderPassLayout = layout;
        _commandList.BeginRenderPass(in descriptor);
    }

    /// <summary>
    /// Begins a depth-only render pass (e.g., shadow mapping).
    /// </summary>
    public void BeginDepthOnlyRenderPass(RenderTexture depthTarget)
    {
        var depthTex = depthTarget.GraphiteDepthTexture;
        if (depthTex == null)
            throw new InvalidOperationException("RenderTexture has no Graphite depth attachment.");

        var desc = RenderPassDescriptor.DepthOnly(
            RenderPassDepthStencilAttachment.Clear(depthTex));

        _currentRenderPassLayout = new RenderPassLayout([], depthTex.Format);
        _commandList.BeginRenderPass(in desc);
    }

    /// <summary>
    /// Ends the current render pass.
    /// </summary>
    public void EndRenderPass()
    {
        _commandList.EndRenderPass();
        _currentRenderPassLayout = default;
    }

    #endregion

    #region Pipeline & State

    /// <summary>
    /// Sets a pre-created pipeline state.
    /// </summary>
    public void SetPipeline(PipelineState pipeline)
    {
        _commandList.SetPipeline(pipeline);
    }

    /// <summary>
    /// Resolves and sets the pipeline state for a material pass, using the
    /// <see cref="PipelineStateCache"/> to avoid per-frame recreation.
    /// </summary>
    /// <param name="program">The compiled shader variant.</param>
    /// <param name="vertexLayout">The vertex layout from the mesh.</param>
    /// <param name="state">The rasterizer/blend/depth state from the shader pass.</param>
    /// <param name="topology">The mesh primitive topology.</param>
    /// <returns>The resolved (or cached) pipeline state.</returns>
    public PipelineState SetMaterialPipeline(
        GraphicsProgram program,
        VertexLayoutDescriptor vertexLayout,
        RasterizerState state,
        Topology topology)
    {
        var pipeline = PipelineStateCache.GetOrCreate(
            program, vertexLayout, state, topology, _currentRenderPassLayout);
        _commandList.SetPipeline(pipeline);
        return pipeline;
    }

    /// <summary>
    /// Resolves and sets the pipeline state for a material pass, including
    /// bind group layouts for Vulkan descriptor set compatibility.
    /// </summary>
    public PipelineState SetMaterialPipeline(
        GraphicsProgram program,
        VertexLayoutDescriptor vertexLayout,
        RasterizerState state,
        Topology topology,
        BindGroupLayout[]? bindGroupLayouts)
    {
        var pipeline = PipelineStateCache.GetOrCreate(
            program, vertexLayout, state, topology, _currentRenderPassLayout, bindGroupLayouts);
        _commandList.SetPipeline(pipeline);
        return pipeline;
    }

    /// <summary>
    /// Sets the viewport within the current render pass.
    /// On Vulkan, this automatically applies a Y-flip for 3D rendering compatibility.
    /// </summary>
    public void SetViewport(float x, float y, float width, float height, float minDepth = 0, float maxDepth = 1)
    {
        _commandList.SetViewport(x, y, width, height, minDepth, maxDepth);
    }

    /// <summary>
    /// Sets the viewport without any coordinate system transformations.
    /// Use this for 2D blit operations where Y-flip is not desired.
    /// </summary>
    public void SetViewportRaw(float x, float y, float width, float height, float minDepth = 0, float maxDepth = 1)
    {
        _commandList.SetViewportRaw(x, y, width, height, minDepth, maxDepth);
    }

    /// <summary>
    /// Sets the scissor rectangle within the current render pass.
    /// </summary>
    public void SetScissor(int x, int y, uint width, uint height)
    {
        _commandList.SetScissor(x, y, width, height);
    }

    /// <summary>
    /// Sets the blend constant color.
    /// </summary>
    public void SetBlendConstants(Float4 color)
    {
        _commandList.SetBlendConstants(color);
    }

    #endregion

    #region Resource Binding

    /// <summary>
    /// Binds a vertex buffer at the specified slot.
    /// </summary>
    public void SetVertexBuffer(uint slot, Graphite.Buffer buffer, uint offset = 0)
    {
        _commandList.SetVertexBuffer(slot, buffer, offset);
    }

    /// <summary>
    /// Binds an index buffer.
    /// </summary>
    public void SetIndexBuffer(Graphite.Buffer buffer, Graphite.IndexFormat format, uint offset = 0)
    {
        _commandList.SetIndexBuffer(buffer, format, offset);
    }

    /// <summary>
    /// Binds a bind group at the specified index.
    /// </summary>
    public void SetBindGroup(uint index, BindGroup bindGroup, ReadOnlySpan<uint> dynamicOffsets = default)
    {
        _commandList.SetBindGroup(index, bindGroup, dynamicOffsets);
    }

    /// <summary>
    /// Binds the vertex and index buffers from a <see cref="Mesh"/>'s Graphite shadow resources.
    /// The mesh must have been uploaded (call <see cref="Mesh.Upload"/> first or rely on
    /// the draw helpers which upload automatically).
    /// </summary>
    public void SetMeshBuffers(Mesh mesh)
    {
        mesh.Upload();

        // Vertex buffer from the VAO's vertex buffer shadow
        var vao = mesh.VertexArrayObject;
        if (vao?.VertexBuffer?.GraphiteBuffer != null)
            _commandList.SetVertexBuffer(0, vao.VertexBuffer.GraphiteBuffer);

        // Index buffer
        if (vao?.IndexBuffer?.GraphiteBuffer != null)
        {
            var indexFormat = mesh.IndexFormat == Resources.IndexFormat.UInt32
                ? Graphite.IndexFormat.Uint32
                : Graphite.IndexFormat.Uint16;
            _commandList.SetIndexBuffer(vao.IndexBuffer.GraphiteBuffer, indexFormat);
        }
    }

    #endregion

    #region Draw Commands

    /// <summary>
    /// Draws non-indexed primitives.
    /// </summary>
    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        _commandList.Draw(vertexCount, instanceCount, firstVertex, firstInstance);
    }

    /// <summary>
    /// Draws indexed primitives.
    /// </summary>
    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        _commandList.DrawIndexed(indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
    }

    /// <summary>
    /// Draws indexed primitives using an indirect buffer.
    /// </summary>
    public void DrawIndexedIndirect(Graphite.Buffer indirectBuffer, uint offset = 0)
    {
        _commandList.DrawIndexedIndirect(indirectBuffer, offset);
    }

    /// <summary>
    /// High-level helper: uploads the mesh, binds its buffers, and issues an indexed draw call.
    /// Does NOT set a pipeline — the caller must call <see cref="SetPipeline"/> or
    /// <see cref="SetMaterialPipeline"/> first.
    /// </summary>
    public void DrawMeshIndexed(Mesh mesh, uint instanceCount = 1)
    {
        SetMeshBuffers(mesh);
        DrawIndexed((uint)mesh.IndexCount, instanceCount);
    }

    #endregion

    #region Synchronization

    /// <summary>
    /// Inserts a resource barrier to transition a resource between states.
    /// Must be called outside a render pass.
    /// </summary>
    public void ResourceBarrier(in Graphite.ResourceBarrier barrier)
    {
        _commandList.ResourceBarrier(in barrier);
    }

    /// <summary>
    /// Inserts a memory barrier to ensure all previous writes are visible.
    /// </summary>
    public void MemoryBarrier()
    {
        _commandList.MemoryBarrier();
    }

    #endregion

    #region Copy Commands

    /// <summary>
    /// Copies data between GPU buffers. Must be called outside a render pass.
    /// </summary>
    public void CopyBufferToBuffer(Graphite.Buffer source, uint srcOffset, Graphite.Buffer destination, uint dstOffset, uint size)
    {
        _commandList.CopyBufferToBuffer(source, srcOffset, destination, dstOffset, size);
    }

    /// <summary>
    /// Copies data between GPU textures. Must be called outside a render pass.
    /// </summary>
    public void CopyTextureToTexture(in TextureTextureCopy copy)
    {
        _commandList.CopyTextureToTexture(in copy);
    }

    #endregion

    #region Debug Markers

    /// <summary>
    /// Begins a named debug group for GPU profiling tools.
    /// </summary>
    public void PushDebugGroup(string name) => _commandList.PushDebugGroup(name);

    /// <summary>
    /// Ends the current debug group.
    /// </summary>
    public void PopDebugGroup() => _commandList.PopDebugGroup();

    /// <summary>
    /// Inserts a named debug marker.
    /// </summary>
    public void InsertDebugMarker(string name) => _commandList.InsertDebugMarker(name);

    #endregion

    #region Submission

    /// <summary>
    /// Ends recording and submits the command list to the Graphite device for execution.
    /// </summary>
    public void Submit()
    {
        if (_submitted) return;
        _submitted = true;

        if (_debugName != null)
            _commandList.PopDebugGroup();

        // Flush any pending upload batch so that textures/buffers uploaded
        // during command recording are available before this command list
        // executes on the GPU.
        Graphics.Graphite.FlushUploadBatch();

        _commandList.End();
        Graphics.Graphite.SubmitCommands(_commandList);
    }

    // Shared debug trace — logs submissions for the first N frames.
    private static int _debugSubmitTraceFrames = 3;

    /// <summary>
    /// Ends recording and submits the command list, signaling a fence when complete.
    /// </summary>
    public void Submit(Fence fence)
    {
        if (_submitted) return;
        _submitted = true;

        if (_debugName != null)
            _commandList.PopDebugGroup();

        // Flush any pending upload batch before submission.
        Graphics.Graphite.FlushUploadBatch();

        _commandList.End();
        Graphics.Graphite.SubmitCommands(_commandList, fence);
    }

    #endregion

    #region Helpers

    private static RenderPassDescriptor BuildRenderPassDescriptor(
        RenderTexture renderTexture,
        LoadOp colorLoadOp,
        Float4 clearColor,
        bool clearDepth)
    {
        var colorAttachments = renderTexture.GraphiteColorTextures;
        var depthAttachment = renderTexture.GraphiteDepthTexture;

        // Build color attachments from the shadow textures
        RenderPassColorAttachment[]? colors = null;
        if (colorAttachments != null)
        {
            var list = new List<RenderPassColorAttachment>();
            foreach (var tex in colorAttachments)
            {
                if (tex == null) continue;
                list.Add(colorLoadOp switch
                {
                    LoadOp.Clear => RenderPassColorAttachment.Clear(tex, clearColor),
                    LoadOp.Load  => RenderPassColorAttachment.Load(tex),
                    _            => new RenderPassColorAttachment { Texture = tex, LoadOp = LoadOp.DontCare, StoreOp = StoreOp.Store },
                });
            }
            if (list.Count > 0)
                colors = list.ToArray();
        }

        // Build depth attachment
        RenderPassDepthStencilAttachment? depth = null;
        if (depthAttachment != null)
        {
            depth = clearDepth
                ? RenderPassDepthStencilAttachment.Clear(depthAttachment)
                : RenderPassDepthStencilAttachment.Load(depthAttachment);
        }

        return new RenderPassDescriptor
        {
            ColorAttachments = colors,
            DepthStencilAttachment = depth,
        };
    }

    #endregion

    #region Disposal

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_submitted)
        {
            // Clean up recording state without submitting
            if (_debugName != null && _commandList.IsRecording)
                _commandList.PopDebugGroup();
            if (_commandList.InRenderPass)
                _commandList.EndRenderPass();
            if (_commandList.IsRecording)
                _commandList.End();
        }

        _commandList.Dispose();
    }

    #endregion
}
