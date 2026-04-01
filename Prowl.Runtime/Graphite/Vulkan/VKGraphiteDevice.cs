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
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

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
    private const int MaxFramesInFlight = 2;

    public override int FramesInFlight => MaxFramesInFlight;
    public override int CurrentFrameIndex => _currentFrame;

    internal Vk Vk { get; private set; } = null!;
    internal Instance VkInstance { get; private set; }
    internal PhysicalDevice PhysicalDevice { get; private set; }
    internal Device Device { get; private set; }
    internal Queue GraphicsQueue { get; private set; }
    internal uint GraphicsQueueFamily { get; private set; }
    internal Queue PresentQueue { get; private set; }
    internal uint PresentQueueFamily { get; private set; }
    internal CommandPool CommandPool { get; private set; }
    internal PhysicalDeviceMemoryProperties MemoryProperties { get; private set; }

    // KHR extensions
    private KhrSurface? _khrSurface;
    private KhrSwapchain? _khrSwapchain;
    private SurfaceKHR _surface;

    // Debug utils extension (available when validation layers are enabled)
    private ExtDebugUtils? _debugUtils;
    internal bool HasDebugUtils => _debugUtils != null;

    // Swapchain
    private SwapchainKHR _swapchain;
    private Image[] _swapchainImages = [];
    private ImageView[] _swapchainImageViews = [];
    private VKSwapchainImageTexture[] _swapchainTextures = [];
    private Format _swapchainFormat;
    private Extent2D _swapchainExtent;

    // Frame-in-flight synchronisation
    private Semaphore[] _imageAvailableSemaphores = [];
    private Semaphore[] _renderFinishedSemaphores = [];
    private VkFence[] _inFlightFences = [];
    private int _currentFrame;
    private uint _currentImageIndex;
    private bool _framebufferResized;
    private bool _frameSyncConsumed;

    private DeviceCapabilities _capabilities;
    private bool _initialized;
    private uint _swapchainWidth;
    private uint _swapchainHeight;

    // Cached render passes
    private readonly Dictionary<RenderPassKey, RenderPass> _renderPassCache = new();

    // Per-frame-slot deferred destruction for resources still referenced by in-flight command buffers.
    // Resources are retired into the current frame slot and destroyed in BeginFrame once the
    // corresponding fence has been signaled, guaranteeing the GPU is no longer using them.
    private List<(List<Framebuffer> Framebuffers, CommandBuffer CommandBuffer)>[] _retiredResources = [];

    // Sub-allocator for GPU memory (avoids the ~4096 vkAllocateMemory limit)
    internal VKMemoryAllocator MemoryAllocator { get; private set; } = null!;

    // Per-frame descriptor pool manager (replaces per-bind-group pools)
    internal VKDescriptorPoolManager DescriptorPoolManager { get; private set; } = null!;

    // Per-frame ring buffer for per-draw uniform data (eliminates per-draw buffer creation)
    internal VKUniformRingBuffer UniformRingBuffer { get; private set; } = null!;

    // Upload batch state (replaces per-upload QueueWaitIdle with batched fence-based submission)
    private CommandBuffer _batchCmdBuffer;
    private bool _isBatching;
    private readonly List<IDisposable> _batchResources = new();
    private VkFence _uploadFence;

    /// <summary>Whether upload batching is currently active.</summary>
    internal bool IsUploadBatching => _isBatching;

    public override string BackendName => "Vulkan 1.3";
    public override GraphicsBackendType BackendType => GraphicsBackendType.Vulkan;
    public override DeviceCapabilities Capabilities => _capabilities;
    public override uint SwapchainWidth => _swapchainWidth;
    public override uint SwapchainHeight => _swapchainHeight;
    public override bool NeedsExplicitSwapchainBlit => true;

    public override void Initialize(GraphiteDeviceOptions options)
    {
        ThrowIfDisposed();
        if (_initialized)
            throw new InvalidOperationException("Device is already initialized.");

        Debug.Log("[Vulkan] Acquiring Vulkan API...");
        try
        {
            Vk = Vk.GetApi();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Vulkan runtime not available. Ensure Vulkan drivers are installed. ({ex.GetType().Name}: {ex.Message})", ex);
        }

        Debug.Log("[Vulkan] Creating instance...");
        CreateInstance(options.EnableDebugLayer);

        Debug.Log("[Vulkan] Creating surface...");
        CreateSurface();

        Debug.Log("[Vulkan] Picking physical device...");
        PickPhysicalDevice();

        Debug.Log("[Vulkan] Creating logical device...");
        CreateLogicalDevice();

        Debug.Log("[Vulkan] Creating command pool...");
        CreateCommandPool();

        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memProps);
        MemoryProperties = memProps;

        // Create swapchain from initial window size
        Debug.Log("[Vulkan] Creating swapchain...");
        var fbSize = Window.InternalWindow.FramebufferSize;
        CreateSwapchain((uint)fbSize.X, (uint)fbSize.Y);

        Debug.Log("[Vulkan] Creating sync objects...");
        CreateSyncObjects();

        _retiredResources = new List<(List<Framebuffer>, CommandBuffer)>[MaxFramesInFlight];
        for (int i = 0; i < MaxFramesInFlight; i++)
            _retiredResources[i] = [];

        // Initialize memory sub-allocator (replaces per-resource vkAllocateMemory)
        MemoryAllocator = new VKMemoryAllocator(this);
        Debug.Log("[Vulkan] Memory sub-allocator initialized.");

        // Initialize per-frame descriptor pool manager
        DescriptorPoolManager = new VKDescriptorPoolManager(this, MaxFramesInFlight);
        Debug.Log("[Vulkan] Descriptor pool manager initialized.");

        // Initialize per-frame uniform ring buffer for per-draw UBO data
        UniformRingBuffer = new VKUniformRingBuffer(this, MaxFramesInFlight);
        Debug.Log("[Vulkan] Uniform ring buffer initialized.");

        // Create a reusable fence for upload operations
        var uploadFenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Check(Vk.CreateFence(Device, &uploadFenceInfo, null, out _uploadFence));

        _capabilities = QueryCapabilities();
        _initialized = true;
        Debug.Log($"[Vulkan] Initialization complete — {_capabilities.DeviceName}");
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
            ApiVersion = Vk.Version13,
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
            // Always request VK_EXT_debug_utils when debug is enabled —
            // it enables debug markers in GPU profilers (RenderDoc, NSight)
            // even when validation layers are not installed.
            extensions.Add("VK_EXT_debug_utils");

            // Check if validation layer is actually available before requesting it
            uint layerCount = 0;
            Vk.EnumerateInstanceLayerProperties(&layerCount, null);
            var availableLayers = new LayerProperties[layerCount];
            fixed (LayerProperties* pLayers = availableLayers)
                Vk.EnumerateInstanceLayerProperties(&layerCount, pLayers);

            bool hasValidation = false;
            for (int i = 0; i < layerCount; i++)
            {
                fixed (byte* pName = availableLayers[i].LayerName)
                {
                    var name = SilkMarshal.PtrToString((nint)pName);
                    if (name == "VK_LAYER_KHRONOS_validation")
                    {
                        hasValidation = true;
                        break;
                    }
                }
            }

            if (hasValidation)
            {
                layers.Add("VK_LAYER_KHRONOS_validation");
                Debug.Log("[Vulkan] Validation layers enabled.");
            }
            else
            {
                Debug.LogWarning("[Vulkan] Validation layers requested but VK_LAYER_KHRONOS_validation not available. Install the Vulkan SDK for validation.");
            }
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

        // Try to acquire the debug utils extension for debug markers and object naming
        if (enableDebug && Vk.TryGetInstanceExtension<ExtDebugUtils>(VkInstance, out var debugUtils))
        {
            _debugUtils = debugUtils;
            Debug.Log("[Vulkan] VK_EXT_debug_utils acquired — debug markers and object naming enabled.");
        }
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
            var name = SilkMarshal.PtrToString((nint)props.DeviceName);
            Debug.Log($"[Vulkan]   GPU {i}: {name} (type={props.DeviceType})");
            if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
            {
                PhysicalDevice = devices[i];
            }
        }

        Vk.GetPhysicalDeviceProperties(PhysicalDevice, out var selectedProps);
        Debug.Log($"[Vulkan] Selected GPU: {SilkMarshal.PtrToString((nint)selectedProps.DeviceName)}");
    }

    private void CreateLogicalDevice()
    {
        uint queueFamilyCount = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &queueFamilyCount, null);
        var queueFamilies = stackalloc QueueFamilyProperties[(int)queueFamilyCount];
        Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &queueFamilyCount, queueFamilies);

        uint graphicsFamily = uint.MaxValue;
        uint presentFamily = uint.MaxValue;

        for (uint i = 0; i < queueFamilyCount; i++)
        {
            if (queueFamilies[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                graphicsFamily = i;

            if (_khrSurface != null && _surface.Handle != 0)
            {
                _khrSurface.GetPhysicalDeviceSurfaceSupport(PhysicalDevice, i, _surface, out var supported);
                if (supported)
                    presentFamily = i;
            }

            if (graphicsFamily != uint.MaxValue && presentFamily != uint.MaxValue)
                break;
        }

        if (graphicsFamily == uint.MaxValue)
            throw new InvalidOperationException("No graphics queue family found.");

        // Fall back to graphics family if no separate present family
        if (presentFamily == uint.MaxValue)
            presentFamily = graphicsFamily;

        GraphicsQueueFamily = graphicsFamily;
        PresentQueueFamily = presentFamily;
        Debug.Log($"[Vulkan] Queue families — graphics: {graphicsFamily}, present: {presentFamily}");

        // Build unique queue create infos
        float priority = 1.0f;
        var uniqueFamilies = new HashSet<uint> { graphicsFamily, presentFamily };
        var queueCreateInfos = new DeviceQueueCreateInfo[uniqueFamilies.Count];
        int idx = 0;
        foreach (var family in uniqueFamilies)
        {
            queueCreateInfos[idx++] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = family,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
        }

        // ── Query supported features and only request available ones ──
        Vk.GetPhysicalDeviceFeatures(PhysicalDevice, out var supportedFeatures);
        var features = new PhysicalDeviceFeatures
        {
            SamplerAnisotropy = supportedFeatures.SamplerAnisotropy,
            FillModeNonSolid = supportedFeatures.FillModeNonSolid,
            GeometryShader = supportedFeatures.GeometryShader,
            TessellationShader = supportedFeatures.TessellationShader,
            MultiDrawIndirect = supportedFeatures.MultiDrawIndirect,
            DepthClamp = supportedFeatures.DepthClamp,
        };

        Debug.Log($"[Vulkan] GPU features — Anisotropy={supportedFeatures.SamplerAnisotropy}, " +
                  $"FillModeNonSolid={supportedFeatures.FillModeNonSolid}, Geometry={supportedFeatures.GeometryShader}, " +
                  $"Tessellation={supportedFeatures.TessellationShader}, MultiDraw={supportedFeatures.MultiDrawIndirect}, " +
                  $"DepthClamp={supportedFeatures.DepthClamp}");

        // ── Query supported device extensions ──
        uint extCount = 0;
        Vk.EnumerateDeviceExtensionProperties(PhysicalDevice, (byte*)null, &extCount, null);
        var availableExts = new ExtensionProperties[extCount];
        fixed (ExtensionProperties* pExts = availableExts)
            Vk.EnumerateDeviceExtensionProperties(PhysicalDevice, (byte*)null, &extCount, pExts);

        var supportedExtNames = new HashSet<string>();
        for (int i = 0; i < extCount; i++)
        {
            fixed (byte* pName = availableExts[i].ExtensionName)
                supportedExtNames.Add(SilkMarshal.PtrToString((nint)pName) ?? string.Empty);
        }

        var extensions = new List<string>();

        if (supportedExtNames.Contains("VK_KHR_swapchain"))
            extensions.Add("VK_KHR_swapchain");
        else
            throw new InvalidOperationException("Required device extension VK_KHR_swapchain is not supported by this GPU.");

        if (supportedExtNames.Contains("VK_KHR_dynamic_rendering"))
        {
            extensions.Add("VK_KHR_dynamic_rendering");
            Debug.Log("[Vulkan] VK_KHR_dynamic_rendering is supported.");
        }
        else
        {
            Debug.LogWarning("[Vulkan] VK_KHR_dynamic_rendering not supported — some features may be limited.");
        }

        Debug.Log($"[Vulkan] Requesting {extensions.Count} device extensions: {string.Join(", ", extensions)}");

        var extPtrs = SilkMarshal.StringArrayToPtr(extensions.ToArray());

        fixed (DeviceQueueCreateInfo* pQueueInfos = queueCreateInfos)
        {
            var deviceCreateInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = (uint)queueCreateInfos.Length,
                PQueueCreateInfos = pQueueInfos,
                PEnabledFeatures = &features,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = (byte**)extPtrs,
            };

            Check(Vk.CreateDevice(PhysicalDevice, &deviceCreateInfo, null, out var device));
            Device = device;
        }

        SilkMarshal.Free(extPtrs);
        Debug.Log("[Vulkan] Logical device created successfully.");

        Vk.GetDeviceQueue(Device, graphicsFamily, 0, out var gQueue);
        GraphicsQueue = gQueue;

        Vk.GetDeviceQueue(Device, presentFamily, 0, out var pQueue);
        PresentQueue = pQueue;

        // Acquire the KHR swapchain extension from the device
        if (!Vk.TryGetDeviceExtension<KhrSwapchain>(VkInstance, Device, out _khrSwapchain))
            throw new InvalidOperationException("Failed to load VK_KHR_swapchain device extension.");
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

    private void CreateSurface()
    {
        if (!Vk.TryGetInstanceExtension<KhrSurface>(VkInstance, out _khrSurface))
            throw new InvalidOperationException("Failed to load VK_KHR_surface instance extension.");

        if (Window.InternalWindow is IVkSurface vkSurface)
        {
            _surface = vkSurface.Create<AllocationCallbacks>(VkInstance.ToHandle(), null).ToSurface();
        }
        else
        {
            throw new InvalidOperationException("Window does not support Vulkan surfaces (IVkSurface).");
        }
    }

    private void CreateSwapchain(uint width, uint height)
    {
        if (_khrSurface == null || _khrSwapchain == null)
            return;

        // Query surface capabilities
        _khrSurface.GetPhysicalDeviceSurfaceCapabilities(PhysicalDevice, _surface, out var capabilities);

        // Choose surface format (prefer BGRA8 SRGB)
        uint formatCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(PhysicalDevice, _surface, &formatCount, null);
        var formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* pFormats = formats)
            _khrSurface.GetPhysicalDeviceSurfaceFormats(PhysicalDevice, _surface, &formatCount, pFormats);

        var surfaceFormat = formats[0];
        foreach (var fmt in formats)
        {
            if (fmt.Format == Format.B8G8R8A8Unorm && fmt.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                surfaceFormat = fmt;
                break;
            }
        }
        _swapchainFormat = surfaceFormat.Format;

        // Choose present mode.
        // FIFO = true VSync (caps to monitor refresh, GPU idles between frames).
        // Mailbox = low-latency triple buffering (GPU renders as fast as possible,
        //           discards all but the latest frame — wastes GPU on static UIs).
        // Only use Mailbox when VSync is explicitly disabled.
        uint presentModeCount = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(PhysicalDevice, _surface, &presentModeCount, null);
        var presentModes = new PresentModeKHR[presentModeCount];
        fixed (PresentModeKHR* pModes = presentModes)
            _khrSurface.GetPhysicalDeviceSurfacePresentModes(PhysicalDevice, _surface, &presentModeCount, pModes);

        var presentMode = PresentModeKHR.FifoKhr; // Always available per spec
        if (!Window.VSync)
        {
            // Prefer Mailbox (low-latency, no tearing) over Immediate (tearing)
            foreach (var mode in presentModes)
            {
                if (mode == PresentModeKHR.MailboxKhr)
                {
                    presentMode = mode;
                    break;
                }
            }
        }

        // Choose extent
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            _swapchainExtent = capabilities.CurrentExtent;
        }
        else
        {
            _swapchainExtent = new Extent2D(
                Math.Clamp(width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp(height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));
        }

        // Image count (prefer min+1, clamped to max)
        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
            imageCount = capabilities.MaxImageCount;

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = _swapchainExtent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = _swapchain, // pass old for recreation
        };

        if (GraphicsQueueFamily != PresentQueueFamily)
        {
            var queueFamilyIndices = stackalloc uint[2] { GraphicsQueueFamily, PresentQueueFamily };
            createInfo.ImageSharingMode = SharingMode.Concurrent;
            createInfo.QueueFamilyIndexCount = 2;
            createInfo.PQueueFamilyIndices = queueFamilyIndices;
        }
        else
        {
            createInfo.ImageSharingMode = SharingMode.Exclusive;
        }

        Check(_khrSwapchain.CreateSwapchain(Device, &createInfo, null, out var newSwapchain));

        // Destroy old swapchain if recreating
        if (_swapchain.Handle != 0)
        {
            CleanupSwapchainResources();
            _khrSwapchain.DestroySwapchain(Device, _swapchain, null);
        }
        _swapchain = newSwapchain;

        // Retrieve swapchain images
        uint swapImageCount = 0;
        _khrSwapchain.GetSwapchainImages(Device, _swapchain, &swapImageCount, null);
        _swapchainImages = new Image[swapImageCount];
        fixed (Image* pImages = _swapchainImages)
            _khrSwapchain.GetSwapchainImages(Device, _swapchain, &swapImageCount, pImages);

        // Create image views
        _swapchainImageViews = new ImageView[swapImageCount];
        _swapchainTextures = new VKSwapchainImageTexture[swapImageCount];
        for (int i = 0; i < swapImageCount; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapchainFormat,
                Components = new ComponentMapping
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity,
                },
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            Check(Vk.CreateImageView(Device, &viewInfo, null, out _swapchainImageViews[i]));

            _swapchainTextures[i] = new VKSwapchainImageTexture(
                _swapchainImages[i], _swapchainImageViews[i],
                _swapchainExtent.Width, _swapchainExtent.Height,
                VKFormatHelper.FromVkFormat(_swapchainFormat));
        }

        _swapchainWidth = _swapchainExtent.Width;
        _swapchainHeight = _swapchainExtent.Height;
    }

    private void CleanupSwapchainResources()
    {
        foreach (var view in _swapchainImageViews)
        {
            if (view.Handle != 0)
                Vk.DestroyImageView(Device, view, null);
        }
        _swapchainImageViews = [];
        _swapchainTextures = [];
        _swapchainImages = [];
    }

    private void CreateSyncObjects()
    {
        _imageAvailableSemaphores = new Semaphore[MaxFramesInFlight];
        _renderFinishedSemaphores = new Semaphore[MaxFramesInFlight];
        _inFlightFences = new VkFence[MaxFramesInFlight];

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit, // start signaled so first WaitForFences succeeds
        };

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            Check(Vk.CreateSemaphore(Device, &semaphoreInfo, null, out _imageAvailableSemaphores[i]));
            Check(Vk.CreateSemaphore(Device, &semaphoreInfo, null, out _renderFinishedSemaphores[i]));
            Check(Vk.CreateFence(Device, &fenceInfo, null, out _inFlightFences[i]));
        }
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

        // When the first command list renders to the swapchain, consume
        // the image-available semaphore so the GPU waits for the acquired
        // image.  The render-finished semaphore and in-flight fence are
        // NOT attached here — they are always signalled in Present() via a
        // lightweight sync batch that is ordered after ALL frame submissions,
        // guaranteeing the fence covers every command buffer this frame.
        if (vkCmd.IsPresentTarget && !_frameSyncConsumed && _khrSwapchain != null)
        {
            var waitSem = _imageAvailableSemaphores[_currentFrame];
            var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &waitSem,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &cb,
            };
            Check(Vk.QueueSubmit(GraphicsQueue, 1, &submitInfo, default));
            _frameSyncConsumed = true;
        }
        else
        {
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cb,
            };
            Check(Vk.QueueSubmit(GraphicsQueue, 1, &submitInfo, default));
        }
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
        if (Device.Handle != 0)
            Vk.DeviceWaitIdle(Device);
    }

    #endregion

    #region Frame Management

    public override void ResizeSwapchain(uint width, uint height)
    {
        ThrowIfDisposed();
        if (width == 0 || height == 0)
        {
            // Minimised — mark dirty but don't recreate
            _framebufferResized = true;
            return;
        }

        Vk.DeviceWaitIdle(Device);
        CreateSwapchain(width, height);
    }

    #endregion

    #region Resource Updates

    /// <summary>
    /// Allocates a sub-region from the per-frame uniform ring buffer
    /// and copies data into it, avoiding per-draw buffer creation.
    /// </summary>
    public override (Buffer Buffer, uint Offset) AllocateTransientUniform(ReadOnlySpan<byte> data)
    {
        return UniformRingBuffer.Allocate(data);
    }

    public override void UpdateBuffer<T>(Buffer buffer, uint offsetInBytes, ReadOnlySpan<T> data)
    {
        ThrowIfDisposed();
        if (buffer is not VKBuffer vkBuffer)
            return;

        uint dataSize = (uint)(data.Length * Unsafe.SizeOf<T>());

        if (vkBuffer.MemoryAccess != MemoryAccess.GpuOnly)
        {
            // Directly write to the mapped memory from the sub-allocator
            var mappedPtr = vkBuffer.Allocation.GetMappedData();
            if (mappedPtr != null)
            {
                fixed (T* src = data)
                    System.Buffer.MemoryCopy(src, (byte*)mappedPtr + offsetInBytes, dataSize, dataSize);
            }
            else
            {
                // Fallback: map manually (shouldn't normally happen with the sub-allocator)
                void* mapped;
                Check(Vk.MapMemory(Device, vkBuffer.Allocation.Memory, vkBuffer.Allocation.Offset + offsetInBytes, dataSize, 0, &mapped));
                fixed (T* src = data)
                    System.Buffer.MemoryCopy(src, mapped, dataSize, dataSize);
                Vk.UnmapMemory(Device, vkBuffer.Allocation.Memory);
            }
        }
        else
        {
            // Use staging buffer for GPU-only memory
            var stagingDesc = new BufferDescriptor(dataSize, BufferUsage.CopySource, MemoryAccess.CpuToGpu);
            var staging = new VKBuffer(this, in stagingDesc);

            var mappedPtr = staging.Allocation.GetMappedData();
            fixed (T* src = data)
                System.Buffer.MemoryCopy(src, mappedPtr, dataSize, dataSize);

            var cmd = BeginSingleTimeCommands();
            var region = new BufferCopy { SrcOffset = 0, DstOffset = offsetInBytes, Size = dataSize };
            Vk.CmdCopyBuffer(cmd, staging.Handle, vkBuffer.Handle, 1, &region);
            EndSingleTimeCommands(cmd);

            if (_isBatching)
                TrackBatchResource(staging);
            else
                staging.Dispose();
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
        var staging = new VKBuffer(this, in stagingDesc);

        var mappedPtr = staging.Allocation.GetMappedData();
        fixed (byte* src = data)
            System.Buffer.MemoryCopy(src, mappedPtr, dataSize, dataSize);

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

        if (_isBatching)
            TrackBatchResource(staging);
        else
            staging.Dispose();
    }

    public override void ReadbackTexture(Texture texture, uint mipLevel, uint arrayLayer, Span<byte> destination)
    {
        ThrowIfDisposed();
        if (texture is not VKTexture vkTexture)
            return;

        // Readback requires synchronous completion — flush any pending batch first
        if (_isBatching)
            FlushUploadBatch();

        uint mipWidth = Math.Max(1, vkTexture.Width >> (int)mipLevel);
        uint mipHeight = Math.Max(1, vkTexture.Height >> (int)mipLevel);
        uint dataSize = (uint)destination.Length;

        // Create a host-visible staging buffer for the readback
        var stagingDesc = new BufferDescriptor(dataSize, BufferUsage.CopyDestination, MemoryAccess.GpuToCpu);
        using var staging = new VKBuffer(this, in stagingDesc);

        var cmd = BeginSingleTimeCommands();

        // Transition image to TransferSrcOptimal
        vkTexture.TransitionLayout(cmd, ImageLayout.TransferSrcOptimal, mipLevel, 1, arrayLayer, 1);

        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = VKFormatHelper.GetAspectFlags(vkTexture.Format),
                MipLevel = mipLevel,
                BaseArrayLayer = arrayLayer,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(mipWidth, mipHeight, Math.Max(1, vkTexture.Depth)),
        };
        Vk.CmdCopyImageToBuffer(cmd, vkTexture.Image, ImageLayout.TransferSrcOptimal, staging.Handle, 1, &region);

        // Transition back to ShaderReadOnlyOptimal
        vkTexture.TransitionLayout(cmd, ImageLayout.ShaderReadOnlyOptimal, mipLevel, 1, arrayLayer, 1);

        EndSingleTimeCommands(cmd);

        // Map the staging buffer and copy data to the destination span
        var mappedPtr = staging.Allocation.GetMappedData();
        if (mappedPtr != null)
        {
            new ReadOnlySpan<byte>(mappedPtr, (int)dataSize).CopyTo(destination);
        }
        else
        {
            void* mapped;
            Check(Vk.MapMemory(Device, staging.Allocation.Memory, staging.Allocation.Offset, dataSize, 0, &mapped));
            new ReadOnlySpan<byte>(mapped, (int)dataSize).CopyTo(destination);
            Vk.UnmapMemory(Device, staging.Allocation.Memory);
        }
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
        if (_swapchainTextures.Length == 0)
            throw new InvalidOperationException("Swapchain not created or has no images.");
        return _swapchainTextures[_currentImageIndex];
    }

    public override bool BeginFrame()
    {
        ThrowIfDisposed();
        if (_khrSwapchain == null)
            return true;

        // Wait for this frame's fence to be signaled (previous use of this frame slot)
        var fence = _inFlightFences[_currentFrame];
        Vk.WaitForFences(Device, 1, &fence, true, ulong.MaxValue);

        // Now that the fence is signaled, all GPU work from the previous use of this
        // frame slot has finished — destroy any retired framebuffers / command buffers.
        FlushRetiredResources(_currentFrame);

        // Reset per-frame sub-systems for this frame slot
        DescriptorPoolManager.BeginFrame(_currentFrame);
        UniformRingBuffer.BeginFrame(_currentFrame);

        // Acquire the next swapchain image
        var result = _khrSwapchain.AcquireNextImage(
            Device, _swapchain, ulong.MaxValue,
            _imageAvailableSemaphores[_currentFrame], default,
            ref _currentImageIndex);

        if (result == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return false;
        }

        if (result != Result.Success && result != Result.SuboptimalKhr)
            Check(result);

        // Only reset the fence if we know we're going to submit work
        Vk.ResetFences(Device, 1, &fence);
        _frameSyncConsumed = false;
        return true;
    }

    public override bool Present()
    {
        ThrowIfDisposed();
        if (_khrSwapchain == null)
            return true;

        // Always submit a lightweight sync batch at the end of the frame.
        // Because Vulkan queue submissions are strictly ordered, this batch
        // will complete only after ALL prior submissions (scene rendering,
        // PaperUI, ImGui, etc.) have finished on the GPU.  Attaching the
        // in-flight fence and render-finished semaphore here guarantees they
        // cover every command buffer submitted this frame.
        {
            var signalSem = _renderFinishedSemaphores[_currentFrame];
            var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

            var syncSubmit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 0,
                PCommandBuffers = null,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &signalSem,
            };

            // If no rendering submission consumed the image-available
            // semaphore, the sync batch must wait on it instead.
            if (!_frameSyncConsumed)
            {
                var waitSem = _imageAvailableSemaphores[_currentFrame];
                syncSubmit.WaitSemaphoreCount = 1;
                syncSubmit.PWaitSemaphores = &waitSem;
                syncSubmit.PWaitDstStageMask = &waitStage;
            }

            Check(Vk.QueueSubmit(GraphicsQueue, 1, &syncSubmit, _inFlightFences[_currentFrame]));
        }

        var waitSemaphore = _renderFinishedSemaphores[_currentFrame];
        var swapchain = _swapchain;
        var imageIndex = _currentImageIndex;

        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };

        var result = _khrSwapchain.QueuePresent(PresentQueue, &presentInfo);

        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr || _framebufferResized)
        {
            _framebufferResized = false;
            RecreateSwapchain();
            // Frame was presented (or discarded) — still advance
        }
        else if (result != Result.Success)
        {
            Check(result);
        }

        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
        return true;
    }

    private void RecreateSwapchain()
    {
        var fbSize = Window.InternalWindow.FramebufferSize;
        while (fbSize.X == 0 || fbSize.Y == 0)
        {
            // Window is minimised — wait for it to become visible again
            fbSize = Window.InternalWindow.FramebufferSize;
            Window.InternalWindow.DoEvents();
        }

        Vk.DeviceWaitIdle(Device);
        CreateSwapchain((uint)fbSize.X, (uint)fbSize.Y);
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
        // If batching is active, return the shared batch command buffer
        if (_isBatching)
            return _batchCmdBuffer;

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
        // If batching is active, defer submission to FlushUploadBatch
        if (_isBatching)
            return;

        Vk.EndCommandBuffer(commandBuffer);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        // Use a fence instead of QueueWaitIdle to avoid stalling unrelated GPU work
        var fence = _uploadFence;
        Check(Vk.QueueSubmit(GraphicsQueue, 1, &submitInfo, fence));
        Check(Vk.WaitForFences(Device, 1, &fence, true, ulong.MaxValue));
        Check(Vk.ResetFences(Device, 1, &fence));
        Vk.FreeCommandBuffers(Device, CommandPool, 1, &commandBuffer);
    }

    /// <summary>
    /// Tracks a staging resource for deferred disposal during upload batching.
    /// If not batching, the caller is responsible for disposal.
    /// </summary>
    internal void TrackBatchResource(IDisposable resource)
    {
        _batchResources.Add(resource);
    }

    /// <summary>
    /// Begins batching upload operations into a single command buffer.
    /// All subsequent <see cref="BeginSingleTimeCommands"/>/<see cref="EndSingleTimeCommands"/>
    /// calls will record into the shared batch buffer. Call <see cref="FlushUploadBatch"/>
    /// to submit all batched work at once, avoiding per-upload pipeline stalls.
    /// </summary>
    public void BeginUploadBatch()
    {
        if (_isBatching) return;
        _isBatching = true;

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = CommandPool,
            CommandBufferCount = 1,
        };

        Vk.AllocateCommandBuffers(Device, &allocInfo, out _batchCmdBuffer);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Vk.BeginCommandBuffer(_batchCmdBuffer, &beginInfo);
    }

    /// <summary>
    /// Submits all batched upload commands and waits for completion using a fence.
    /// Frees all staging resources that were tracked during the batch.
    /// </summary>
    public void FlushUploadBatch()
    {
        if (!_isBatching) return;
        _isBatching = false;

        Vk.EndCommandBuffer(_batchCmdBuffer);

        var cb = _batchCmdBuffer;
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
        };
        var fence = _uploadFence;
        Check(Vk.QueueSubmit(GraphicsQueue, 1, &submitInfo, fence));
        Check(Vk.WaitForFences(Device, 1, &fence, true, ulong.MaxValue));
        Check(Vk.ResetFences(Device, 1, &fence));
        Vk.FreeCommandBuffers(Device, CommandPool, 1, &cb);

        // Free staging resources
        foreach (var r in _batchResources)
            try { r.Dispose(); } catch { }
        _batchResources.Clear();
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

            // Swapchain present-target images start Undefined after AcquireNextImage
            // (first touch uses Clear/DontCare) and end in PresentSrcKhr.
            // When a *subsequent* render pass uses LoadOp.Load to preserve the
            // content already rendered to the swapchain, initialLayout must be
            // PresentSrcKhr — using Undefined would discard the contents.
            var initialLayout = key.IsPresentTarget
                ? (key.ColorLoadOps[i] == LoadOp.Load ? ImageLayout.PresentSrcKhr : ImageLayout.Undefined)
                : (key.ColorLoadOps[i] == LoadOp.Load ? ImageLayout.ColorAttachmentOptimal : ImageLayout.Undefined);
            var finalLayout = key.IsPresentTarget
                ? ImageLayout.PresentSrcKhr
                : ImageLayout.ColorAttachmentOptimal;

            attachments.Add(new AttachmentDescription
            {
                Format = VKFormatHelper.ToVkFormat(key.ColorFormats[i]),
                Samples = VKFormatHelper.ToVkSampleCount(key.SampleCount),
                LoadOp = VKFormatHelper.ToVkLoadOp(key.ColorLoadOps[i]),
                StoreOp = VKFormatHelper.ToVkStoreOp(key.ColorStoreOps[i]),
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = initialLayout,
                FinalLayout = finalLayout,
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

            // Exit dependency: ensure render pass writes are complete before
            // subsequent fragment shader reads or transfer operations.
            var exitDependency = new SubpassDependency
            {
                SrcSubpass = 0,
                DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit,
            };

            var dependencies = stackalloc SubpassDependency[] { dependency, exitDependency };

            var rpInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = (uint)attachments.Count,
                PAttachments = pAttachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 2,
                PDependencies = dependencies,
            };

            Check(Vk.CreateRenderPass(Device, &rpInfo, null, out var renderPass));
            _renderPassCache[key] = renderPass;
            return renderPass;
        }
    }

    /// <summary>
    /// Moves framebuffers and the command buffer from a disposed <see cref="VKCommandList"/>
    /// into the current frame slot's retirement list so they are destroyed only after the
    /// GPU has finished executing the commands that reference them.
    /// </summary>
    internal void RetireCommandListResources(List<Framebuffer> framebuffers, CommandBuffer commandBuffer)
    {
        _retiredResources[_currentFrame].Add((framebuffers, commandBuffer));
    }

    private void FlushRetiredResources(int frameSlot)
    {
        var list = _retiredResources[frameSlot];
        foreach (var (framebuffers, cb) in list)
        {
            foreach (var fb in framebuffers)
                Vk.DestroyFramebuffer(Device, fb, null);
            var cbLocal = cb;
            Vk.FreeCommandBuffers(Device, CommandPool, 1, &cbLocal);
        }
        list.Clear();
    }

    /// <summary>
    /// Sets a debug name on a Vulkan object via <c>VK_EXT_debug_utils</c>.
    /// No-op when debug utils are not available.
    /// </summary>
    internal void SetDebugName(ObjectType objectType, ulong handle, string name)
    {
        if (_debugUtils == null || string.IsNullOrEmpty(name))
            return;

        var namePtr = SilkMarshal.StringToPtr(name);
        try
        {
            var nameInfo = new DebugUtilsObjectNameInfoEXT
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = objectType,
                ObjectHandle = handle,
                PObjectName = (byte*)namePtr,
            };
            _debugUtils.SetDebugUtilsObjectName(Device, &nameInfo);
        }
        finally
        {
            SilkMarshal.Free(namePtr);
        }
    }

    /// <summary>
    /// Begins a debug label region in a command buffer via <c>VK_EXT_debug_utils</c>.
    /// </summary>
    internal void CmdBeginDebugLabel(CommandBuffer cb, string name, float r = 0, float g = 0.5f, float b = 1, float a = 1)
    {
        if (_debugUtils == null)
            return;

        var namePtr = SilkMarshal.StringToPtr(name);
        try
        {
            var labelInfo = new DebugUtilsLabelEXT
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = (byte*)namePtr,
            };
            labelInfo.Color[0] = r;
            labelInfo.Color[1] = g;
            labelInfo.Color[2] = b;
            labelInfo.Color[3] = a;
            _debugUtils.CmdBeginDebugUtilsLabel(cb, &labelInfo);
        }
        finally
        {
            SilkMarshal.Free(namePtr);
        }
    }

    /// <summary>
    /// Ends a debug label region in a command buffer via <c>VK_EXT_debug_utils</c>.
    /// </summary>
    internal void CmdEndDebugLabel(CommandBuffer cb)
    {
        _debugUtils?.CmdEndDebugUtilsLabel(cb);
    }

    /// <summary>
    /// Inserts a single debug label in a command buffer via <c>VK_EXT_debug_utils</c>.
    /// </summary>
    internal void CmdInsertDebugLabel(CommandBuffer cb, string name, float r = 1, float g = 1, float b = 0, float a = 1)
    {
        if (_debugUtils == null)
            return;

        var namePtr = SilkMarshal.StringToPtr(name);
        try
        {
            var labelInfo = new DebugUtilsLabelEXT
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = (byte*)namePtr,
            };
            labelInfo.Color[0] = r;
            labelInfo.Color[1] = g;
            labelInfo.Color[2] = b;
            labelInfo.Color[3] = a;
            _debugUtils.CmdInsertDebugUtilsLabel(cb, &labelInfo);
        }
        finally
        {
            SilkMarshal.Free(namePtr);
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
        // Guard: if Vk API was never obtained, nothing to tear down.
        if (Vk is null)
            return;

        bool hasDevice = Device.Handle != 0;

        // Flush all deferred deletions before tearing down
        if (hasDevice)
        {
            for (int i = 0; i < _retiredResources.Length; i++)
                FlushRetiredResources(i);
        }

        // Dispose new sub-systems before destroying the device
        UniformRingBuffer?.Dispose();
        DescriptorPoolManager?.Dispose();
        MemoryAllocator?.Dispose();

        if (hasDevice && _uploadFence.Handle != 0)
            Vk.DestroyFence(Device, _uploadFence, null);

        // Destroy sync objects
        if (hasDevice)
        {
            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                if (i < _imageAvailableSemaphores.Length && _imageAvailableSemaphores[i].Handle != 0)
                    Vk.DestroySemaphore(Device, _imageAvailableSemaphores[i], null);
                if (i < _renderFinishedSemaphores.Length && _renderFinishedSemaphores[i].Handle != 0)
                    Vk.DestroySemaphore(Device, _renderFinishedSemaphores[i], null);
                if (i < _inFlightFences.Length && _inFlightFences[i].Handle != 0)
                    Vk.DestroyFence(Device, _inFlightFences[i], null);
            }
        }

        // Destroy swapchain resources
        if (hasDevice)
        {
            CleanupSwapchainResources();
            if (_khrSwapchain != null && _swapchain.Handle != 0)
                _khrSwapchain.DestroySwapchain(Device, _swapchain, null);

            foreach (var rp in _renderPassCache.Values)
                Vk.DestroyRenderPass(Device, rp, null);
            _renderPassCache.Clear();

            if (CommandPool.Handle != 0)
                Vk.DestroyCommandPool(Device, CommandPool, null);
        }

        if (hasDevice)
            Vk.DestroyDevice(Device, null);

        // Destroy surface before instance
        if (_khrSurface != null && _surface.Handle != 0 && VkInstance.Handle != 0)
            _khrSurface.DestroySurface(VkInstance, _surface, null);

        if (VkInstance.Handle != 0)
            Vk.DestroyInstance(VkInstance, null);

        _debugUtils?.Dispose();
        _debugUtils = null;

        Vk.Dispose();
    }
}

/// <summary>
/// Texture wrapping a Vulkan swapchain image.
/// The image and view are owned by the swapchain — this wrapper does not destroy them.
/// </summary>
internal class VKSwapchainImageTexture : Texture
{
    internal Image Image { get; }
    internal ImageView ImageView { get; }

    internal VKSwapchainImageTexture(Image image, ImageView imageView, uint width, uint height, TextureFormat format)
    {
        Image = image;
        ImageView = imageView;
        Dimension = TextureDimension.Texture2D;
        Width = width;
        Height = height;
        Depth = 1;
        MipLevels = 1;
        ArrayLayers = 1;
        Format = format;
        Usage = TextureUsage.RenderTarget;
        SampleCount = SampleCount.Count1;
    }

    /// <summary>
    /// Updates the dimensions when the swapchain is recreated.
    /// </summary>
    internal void UpdateDimensions(uint width, uint height)
    {
        Width = width;
        Height = height;
    }

    protected override void DisposeResources()
    {
        // Swapchain images are owned by the swapchain — nothing to destroy here.
    }
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
    public bool IsPresentTarget;

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
        hash.Add(IsPresentTarget);
        return hash.ToHashCode();
    }

    public override readonly bool Equals(object? obj) => obj is RenderPassKey key && Equals(key);

    public readonly bool Equals(RenderPassKey other)
    {
        if (DepthFormat != other.DepthFormat || DepthLoadOp != other.DepthLoadOp || DepthStoreOp != other.DepthStoreOp ||
            StencilLoadOp != other.StencilLoadOp || StencilStoreOp != other.StencilStoreOp || SampleCount != other.SampleCount ||
            IsPresentTarget != other.IsPresentTarget)
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
