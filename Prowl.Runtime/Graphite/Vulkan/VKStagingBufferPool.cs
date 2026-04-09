// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Silk.NET.Vulkan;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Pools staging buffers used for GPU-only uploads (<c>UpdateTexture</c>,
/// <c>UpdateBuffer</c> initial data) to avoid per-upload
/// <c>vkCreateBuffer</c> + <c>vkAllocateMemory</c> overhead.
/// <para>
/// Buffers are bucketed by power-of-two size. Returned buffers are placed
/// in a pending list until the current frame's fence signals, at which point
/// they are moved back to the available pool for reuse.
/// </para>
/// </summary>
internal sealed class VKStagingBufferPool : IDisposable
{
    // Minimum bucket: 256 bytes, maximum bucket: 16 MB
    private const int MinBucketShift = 8;   // 2^8  = 256
    private const int MaxBucketShift = 24;  // 2^24 = 16 MB
    private const int BucketCount = MaxBucketShift - MinBucketShift + 1;

    private readonly VKGraphiteDevice _device;
    private readonly int _framesInFlight;

    // Available buffers per size bucket (ready for immediate reuse).
    private readonly List<VKBuffer>[] _available;

    // Buffers that are in-flight, keyed by frame slot.
    // Once the fence for that slot signals, these move to _available.
    private readonly List<VKBuffer>[] _pending;

    private bool _disposed;

    internal VKStagingBufferPool(VKGraphiteDevice device, int framesInFlight)
    {
        _device = device;
        _framesInFlight = framesInFlight;

        _available = new List<VKBuffer>[BucketCount];
        for (int i = 0; i < BucketCount; i++)
            _available[i] = new List<VKBuffer>();

        _pending = new List<VKBuffer>[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
            _pending[i] = new List<VKBuffer>();
    }

    /// <summary>
    /// Call at the start of each frame (after the fence wait) to recycle
    /// staging buffers from the completed frame slot.
    /// </summary>
    internal void BeginFrame(int frameIndex)
    {
        List<VKBuffer> completed = _pending[frameIndex];
        foreach (VKBuffer buf in completed)
        {
            int bucket = GetBucket(buf.SizeInBytes);
            _available[bucket].Add(buf);
        }
        completed.Clear();
    }

    /// <summary>
    /// Rents a staging buffer of at least <paramref name="sizeInBytes"/>.
    /// The buffer is <c>CpuToGpu</c> with <c>CopySource</c> usage.
    /// </summary>
    internal VKBuffer Rent(uint sizeInBytes)
    {
        int bucket = GetBucket(sizeInBytes);
        List<VKBuffer> pool = _available[bucket];

        if (pool.Count > 0)
        {
            VKBuffer buf = pool[pool.Count - 1];
            // For the max bucket, pooled buffers may be smaller than the
            // requested size (oversized allocations land in the last bucket).
            if (buf.SizeInBytes >= sizeInBytes)
            {
                pool.RemoveAt(pool.Count - 1);
                return buf;
            }
        }

        // No available buffer (or pooled buffer too small) — create a new
        // one at the larger of the bucket size or the actual requested size.
        uint allocSize = Math.Max(BucketSize(bucket), sizeInBytes);
        BufferDescriptor desc = new(allocSize, BufferUsage.CopySource, MemoryAccess.CpuToGpu);
        return new VKBuffer(_device, in desc);
    }

    /// <summary>
    /// Returns a staging buffer to the pool. It will become available again
    /// once the fence for <paramref name="frameIndex"/> signals.
    /// </summary>
    internal void Return(VKBuffer buffer, int frameIndex)
    {
        _pending[frameIndex].Add(buffer);
    }

    /// <summary>
    /// Returns the bucket index for a given size, rounding up to the next
    /// power-of-two bucket.
    /// </summary>
    private static int GetBucket(uint size)
    {
        // Clamp to minimum bucket
        if (size <= (1u << MinBucketShift))
            return 0;

        // Find the bit position of the highest set bit, rounding up.
        int bits = 32 - int.LeadingZeroCount((int)(size - 1));
        int bucket = bits - MinBucketShift;
        return Math.Min(bucket, BucketCount - 1);
    }

    private static uint BucketSize(int bucket) => 1u << (bucket + MinBucketShift);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (int i = 0; i < BucketCount; i++)
        {
            foreach (VKBuffer buf in _available[i])
                buf.Dispose();
            _available[i].Clear();
        }

        for (int i = 0; i < _framesInFlight; i++)
        {
            foreach (VKBuffer buf in _pending[i])
                buf.Dispose();
            _pending[i].Clear();
        }
    }
}
