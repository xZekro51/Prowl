// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.Vulkan;

using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Vulkan implementation of a GPU buffer.
/// </summary>
internal unsafe class VKBuffer : Buffer
{
    private readonly VKGraphiteDevice _device;
    internal VkBuffer Handle { get; }
    internal DeviceMemory Memory { get; }

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

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReqs.Size,
            MemoryTypeIndex = device.FindMemoryType(memReqs.MemoryTypeBits, ToMemoryProperties(descriptor.MemoryAccess)),
        };

        VKGraphiteDevice.Check(device.Vk.AllocateMemory(device.Device, &allocInfo, null, out var memory));
        Memory = memory;

        VKGraphiteDevice.Check(device.Vk.BindBufferMemory(device.Device, Handle, Memory, 0));

        if (descriptor.InitialData.HasValue)
        {
            var span = descriptor.InitialData.Value.Span;
            if (descriptor.MemoryAccess == Graphite.MemoryAccess.GpuOnly)
            {
                // Use staging buffer via device UpdateBuffer
                var tempDesc = new BufferDescriptor((uint)span.Length, BufferUsage.CopySource, Graphite.MemoryAccess.CpuToGpu);
                using var staging = new VKBuffer(device, in tempDesc);

                void* mapped;
                VKGraphiteDevice.Check(device.Vk.MapMemory(device.Device, staging.Memory, 0, (ulong)span.Length, 0, &mapped));
                fixed (byte* src = span)
                    System.Buffer.MemoryCopy(src, mapped, span.Length, span.Length);
                device.Vk.UnmapMemory(device.Device, staging.Memory);

                var cmd = device.BeginSingleTimeCommands();
                var region = new BufferCopy { SrcOffset = 0, DstOffset = 0, Size = (ulong)span.Length };
                device.Vk.CmdCopyBuffer(cmd, staging.Handle, Handle, 1, &region);
                device.EndSingleTimeCommands(cmd);
            }
            else
            {
                void* mapped;
                VKGraphiteDevice.Check(device.Vk.MapMemory(device.Device, Memory, 0, (ulong)span.Length, 0, &mapped));
                fixed (byte* src = span)
                    System.Buffer.MemoryCopy(src, mapped, span.Length, span.Length);
                device.Vk.UnmapMemory(device.Device, Memory);
            }
        }
    }

    protected override void DisposeResources()
    {
        _device.Vk.DestroyBuffer(_device.Device, Handle, null);
        _device.Vk.FreeMemory(_device.Device, Memory, null);
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
