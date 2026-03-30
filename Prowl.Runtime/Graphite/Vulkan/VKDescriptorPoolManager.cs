// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Silk.NET.Vulkan;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Manages Vulkan descriptor pools with per-frame reset.
/// Replaces the per-bind-group descriptor pool approach with shared pools
/// that can allocate many descriptor sets and are reset at frame boundaries.
/// Each frame-in-flight slot has its own chain of pools to avoid contention.
/// </summary>
internal unsafe class VKDescriptorPoolManager : IDisposable
{
    private const uint MaxSetsPerPool = 512;
    private const uint DescriptorsPerType = 1024;

    private readonly VKGraphiteDevice _device;
    private readonly List<DescriptorPool>[] _framePools;
    private readonly int[] _currentPoolIndex;
    private int _currentFrame;
    private bool _disposed;

    internal VKDescriptorPoolManager(VKGraphiteDevice device, int framesInFlight)
    {
        _device = device;
        _framePools = new List<DescriptorPool>[framesInFlight];
        _currentPoolIndex = new int[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
            _framePools[i] = new List<DescriptorPool>();
    }

    /// <summary>
    /// Resets all descriptor pools for the given frame slot.
    /// Call after the fence wait in <see cref="VKGraphiteDevice.BeginFrame"/>.
    /// </summary>
    internal void BeginFrame(int frameIndex)
    {
        _currentFrame = frameIndex;
        foreach (var pool in _framePools[_currentFrame])
            _device.Vk.ResetDescriptorPool(_device.Device, pool, 0);
        _currentPoolIndex[_currentFrame] = 0;
    }

    /// <summary>
    /// Allocates a descriptor set from the current frame's pool chain.
    /// Automatically creates new pools if existing ones are full.
    /// </summary>
    internal DescriptorSet Allocate(DescriptorSetLayout layout)
    {
        var pools = _framePools[_currentFrame];
        ref int poolIdx = ref _currentPoolIndex[_currentFrame];

        // Try to allocate from the current pool
        while (poolIdx < pools.Count)
        {
            var setLayout = layout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pools[poolIdx],
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };

            var result = _device.Vk.AllocateDescriptorSets(_device.Device, &allocInfo, out var descriptorSet);
            if (result == Result.Success)
                return descriptorSet;

            // Pool is full or fragmented, try the next one
            poolIdx++;
        }

        // Need a new pool
        var newPool = CreatePool();
        pools.Add(newPool);

        {
            var setLayout = layout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = newPool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };

            VKGraphiteDevice.Check(_device.Vk.AllocateDescriptorSets(_device.Device, &allocInfo, out var descriptorSet));
            return descriptorSet;
        }
    }

    private DescriptorPool CreatePool()
    {
        // Create a generous pool with all common descriptor types
        Span<DescriptorPoolSize> poolSizes =
        [
            new() { Type = DescriptorType.UniformBuffer, DescriptorCount = DescriptorsPerType },
            new() { Type = DescriptorType.UniformBufferDynamic, DescriptorCount = DescriptorsPerType },
            new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = DescriptorsPerType },
            new() { Type = DescriptorType.StorageBuffer, DescriptorCount = DescriptorsPerType },
            new() { Type = DescriptorType.StorageBufferDynamic, DescriptorCount = DescriptorsPerType / 4 },
            new() { Type = DescriptorType.SampledImage, DescriptorCount = DescriptorsPerType / 4 },
            new() { Type = DescriptorType.Sampler, DescriptorCount = DescriptorsPerType / 4 },
            new() { Type = DescriptorType.StorageImage, DescriptorCount = DescriptorsPerType / 4 },
        ];

        fixed (DescriptorPoolSize* pSizes = poolSizes)
        {
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = MaxSetsPerPool,
                PoolSizeCount = (uint)poolSizes.Length,
                PPoolSizes = pSizes,
            };

            VKGraphiteDevice.Check(_device.Vk.CreateDescriptorPool(_device.Device, &poolInfo, null, out var pool));
            return pool;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var framePools in _framePools)
        {
            foreach (var pool in framePools)
                _device.Vk.DestroyDescriptorPool(_device.Device, pool, null);
            framePools.Clear();
        }
    }
}
