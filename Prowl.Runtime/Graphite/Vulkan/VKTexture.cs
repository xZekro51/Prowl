// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.Vulkan;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Vulkan implementation of a GPU texture.
/// </summary>
internal unsafe class VKTexture : Texture
{
    private readonly VKGraphiteDevice _device;
    internal Image Image { get; }
    internal ImageView ImageView { get; }
    internal DeviceMemory Memory { get; }

    // Per-mip layout tracking (simplified: tracks first layer only)
    private readonly ImageLayout[] _mipLayouts;

    internal VKTexture(VKGraphiteDevice device, in TextureDescriptor descriptor)
    {
        _device = device;
        Dimension = descriptor.Dimension;
        Width = descriptor.Width;
        Height = descriptor.Height;
        Depth = descriptor.Depth;
        MipLevels = descriptor.MipLevels;
        ArrayLayers = descriptor.ArrayLayers;
        Format = descriptor.Format;
        Usage = descriptor.Usage;
        SampleCount = descriptor.SampleCount;
        DebugName = descriptor.DebugName;

        _mipLayouts = new ImageLayout[MipLevels];
        Array.Fill(_mipLayouts, ImageLayout.Undefined);

        var vkFormat = VKFormatHelper.ToVkFormat(descriptor.Format);
        var imageType = descriptor.Dimension switch
        {
            TextureDimension.Texture1D => ImageType.Type1D,
            TextureDimension.Texture3D => ImageType.Type3D,
            _ => ImageType.Type2D,
        };

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = imageType,
            Format = vkFormat,
            Extent = new Extent3D(descriptor.Width, descriptor.Height, descriptor.Depth),
            MipLevels = descriptor.MipLevels,
            ArrayLayers = descriptor.ArrayLayers,
            Samples = VKFormatHelper.ToVkSampleCount(descriptor.SampleCount),
            Tiling = ImageTiling.Optimal,
            Usage = ToVkImageUsage(descriptor.Usage, descriptor.Format),
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        if (descriptor.Dimension == TextureDimension.TextureCube)
            imageInfo.Flags |= ImageCreateFlags.CreateCubeCompatibleBit;

        VKGraphiteDevice.Check(device.Vk.CreateImage(device.Device, &imageInfo, null, out var image));
        Image = image;

        device.Vk.GetImageMemoryRequirements(device.Device, Image, out var memReqs);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReqs.Size,
            MemoryTypeIndex = device.FindMemoryType(memReqs.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };

        VKGraphiteDevice.Check(device.Vk.AllocateMemory(device.Device, &allocInfo, null, out var memory));
        Memory = memory;

        VKGraphiteDevice.Check(device.Vk.BindImageMemory(device.Device, Image, Memory, 0));

        // Create image view
        var viewType = descriptor.Dimension switch
        {
            TextureDimension.Texture1D => descriptor.ArrayLayers > 1 ? ImageViewType.Type1DArray : ImageViewType.Type1D,
            TextureDimension.Texture3D => ImageViewType.Type3D,
            TextureDimension.TextureCube => descriptor.ArrayLayers > 6 ? ImageViewType.TypeCubeArray : ImageViewType.TypeCube,
            _ => descriptor.ArrayLayers > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D,
        };

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = viewType,
            Format = vkFormat,
            Components = new ComponentMapping
            {
                R = ComponentSwizzle.Identity,
                G = ComponentSwizzle.Identity,
                B = ComponentSwizzle.Identity,
                A = ComponentSwizzle.Identity,
            },
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = VKFormatHelper.GetAspectFlags(descriptor.Format),
                BaseMipLevel = 0,
                LevelCount = descriptor.MipLevels,
                BaseArrayLayer = 0,
                LayerCount = descriptor.ArrayLayers,
            },
        };

        VKGraphiteDevice.Check(device.Vk.CreateImageView(device.Device, &viewInfo, null, out var imageView));
        ImageView = imageView;
    }

    internal void TransitionLayout(CommandBuffer cmd, ImageLayout newLayout, uint baseMip, uint mipCount, uint baseLayer, uint layerCount)
    {
        var oldLayout = baseMip < (uint)_mipLayouts.Length ? _mipLayouts[baseMip] : ImageLayout.Undefined;

        if (oldLayout == newLayout)
            return;

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = Image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = VKFormatHelper.GetAspectFlags(Format),
                BaseMipLevel = baseMip,
                LevelCount = mipCount,
                BaseArrayLayer = baseLayer,
                LayerCount = layerCount,
            },
        };

        PipelineStageFlags srcStage;
        PipelineStageFlags dstStage;

        switch (oldLayout)
        {
            case ImageLayout.Undefined:
                barrier.SrcAccessMask = 0;
                srcStage = PipelineStageFlags.TopOfPipeBit;
                break;
            case ImageLayout.TransferDstOptimal:
                barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
                srcStage = PipelineStageFlags.TransferBit;
                break;
            case ImageLayout.TransferSrcOptimal:
                barrier.SrcAccessMask = AccessFlags.TransferReadBit;
                srcStage = PipelineStageFlags.TransferBit;
                break;
            case ImageLayout.ShaderReadOnlyOptimal:
                barrier.SrcAccessMask = AccessFlags.ShaderReadBit;
                srcStage = PipelineStageFlags.FragmentShaderBit;
                break;
            case ImageLayout.ColorAttachmentOptimal:
                barrier.SrcAccessMask = AccessFlags.ColorAttachmentWriteBit;
                srcStage = PipelineStageFlags.ColorAttachmentOutputBit;
                break;
            case ImageLayout.DepthStencilAttachmentOptimal:
                barrier.SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit;
                srcStage = PipelineStageFlags.LateFragmentTestsBit;
                break;
            default:
                barrier.SrcAccessMask = 0;
                srcStage = PipelineStageFlags.AllCommandsBit;
                break;
        }

        switch (newLayout)
        {
            case ImageLayout.TransferDstOptimal:
                barrier.DstAccessMask = AccessFlags.TransferWriteBit;
                dstStage = PipelineStageFlags.TransferBit;
                break;
            case ImageLayout.TransferSrcOptimal:
                barrier.DstAccessMask = AccessFlags.TransferReadBit;
                dstStage = PipelineStageFlags.TransferBit;
                break;
            case ImageLayout.ShaderReadOnlyOptimal:
                barrier.DstAccessMask = AccessFlags.ShaderReadBit;
                dstStage = PipelineStageFlags.FragmentShaderBit;
                break;
            case ImageLayout.ColorAttachmentOptimal:
                barrier.DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;
                dstStage = PipelineStageFlags.ColorAttachmentOutputBit;
                break;
            case ImageLayout.DepthStencilAttachmentOptimal:
                barrier.DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;
                dstStage = PipelineStageFlags.EarlyFragmentTestsBit;
                break;
            default:
                barrier.DstAccessMask = 0;
                dstStage = PipelineStageFlags.AllCommandsBit;
                break;
        }

        _device.Vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);

        // Update tracked layouts
        for (uint m = baseMip; m < baseMip + mipCount && m < (uint)_mipLayouts.Length; m++)
            _mipLayouts[m] = newLayout;
    }

    protected override void DisposeResources()
    {
        _device.Vk.DestroyImageView(_device.Device, ImageView, null);
        _device.Vk.DestroyImage(_device.Device, Image, null);
        _device.Vk.FreeMemory(_device.Device, Memory, null);
    }

    private static ImageUsageFlags ToVkImageUsage(TextureUsage usage, TextureFormat format)
    {
        var flags = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        if (usage.HasFlag(TextureUsage.Sampled)) flags |= ImageUsageFlags.SampledBit;
        if (usage.HasFlag(TextureUsage.Storage)) flags |= ImageUsageFlags.StorageBit;
        if (usage.HasFlag(TextureUsage.RenderTarget)) flags |= ImageUsageFlags.ColorAttachmentBit;
        if (usage.HasFlag(TextureUsage.DepthStencil)) flags |= ImageUsageFlags.DepthStencilAttachmentBit;
        return flags;
    }
}
