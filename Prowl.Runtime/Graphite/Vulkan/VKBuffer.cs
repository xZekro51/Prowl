// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.Vulkan;

using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Vulkan implementation of a GPU buffer.
/// Uses the device's <see cref="VKMemoryAllocator"/> for sub-allocated memory,
/// avoiding the ~4096 <c>vkAllocateMemory</c> driver limit.
/// </summary>
internal unsafe class VKBuffer : Buffer
{
    private readonly VKGraphiteDevice _device;
    internal VkBuffer Handle { get; }
    internal VKAllocation Allocation { get; }

    internal VKBuffer(VKGraphiteDevice device, in BufferDescriptor descriptor)
    {
        _device = device;
        SizeInBytes = descriptor.SizeInBytes;
        Usage = descriptor.Usage;
        MemoryAccess = descriptor.MemoryAccess;
        DebugName = descriptor.DebugName;

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = descriptor.SizeInBytes,
            Usage = ToVkBufferUsage(descriptor.Usage),
            SharingMode = SharingMode.Exclusive,
        };

        VKGraphiteDevice.Check(device.Vk.CreateBuffer(device.Device, &bufferInfo, null, out var buffer));
        Handle = buffer;

        device.Vk.GetBufferMemoryRequirements(device.Device, Handle, out var memReqs);

        // Sub-allocate from the shared memory allocator
        Allocation = device.MemoryAllocator.Allocate(memReqs, ToMemoryProperties(descriptor.MemoryAccess));
        VKGraphiteDevice.Check(device.Vk.BindBufferMemory(device.Device, Handle, Allocation.Memory, Allocation.Offset));

        // Set debug name via VK_EXT_debug_utils for GPU debugger visibility
        device.SetDebugName(ObjectType.Buffer, Handle.Handle, descriptor.DebugName);

        if (descriptor.InitialData.HasValue)
        {
            var span = descriptor.InitialData.Value.Span;
            if (descriptor.MemoryAccess == Graphite.MemoryAccess.GpuOnly)
            {
                // Use staging buffer for GPU-only memory
                var tempDesc = new BufferDescriptor((uint)span.Length, BufferUsage.CopySource, Graphite.MemoryAccess.CpuToGpu);
                var staging = new VKBuffer(device, in tempDesc);

                var mappedPtr = staging.Allocation.GetMappedData();
                fixed (byte* src = span)
                    System.Buffer.MemoryCopy(src, mappedPtr, span.Length, span.Length);

                var cmd = device.BeginSingleTimeCommands();
                var region = new BufferCopy { SrcOffset = 0, DstOffset = 0, Size = (ulong)span.Length };
                device.Vk.CmdCopyBuffer(cmd, staging.Handle, Handle, 1, &region);
                device.EndSingleTimeCommands(cmd);

                if (device.IsUploadBatching)
                {
                    device.TrackBatchResource(staging);
                    device.IncrementBatchUploadCounters(span.Length);
                }
                else
                    staging.Dispose();
            }
            else
            {
                // Host-visible: write directly to the persistently mapped sub-allocation
                var mappedPtr = Allocation.GetMappedData();
                fixed (byte* src = span)
                    System.Buffer.MemoryCopy(src, mappedPtr, span.Length, span.Length);
            }
        }
    }

    protected override void DisposeResources()
    {
        _device.Vk.DestroyBuffer(_device.Device, Handle, null);
        var alloc = Allocation;
        _device.MemoryAllocator.Free(in alloc);
    }

    private static BufferUsageFlags ToVkBufferUsage(BufferUsage usage)
    {
        var flags = BufferUsageFlags.None;
        if (usage.HasFlag(BufferUsage.Vertex)) flags |= BufferUsageFlags.VertexBufferBit;
        if (usage.HasFlag(BufferUsage.Index)) flags |= BufferUsageFlags.IndexBufferBit;
        if (usage.HasFlag(BufferUsage.Uniform)) flags |= BufferUsageFlags.UniformBufferBit;
        if (usage.HasFlag(BufferUsage.Storage)) flags |= BufferUsageFlags.StorageBufferBit;
        if (usage.HasFlag(BufferUsage.Indirect)) flags |= BufferUsageFlags.IndirectBufferBit;
        if (usage.HasFlag(BufferUsage.CopySource)) flags |= BufferUsageFlags.TransferSrcBit;
        if (usage.HasFlag(BufferUsage.CopyDestination)) flags |= BufferUsageFlags.TransferDstBit;
        return flags;
    }

    private static MemoryPropertyFlags ToMemoryProperties(Graphite.MemoryAccess access) => access switch
    {
        Graphite.MemoryAccess.GpuOnly => MemoryPropertyFlags.DeviceLocalBit,
        Graphite.MemoryAccess.CpuToGpu => MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
        Graphite.MemoryAccess.GpuToCpu => MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit,
        _ => MemoryPropertyFlags.DeviceLocalBit,
    };
}
