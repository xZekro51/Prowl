// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.Vulkan;

using VkBuffer = Silk.NET.Vulkan.Buffer;

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
            var descType = VKFormatHelper.ToVkDescriptorType(entry.Type);
            if (entry.HasDynamicOffset)
            {
                descType = descType switch
                {
                    DescriptorType.UniformBuffer => DescriptorType.UniformBufferDynamic,
                    DescriptorType.StorageBuffer => DescriptorType.StorageBufferDynamic,
                    _ => descType,
                };
            }
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = entry.Binding,
                DescriptorType = descType,
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
/// Vulkan implementation of a bind group (descriptor set).
/// Uses the device's shared <see cref="VKDescriptorPoolManager"/> instead of
/// creating a dedicated pool per bind group. Descriptor sets are pooled and
/// reset at frame boundaries, so this class does not own any pool resources.
/// </summary>
internal unsafe class VKBindGroup : BindGroup
{
    internal DescriptorSet DescriptorSet { get; }

    internal VKBindGroup(VKGraphiteDevice device, in BindGroupDescriptor descriptor)
    {
        Layout = descriptor.Layout;
        DebugName = descriptor.DebugName;

        var vkLayout = (VKBindGroupLayout)descriptor.Layout;

        // Allocate descriptor set from the shared per-frame pool manager
        DescriptorSet = device.DescriptorPoolManager.Allocate(vkLayout.Handle);

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
                    if (layoutEntry.HasDynamicOffset)
                    {
                        descType = descType switch
                        {
                            DescriptorType.UniformBuffer => DescriptorType.UniformBufferDynamic,
                            DescriptorType.StorageBuffer => DescriptorType.StorageBufferDynamic,
                            _ => descType,
                        };
                    }
                    break;
                }
            }
            writes[i].DescriptorType = descType;

            if (entry.Buffer.HasValue)
            {
                // Support both VKBuffer (regular allocations) and VKRingSubBuffer (ring buffer)
                VkBuffer vkHandle;
                if (entry.Buffer.Value.Buffer is VKBuffer vkBuf)
                    vkHandle = vkBuf.Handle;
                else if (entry.Buffer.Value.Buffer is VKRingSubBuffer ringBuf)
                    vkHandle = ringBuf.VkHandle;
                else
                    throw new InvalidOperationException($"Unexpected buffer type: {entry.Buffer.Value.Buffer.GetType().Name}");

                bufferInfos[i] = new DescriptorBufferInfo
                {
                    Buffer = vkHandle,
                    Offset = entry.Buffer.Value.Offset,
                    Range = entry.Buffer.Value.Size == 0 ? Vk.WholeSize : entry.Buffer.Value.Size,
                };
            }
            else if (entry.Texture != null && entry.Sampler != null)
            {
                var tex = (VKTexture)entry.Texture;
                var samp = (VKSampler)entry.Sampler;
                if (tex.IsDisposed)
                    throw new ObjectDisposedException(tex.DebugName ?? nameof(VKTexture),
                        $"Cannot create bind group: texture at binding {entry.Binding} has been disposed.");
                if (samp.IsDisposed)
                    throw new ObjectDisposedException(samp.DebugName ?? nameof(VKSampler),
                        $"Cannot create bind group: sampler at binding {entry.Binding} has been disposed.");
                imageInfos[i] = new DescriptorImageInfo
                {
                    Sampler = samp.Handle,
                    ImageView = tex.ImageView,
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                };
            }
            else if (entry.Texture != null)
            {
                if (descType == DescriptorType.CombinedImageSampler)
                    throw new InvalidOperationException(
                        $"Bind group entry at binding {entry.Binding} uses CombinedImageSampler but no Sampler was provided. " +
                        $"Use BindGroupEntry.ForTextureSampler() instead of BindGroupEntry.ForTexture().");

                var tex = (VKTexture)entry.Texture;
                if (tex.IsDisposed)
                    throw new ObjectDisposedException(tex.DebugName ?? nameof(VKTexture),
                        $"Cannot create bind group: texture at binding {entry.Binding} has been disposed.");
                imageInfos[i] = new DescriptorImageInfo
                {
                    ImageView = tex.ImageView,
                    ImageLayout = descType == DescriptorType.StorageImage ? ImageLayout.General : ImageLayout.ShaderReadOnlyOptimal,
                };
            }
            else if (entry.Sampler != null)
            {
                var samp = (VKSampler)entry.Sampler;
                if (samp.IsDisposed)
                    throw new ObjectDisposedException(samp.DebugName ?? nameof(VKSampler),
                        $"Cannot create bind group: sampler at binding {entry.Binding} has been disposed.");
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
        // Descriptor sets are pooled and reset at frame boundaries by VKDescriptorPoolManager.
        // No per-bind-group cleanup is needed.
    }
}
