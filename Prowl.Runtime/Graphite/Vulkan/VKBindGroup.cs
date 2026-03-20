// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.Vulkan;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Vulkan implementation of a bind group layout (descriptor set layout).
/// </summary>
internal unsafe class VKBindGroupLayout : BindGroupLayout
{
    private readonly VKGraphiteDevice _device;
    internal DescriptorSetLayout Handle { get; }
    internal BindGroupLayoutEntry[] Entries { get; }

    internal VKBindGroupLayout(VKGraphiteDevice device, in BindGroupLayoutDescriptor descriptor)
    {
        _device = device;
        Entries = descriptor.Entries;
        DebugName = descriptor.DebugName;

        var bindings = stackalloc DescriptorSetLayoutBinding[descriptor.Entries.Length];
        for (int i = 0; i < descriptor.Entries.Length; i++)
        {
            ref readonly var entry = ref descriptor.Entries[i];
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = entry.Binding,
                DescriptorType = VKFormatHelper.ToVkDescriptorType(entry.Type),
                DescriptorCount = entry.Count,
                StageFlags = VKFormatHelper.ToVkShaderStageFlags(entry.Visibility),
                PImmutableSamplers = null,
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = (uint)descriptor.Entries.Length,
            PBindings = bindings,
        };

        VKGraphiteDevice.Check(device.Vk.CreateDescriptorSetLayout(device.Device, &layoutInfo, null, out var layout));
        Handle = layout;
    }

    protected override void DisposeResources()
    {
        _device.Vk.DestroyDescriptorSetLayout(_device.Device, Handle, null);
    }
}

/// <summary>
/// Vulkan implementation of a bind group (descriptor set + pool).
/// Each bind group owns its own descriptor pool for simplicity.
/// </summary>
internal unsafe class VKBindGroup : BindGroup
{
    private readonly VKGraphiteDevice _device;
    internal DescriptorSet DescriptorSet { get; }
    private readonly DescriptorPool _pool;

    internal VKBindGroup(VKGraphiteDevice device, in BindGroupDescriptor descriptor)
    {
        _device = device;
        Layout = descriptor.Layout;
        DebugName = descriptor.DebugName;

        var vkLayout = (VKBindGroupLayout)descriptor.Layout;

        // Calculate pool sizes from layout entries
        Span<DescriptorPoolSize> poolSizes = stackalloc DescriptorPoolSize[descriptor.Entries.Length];
        int poolSizeCount = 0;

        foreach (var entry in vkLayout.Entries)
        {
            var descType = VKFormatHelper.ToVkDescriptorType(entry.Type);
            bool found = false;
            for (int i = 0; i < poolSizeCount; i++)
            {
                if (poolSizes[i].Type == descType)
                {
                    poolSizes[i].DescriptorCount += entry.Count;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                poolSizes[poolSizeCount] = new DescriptorPoolSize { Type = descType, DescriptorCount = entry.Count };
                poolSizeCount++;
            }
        }

        fixed (DescriptorPoolSize* pPoolSizes = poolSizes)
        {
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = (uint)poolSizeCount,
                PPoolSizes = pPoolSizes,
            };

            VKGraphiteDevice.Check(device.Vk.CreateDescriptorPool(device.Device, &poolInfo, null, out _pool));
        }

        var setLayout = vkLayout.Handle;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };

        VKGraphiteDevice.Check(device.Vk.AllocateDescriptorSets(device.Device, &allocInfo, out var descriptorSet));
        DescriptorSet = descriptorSet;

        // Write descriptor bindings
        var writes = new WriteDescriptorSet[descriptor.Entries.Length];
        // Keep native structs alive on stack
        var bufferInfos = new DescriptorBufferInfo[descriptor.Entries.Length];
        var imageInfos = new DescriptorImageInfo[descriptor.Entries.Length];

        for (int i = 0; i < descriptor.Entries.Length; i++)
        {
            ref readonly var entry = ref descriptor.Entries[i];
            writes[i].SType = StructureType.WriteDescriptorSet;
            writes[i].DstSet = DescriptorSet;
            writes[i].DstBinding = entry.Binding;
            writes[i].DstArrayElement = 0;
            writes[i].DescriptorCount = 1;

            // Find the matching layout entry to determine descriptor type
            DescriptorType descType = DescriptorType.UniformBuffer;
            foreach (var layoutEntry in vkLayout.Entries)
            {
                if (layoutEntry.Binding == entry.Binding)
                {
                    descType = VKFormatHelper.ToVkDescriptorType(layoutEntry.Type);
                    break;
                }
            }
            writes[i].DescriptorType = descType;

            if (entry.Buffer.HasValue)
            {
                var buf = (VKBuffer)entry.Buffer.Value.Buffer;
                bufferInfos[i] = new DescriptorBufferInfo
                {
                    Buffer = buf.Handle,
                    Offset = entry.Buffer.Value.Offset,
                    Range = entry.Buffer.Value.Size == 0 ? Vk.WholeSize : entry.Buffer.Value.Size,
                };
            }
            else if (entry.Texture != null && entry.Sampler != null)
            {
                var tex = (VKTexture)entry.Texture;
                var samp = (VKSampler)entry.Sampler;
                imageInfos[i] = new DescriptorImageInfo
                {
                    Sampler = samp.Handle,
                    ImageView = tex.ImageView,
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                };
            }
            else if (entry.Texture != null)
            {
                var tex = (VKTexture)entry.Texture;
                imageInfos[i] = new DescriptorImageInfo
                {
                    ImageView = tex.ImageView,
                    ImageLayout = descType == DescriptorType.StorageImage ? ImageLayout.General : ImageLayout.ShaderReadOnlyOptimal,
                };
            }
            else if (entry.Sampler != null)
            {
                var samp = (VKSampler)entry.Sampler;
                imageInfos[i] = new DescriptorImageInfo
                {
                    Sampler = samp.Handle,
                };
            }
        }

        // Pin and set pointers
        fixed (DescriptorBufferInfo* pBufferInfos = bufferInfos)
        fixed (DescriptorImageInfo* pImageInfos = imageInfos)
        fixed (WriteDescriptorSet* pWrites = writes)
        {
            for (int i = 0; i < descriptor.Entries.Length; i++)
            {
                ref readonly var entry = ref descriptor.Entries[i];
                if (entry.Buffer.HasValue)
                    pWrites[i].PBufferInfo = &pBufferInfos[i];
                else
                    pWrites[i].PImageInfo = &pImageInfos[i];
            }

            device.Vk.UpdateDescriptorSets(device.Device, (uint)writes.Length, pWrites, 0, null);
        }
    }

    protected override void DisposeResources()
    {
        _device.Vk.DestroyDescriptorPool(_device.Device, _pool, null);
    }
}
