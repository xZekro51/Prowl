// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Prowl.Vector;

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Vulkan implementation of a command list backed by a VkCommandBuffer.
/// </summary>
internal unsafe class VKCommandList : CommandList
{
    private readonly VKGraphiteDevice _device;
    private readonly CommandPool _sourcePool;
    internal CommandBuffer Handle { get; private set; }

    private VKPipelineState? _currentPipeline;
    private VKComputePipelineState? _currentComputePipeline;
    private Framebuffer _currentFramebuffer;
    private RenderPass _currentRenderPass;
    private uint _currentFBWidth;
    private uint _currentFBHeight;

    /// <summary>True when the command list records a render pass that targets the swapchain.</summary>
    internal bool IsPresentTarget { get; private set; }

    // Track textures attached to the current render pass so EndRenderPassCore
    // can update their tracked layouts to match the render pass's finalLayout.
    private readonly List<VKTexture> _currentRPColorAttachments = new();
    private VKTexture? _currentRPDepthAttachment;
    private bool _currentRPIsPresentTarget;
    private bool _currentRPColorFinalShaderRead;

    internal VKCommandList(VKGraphiteDevice device)
    {
        _device = device;
        _sourcePool = device.GetGraphicsCommandPool();
        Handle = device.RentCommandBuffer(_sourcePool);
    }

    #region Recording

    protected override void BeginRecording()
    {
        IsPresentTarget = false;
        _device.Vk.ResetCommandBuffer(Handle, 0);
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VKGraphiteDevice.Check(_device.Vk.BeginCommandBuffer(Handle, &beginInfo));
    }

    protected override void EndRecording()
    {
        VKGraphiteDevice.Check(_device.Vk.EndCommandBuffer(Handle));
    }

    #endregion

    #region Render Pass

    protected override void BeginRenderPassCore(in RenderPassDescriptor descriptor)
    {
        // Build render pass key
        int colorCount = descriptor.ColorAttachments?.Length ?? 0;
        bool isPresentTarget = false;
        var rpKey = new RenderPassKey
        {
            ColorFormats = new TextureFormat[colorCount],
            ColorLoadOps = new LoadOp[colorCount],
            ColorStoreOps = new StoreOp[colorCount],
            HasResolve = new bool[colorCount],
            SampleCount = SampleCount.Count1,
        };

        uint width = 0, height = 0;

        // stackalloc for image views: max 8 color + 8 resolve + 1 depth = 17
        Span<ImageView> imageViews = stackalloc ImageView[17];
        int imageViewCount = 0;

        // Clear per-pass attachment tracking
        _currentRPColorAttachments.Clear();
        _currentRPDepthAttachment = null;

        for (int i = 0; i < colorCount; i++)
        {
            ref readonly var att = ref descriptor.ColorAttachments![i];
            rpKey.ColorFormats[i] = att.Texture.Format;
            rpKey.ColorLoadOps[i] = att.LoadOp;
            rpKey.ColorStoreOps[i] = att.StoreOp;
            rpKey.HasResolve![i] = att.ResolveTarget != null;
            rpKey.SampleCount = att.Texture.SampleCount;

            if (att.Texture is VKTexture vkTex)
            {
                imageViews[imageViewCount++] = vkTex.ImageView;
                _currentRPColorAttachments.Add(vkTex);
                if (width == 0) { width = vkTex.Width; height = vkTex.Height; }
            }
            else if (att.Texture is VKSwapchainImageTexture swapTex)
            {
                imageViews[imageViewCount++] = swapTex.ImageView;
                width = swapTex.Width;
                height = swapTex.Height;
                isPresentTarget = true;
            }

            if (att.ResolveTarget is VKTexture resolveTex)
                imageViews[imageViewCount++] = resolveTex.ImageView;
        }

        if (descriptor.DepthStencilAttachment is { } depth)
        {
            rpKey.DepthFormat = depth.Texture.Format;
            rpKey.DepthLoadOp = depth.DepthLoadOp;
            rpKey.DepthStoreOp = depth.DepthStoreOp;
            rpKey.StencilLoadOp = depth.StencilLoadOp;
            rpKey.StencilStoreOp = depth.StencilStoreOp;
            if (rpKey.SampleCount == SampleCount.Count1)
                rpKey.SampleCount = depth.Texture.SampleCount;

            if (depth.Texture is VKTexture vkDepth)
            {
                imageViews[imageViewCount++] = vkDepth.ImageView;
                _currentRPDepthAttachment = vkDepth;
                if (width == 0) { width = vkDepth.Width; height = vkDepth.Height; }
            }
        }

        rpKey.IsPresentTarget = isPresentTarget;
        rpKey.ColorFinalLayoutShaderRead = !isPresentTarget && descriptor.HintNextUsageShaderRead;
        _currentRPIsPresentTarget = isPresentTarget;
        _currentRPColorFinalShaderRead = rpKey.ColorFinalLayoutShaderRead;
        if (isPresentTarget)
            IsPresentTarget = true;
        _currentRenderPass = _device.GetOrCreateRenderPass(in rpKey);

        // Look up or create a cached framebuffer
        _currentFramebuffer = _device.GetOrCreateFramebuffer(
            _currentRenderPass,
            imageViews.Slice(0, imageViewCount),
            width, height);

        _currentFBWidth = width;
        _currentFBHeight = height;

        // Auto-transition attachments to the layout expected by the render pass.
        // The cached VkRenderPass encodes an initialLayout for each attachment
        // based on LoadOp (Load → specific layout, Clear/DontCare → Undefined).
        // If a texture's tracked layout diverges (e.g. freshly created with
        // Undefined, or left in ShaderReadOnly after a prior pass), starting the
        // render pass without correcting the layout is undefined behaviour and
        // causes ErrorDeviceLost on many drivers.
        if (descriptor.ColorAttachments != null)
        {
            for (int i = 0; i < colorCount; i++)
            {
                ref readonly var att = ref descriptor.ColorAttachments[i];
                if (att.LoadOp == LoadOp.Load && att.Texture is VKTexture vkColorTex)
                {
                    var expected = isPresentTarget
                        ? ImageLayout.PresentSrcKhr
                        : ImageLayout.ColorAttachmentOptimal;
                    vkColorTex.TransitionLayout(Handle, expected, att.MipLevel, 1, att.ArrayLayer, 1);
                }
            }
        }

        if (descriptor.DepthStencilAttachment is { } depthAtt
            && depthAtt.DepthLoadOp == LoadOp.Load
            && depthAtt.Texture is VKTexture vkDepthTex)
        {
            vkDepthTex.TransitionLayout(Handle, ImageLayout.DepthStencilAttachmentOptimal,
                depthAtt.MipLevel, 1, depthAtt.ArrayLayer, 1);
        }

        // Build clear values on the stack (max 8 color + 8 resolve + 1 depth = 17)
        Span<ClearValue> clearValues = stackalloc ClearValue[17];
        int clearValueCount = 0;
        if (descriptor.ColorAttachments != null)
        {
            foreach (ref readonly var att in descriptor.ColorAttachments.AsSpan())
            {
                var color = new ClearColorValue();
                color.Float32_0 = att.ClearColor.X;
                color.Float32_1 = att.ClearColor.Y;
                color.Float32_2 = att.ClearColor.Z;
                color.Float32_3 = att.ClearColor.W;
                clearValues[clearValueCount++] = new ClearValue { Color = color };
                if (att.ResolveTarget != null)
                    clearValues[clearValueCount++] = default;
            }
        }
        if (descriptor.DepthStencilAttachment is { } ds)
        {
            var cv = new ClearValue();
            cv.DepthStencil = new ClearDepthStencilValue(ds.DepthClearValue, ds.StencilClearValue);
            clearValues[clearValueCount++] = cv;
        }

        fixed (ClearValue* pClears = clearValues)
        {
            var rpBegin = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _currentRenderPass,
                Framebuffer = _currentFramebuffer,
                RenderArea = new Rect2D(default, new Extent2D(width, height)),
                ClearValueCount = (uint)clearValueCount,
                PClearValues = pClears,
            };
            _device.Vk.CmdBeginRenderPass(Handle, &rpBegin, SubpassContents.Inline);
        }
    }

    protected override void EndRenderPassCore()
    {
        _device.Vk.CmdEndRenderPass(Handle);
        _currentPipeline = null;

        // Sync tracked layouts with the render pass's finalLayout.
        // Without this, VKTexture._mipLayouts would remain stale after
        // the render pass automatically transitions image layouts.
        ImageLayout colorFinalLayout;
        if (_currentRPIsPresentTarget)
            colorFinalLayout = ImageLayout.PresentSrcKhr;
        else if (_currentRPColorFinalShaderRead)
            colorFinalLayout = ImageLayout.ShaderReadOnlyOptimal;
        else
            colorFinalLayout = ImageLayout.ColorAttachmentOptimal;

        foreach (var tex in _currentRPColorAttachments)
            tex.SetTrackedLayout(colorFinalLayout);

        _currentRPDepthAttachment?.SetTrackedLayout(ImageLayout.DepthStencilAttachmentOptimal);

        _currentRPColorAttachments.Clear();
        _currentRPDepthAttachment = null;
    }

    #endregion

    #region Pipeline & Binding

    protected override void SetPipelineCore(PipelineState pipeline)
    {
        _currentPipeline = (VKPipelineState)pipeline;
        _device.Vk.CmdBindPipeline(Handle, PipelineBindPoint.Graphics, _currentPipeline.Handle);
    }

    protected override void SetBindGroupCore(uint index, BindGroup bindGroup, ReadOnlySpan<uint> dynamicOffsets)
    {
        var vkBG = (VKBindGroup)bindGroup;
        var set = vkBG.DescriptorSet;

        PipelineLayout layout;
        PipelineBindPoint bindPoint;
        if (_currentPipeline != null)
        {
            layout = _currentPipeline.PipelineLayoutHandle;
            bindPoint = PipelineBindPoint.Graphics;
        }
        else if (_currentComputePipeline != null)
        {
            layout = _currentComputePipeline.PipelineLayoutHandle;
            bindPoint = PipelineBindPoint.Compute;
        }
        else
        {
            return;
        }

        fixed (uint* pOffsets = dynamicOffsets)
        {
            _device.Vk.CmdBindDescriptorSets(Handle, bindPoint, layout, index, 1, &set, (uint)dynamicOffsets.Length, pOffsets);
        }
    }

    protected override void SetVertexBufferCore(uint slot, Graphite.Buffer buffer, uint offset)
    {
        var vkBuf = ((VKBuffer)buffer).Handle;
        ulong vkOffset = offset;
        _device.Vk.CmdBindVertexBuffers(Handle, slot, 1, &vkBuf, &vkOffset);
    }

    protected override void SetIndexBufferCore(Graphite.Buffer buffer, IndexFormat format, uint offset)
    {
        var vkBuf = ((VKBuffer)buffer).Handle;
        _device.Vk.CmdBindIndexBuffer(Handle, vkBuf, offset, VKFormatHelper.ToVkIndexType(format));
    }

    #endregion

    #region Dynamic State

    protected override void SetViewportCore(float x, float y, float width, float height, float minDepth, float maxDepth)
    {
        // Vulkan viewport Y is flipped relative to OpenGL for 3D rendering compatibility
        var viewport = new Viewport(x, y + height, width, -height, minDepth, maxDepth);
        _device.Vk.CmdSetViewport(Handle, 0, 1, &viewport);
    }

    protected override void SetViewportRawCore(float x, float y, float width, float height, float minDepth, float maxDepth)
    {
        // Raw viewport without Y-flip - used for 2D blit operations
        var viewport = new Viewport(x, y, width, height, minDepth, maxDepth);
        _device.Vk.CmdSetViewport(Handle, 0, 1, &viewport);
    }

    protected override void SetScissorCore(int x, int y, uint width, uint height)
    {
        var scissor = new Rect2D(new Offset2D(x, y), new Extent2D(width, height));
        _device.Vk.CmdSetScissor(Handle, 0, 1, &scissor);
    }

    protected override void SetBlendConstantsCore(Float4 color)
    {
        var constants = stackalloc float[4] { color.X, color.Y, color.Z, color.W };
        _device.Vk.CmdSetBlendConstants(Handle, constants);
    }

    protected override void SetStencilReferenceCore(uint reference)
    {
        _device.Vk.CmdSetStencilReference(Handle, StencilFaceFlags.FrontAndBack, reference);
    }

    #endregion

    #region Draw Commands

    protected override void DrawCore(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
    {
        _device.Vk.CmdDraw(Handle, vertexCount, instanceCount, firstVertex, firstInstance);
    }

    protected override void DrawIndexedCore(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
    {
        _device.Vk.CmdDrawIndexed(Handle, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
    }

    protected override void DrawIndirectCore(Graphite.Buffer indirectBuffer, uint offset)
    {
        var vkBuf = ((VKBuffer)indirectBuffer).Handle;
        _device.Vk.CmdDrawIndirect(Handle, vkBuf, offset, 1, 0);
    }

    protected override void DrawIndexedIndirectCore(Graphite.Buffer indirectBuffer, uint offset)
    {
        var vkBuf = ((VKBuffer)indirectBuffer).Handle;
        _device.Vk.CmdDrawIndexedIndirect(Handle, vkBuf, offset, 1, 0);
    }

    #endregion

    #region Compute Commands

    protected override void SetComputePipelineCore(ComputePipelineState pipeline)
    {
        _currentComputePipeline = (VKComputePipelineState)pipeline;
        _device.Vk.CmdBindPipeline(Handle, PipelineBindPoint.Compute, _currentComputePipeline.Handle);
    }

    protected override void DispatchCore(uint groupCountX, uint groupCountY, uint groupCountZ)
    {
        _device.Vk.CmdDispatch(Handle, groupCountX, groupCountY, groupCountZ);
    }

    protected override void DispatchIndirectCore(Graphite.Buffer indirectBuffer, uint offset)
    {
        var vkBuf = ((VKBuffer)indirectBuffer).Handle;
        _device.Vk.CmdDispatchIndirect(Handle, vkBuf, offset);
    }

    #endregion

    #region Copy Commands

    protected override void CopyBufferToBufferCore(Graphite.Buffer source, uint sourceOffset, Graphite.Buffer destination, uint destinationOffset, uint size)
    {
        var region = new BufferCopy { SrcOffset = sourceOffset, DstOffset = destinationOffset, Size = size };
        _device.Vk.CmdCopyBuffer(Handle, ((VKBuffer)source).Handle, ((VKBuffer)destination).Handle, 1, &region);
    }

    protected override void CopyBufferToTextureCore(in BufferTextureCopy copy)
    {
        var vkBuf = ((VKBuffer)copy.Buffer).Handle;
        var vkTex = (VKTexture)copy.Texture;

        var region = new BufferImageCopy
        {
            BufferOffset = copy.BufferOffset,
            BufferRowLength = copy.BufferRowLength,
            BufferImageHeight = copy.BufferImageHeight,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = VKFormatHelper.GetAspectFlags(vkTex.Format),
                MipLevel = copy.MipLevel,
                BaseArrayLayer = copy.ArrayLayer,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D((int)copy.X, (int)copy.Y, (int)copy.Z),
            ImageExtent = new Extent3D(copy.Width, copy.Height, copy.Depth),
        };
        _device.Vk.CmdCopyBufferToImage(Handle, vkBuf, vkTex.Image, ImageLayout.TransferDstOptimal, 1, &region);
    }

    protected override void CopyTextureToBufferCore(in BufferTextureCopy copy)
    {
        var vkBuf = ((VKBuffer)copy.Buffer).Handle;
        var vkTex = (VKTexture)copy.Texture;

        var region = new BufferImageCopy
        {
            BufferOffset = copy.BufferOffset,
            BufferRowLength = copy.BufferRowLength,
            BufferImageHeight = copy.BufferImageHeight,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = VKFormatHelper.GetAspectFlags(vkTex.Format),
                MipLevel = copy.MipLevel,
                BaseArrayLayer = copy.ArrayLayer,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D((int)copy.X, (int)copy.Y, (int)copy.Z),
            ImageExtent = new Extent3D(copy.Width, copy.Height, copy.Depth),
        };
        _device.Vk.CmdCopyImageToBuffer(Handle, vkTex.Image, ImageLayout.TransferSrcOptimal, vkBuf, 1, &region);
    }

    protected override void CopyTextureToTextureCore(in TextureTextureCopy copy)
    {
        var src = (VKTexture)copy.Source;
        var dst = (VKTexture)copy.Destination;

        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = VKFormatHelper.GetAspectFlags(src.Format),
                MipLevel = copy.SourceMipLevel,
                BaseArrayLayer = copy.SourceArrayLayer,
                LayerCount = 1,
            },
            SrcOffset = new Offset3D((int)copy.SourceX, (int)copy.SourceY, (int)copy.SourceZ),
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = VKFormatHelper.GetAspectFlags(dst.Format),
                MipLevel = copy.DestinationMipLevel,
                BaseArrayLayer = copy.DestinationArrayLayer,
                LayerCount = 1,
            },
            DstOffset = new Offset3D((int)copy.DestinationX, (int)copy.DestinationY, (int)copy.DestinationZ),
            Extent = new Extent3D(copy.Width, copy.Height, copy.Depth),
        };
        _device.Vk.CmdCopyImage(Handle, src.Image, ImageLayout.TransferSrcOptimal, dst.Image, ImageLayout.TransferDstOptimal, 1, &region);
    }

    #endregion

    #region Synchronization

    protected override void ResourceBarrierCore(in ResourceBarrier barrier)
    {
        if (barrier.Resource is VKTexture tex)
        {
            tex.TransitionLayout(Handle, ToImageLayout(barrier.StateAfter), 0, tex.MipLevels, 0, tex.ArrayLayers);
        }
        else if (barrier.Resource is VKBuffer buf)
        {
            var memBarrier = new Silk.NET.Vulkan.BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                Buffer = buf.Handle,
                Offset = 0,
                Size = Vk.WholeSize,
                SrcAccessMask = ToAccessFlags(barrier.StateBefore),
                DstAccessMask = ToAccessFlags(barrier.StateAfter),
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            };
            _device.Vk.CmdPipelineBarrier(Handle,
                ToPipelineStageFlags(barrier.StateBefore),
                ToPipelineStageFlags(barrier.StateAfter),
                0, 0, null, 1, &memBarrier, 0, null);
        }
    }

    protected override void ResourceBarriersCore(ReadOnlySpan<Graphite.ResourceBarrier> barriers)
    {
        // Batch all image layout transitions and buffer memory barriers into a single
        // vkCmdPipelineBarrier call to avoid per-barrier driver overhead.
        // Worst case: each texture has divergent per-mip layouts, but typically 1 barrier per resource.
        int maxImageBarriers = 0;
        int bufferCount = 0;
        foreach (ref readonly Graphite.ResourceBarrier b in barriers)
        {
            if (b.Resource is VKTexture tex)
                maxImageBarriers += (int)(tex.MipLevels * tex.ArrayLayers);
            else if (b.Resource is VKBuffer)
                bufferCount++;
        }

        Span<ImageMemoryBarrier> imageBarriers = maxImageBarriers <= 16
            ? stackalloc ImageMemoryBarrier[maxImageBarriers]
            : new ImageMemoryBarrier[maxImageBarriers];
        Span<Silk.NET.Vulkan.BufferMemoryBarrier> bufferBarriers = bufferCount <= 8
            ? stackalloc Silk.NET.Vulkan.BufferMemoryBarrier[bufferCount]
            : new Silk.NET.Vulkan.BufferMemoryBarrier[bufferCount];

        PipelineStageFlags combinedSrcStage = 0;
        PipelineStageFlags combinedDstStage = 0;
        int imgIdx = 0;
        int bufIdx = 0;

        foreach (ref readonly Graphite.ResourceBarrier b in barriers)
        {
            if (b.Resource is VKTexture tex)
            {
                ImageLayout newLayout = ToImageLayout(b.StateAfter);
                imgIdx += tex.CollectTransitionBarriers(newLayout, imageBarriers, imgIdx,
                    ref combinedSrcStage, ref combinedDstStage);
            }
            else if (b.Resource is VKBuffer buf)
            {
                PipelineStageFlags srcStage = ToPipelineStageFlags(b.StateBefore);
                PipelineStageFlags dstStage = ToPipelineStageFlags(b.StateAfter);
                combinedSrcStage |= srcStage;
                combinedDstStage |= dstStage;
                bufferBarriers[bufIdx++] = new Silk.NET.Vulkan.BufferMemoryBarrier
                {
                    SType = StructureType.BufferMemoryBarrier,
                    Buffer = buf.Handle,
                    Offset = 0,
                    Size = Vk.WholeSize,
                    SrcAccessMask = ToAccessFlags(b.StateBefore),
                    DstAccessMask = ToAccessFlags(b.StateAfter),
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                };
            }
        }

        if (imgIdx == 0 && bufIdx == 0) return;

        fixed (ImageMemoryBarrier* pImg = imageBarriers)
        fixed (Silk.NET.Vulkan.BufferMemoryBarrier* pBuf = bufferBarriers)
        {
            _device.Vk.CmdPipelineBarrier(Handle,
                combinedSrcStage, combinedDstStage, 0,
                0, null,
                (uint)bufIdx, pBuf,
                (uint)imgIdx, pImg);
        }
    }

    protected override void MemoryBarrierCore()
    {
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.MemoryReadBit,
        };
        // Narrow the pipeline stages to graphics + compute, which covers all
        // engine use cases (render passes, compute dispatches, image effects).
        // AllCommandsBit unnecessarily stalls transfer and host stages, killing
        // GPU parallelism between render passes and compute dispatches.
        const PipelineStageFlags stages =
            PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit;
        _device.Vk.CmdPipelineBarrier(Handle,
            stages, stages,
            0, 1, &barrier, 0, null, 0, null);
    }

    protected override void GenerateMipmapsCore(Texture texture)
    {
        if (texture is not VKTexture vkTex)
            return;

        uint width = vkTex.Width;
        uint height = vkTex.Height;
        uint depth = vkTex.Depth;
        uint mipLevels = vkTex.MipLevels;

        if (mipLevels <= 1)
            return;

        // Transition mip 0 to TransferSrc using tracked layout so the
        // barrier works even when the image starts in Undefined (first use).
        vkTex.TransitionLayout(Handle, ImageLayout.TransferSrcOptimal, 0, 1, 0, 1);

        for (uint i = 1; i < mipLevels; i++)
        {
            uint srcWidth = Math.Max(1, width >> (int)(i - 1));
            uint srcHeight = Math.Max(1, height >> (int)(i - 1));
            uint srcDepth = Math.Max(1, depth >> (int)(i - 1));
            uint dstWidth = Math.Max(1, width >> (int)i);
            uint dstHeight = Math.Max(1, height >> (int)i);
            uint dstDepth = Math.Max(1, depth >> (int)i);

            // Transition destination mip to TransferDst using tracked layout
            // so _mipLayouts stays in sync with actual Vulkan image layouts.
            vkTex.TransitionLayout(Handle, ImageLayout.TransferDstOptimal, i, 1, 0, 1);

            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = i - 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = i,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            blit.SrcOffsets[1] = new Offset3D((int)srcWidth, (int)srcHeight, (int)srcDepth);
            blit.DstOffsets[1] = new Offset3D((int)dstWidth, (int)dstHeight, (int)dstDepth);

            _device.Vk.CmdBlitImage(Handle,
                vkTex.Image, ImageLayout.TransferSrcOptimal,
                vkTex.Image, ImageLayout.TransferDstOptimal,
                1, &blit, Filter.Linear);

            // Transition this mip level to TransferSrc for the next iteration
            vkTex.TransitionLayout(Handle, ImageLayout.TransferSrcOptimal, i, 1, 0, 1);
        }

        // Transition all mip levels back to General for compute/shader access.
        // All mips are now tracked as TransferSrcOptimal, so the batch barrier
        // correctly uses the right OldLayout.
        vkTex.TransitionLayout(Handle, ImageLayout.General, 0, mipLevels, 0, 1);
    }

    #endregion

    #region Debug Markers

    protected override void PushDebugGroupCore(string name)
    {
        _device.CmdBeginDebugLabel(Handle, name);
    }

    protected override void PopDebugGroupCore()
    {
        _device.CmdEndDebugLabel(Handle);
    }

    protected override void InsertDebugMarkerCore(string name)
    {
        _device.CmdInsertDebugLabel(Handle, name);
    }

    #endregion

    protected override void DisposeResources()
    {
        // Retire the command buffer for deferred recycling once the GPU fence signals.
        _device.RetireCommandListResources(Handle, _sourcePool);
    }

    #region Helpers

    private static ImageLayout ToImageLayout(ResourceState state) => state switch
    {
        ResourceState.Common => ImageLayout.General,
        ResourceState.RenderTarget => ImageLayout.ColorAttachmentOptimal,
        ResourceState.DepthWrite => ImageLayout.DepthStencilAttachmentOptimal,
        ResourceState.DepthRead => ImageLayout.DepthStencilReadOnlyOptimal,
        ResourceState.ShaderResource => ImageLayout.ShaderReadOnlyOptimal,
        ResourceState.UnorderedAccess => ImageLayout.General,
        ResourceState.CopySource => ImageLayout.TransferSrcOptimal,
        ResourceState.CopyDestination => ImageLayout.TransferDstOptimal,
        ResourceState.Present => ImageLayout.PresentSrcKhr,
        _ => ImageLayout.General,
    };

    private static AccessFlags ToAccessFlags(ResourceState state) => state switch
    {
        ResourceState.Common => AccessFlags.None,
        ResourceState.RenderTarget => AccessFlags.ColorAttachmentWriteBit,
        ResourceState.DepthWrite => AccessFlags.DepthStencilAttachmentWriteBit,
        ResourceState.DepthRead => AccessFlags.DepthStencilAttachmentReadBit,
        ResourceState.ShaderResource => AccessFlags.ShaderReadBit,
        ResourceState.UnorderedAccess => AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
        ResourceState.CopySource => AccessFlags.TransferReadBit,
        ResourceState.CopyDestination => AccessFlags.TransferWriteBit,
        ResourceState.Present => AccessFlags.None,
        _ => AccessFlags.None,
    };

    private static PipelineStageFlags ToPipelineStageFlags(ResourceState state) => state switch
    {
        // Common/General may follow any kind of GPU work — use AllCommandsBit so
        // we correctly wait for all prior writes (TopOfPipeBit was too narrow and
        // could miss in-flight graphics/compute/transfer work).
        ResourceState.Common => PipelineStageFlags.AllCommandsBit,
        ResourceState.RenderTarget => PipelineStageFlags.ColorAttachmentOutputBit,
        ResourceState.DepthWrite => PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
        ResourceState.DepthRead => PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
        ResourceState.ShaderResource => PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
        ResourceState.UnorderedAccess => PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit,
        ResourceState.CopySource => PipelineStageFlags.TransferBit,
        ResourceState.CopyDestination => PipelineStageFlags.TransferBit,
        // Present must synchronise with color attachment output so the
        // semaphore/barrier interaction with the presentation engine works.
        ResourceState.Present => PipelineStageFlags.ColorAttachmentOutputBit,
        _ => PipelineStageFlags.AllCommandsBit,
    };

    #endregion
}
