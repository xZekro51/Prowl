// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Silk.NET.Vulkan;

using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Per-frame ring buffer for per-draw uniform data.
/// Allocates a large persistent-mapped buffer per frame-in-flight slot and
/// bump-allocates per-draw UBO sub-regions from it, eliminating per-draw
/// <c>vkCreateBuffer</c> + <c>vkAllocateMemory</c> overhead.
/// </summary>
internal unsafe class VKUniformRingBuffer : IDisposable
{
    private const uint DefaultBufferSize = 4 * 1024 * 1024; // 4 MB per frame

    private readonly VKGraphiteDevice _device;
    private FrameBuffer[] _frameBuffers;
    private int _currentFrame;
    private bool _disposed;

    // Minimum UBO offset alignment (from device limits)
    private readonly uint _minAlignment;

    // Old buffers that were replaced by GrowFrameBuffer mid-frame.
    // Descriptor sets already written this frame still reference these handles,
    // so they must stay alive until the GPU finishes the frame (i.e. until the
    // next BeginFrame call for this slot, which waits on the in-flight fence).
    private readonly List<(VkBuffer Handle, DeviceMemory Memory)>[] _retiredBuffers;

    internal VKUniformRingBuffer(VKGraphiteDevice device, int framesInFlight, uint bufferSize = DefaultBufferSize)
    {
        _device = device;
        _frameBuffers = new FrameBuffer[framesInFlight];

        device.Vk.GetPhysicalDeviceProperties(device.PhysicalDevice, out var props);
        _minAlignment = (uint)props.Limits.MinUniformBufferOffsetAlignment;
        if (_minAlignment == 0) _minAlignment = 256; // safe default

        _retiredBuffers = new List<(VkBuffer, DeviceMemory)>[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            _frameBuffers[i] = CreateFrameBuffer(bufferSize);
            _retiredBuffers[i] = new List<(VkBuffer, DeviceMemory)>();
        }
    }

    /// <summary>
    /// Resets the offset for the given frame slot.
    /// Call after the fence wait in <see cref="VKGraphiteDevice.BeginFrame"/>.
    /// </summary>
    internal void BeginFrame(int frameIndex)
    {
        _currentFrame = frameIndex;
        _frameBuffers[_currentFrame].Offset = 0;

        // Destroy any retired buffers from the previous use of this slot.
        // The fence wait in BeginFrame guarantees the GPU is done with them.
        FlushRetiredBuffers(_currentFrame);
    }

    /// <summary>
    /// Allocates a sub-region from the ring buffer and copies data into it.
    /// Returns the <see cref="VKRingSubBuffer"/> wrapper and byte offset for descriptor binding.
    /// </summary>
    internal (VKRingSubBuffer Buffer, uint Offset) Allocate(ReadOnlySpan<byte> data)
    {
        ref var fb = ref _frameBuffers[_currentFrame];
        uint size = (uint)data.Length;

        // Align offset to minUniformBufferOffsetAlignment
        uint alignedOffset = (fb.Offset + _minAlignment - 1) & ~(_minAlignment - 1);

        if (alignedOffset + size > fb.Size)
        {
            // Buffer is full — grow it
            GrowFrameBuffer(ref fb, alignedOffset + size);
            // New buffer starts at offset 0 — re-align from the beginning
            alignedOffset = 0;
        }

        // Copy data into mapped memory
        fixed (byte* src = data)
        {
            System.Buffer.MemoryCopy(src, fb.MappedPtr + alignedOffset, fb.Size - alignedOffset, size);
        }

        fb.Offset = alignedOffset + size;
        return (fb.Wrapper, alignedOffset);
    }

    private FrameBuffer CreateFrameBuffer(uint size)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.UniformBufferBit | BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };

        VKGraphiteDevice.Check(_device.Vk.CreateBuffer(_device.Device, &bufferInfo, null, out var buffer));
        _device.Vk.GetBufferMemoryRequirements(_device.Device, buffer, out var memReqs);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReqs.Size,
            MemoryTypeIndex = _device.FindMemoryType(memReqs.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
        };

        VKGraphiteDevice.Check(_device.Vk.AllocateMemory(_device.Device, &allocInfo, null, out var memory));
        VKGraphiteDevice.Check(_device.Vk.BindBufferMemory(_device.Device, buffer, memory, 0));

        // Persistently map
        void* mapped;
        VKGraphiteDevice.Check(_device.Vk.MapMemory(_device.Device, memory, 0, size, 0, &mapped));

        _device.SetDebugName(ObjectType.Buffer, buffer.Handle, $"UBO RingBuffer ({size / 1024}KB)");

        var wrapper = new VKRingSubBuffer(buffer, size);
        return new FrameBuffer
        {
            Handle = buffer,
            Memory = memory,
            MappedPtr = (byte*)mapped,
            Size = size,
            Offset = 0,
            Wrapper = wrapper,
        };
    }

    private void GrowFrameBuffer(ref FrameBuffer fb, uint requiredSize)
    {
        uint newSize = Math.Max(fb.Size * 2, requiredSize);

        // Retire the old buffer instead of destroying it immediately.
        // Descriptor sets written earlier this frame still reference the old
        // VkBuffer handle; destroying it now would cause ErrorDeviceLost.
        // The old buffer will be destroyed in the next BeginFrame for this slot,
        // after the in-flight fence guarantees the GPU is finished.
        _device.Vk.UnmapMemory(_device.Device, fb.Memory);
        _retiredBuffers[_currentFrame].Add((fb.Handle, fb.Memory));

        fb = CreateFrameBuffer(newSize);
        Debug.LogWarning($"[Vulkan] UBO ring buffer grew to {newSize / 1024}KB");
    }

    private void FlushRetiredBuffers(int frameIndex)
    {
        var retired = _retiredBuffers[frameIndex];
        foreach (var (handle, memory) in retired)
        {
            _device.Vk.DestroyBuffer(_device.Device, handle, null);
            _device.Vk.FreeMemory(_device.Device, memory, null);
        }
        retired.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Flush any retired buffers still pending
        for (int i = 0; i < _retiredBuffers.Length; i++)
            FlushRetiredBuffers(i);

        foreach (ref var fb in _frameBuffers.AsSpan())
        {
            if (fb.Handle.Handle != 0)
            {
                _device.Vk.UnmapMemory(_device.Device, fb.Memory);
                _device.Vk.DestroyBuffer(_device.Device, fb.Handle, null);
                _device.Vk.FreeMemory(_device.Device, fb.Memory, null);
            }
        }
    }

    private struct FrameBuffer
    {
        public VkBuffer Handle;
        public DeviceMemory Memory;
        public byte* MappedPtr;
        public uint Size;
        public uint Offset;
        public VKRingSubBuffer Wrapper;
    }
}

/// <summary>
/// Lightweight <see cref="Buffer"/> wrapper that exposes a <see cref="VkBuffer"/> handle
/// for use in <see cref="BindGroupEntry"/> construction.
/// Does NOT own the underlying Vulkan resource — the <see cref="VKUniformRingBuffer"/>
/// manages its lifetime. Dispose is a safe no-op.
/// </summary>
internal class VKRingSubBuffer : Buffer
{
    internal VkBuffer VkHandle { get; private set; }

    internal VKRingSubBuffer(VkBuffer handle, uint size)
    {
        VkHandle = handle;
        SizeInBytes = size;
        Usage = BufferUsage.Uniform;
        MemoryAccess = Graphite.MemoryAccess.CpuToGpu;
        DebugName = "UBO RingBuffer";
    }

    internal void Update(VkBuffer handle, uint size)
    {
        VkHandle = handle;
        SizeInBytes = size;
    }

    protected override void DisposeResources()
    {
        // Ring buffer manages the actual Vulkan resource — nothing to do here.
    }
}
