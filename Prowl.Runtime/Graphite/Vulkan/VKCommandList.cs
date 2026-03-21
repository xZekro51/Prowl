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
    internal CommandBuffer Handle { get; private set; }

    private VKPipelineState? _currentPipeline;
    private VKComputePipelineState? _currentComputePipeline;
    private Framebuffer _currentFramebuffer;
    private RenderPass _currentRenderPass;
    private uint _currentFBWidth;
    private uint _currentFBHeight;

    // Track framebuffers created during recording so we can destroy them after submission
    private readonly List<Framebuffer> _framebuffers = new();

    internal VKCommandList(VKGraphiteDevice device)
    {
        _device = device;

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = device.CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        VKGraphiteDevice.Check(device.Vk.AllocateCommandBuffers(device.Device, &allocInfo, out var cb));
        Handle = cb;
    }

    #region Recording

    protected override void BeginRecording()
    {
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
        var rpKey = new RenderPassKey
        {
            ColorFormats = new TextureFormat[colorCount],
            ColorLoadOps = new LoadOp[colorCount],
            ColorStoreOps = new StoreOp[colorCount],
            HasResolve = new bool[colorCount],
            SampleCount = SampleCount.Count1,
        };

        uint width = 0, height = 0;
        var imageViews = new List<ImageView>();

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
                imageViews.Add(vkTex.ImageView);
                if (width == 0) { width = vkTex.Width; height = vkTex.Height; }
            }
            else if (att.Texture is VKSwapchainImageTexture swapTex)
            {
                imageViews.Add(swapTex.ImageView);
                width = swapTex.Width;
                height = swapTex.Height;
            }

            if (att.ResolveTarget is VKTexture resolveTex)
                imageViews.Add(resolveTex.ImageView);
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
                imageViews.Add(vkDepth.ImageView);
                if (width == 0) { width = vkDepth.Width; height = vkDepth.Height; }
            }
        }

        _currentRenderPass = _device.GetOrCreateRenderPass(in rpKey);

        // Create framebuffer
        fixed (ImageView* pViews = imageViews.ToArray())
        {
            var fbInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _currentRenderPass,
                AttachmentCount = (uint)imageViews.Count,
                PAttachments = pViews,
                Width = width,
                Height = height,
                Layers = 1,
            };
            VKGraphiteDevice.Check(_device.Vk.CreateFramebuffer(_device.Device, &fbInfo, null, out _currentFramebuffer));
            _framebuffers.Add(_currentFramebuffer);
        }

        _currentFBWidth = width;
        _currentFBHeight = height;

        // Build clear values
        var clearValues = new List<ClearValue>();
        if (descriptor.ColorAttachments != null)
        {
            foreach (ref readonly var att in descriptor.ColorAttachments.AsSpan())
            {
                var color = new ClearColorValue();
                color.Float32_0 = att.ClearColor.X;
                color.Float32_1 = att.ClearColor.Y;
                color.Float32_2 = att.ClearColor.Z;
                color.Float32_3 = att.ClearColor.W;
                var cv = new ClearValue { Color = color };
                clearValues.Add(cv);
                if (att.ResolveTarget != null)
                    clearValues.Add(default);
            }
        }
        if (descriptor.DepthStencilAttachment is { } ds)
        {
            var cv = new ClearValue();
            cv.DepthStencil = new ClearDepthStencilValue(ds.DepthClearValue, ds.StencilClearValue);
            clearValues.Add(cv);
        }

        fixed (ClearValue* pClears = clearValues.ToArray())
        {
            var rpBegin = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _currentRenderPass,
                Framebuffer = _currentFramebuffer,
                RenderArea = new Rect2D(default, new Extent2D(width, height)),
                ClearValueCount = (uint)clearValues.Count,
                PClearValues = pClears,
            };
            _device.Vk.CmdBeginRenderPass(Handle, &rpBegin, SubpassContents.Inline);
        }
    }

    protected override void EndRenderPassCore()
    {
        _device.Vk.CmdEndRenderPass(Handle);
        _currentPipeline = null;
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
        // Vulkan viewport Y is flipped relative to OpenGL
        var viewport = new Viewport(x, y + height, width, -height, minDepth, maxDepth);
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
            _device.Vk.CmdPipelineBarrier(Handle, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
                0, 0, null, 1, &memBarrier, 0, null);
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
        _device.Vk.CmdPipelineBarrier(Handle, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 1, &barrier, 0, null, 0, null);
    }

    #endregion

    #region Debug Markers

    protected override void PushDebugGroupCore(string name)
    {
        // Debug markers require VK_EXT_debug_utils - no-op if not available
    }

    protected override void PopDebugGroupCore()
    {
    }

    protected override void InsertDebugMarkerCore(string name)
    {
    }

    #endregion

    protected override void DisposeResources()
    {
        // Destroy any framebuffers created during recording
        foreach (var fb in _framebuffers)
            _device.Vk.DestroyFramebuffer(_device.Device, fb, null);
        _framebuffers.Clear();

        var cb = Handle;
        _device.Vk.FreeCommandBuffers(_device.Device, _device.CommandPool, 1, &cb);
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

    #endregion
}
