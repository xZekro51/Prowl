// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Prowl.Runtime.Graphite;
using Prowl.Vector;

using GBuffer = Prowl.Runtime.Graphite.Buffer;

namespace Prowl.Runtime.Rendering.Compute;

/// <summary>
/// Uniform data for a compute dispatch, packed into a GPU-uploadable buffer.
/// Collects scalar, vector, and matrix uniforms and uploads them to a
/// uniform buffer for binding.
/// </summary>
public sealed class ComputeUniforms : IDisposable
{
    private readonly List<byte> _data = new(256);
    private readonly Dictionary<string, int> _offsets = new();
    private GBuffer? _gpuBuffer;
    private bool _dirty = true;

    /// <summary>The GPU buffer containing the packed uniform data.</summary>
    public GBuffer? GpuBuffer => _gpuBuffer;

    /// <summary>Size of the packed uniform data in bytes.</summary>
    public int SizeInBytes => _data.Count;

    public void SetInt(string name, int value) => SetValue(name, value, 4);
    public void SetFloat(string name, float value) => SetValue(name, value, 4);
    public void SetVector2(string name, Float2 value) => SetValue(name, value, 8);
    public void SetVector3(string name, Float3 value) => SetValue(name, value, 16);
    public void SetVector4(string name, Float4 value) => SetValue(name, value, 16);
    public void SetMatrix(string name, Float4x4 value) => SetValue(name, value, 16);

    /// <summary>
    /// Sets a uniform value using std140 layout alignment rules.
    /// <para>std140 alignment: int/float = 4, vec2 = 8, vec3/vec4/mat4 = 16.</para>
    /// <para>After a vec3 (12 bytes), a subsequent scalar packs at offset 12 (4-byte aligned)
    /// inside the same 16-byte row. This differs from the previous approach that padded
    /// every value to 16 bytes.</para>
    /// </summary>
    private unsafe void SetValue<T>(string name, T value, int alignment) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();

        if (_offsets.TryGetValue(name, out int offset))
        {
            // Update existing value
            byte* ptr = (byte*)Unsafe.AsPointer(ref value);
            for (int i = 0; i < size; i++)
                _data[offset + i] = ptr[i];
        }
        else
        {
            // Pad to alignment boundary
            int currentSize = _data.Count;
            int aligned = (currentSize + alignment - 1) & ~(alignment - 1);
            while (_data.Count < aligned)
                _data.Add(0);

            offset = _data.Count;
            _offsets[name] = offset;

            // Append value bytes
            byte* ptr = (byte*)Unsafe.AsPointer(ref value);
            for (int i = 0; i < size; i++)
                _data.Add(ptr[i]);
        }

        _dirty = true;
    }

    /// <summary>
    /// Uploads the uniform data to the GPU buffer.
    /// Always creates a new buffer and retires the old one to avoid
    /// write-after-read hazards when the GPU is still reading the
    /// previous frame's data (Vulkan multi-frame-in-flight).
    /// </summary>
    public void Upload()
    {
        if (!_dirty || _data.Count == 0 || !Graphics.IsGraphiteReady)
            return;

        uint size = (uint)_data.Count;

        // Retire the previous buffer so the GPU can finish reading it
        // before it is reclaimed. Create a fresh buffer for this frame.
        if (_gpuBuffer != null)
            GraphiteMaterialBinder.Retire(_gpuBuffer);

        _gpuBuffer = Graphics.Graphite.CreateBuffer(new BufferDescriptor
        {
            SizeInBytes = size,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDestination,
            MemoryAccess = MemoryAccess.CpuToGpu,
            DebugName = "ComputeUniforms",
        });

        // Upload data
        byte[] dataArray = _data.ToArray();
        Graphics.Graphite.UpdateBuffer<byte>(_gpuBuffer, 0, dataArray.AsSpan());
        _dirty = false;
    }

    public void Clear()
    {
        _data.Clear();
        _offsets.Clear();
        _dirty = true;
    }

    public void Dispose()
    {
        // Retire rather than dispose — the GPU may still be reading the last
        // uploaded buffer. GraphiteMaterialBinder.Retire defers the disposal
        // until after the GPU fence for this frame has been waited on.
        if (_gpuBuffer != null)
        {
            GraphiteMaterialBinder.Retire(_gpuBuffer);
            _gpuBuffer = null;
        }
    }
}

/// <summary>
/// Orchestrates compute shader dispatches through the Graphite command list.
/// Handles uniform buffer upload, bind group creation, pipeline binding,
/// and dispatch for both OpenGL and Vulkan backends.
/// </summary>
public static class ComputeDispatcher
{
    /// <summary>
    /// Dispatches a compute kernel with the given uniforms and image bindings.
    /// Must be called outside a render pass.
    /// </summary>
    /// <param name="cmd">The command list to record into. Must be recording and not inside a render pass.</param>
    /// <param name="kernel">The compiled compute kernel to dispatch.</param>
    /// <param name="groupCountX">Number of workgroups in X dimension.</param>
    /// <param name="groupCountY">Number of workgroups in Y dimension.</param>
    /// <param name="groupCountZ">Number of workgroups in Z dimension.</param>
    /// <param name="uniforms">Optional uniform data to bind at set 0, binding 0.</param>
    /// <param name="images">Optional storage image bindings (binding index → texture).</param>
    /// <param name="textures">Optional sampled texture bindings (binding index → texture + sampler).</param>
    /// <param name="storageBuffers">Optional storage buffer bindings (binding index → buffer).</param>
    public static void Dispatch(
        CommandList cmd,
        ComputeKernel kernel,
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ,
        ComputeUniforms? uniforms = null,
        (uint binding, Graphite.Texture texture)[]? images = null,
        (uint binding, Graphite.Texture texture, Sampler sampler)[]? textures = null,
        (uint binding, GBuffer buffer)[]? storageBuffers = null)
    {
        if (!kernel.IsValid || kernel.Pipeline == null)
            return;

        // Ensure storage images are in General layout for compute access.
        // Newly created textures start in Undefined; without this barrier
        // the compute dispatch accesses an invalid layout → ErrorDeviceLost.
        if (images != null)
        {
            foreach ((uint binding, Graphite.Texture texture) in images)
            {
                cmd.ResourceBarrier(new ResourceBarrier(
                    texture, ResourceState.Common, ResourceState.UnorderedAccess));
            }
        }

        // Upload uniforms
        uniforms?.Upload();

        // Set compute pipeline
        cmd.SetComputePipeline(kernel.Pipeline);

        // Build and bind a bind group if we have any resources
        if (kernel.BindGroupLayout != null && (uniforms?.GpuBuffer is not null || images != null || textures != null || storageBuffers != null))
        {
            // Estimate max entry count to avoid resizing
            int maxEntries = (uniforms?.GpuBuffer is not null ? 1 : 0)
                + (images?.Length ?? 0)
                + (textures?.Length ?? 0)
                + (storageBuffers?.Length ?? 0);

            BindGroupEntry[] pooledEntries = ArrayPool<BindGroupEntry>.Shared.Rent(Math.Max(maxEntries, 1));
            int entryCount = 0;

            try
            {
                if (uniforms?.GpuBuffer is GBuffer gpuBuf)
                {
                    // Find the correct binding index for the uniform buffer from the kernel's layout.
                    // Do not assume binding 0 — the layout may place it at any index.
                    uint uniformBinding = 0;
                    if (kernel.LayoutEntries != null)
                    {
                        foreach (BindGroupLayoutEntry layoutEntry in kernel.LayoutEntries)
                        {
                            if (layoutEntry.Type == BindingType.UniformBuffer)
                            {
                                uniformBinding = layoutEntry.Binding;
                                break;
                            }
                        }
                    }

                    pooledEntries[entryCount++] = BindGroupEntry.ForBuffer(uniformBinding, gpuBuf);
                }

                if (images != null)
                {
                    foreach ((uint binding, Graphite.Texture texture) in images)
                    {
                        pooledEntries[entryCount++] = BindGroupEntry.ForTexture(binding, texture);
                    }
                }

                if (textures != null)
                {
                    foreach ((uint binding, Graphite.Texture texture, Sampler sampler) in textures)
                    {
                        pooledEntries[entryCount++] = BindGroupEntry.ForTextureSampler(binding, texture, sampler);
                    }
                }

                if (storageBuffers != null)
                {
                    foreach ((uint binding, GBuffer buffer) in storageBuffers)
                    {
                        pooledEntries[entryCount++] = BindGroupEntry.ForBuffer(binding, buffer);
                    }
                }

                BindGroupEntry[] exactEntries = new BindGroupEntry[entryCount];
                Array.Copy(pooledEntries, exactEntries, entryCount);

                BindGroup bindGroup = Graphics.Graphite.CreateBindGroup(new BindGroupDescriptor(
                    kernel.BindGroupLayout, exactEntries));

                cmd.SetBindGroup(0, bindGroup);

                // Queue the bind group for deferred disposal so the GPU finishes
                // using the descriptor set before it is reclaimed.
                GraphiteMaterialBinder.Retire(bindGroup);
            }
            finally
            {
                ArrayPool<BindGroupEntry>.Shared.Return(pooledEntries);
            }
        }

        // Dispatch
        cmd.Dispatch(groupCountX, groupCountY, groupCountZ);

        // Memory barrier to ensure writes are visible
        cmd.MemoryBarrier();
    }

    /// <summary>
    /// Calculates the number of workgroups needed to cover a given size
    /// with the specified local workgroup size.
    /// </summary>
    public static uint WorkGroupCount(int totalSize, int localSize)
    {
        return (uint)Math.Max(1, (totalSize + localSize - 1) / localSize);
    }
}
