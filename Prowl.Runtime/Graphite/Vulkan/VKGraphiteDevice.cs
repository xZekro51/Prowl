// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkFence = Silk.NET.Vulkan.Fence;
using VkSampler = Silk.NET.Vulkan.Sampler;
using VkShaderModule = Silk.NET.Vulkan.ShaderModule;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Vulkan implementation of the Graphite graphics device.
/// </summary>
public unsafe class VKGraphiteDevice : GraphiteDevice
{
    internal Vk Vk { get; private set; } = null!;
    internal Instance VkInstance { get; private set; }
    internal PhysicalDevice PhysicalDevice { get; private set; }
    internal Device Device { get; private set; }
    internal Queue GraphicsQueue { get; private set; }
    internal uint GraphicsQueueFamily { get; private set; }
    internal CommandPool CommandPool { get; private set; }
    internal PhysicalDeviceMemoryProperties MemoryProperties { get; private set; }

    private DeviceCapabilities _capabilities;
    private bool _initialized;
    private uint _swapchainWidth;
    private uint _swapchainHeight;

    // Cached render passes
    private readonly Dictionary<RenderPassKey, RenderPass> _renderPassCache = new();

    public override string BackendName => "Vulkan 1.0";
    public override GraphicsBackendType BackendType => GraphicsBackendType.Vulkan;
    public override DeviceCapabilities Capabilities => _capabilities;
    public override uint SwapchainWidth => _swapchainWidth;
    public override uint SwapchainHeight => _swapchainHeight;

    public override void Initialize(GraphiteDeviceOptions options)
    {
        ThrowIfDisposed();
        if (_initialized)
            throw new InvalidOperationException("Device is already initialized.");

        Vk = Vk.GetApi();

        CreateInstance(options.EnableDebugLayer);
        PickPhysicalDevice();
        CreateLogicalDevice();
        CreateCommandPool();

        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memProps);
        MemoryProperties = memProps;

        _capabilities = QueryCapabilities();
        _initialized = true;
    }

    private void CreateInstance(bool enableDebug)
    {
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)SilkMarshal.StringToPtr("Prowl Engine"),
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = (byte*)SilkMarshal.StringToPtr("Prowl"),
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version10,
        };

        var extensions = new List<string>();
        var layers = new List<string>();

        // Get required surface extensions from the window if available
        if (Window.InternalWindow is IVkSurface vkSurface)
        {
            var surfaceExtensions = vkSurface.GetRequiredExtensions(out var count);
            for (int i = 0; i < (int)count; i++)
                extensions.Add(Marshal.PtrToStringAnsi((nint)surfaceExtensions[i])!);
        }

        if (enableDebug)
        {
            extensions.Add("VK_EXT_debug_utils");
            layers.Add("VK_LAYER_KHRONOS_validation");
        }

        var extPtrs = SilkMarshal.StringArrayToPtr(extensions.ToArray());
        var layerPtrs = SilkMarshal.StringArrayToPtr(layers.ToArray());

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = (byte**)extPtrs,
            EnabledLayerCount = (uint)layers.Count,
            PpEnabledLayerNames = (byte**)layerPtrs,
        };

        Check(Vk.CreateInstance(&createInfo, null, out var instance));
        VkInstance = instance;

        SilkMarshal.Free(extPtrs);
        SilkMarshal.Free(layerPtrs);
        SilkMarshal.Free((nint)appInfo.PApplicationName);
        SilkMarshal.Free((nint)appInfo.PEngineName);
    }

    private void PickPhysicalDevice()
    {
        uint count = 0;
        Vk.EnumeratePhysicalDevices(VkInstance, &count, null);
        if (count == 0)
            throw new InvalidOperationException("No Vulkan-capable GPU found.");

        var devices = stackalloc PhysicalDevice[(int)count];
        Vk.EnumeratePhysicalDevices(VkInstance, &count, devices);

        // Prefer discrete GPU
        PhysicalDevice = devices[0];
        for (int i = 0; i < count; i++)
        {
            Vk.GetPhysicalDeviceProperties(devices[i], out var props);
            if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
            {
                PhysicalDevice = devices[i];
                break;
            }
        }
    }

    private void CreateLogicalDevice()
    {
        uint queueFamilyCount = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &queueFamilyCount, null);
        var queueFamilies = stackalloc QueueFamilyProperties[(int)queueFamilyCount];
        Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &queueFamilyCount, queueFamilies);

        uint graphicsFamily = uint.MaxValue;
        for (uint i = 0; i < queueFamilyCount; i++)
        {
            if (queueFamilies[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                graphicsFamily = i;
                break;
            }
        }

        if (graphicsFamily == uint.MaxValue)
            throw new InvalidOperationException("No graphics queue family found.");

        GraphicsQueueFamily = graphicsFamily;

        float priority = 1.0f;
        var queueCreateInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = graphicsFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        var features = new PhysicalDeviceFeatures
        {
            SamplerAnisotropy = true,
            FillModeNonSolid = true,
            GeometryShader = true,
            TessellationShader = true,
            MultiDrawIndirect = true,
            DepthClamp = true,
        };

        var extensions = new List<string> { "VK_KHR_swapchain" };
        var extPtrs = SilkMarshal.StringArrayToPtr(extensions.ToArray());

        var deviceCreateInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueCreateInfo,
            PEnabledFeatures = &features,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = (byte**)extPtrs,
        };

        Check(Vk.CreateDevice(PhysicalDevice, &deviceCreateInfo, null, out var device));
        Device = device;

        SilkMarshal.Free(extPtrs);

        Vk.GetDeviceQueue(Device, graphicsFamily, 0, out var queue);
        GraphicsQueue = queue;
    }

    private void CreateCommandPool()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = GraphicsQueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Check(Vk.CreateCommandPool(Device, &poolInfo, null, out var pool));
        CommandPool = pool;
    }

    private DeviceCapabilities QueryCapabilities()
    {
        Vk.GetPhysicalDeviceProperties(PhysicalDevice, out var props);
        var limits = props.Limits;

        return new DeviceCapabilities
        {
            DeviceName = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "Unknown",
            VendorName = $"VendorID: {props.VendorID:X4}",
            SupportsCompute = true,
            SupportsGeometryShaders = true,
            SupportsTessellation = true,
            SupportsMultiDrawIndirect = true,
            SupportsBindless = false,
            MaxTextureSize = limits.MaxImageDimension2D,
            MaxUniformBufferSize = (uint)limits.MaxUniformBufferRange,
            MaxStorageBufferSize = (uint)limits.MaxStorageBufferRange,
            MaxBindGroups = limits.MaxBoundDescriptorSets,
            MaxSamplersPerStage = limits.MaxPerStageDescriptorSamplers,
            MaxTexturesPerStage = limits.MaxPerStageDescriptorSampledImages,
            MaxVertexAttributes = limits.MaxVertexInputAttributes,
            MaxVertexBuffers = limits.MaxVertexInputBindings,
            MaxColorAttachments = limits.MaxColorAttachments,
            MaxComputeWorkgroupSizeX = limits.MaxComputeWorkGroupSize[0],
            MaxComputeWorkgroupSizeY = limits.MaxComputeWorkGroupSize[1],
            MaxComputeWorkgroupSizeZ = limits.MaxComputeWorkGroupSize[2],
            MaxComputeInvocationsPerWorkgroup = limits.MaxComputeWorkGroupInvocations,
        };
    }

    #region Resource Creation

    public override Buffer CreateBuffer(in BufferDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new VKBuffer(this, in descriptor);
    }

    public override Texture CreateTexture(in TextureDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new VKTexture(this, in descriptor);
    }

    public override Sampler CreateSampler(in SamplerDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new VKSampler(this, in descriptor);
    }

    public override ShaderModule CreateShaderModule(in ShaderModuleDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new VKShaderModule(this, in descriptor);
    }

    public override PipelineState CreatePipelineState(in PipelineStateDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new VKPipelineState(this, in descriptor);
    }

    public override ComputePipelineState CreateComputePipelineState(in ComputePipelineStateDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new VKComputePipelineState(this, in descriptor);
    }

    public override BindGroupLayout CreateBindGroupLayout(in BindGroupLayoutDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new VKBindGroupLayout(this, in descriptor);
    }

    public override BindGroup CreateBindGroup(in BindGroupDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new VKBindGroup(this, in descriptor);
    }

    public override Fence CreateFence(bool signaled = false)
    {
        ThrowIfDisposed();
        return new VKFence(this, signaled);
    }

    #endregion

    #region Command List Management

    public override CommandList CreateCommandList()
    {
        ThrowIfDisposed();
        return new VKCommandList(this);
    }

    public override void SubmitCommands(CommandList commandList)
    {
        ThrowIfDisposed();
        if (commandList is not VKCommandList vkCmd)
            throw new ArgumentException("Command list is not a Vulkan command list.", nameof(commandList));

        var cb = vkCmd.Handle;
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
        };
        Check(Vk.QueueSubmit(GraphicsQueue, 1, &submitInfo, default));
    }

    public override void SubmitCommands(ReadOnlySpan<CommandList> commandLists)
    {
        ThrowIfDisposed();
        var buffers = stackalloc CommandBuffer[commandLists.Length];
        for (int i = 0; i < commandLists.Length; i++)
        {
            if (commandLists[i] is not VKCommandList vkCmd)
                throw new ArgumentException("Command list is not a Vulkan command list.");
            buffers[i] = vkCmd.Handle;
        }

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = (uint)commandLists.Length,
            PCommandBuffers = buffers,
        };
        Check(Vk.QueueSubmit(GraphicsQueue, 1, &submitInfo, default));
    }

    public override void SubmitCommands(CommandList commandList, Fence fence)
    {
        ThrowIfDisposed();
        if (commandList is not VKCommandList vkCmd)
            throw new ArgumentException("Command list is not a Vulkan command list.", nameof(commandList));

        VkFence vkFence = default;
        if (fence is Vulkan.VKFence f)
            vkFence = f.Handle;

        var cb = vkCmd.Handle;
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
        };
        Check(Vk.QueueSubmit(GraphicsQueue, 1, &submitInfo, vkFence));
    }

    #endregion

    #region Synchronization

    public override void WaitForFence(Fence fence)
    {
        ThrowIfDisposed();
        fence.Wait();
    }

    public override void WaitForIdle()
    {
        ThrowIfDisposed();
        Vk.DeviceWaitIdle(Device);
    }

    #endregion

    #region Frame Management

    public override void ResizeSwapchain(uint width, uint height)
    {
        ThrowIfDisposed();
        _swapchainWidth = width;
        _swapchainHeight = height;
    }

    #endregion

    #region Resource Updates

    public override void UpdateBuffer<T>(Buffer buffer, uint offsetInBytes, ReadOnlySpan<T> data)
    {
        ThrowIfDisposed();
        if (buffer is not VKBuffer vkBuffer)
            return;

        uint dataSize = (uint)(data.Length * Unsafe.SizeOf<T>());

        if (vkBuffer.MemoryAccess != MemoryAccess.GpuOnly)
        {
            // Directly map and copy for host-visible buffers
            void* mapped;
            Check(Vk.MapMemory(Device, vkBuffer.Memory, offsetInBytes, dataSize, 0, &mapped));
            fixed (T* src = data)
                System.Buffer.MemoryCopy(src, mapped, dataSize, dataSize);
            Vk.UnmapMemory(Device, vkBuffer.Memory);
        }
        else
        {
            // Use staging buffer for GPU-only memory
            var stagingDesc = new BufferDescriptor(dataSize, BufferUsage.CopySource, MemoryAccess.CpuToGpu);
            using var staging = new VKBuffer(this, in stagingDesc);

            void* mapped;
            Check(Vk.MapMemory(Device, staging.Memory, 0, dataSize, 0, &mapped));
            fixed (T* src = data)
                System.Buffer.MemoryCopy(src, mapped, dataSize, dataSize);
            Vk.UnmapMemory(Device, staging.Memory);

            var cmd = BeginSingleTimeCommands();
            var region = new BufferCopy { SrcOffset = 0, DstOffset = offsetInBytes, Size = dataSize };
            Vk.CmdCopyBuffer(cmd, staging.Handle, vkBuffer.Handle, 1, &region);
            EndSingleTimeCommands(cmd);
        }
    }

    public override void UpdateTexture(Texture texture, in TextureUpdateDescriptor descriptor, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        if (texture is not VKTexture vkTexture)
            return;

        // Create staging buffer
        uint dataSize = (uint)data.Length;
        var stagingDesc = new BufferDescriptor(dataSize, BufferUsage.CopySource, MemoryAccess.CpuToGpu);
        using var staging = new VKBuffer(this, in stagingDesc);

        void* mapped;
        Check(Vk.MapMemory(Device, staging.Memory, 0, dataSize, 0, &mapped));
        fixed (byte* src = data)
            System.Buffer.MemoryCopy(src, mapped, dataSize, dataSize);
        Vk.UnmapMemory(Device, staging.Memory);

        var cmd = BeginSingleTimeCommands();

        // Transition to TransferDstOptimal
        vkTexture.TransitionLayout(cmd, ImageLayout.TransferDstOptimal, descriptor.MipLevel, 1, descriptor.ArrayLayer, 1);

        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = VKFormatHelper.GetAspectFlags(vkTexture.Format),
                MipLevel = descriptor.MipLevel,
                BaseArrayLayer = descriptor.ArrayLayer,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D((int)descriptor.X, (int)descriptor.Y, (int)descriptor.Z),
            ImageExtent = new Extent3D(descriptor.Width, descriptor.Height, Math.Max(descriptor.Depth, 1)),
        };
        Vk.CmdCopyBufferToImage(cmd, staging.Handle, vkTexture.Image, ImageLayout.TransferDstOptimal, 1, &region);

        // Transition to ShaderReadOnlyOptimal
        vkTexture.TransitionLayout(cmd, ImageLayout.ShaderReadOnlyOptimal, descriptor.MipLevel, 1, descriptor.ArrayLayer, 1);

        EndSingleTimeCommands(cmd);
    }

    public override void GenerateMipmaps(Texture texture)
    {
        ThrowIfDisposed();
        if (texture is not VKTexture vkTexture || vkTexture.MipLevels <= 1)
            return;

        var cmd = BeginSingleTimeCommands();

        int mipWidth = (int)vkTexture.Width;
        int mipHeight = (int)vkTexture.Height;

        for (uint i = 1; i < vkTexture.MipLevels; i++)
        {
            // Transition previous mip to TransferSrcOptimal
            vkTexture.TransitionLayout(cmd, ImageLayout.TransferSrcOptimal, i - 1, 1, 0, vkTexture.ArrayLayers);

            // Transition current mip to TransferDstOptimal
            vkTexture.TransitionLayout(cmd, ImageLayout.TransferDstOptimal, i, 1, 0, vkTexture.ArrayLayers);

            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = i - 1,
                    BaseArrayLayer = 0,
                    LayerCount = vkTexture.ArrayLayers,
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = i,
                    BaseArrayLayer = 0,
                    LayerCount = vkTexture.ArrayLayers,
                },
            };
            blit.SrcOffsets[0] = new Offset3D(0, 0, 0);
            blit.SrcOffsets[1] = new Offset3D(mipWidth, mipHeight, 1);
            blit.DstOffsets[0] = new Offset3D(0, 0, 0);
            blit.DstOffsets[1] = new Offset3D(
                mipWidth > 1 ? mipWidth / 2 : 1,
                mipHeight > 1 ? mipHeight / 2 : 1,
                1);

            Vk.CmdBlitImage(cmd, vkTexture.Image, ImageLayout.TransferSrcOptimal,
                vkTexture.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);

            // Transition previous mip to ShaderReadOnlyOptimal
            vkTexture.TransitionLayout(cmd, ImageLayout.ShaderReadOnlyOptimal, i - 1, 1, 0, vkTexture.ArrayLayers);

            if (mipWidth > 1) mipWidth /= 2;
            if (mipHeight > 1) mipHeight /= 2;
        }

        // Transition last mip to ShaderReadOnlyOptimal
        vkTexture.TransitionLayout(cmd, ImageLayout.ShaderReadOnlyOptimal, vkTexture.MipLevels - 1, 1, 0, vkTexture.ArrayLayers);

        EndSingleTimeCommands(cmd);
    }

    #endregion

    #region Swapchain

    public override Texture GetSwapchainTexture()
    {
        ThrowIfDisposed();
        return VKSwapchainTexture.Instance;
    }

    #endregion

    #region Internal Helpers

    internal uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        for (uint i = 0; i < MemoryProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0 &&
                (MemoryProperties.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
            {
                return i;
            }
        }
        throw new InvalidOperationException($"Failed to find suitable memory type (filter={typeFilter}, props={properties}).");
    }

    internal CommandBuffer BeginSingleTimeCommands()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = CommandPool,
            CommandBufferCount = 1,
        };

        Vk.AllocateCommandBuffers(Device, &allocInfo, out var commandBuffer);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Vk.BeginCommandBuffer(commandBuffer, &beginInfo);
        return commandBuffer;
    }

    internal void EndSingleTimeCommands(CommandBuffer commandBuffer)
    {
        Vk.EndCommandBuffer(commandBuffer);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        Vk.QueueSubmit(GraphicsQueue, 1, &submitInfo, default);
        Vk.QueueWaitIdle(GraphicsQueue);
        Vk.FreeCommandBuffers(Device, CommandPool, 1, &commandBuffer);
    }

    internal RenderPass GetOrCreateRenderPass(in RenderPassKey key)
    {
        if (_renderPassCache.TryGetValue(key, out var cached))
            return cached;

        var attachments = new List<AttachmentDescription>();
        var colorRefs = new List<AttachmentReference>();
        var resolveRefs = new List<AttachmentReference>();
        bool hasResolve = false;

        for (int i = 0; i < key.ColorFormats.Length; i++)
        {
            bool resolve = key.HasResolve != null && i < key.HasResolve.Length && key.HasResolve[i];
            attachments.Add(new AttachmentDescription
            {
                Format = VKFormatHelper.ToVkFormat(key.ColorFormats[i]),
                Samples = VKFormatHelper.ToVkSampleCount(key.SampleCount),
                LoadOp = VKFormatHelper.ToVkLoadOp(key.ColorLoadOps[i]),
                StoreOp = VKFormatHelper.ToVkStoreOp(key.ColorStoreOps[i]),
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = key.ColorLoadOps[i] == LoadOp.Load ? ImageLayout.ColorAttachmentOptimal : ImageLayout.Undefined,
                FinalLayout = ImageLayout.ColorAttachmentOptimal,
            });
            colorRefs.Add(new AttachmentReference { Attachment = (uint)attachments.Count - 1, Layout = ImageLayout.ColorAttachmentOptimal });

            if (resolve)
            {
                hasResolve = true;
                attachments.Add(new AttachmentDescription
                {
                    Format = VKFormatHelper.ToVkFormat(key.ColorFormats[i]),
                    Samples = SampleCountFlags.Count1Bit,
                    LoadOp = AttachmentLoadOp.DontCare,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = ImageLayout.Undefined,
                    FinalLayout = ImageLayout.ColorAttachmentOptimal,
                });
                resolveRefs.Add(new AttachmentReference { Attachment = (uint)attachments.Count - 1, Layout = ImageLayout.ColorAttachmentOptimal });
            }
            else
            {
                resolveRefs.Add(new AttachmentReference { Attachment = Vk.AttachmentUnused, Layout = ImageLayout.Undefined });
            }
        }

        AttachmentReference? depthRef = null;
        if (key.DepthFormat.HasValue)
        {
            attachments.Add(new AttachmentDescription
            {
                Format = VKFormatHelper.ToVkFormat(key.DepthFormat.Value),
                Samples = VKFormatHelper.ToVkSampleCount(key.SampleCount),
                LoadOp = VKFormatHelper.ToVkLoadOp(key.DepthLoadOp),
                StoreOp = VKFormatHelper.ToVkStoreOp(key.DepthStoreOp),
                StencilLoadOp = VKFormatHelper.ToVkLoadOp(key.StencilLoadOp),
                StencilStoreOp = VKFormatHelper.ToVkStoreOp(key.StencilStoreOp),
                InitialLayout = key.DepthLoadOp == LoadOp.Load ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.Undefined,
                FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
            });
            depthRef = new AttachmentReference { Attachment = (uint)attachments.Count - 1, Layout = ImageLayout.DepthStencilAttachmentOptimal };
        }

        fixed (AttachmentDescription* pAttachments = attachments.ToArray())
        fixed (AttachmentReference* pColorRefs = colorRefs.ToArray())
        fixed (AttachmentReference* pResolveRefs = resolveRefs.ToArray())
        {
            var depthRefValue = depthRef ?? default;
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = (uint)colorRefs.Count,
                PColorAttachments = pColorRefs,
                PResolveAttachments = hasResolve ? pResolveRefs : null,
                PDepthStencilAttachment = depthRef.HasValue ? &depthRefValue : null,
            };

            var dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                SrcAccessMask = 0,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            };

            var rpInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = (uint)attachments.Count,
                PAttachments = pAttachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };

            Check(Vk.CreateRenderPass(Device, &rpInfo, null, out var renderPass));
            _renderPassCache[key] = renderPass;
            return renderPass;
        }
    }

    internal static void Check(Result result, [CallerMemberName] string? caller = null)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Vulkan error in {caller}: {result}");
    }

    #endregion

    protected override void DisposeResources()
    {
        foreach (var rp in _renderPassCache.Values)
            Vk.DestroyRenderPass(Device, rp, null);
        _renderPassCache.Clear();

        Vk.DestroyCommandPool(Device, CommandPool, null);

        Vk.DestroyDevice(Device, null);
        Vk.DestroyInstance(VkInstance, null);
        Vk.Dispose();
    }
}

/// <summary>
/// Special texture type representing the Vulkan swapchain image.
/// </summary>
internal class VKSwapchainTexture : Texture
{
    public static readonly VKSwapchainTexture Instance = new();

    private VKSwapchainTexture()
    {
        Dimension = TextureDimension.Texture2D;
        Width = 0;
        Height = 0;
        Depth = 1;
        MipLevels = 1;
        ArrayLayers = 1;
        Format = TextureFormat.BGRA8Unorm;
        Usage = TextureUsage.RenderTarget;
        SampleCount = SampleCount.Count1;
    }

    protected override void DisposeResources() { }
}

/// <summary>
/// Key for caching Vulkan render passes by their attachment configuration.
/// </summary>
internal struct RenderPassKey : IEquatable<RenderPassKey>
{
    public TextureFormat[] ColorFormats;
    public LoadOp[] ColorLoadOps;
    public StoreOp[] ColorStoreOps;
    public bool[]? HasResolve;
    public TextureFormat? DepthFormat;
    public LoadOp DepthLoadOp;
    public StoreOp DepthStoreOp;
    public LoadOp StencilLoadOp;
    public StoreOp StencilStoreOp;
    public SampleCount SampleCount;

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        if (ColorFormats != null)
        {
            for (int i = 0; i < ColorFormats.Length; i++)
            {
                hash.Add(ColorFormats[i]);
                hash.Add(ColorLoadOps[i]);
                hash.Add(ColorStoreOps[i]);
                if (HasResolve != null && i < HasResolve.Length)
                    hash.Add(HasResolve[i]);
            }
        }
        hash.Add(DepthFormat);
        hash.Add(DepthLoadOp);
        hash.Add(DepthStoreOp);
        hash.Add(StencilLoadOp);
        hash.Add(StencilStoreOp);
        hash.Add(SampleCount);
        return hash.ToHashCode();
    }

    public override readonly bool Equals(object? obj) => obj is RenderPassKey key && Equals(key);

    public readonly bool Equals(RenderPassKey other)
    {
        if (DepthFormat != other.DepthFormat || DepthLoadOp != other.DepthLoadOp || DepthStoreOp != other.DepthStoreOp ||
            StencilLoadOp != other.StencilLoadOp || StencilStoreOp != other.StencilStoreOp || SampleCount != other.SampleCount)
            return false;

        if ((ColorFormats == null) != (other.ColorFormats == null))
            return false;

        if (ColorFormats != null && other.ColorFormats != null)
        {
            if (ColorFormats.Length != other.ColorFormats.Length)
                return false;
            for (int i = 0; i < ColorFormats.Length; i++)
            {
                if (ColorFormats[i] != other.ColorFormats[i] || ColorLoadOps[i] != other.ColorLoadOps[i] || ColorStoreOps[i] != other.ColorStoreOps[i])
                    return false;
                bool r1 = HasResolve != null && i < HasResolve.Length && HasResolve[i];
                bool r2 = other.HasResolve != null && i < other.HasResolve.Length && other.HasResolve[i];
                if (r1 != r2)
                    return false;
            }
        }

        return true;
    }
}
