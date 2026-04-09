// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Silk.NET.Vulkan;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Represents a sub-allocation from a larger Vulkan memory block.
/// </summary>
internal struct VKAllocation
{
    public DeviceMemory Memory;
    public ulong Offset;
    public ulong Size;
    public nint MappedPtr;
    internal int BlockIndex;
    internal int MemoryTypeIndex;
    internal bool IsDedicated;

    /// <summary>
    /// Returns a pointer to the mapped memory for this sub-allocation.
    /// Only valid for host-visible memory that has been persistently mapped.
    /// </summary>
    public readonly unsafe void* GetMappedData() => (void*)MappedPtr;
}

/// <summary>
/// Simple block-based Vulkan memory sub-allocator.
/// Allocates large memory blocks per memory type and sub-divides them,
/// avoiding the ~4096 allocation limit imposed by most Vulkan implementations.
/// Large allocations (>= <see cref="DedicatedThreshold"/>) use dedicated
/// <c>vkAllocateMemory</c> calls.
/// </summary>
internal unsafe class VKMemoryAllocator : IDisposable
{
    private const ulong DefaultBlockSize = 64 * 1024 * 1024; // 64 MB
    private const ulong DedicatedThreshold = 16 * 1024 * 1024; // 16 MB

    private readonly VKGraphiteDevice _device;
    private readonly List<MemoryPool> _pools = new();
    private bool _disposed;

    internal VKMemoryAllocator(VKGraphiteDevice device)
    {
        _device = device;
        // Pre-allocate pool slots for each memory type
        for (int i = 0; i < device.MemoryProperties.MemoryTypeCount; i++)
            _pools.Add(new MemoryPool());
    }

    /// <summary>
    /// Allocates GPU memory satisfying the given requirements and properties.
    /// Returns a sub-allocation from a shared block or a dedicated allocation
    /// for large resources.
    /// </summary>
    internal VKAllocation Allocate(MemoryRequirements memReqs, MemoryPropertyFlags properties)
    {
        uint memoryTypeIndex = _device.FindMemoryType(memReqs.MemoryTypeBits, properties);
        ulong size = memReqs.Size;
        ulong alignment = memReqs.Alignment;

        // Large allocations get their own dedicated memory
        if (size >= DedicatedThreshold)
        {
            return AllocateDedicated(memoryTypeIndex, size, properties);
        }

        var pool = _pools[(int)memoryTypeIndex];

        // Try to allocate from existing blocks
        for (int i = 0; i < pool.Blocks.Count; i++)
        {
            var block = pool.Blocks[i];
            if (TryAllocateFromBlock(block, size, alignment, out ulong offset))
            {
                return new VKAllocation
                {
                    Memory = block.Memory,
                    Offset = offset,
                    Size = size,
                    MappedPtr = block.MappedPtr != null ? (nint)(block.MappedPtr + offset) : 0,
                    BlockIndex = i,
                    MemoryTypeIndex = (int)memoryTypeIndex,
                    IsDedicated = false,
                };
            }
        }

        // No existing block has space — allocate a new block
        ulong blockSize = Math.Max(DefaultBlockSize, size * 2);
        bool isHostVisible = (properties & MemoryPropertyFlags.HostVisibleBit) != 0;
        var newBlock = AllocateBlock(memoryTypeIndex, blockSize, isHostVisible);
        pool.Blocks.Add(newBlock);

        if (TryAllocateFromBlock(newBlock, size, alignment, out ulong newOffset))
        {
            return new VKAllocation
            {
                Memory = newBlock.Memory,
                Offset = newOffset,
                Size = size,
                MappedPtr = newBlock.MappedPtr != null ? (nint)(newBlock.MappedPtr + newOffset) : 0,
                BlockIndex = pool.Blocks.Count - 1,
                MemoryTypeIndex = (int)memoryTypeIndex,
                IsDedicated = false,
            };
        }

        throw new InvalidOperationException("Failed to sub-allocate from newly created memory block.");
    }

    /// <summary>
    /// Frees a previously allocated region, returning it to the block's free list.
    /// </summary>
    internal void Free(in VKAllocation allocation)
    {
        if (allocation.IsDedicated)
        {
            if (allocation.MappedPtr != 0)
                _device.Vk.UnmapMemory(_device.Device, allocation.Memory);
            _device.Vk.FreeMemory(_device.Device, allocation.Memory, null);
            return;
        }

        var pool = _pools[allocation.MemoryTypeIndex];
        if (allocation.BlockIndex >= 0 && allocation.BlockIndex < pool.Blocks.Count)
        {
            var block = pool.Blocks[allocation.BlockIndex];
            FreeFromBlock(block, allocation.Offset, allocation.Size);
        }
    }

    private VKAllocation AllocateDedicated(uint memoryTypeIndex, ulong size, MemoryPropertyFlags properties)
    {
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = size,
            MemoryTypeIndex = memoryTypeIndex,
        };

        VKGraphiteDevice.Check(_device.Vk.AllocateMemory(_device.Device, &allocInfo, null, out var memory));

        nint mappedPtr = 0;
        bool isHostVisible = (properties & MemoryPropertyFlags.HostVisibleBit) != 0;
        if (isHostVisible)
        {
            void* ptr;
            VKGraphiteDevice.Check(_device.Vk.MapMemory(_device.Device, memory, 0, size, 0, &ptr));
            mappedPtr = (nint)ptr;
        }

        return new VKAllocation
        {
            Memory = memory,
            Offset = 0,
            Size = size,
            MappedPtr = mappedPtr,
            BlockIndex = -1,
            MemoryTypeIndex = (int)memoryTypeIndex,
            IsDedicated = true,
        };
    }

    private MemoryBlock AllocateBlock(uint memoryTypeIndex, ulong blockSize, bool persistentMap)
    {
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = blockSize,
            MemoryTypeIndex = memoryTypeIndex,
        };

        VKGraphiteDevice.Check(_device.Vk.AllocateMemory(_device.Device, &allocInfo, null, out var memory));

        byte* mappedPtr = null;
        if (persistentMap)
        {
            void* ptr;
            VKGraphiteDevice.Check(_device.Vk.MapMemory(_device.Device, memory, 0, blockSize, 0, &ptr));
            mappedPtr = (byte*)ptr;
        }

        return new MemoryBlock
        {
            Memory = memory,
            Size = blockSize,
            MappedPtr = mappedPtr,
            FreeList = new SortedList<ulong, ulong> { { 0, blockSize } },
        };
    }

    private static bool TryAllocateFromBlock(MemoryBlock block, ulong size, ulong alignment, out ulong offset)
    {
        offset = 0;
        var freeList = block.FreeList;

        for (int i = 0; i < freeList.Count; i++)
        {
            ulong freeOffset = freeList.Keys[i];
            ulong freeSize = freeList.Values[i];

            // Align the offset
            ulong alignedOffset = (freeOffset + alignment - 1) & ~(alignment - 1);
            ulong alignmentPadding = alignedOffset - freeOffset;

            if (freeSize >= alignmentPadding + size)
            {
                offset = alignedOffset;

                // Remove the old free region
                freeList.RemoveAt(i);

                // Add back the padding region (if any)
                if (alignmentPadding > 0)
                    freeList.Add(freeOffset, alignmentPadding);

                // Add back the remainder (if any)
                ulong remainder = freeSize - alignmentPadding - size;
                if (remainder > 0)
                    freeList.Add(alignedOffset + size, remainder);

                return true;
            }
        }

        return false;
    }

    private static void FreeFromBlock(MemoryBlock block, ulong offset, ulong size)
    {
        var freeList = block.FreeList;

        ulong freeStart = offset;
        ulong freeEnd = offset + size;

        // Coalesce with adjacent free regions without allocating a List<ulong>.
        // At most two neighbors: the region ending at freeStart (left) and
        // the region starting at freeEnd (right).
        ulong mergedStart = freeStart;
        ulong mergedEnd = freeEnd;

        // Right neighbor: O(log n) lookup — a region starting exactly at freeEnd
        if (freeList.TryGetValue(freeEnd, out ulong rightSize))
        {
            mergedEnd = freeEnd + rightSize;
            freeList.Remove(freeEnd);
        }

        // Left neighbor: find the largest key < freeStart via binary search on
        // the sorted keys, then check if that region ends exactly at freeStart.
        int insertIdx = BinarySearchInsertionIndex(freeList, freeStart);
        if (insertIdx > 0)
        {
            ulong prevKey = freeList.Keys[insertIdx - 1];
            ulong prevSize = freeList.Values[insertIdx - 1];
            if (prevKey + prevSize == freeStart)
            {
                mergedStart = prevKey;
                freeList.RemoveAt(insertIdx - 1);
            }
        }

        freeList.Add(mergedStart, mergedEnd - mergedStart);
    }

    /// <summary>
    /// Binary search for the insertion index of <paramref name="key"/> in a
    /// <see cref="SortedList{TKey,TValue}"/>'s keys. Returns the index of the
    /// first key greater than or equal to <paramref name="key"/>.
    /// </summary>
    private static int BinarySearchInsertionIndex(SortedList<ulong, ulong> list, ulong key)
    {
        int lo = 0;
        int hi = list.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (list.Keys[mid] < key)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return lo;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var pool in _pools)
        {
            foreach (var block in pool.Blocks)
            {
                if (block.MappedPtr != null)
                    _device.Vk.UnmapMemory(_device.Device, block.Memory);
                _device.Vk.FreeMemory(_device.Device, block.Memory, null);
            }
            pool.Blocks.Clear();
        }
    }

    private class MemoryPool
    {
        public readonly List<MemoryBlock> Blocks = new();
    }

    private class MemoryBlock
    {
        public DeviceMemory Memory;
        public ulong Size;
        public byte* MappedPtr;
        public SortedList<ulong, ulong> FreeList = new();
    }
}
