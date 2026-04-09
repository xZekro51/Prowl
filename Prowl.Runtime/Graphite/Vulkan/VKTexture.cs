// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.Vulkan;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Vulkan implementation of a GPU texture.
/// Uses the device's <see cref="VKMemoryAllocator"/> for sub-allocated memory.
/// </summary>
internal unsafe class VKTexture : Texture
{
    private readonly VKGraphiteDevice _device;
    internal Image Image { get; }
    internal ImageView ImageView { get; }
    internal VKAllocation Allocation { get; }

    // Per-mip-per-layer layout tracking for correct transitions on
    // array textures, cubemaps, and any texture with multiple layers.
    // Index as _mipLayerLayouts[mip * ArrayLayers + layer].
    private readonly ImageLayout[] _mipLayerLayouts;

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

        _mipLayerLayouts = new ImageLayout[MipLevels * ArrayLayers];
        Array.Fill(_mipLayerLayouts, ImageLayout.Undefined);

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

        // Sub-allocate from the shared memory allocator
        Allocation = device.MemoryAllocator.Allocate(memReqs, MemoryPropertyFlags.DeviceLocalBit);
        VKGraphiteDevice.Check(device.Vk.BindImageMemory(device.Device, Image, Allocation.Memory, Allocation.Offset));

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

        // Set debug names via VK_EXT_debug_utils for GPU debugger visibility
        device.SetDebugName(ObjectType.Image, Image.Handle, descriptor.DebugName);
        if (!string.IsNullOrEmpty(descriptor.DebugName))
            device.SetDebugName(ObjectType.ImageView, ImageView.Handle, descriptor.DebugName + "_view");
    }

    internal void TransitionLayout(CommandBuffer cmd, ImageLayout newLayout, uint baseMip, uint mipCount, uint baseLayer, uint layerCount)
    {
        // When transitioning multiple mips/layers, each sub-resource may be in a
        // different layout. Issue one barrier per unique (oldLayout → newLayout) group.
        // For the common case (all sub-resources share the same layout), this collapses
        // to a single barrier with the full range.

        // Fast path: single mip, single layer
        if (mipCount == 1 && layerCount == 1)
        {
            uint idx = baseMip * ArrayLayers + baseLayer;
            ImageLayout oldLayout = idx < (uint)_mipLayerLayouts.Length ? _mipLayerLayouts[idx] : ImageLayout.Undefined;
            if (oldLayout == newLayout)
                return;

            EmitBarrier(cmd, oldLayout, newLayout, baseMip, 1, baseLayer, 1);
            _mipLayerLayouts[idx] = newLayout;
            return;
        }

        // Check if all sub-resources in the range share the same layout (common case)
        bool allSame = true;
        uint firstIdx = baseMip * ArrayLayers + baseLayer;
        ImageLayout firstLayout = firstIdx < (uint)_mipLayerLayouts.Length ? _mipLayerLayouts[firstIdx] : ImageLayout.Undefined;
        for (uint m = baseMip; m < baseMip + mipCount; m++)
        {
            for (uint l = baseLayer; l < baseLayer + layerCount; l++)
            {
                uint idx = m * ArrayLayers + l;
                ImageLayout cur = idx < (uint)_mipLayerLayouts.Length ? _mipLayerLayouts[idx] : ImageLayout.Undefined;
                if (cur != firstLayout) { allSame = false; break; }
            }
            if (!allSame) break;
        }

        if (allSame)
        {
            if (firstLayout != newLayout)
                EmitBarrier(cmd, firstLayout, newLayout, baseMip, mipCount, baseLayer, layerCount);
        }
        else
        {
            // Slow path: issue per-sub-resource barriers for differing layouts
            for (uint m = baseMip; m < baseMip + mipCount; m++)
            {
                for (uint l = baseLayer; l < baseLayer + layerCount; l++)
                {
                    uint idx = m * ArrayLayers + l;
                    ImageLayout oldLayout = idx < (uint)_mipLayerLayouts.Length ? _mipLayerLayouts[idx] : ImageLayout.Undefined;
                    if (oldLayout == newLayout) continue;
                    EmitBarrier(cmd, oldLayout, newLayout, m, 1, l, 1);
                }
            }
        }

        // Update tracked layouts
        for (uint m = baseMip; m < baseMip + mipCount; m++)
        {
            for (uint l = baseLayer; l < baseLayer + layerCount; l++)
            {
                uint idx = m * ArrayLayers + l;
                if (idx < (uint)_mipLayerLayouts.Length)
                    _mipLayerLayouts[idx] = newLayout;
            }
        }
    }

    /// <summary>
    /// Builds an <see cref="ImageMemoryBarrier"/> for a layout transition without emitting it.
    /// Used by both single-barrier and batch-barrier paths.
    /// </summary>
    internal void BuildBarrier(
        ImageLayout oldLayout, ImageLayout newLayout,
        uint baseMip, uint mipCount, uint baseLayer, uint layerCount,
        out ImageMemoryBarrier barrier, out PipelineStageFlags srcStage, out PipelineStageFlags dstStage)
    {
        barrier = new ImageMemoryBarrier
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

        switch (oldLayout)
        {
            case ImageLayout.Undefined:
                barrier.SrcAccessMask = 0;
                srcStage = PipelineStageFlags.TopOfPipeBit;
                break;
            case ImageLayout.General:
                barrier.SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
                srcStage = PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;
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
                srcStage = PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit;
                break;
            case ImageLayout.DepthStencilReadOnlyOptimal:
                barrier.SrcAccessMask = AccessFlags.DepthStencilAttachmentReadBit;
                srcStage = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
                break;
            case ImageLayout.PresentSrcKhr:
                barrier.SrcAccessMask = 0;
                srcStage = PipelineStageFlags.ColorAttachmentOutputBit;
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
                dstStage = PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit;
                break;
            case ImageLayout.ColorAttachmentOptimal:
                barrier.DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;
                dstStage = PipelineStageFlags.ColorAttachmentOutputBit;
                break;
            case ImageLayout.DepthStencilAttachmentOptimal:
                barrier.DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;
                dstStage = PipelineStageFlags.EarlyFragmentTestsBit;
                break;
            case ImageLayout.DepthStencilReadOnlyOptimal:
                barrier.DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.ShaderReadBit;
                dstStage = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit | PipelineStageFlags.FragmentShaderBit;
                break;
            case ImageLayout.PresentSrcKhr:
                barrier.DstAccessMask = 0;
                dstStage = PipelineStageFlags.BottomOfPipeBit;
                break;
            case ImageLayout.General:
                barrier.DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
                dstStage = PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;
                break;
            default:
                barrier.DstAccessMask = 0;
                dstStage = PipelineStageFlags.AllCommandsBit;
                break;
        }
    }

    private void EmitBarrier(CommandBuffer cmd, ImageLayout oldLayout, ImageLayout newLayout, uint baseMip, uint mipCount, uint baseLayer, uint layerCount)
    {
        BuildBarrier(oldLayout, newLayout, baseMip, mipCount, baseLayer, layerCount,
            out ImageMemoryBarrier barrier, out PipelineStageFlags srcStage, out PipelineStageFlags dstStage);
        _device.Vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    /// <summary>
    /// Collects the <see cref="ImageMemoryBarrier"/>(s) needed to transition this texture
    /// to <paramref name="newLayout"/> across all mip levels and layers, appending them
    /// to <paramref name="barriers"/>. Returns the number of barriers appended and
    /// ORs the required stage flags into <paramref name="combinedSrcStage"/> and
    /// <paramref name="combinedDstStage"/>. Also updates the tracked layouts.
    /// </summary>
    internal int CollectTransitionBarriers(
        ImageLayout newLayout,
        Span<ImageMemoryBarrier> barriers, int offset,
        ref PipelineStageFlags combinedSrcStage, ref PipelineStageFlags combinedDstStage)
    {
        uint baseMip = 0;
        uint mipCount = MipLevels;
        uint baseLayer = 0;
        uint layerCount = ArrayLayers;
        int written = 0;

        if (mipCount == 1 && layerCount == 1)
        {
            uint idx = 0;
            ImageLayout oldLayout = idx < (uint)_mipLayerLayouts.Length ? _mipLayerLayouts[idx] : ImageLayout.Undefined;
            if (oldLayout != newLayout)
            {
                BuildBarrier(oldLayout, newLayout, 0, 1, 0, 1,
                    out barriers[offset], out PipelineStageFlags src, out PipelineStageFlags dst);
                combinedSrcStage |= src;
                combinedDstStage |= dst;
                written = 1;
                _mipLayerLayouts[idx] = newLayout;
            }
            return written;
        }

        // Check if all sub-resources share the same layout
        bool allSame = true;
        uint firstIdx = 0;
        ImageLayout firstLayout = firstIdx < (uint)_mipLayerLayouts.Length ? _mipLayerLayouts[firstIdx] : ImageLayout.Undefined;
        for (uint m = baseMip; m < baseMip + mipCount && allSame; m++)
        {
            for (uint l = baseLayer; l < baseLayer + layerCount && allSame; l++)
            {
                uint idx2 = m * ArrayLayers + l;
                ImageLayout cur = idx2 < (uint)_mipLayerLayouts.Length ? _mipLayerLayouts[idx2] : ImageLayout.Undefined;
                if (cur != firstLayout) allSame = false;
            }
        }

        if (allSame)
        {
            if (firstLayout != newLayout)
            {
                BuildBarrier(firstLayout, newLayout, baseMip, mipCount, baseLayer, layerCount,
                    out barriers[offset], out PipelineStageFlags src, out PipelineStageFlags dst);
                combinedSrcStage |= src;
                combinedDstStage |= dst;
                written = 1;
            }
        }
        else
        {
            for (uint m = baseMip; m < baseMip + mipCount; m++)
            {
                for (uint l = baseLayer; l < baseLayer + layerCount; l++)
                {
                    uint idx2 = m * ArrayLayers + l;
                    ImageLayout oldLayout = idx2 < (uint)_mipLayerLayouts.Length ? _mipLayerLayouts[idx2] : ImageLayout.Undefined;
                    if (oldLayout != newLayout)
                    {
                        BuildBarrier(oldLayout, newLayout, m, 1, l, 1,
                            out barriers[offset + written], out PipelineStageFlags src, out PipelineStageFlags dst);
                        combinedSrcStage |= src;
                        combinedDstStage |= dst;
                        written++;
                    }
                }
            }
        }

        // Update tracked layouts
        for (uint m = baseMip; m < baseMip + mipCount; m++)
        {
            for (uint l = baseLayer; l < baseLayer + layerCount; l++)
            {
                uint idx2 = m * ArrayLayers + l;
                if (idx2 < (uint)_mipLayerLayouts.Length)
                    _mipLayerLayouts[idx2] = newLayout;
            }
        }

        return written;
    }

    /// <summary>
    /// Updates the tracked layout for all mip levels and all layers without issuing a barrier.
    /// Called by <see cref="VKCommandList.EndRenderPassCore"/> to sync the tracked
    /// layout with the render pass's <c>finalLayout</c>, which Vulkan applies
    /// automatically at render pass end.
    /// </summary>
    internal void SetTrackedLayout(ImageLayout layout)
    {
        Array.Fill(_mipLayerLayouts, layout);
    }

    /// <summary>
    /// Updates the tracked layout for a specific mip level and layer range without issuing a barrier.
    /// </summary>
    internal void SetTrackedLayout(ImageLayout layout, uint baseMip, uint mipCount, uint baseLayer, uint layerCount)
    {
        for (uint m = baseMip; m < baseMip + mipCount; m++)
        {
            for (uint l = baseLayer; l < baseLayer + layerCount; l++)
            {
                uint idx = m * ArrayLayers + l;
                if (idx < (uint)_mipLayerLayouts.Length)
                    _mipLayerLayouts[idx] = layout;
            }
        }
    }

    protected override void DisposeResources()
    {
        _device.InvalidateFramebuffersForImageView(ImageView);
        _device.Vk.DestroyImageView(_device.Device, ImageView, null);
        _device.Vk.DestroyImage(_device.Device, Image, null);
        var alloc = Allocation;
        _device.MemoryAllocator.Free(in alloc);
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
