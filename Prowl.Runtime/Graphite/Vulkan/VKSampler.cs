// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Silk.NET.Vulkan;

using VkSampler = Silk.NET.Vulkan.Sampler;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Vulkan implementation of a sampler.
/// </summary>
internal unsafe class VKSampler : Graphite.Sampler
{
    private readonly VKGraphiteDevice _device;
    internal VkSampler Handle { get; }

    /// <summary>
    /// When true, this sampler is owned by the device's sampler cache and
    /// <see cref="DisposeResources"/> will skip Vulkan handle destruction.
    /// The device itself destroys the handle during its own disposal.
    /// </summary>
    internal bool IsCached { get; set; }

    /// <inheritdoc/>
    protected override bool IsDisposeSuppressed => IsCached;

    internal VKSampler(VKGraphiteDevice device, in SamplerDescriptor descriptor)
    {
        _device = device;
        MinFilter = descriptor.MinFilter;
        MagFilter = descriptor.MagFilter;
        MipmapFilter = descriptor.MipmapFilter;
        AddressModeU = descriptor.AddressModeU;
        AddressModeV = descriptor.AddressModeV;
        AddressModeW = descriptor.AddressModeW;
        MaxAnisotropy = descriptor.MaxAnisotropy;
        CompareFunction = descriptor.CompareFunction;
        DebugName = descriptor.DebugName;

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MinFilter = VKFormatHelper.ToVkFilter(descriptor.MinFilter),
            MagFilter = VKFormatHelper.ToVkFilter(descriptor.MagFilter),
            MipmapMode = VKFormatHelper.ToVkMipmapMode(descriptor.MipmapFilter),
            AddressModeU = VKFormatHelper.ToVkAddressMode(descriptor.AddressModeU),
            AddressModeV = VKFormatHelper.ToVkAddressMode(descriptor.AddressModeV),
            AddressModeW = VKFormatHelper.ToVkAddressMode(descriptor.AddressModeW),
            MipLodBias = descriptor.MipLodBias,
            AnisotropyEnable = descriptor.MaxAnisotropy > 1.0f,
            MaxAnisotropy = descriptor.MaxAnisotropy,
            CompareEnable = descriptor.CompareFunction.HasValue,
            CompareOp = descriptor.CompareFunction.HasValue ? VKFormatHelper.ToVkCompareOp(descriptor.CompareFunction.Value) : CompareOp.Always,
            MinLod = descriptor.MinLod,
            MaxLod = descriptor.MaxLod,
            BorderColor = VKFormatHelper.ToVkBorderColor(descriptor.BorderColor),
            UnnormalizedCoordinates = false,
        };

        VKGraphiteDevice.Check(device.Vk.CreateSampler(device.Device, &samplerInfo, null, out var sampler));
        Handle = sampler;
    }

    protected override void DisposeResources()
    {
        if (IsCached)
            return; // Cache owns the handle — destroyed in VKGraphiteDevice.DisposeResources
        _device.Vk.DestroySampler(_device.Device, Handle, null);
    }

    /// <summary>
    /// Unconditionally destroys the Vulkan sampler handle.
    /// Called by the device's sampler cache during device disposal.
    /// </summary>
    internal void DestroyHandle()
    {
        _device.Vk.DestroySampler(_device.Device, Handle, null);
    }
}
