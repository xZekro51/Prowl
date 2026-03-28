// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Xunit;

using Prowl.Runtime.Graphite;
using Prowl.Runtime.Graphite.Vulkan;
using Prowl.Vector;

using VkFormat = Silk.NET.Vulkan.Format;
using VkSampleCountFlags = Silk.NET.Vulkan.SampleCountFlags;
using VkImageAspectFlags = Silk.NET.Vulkan.ImageAspectFlags;
using VkDescriptorType = Silk.NET.Vulkan.DescriptorType;
using VkShaderStageFlags = Silk.NET.Vulkan.ShaderStageFlags;
using VkAttachmentLoadOp = Silk.NET.Vulkan.AttachmentLoadOp;
using VkAttachmentStoreOp = Silk.NET.Vulkan.AttachmentStoreOp;

namespace Prowl.Runtime.Test.Graphite;

/// <summary>
/// Tests that verify Vulkan rendering pipeline data structures and helper logic.
/// These cover format conversions, render pass key hashing/equality, resource barrier
/// construction, and the swapchain blit rendering flow using the GL backend as a proxy.
/// </summary>
public class VulkanPipelineTests
{
    #region VKFormatHelper Tests

    [Theory]
    [InlineData(TextureFormat.RGBA8Unorm, VkFormat.R8G8B8A8Unorm)]
    [InlineData(TextureFormat.BGRA8Unorm, VkFormat.B8G8R8A8Unorm)]
    [InlineData(TextureFormat.BGRA8UnormSrgb, VkFormat.B8G8R8A8Srgb)]
    [InlineData(TextureFormat.R16Float, VkFormat.R16Sfloat)]
    [InlineData(TextureFormat.RGBA16Float, VkFormat.R16G16B16A16Sfloat)]
    [InlineData(TextureFormat.RGBA32Float, VkFormat.R32G32B32A32Sfloat)]
    [InlineData(TextureFormat.Depth32Float, VkFormat.D32Sfloat)]
    [InlineData(TextureFormat.Depth24PlusStencil8, VkFormat.D24UnormS8Uint)]
    public void ToVkFormat_MapsCorrectly(TextureFormat input, VkFormat expected)
    {
        Assert.Equal(expected, VKFormatHelper.ToVkFormat(input));
    }

    [Theory]
    [InlineData(VkFormat.R8G8B8A8Unorm, TextureFormat.RGBA8Unorm)]
    [InlineData(VkFormat.B8G8R8A8Unorm, TextureFormat.BGRA8Unorm)]
    [InlineData(VkFormat.B8G8R8A8Srgb, TextureFormat.BGRA8UnormSrgb)]
    [InlineData(VkFormat.D32Sfloat, TextureFormat.Depth32Float)]
    public void FromVkFormat_RoundTripsCorrectly(VkFormat input, TextureFormat expected)
    {
        Assert.Equal(expected, VKFormatHelper.FromVkFormat(input));
    }

    [Theory]
    [InlineData(TextureFormat.RGBA8Unorm, TextureFormat.RGBA8Unorm)]
    [InlineData(TextureFormat.BGRA8Unorm, TextureFormat.BGRA8Unorm)]
    [InlineData(TextureFormat.RGBA16Float, TextureFormat.RGBA16Float)]
    [InlineData(TextureFormat.Depth32Float, TextureFormat.Depth32Float)]
    public void ToVkFormat_FromVkFormat_RoundTrip(TextureFormat format, TextureFormat expected)
    {
        var vkFormat = VKFormatHelper.ToVkFormat(format);
        var roundTripped = VKFormatHelper.FromVkFormat(vkFormat);
        Assert.Equal(expected, roundTripped);
    }

    [Theory]
    [InlineData(SampleCount.Count1, VkSampleCountFlags.Count1Bit)]
    [InlineData(SampleCount.Count4, VkSampleCountFlags.Count4Bit)]
    [InlineData(SampleCount.Count8, VkSampleCountFlags.Count8Bit)]
    public void ToVkSampleCount_MapsCorrectly(SampleCount input, VkSampleCountFlags expected)
    {
        Assert.Equal(expected, VKFormatHelper.ToVkSampleCount(input));
    }

    [Theory]
    [InlineData(TextureFormat.Depth16Unorm, true)]
    [InlineData(TextureFormat.Depth24Plus, true)]
    [InlineData(TextureFormat.Depth32Float, true)]
    [InlineData(TextureFormat.Depth24PlusStencil8, true)]
    [InlineData(TextureFormat.Depth32FloatStencil8, true)]
    [InlineData(TextureFormat.RGBA8Unorm, false)]
    [InlineData(TextureFormat.RGBA16Float, false)]
    public void IsDepthFormat_ClassifiesCorrectly(TextureFormat format, bool expected)
    {
        Assert.Equal(expected, VKFormatHelper.IsDepthFormat(format));
    }

    [Theory]
    [InlineData(TextureFormat.RGBA8Unorm, VkImageAspectFlags.ColorBit)]
    [InlineData(TextureFormat.Depth32Float, VkImageAspectFlags.DepthBit)]
    [InlineData(TextureFormat.Depth24PlusStencil8, VkImageAspectFlags.DepthBit | VkImageAspectFlags.StencilBit)]
    public void GetAspectFlags_ReturnsCorrectAspect(TextureFormat format, VkImageAspectFlags expected)
    {
        Assert.Equal(expected, VKFormatHelper.GetAspectFlags(format));
    }

    [Theory]
    [InlineData(TextureFormat.R8Unorm, 1u)]
    [InlineData(TextureFormat.RG8Unorm, 2u)]
    [InlineData(TextureFormat.RGBA8Unorm, 4u)]
    [InlineData(TextureFormat.RGBA16Float, 8u)]
    [InlineData(TextureFormat.RGBA32Float, 16u)]
    [InlineData(TextureFormat.Depth16Unorm, 2u)]
    [InlineData(TextureFormat.Depth32Float, 4u)]
    public void GetFormatSizeInBytes_ReturnsCorrectSize(TextureFormat format, uint expected)
    {
        Assert.Equal(expected, VKFormatHelper.GetFormatSizeInBytes(format));
    }

    [Theory]
    [InlineData(TextureFormat.RGBA8UnormSrgb, true)]
    [InlineData(TextureFormat.BGRA8UnormSrgb, true)]
    [InlineData(TextureFormat.BC7UnormSrgb, true)]
    [InlineData(TextureFormat.RGBA8Unorm, false)]
    [InlineData(TextureFormat.RGBA16Float, false)]
    public void IsSrgbFormat_ClassifiesCorrectly(TextureFormat format, bool expected)
    {
        Assert.Equal(expected, VKFormatHelper.IsSrgbFormat(format));
    }

    [Theory]
    [InlineData(TextureFormat.BC1Unorm, true)]
    [InlineData(TextureFormat.BC7Unorm, true)]
    [InlineData(TextureFormat.BC7UnormSrgb, true)]
    [InlineData(TextureFormat.RGBA8Unorm, false)]
    public void IsCompressedFormat_ClassifiesCorrectly(TextureFormat format, bool expected)
    {
        Assert.Equal(expected, VKFormatHelper.IsCompressedFormat(format));
    }

    [Theory]
    [InlineData(BindingType.UniformBuffer, VkDescriptorType.UniformBuffer)]
    [InlineData(BindingType.StorageBuffer, VkDescriptorType.StorageBuffer)]
    [InlineData(BindingType.CombinedTextureSampler, VkDescriptorType.CombinedImageSampler)]
    [InlineData(BindingType.SampledTexture, VkDescriptorType.SampledImage)]
    [InlineData(BindingType.StorageTexture, VkDescriptorType.StorageImage)]
    public void ToVkDescriptorType_MapsCorrectly(BindingType input, VkDescriptorType expected)
    {
        Assert.Equal(expected, VKFormatHelper.ToVkDescriptorType(input));
    }

    [Theory]
    [InlineData(BindingType.UniformBuffer, VkDescriptorType.UniformBufferDynamic)]
    [InlineData(BindingType.StorageBuffer, VkDescriptorType.StorageBufferDynamic)]
    public void ToVkDynamicDescriptorType_MapsCorrectly(BindingType input, VkDescriptorType expected)
    {
        Assert.Equal(expected, VKFormatHelper.ToVkDynamicDescriptorType(input));
    }

    [Fact]
    public void ToVkShaderStageFlags_CombinesMultipleStages()
    {
        var combined = ShaderStage.Vertex | ShaderStage.Fragment;
        var flags = VKFormatHelper.ToVkShaderStageFlags(combined);
        Assert.True(flags.HasFlag(VkShaderStageFlags.VertexBit));
        Assert.True(flags.HasFlag(VkShaderStageFlags.FragmentBit));
        Assert.False(flags.HasFlag(VkShaderStageFlags.ComputeBit));
    }

    [Fact]
    public void ToVkVertexFormat_MapsCorrectly()
    {
        Assert.Equal(VkFormat.R32Sfloat, VKFormatHelper.ToVkVertexFormat(Prowl.Runtime.Graphite.VertexFormat.Float));
        Assert.Equal(VkFormat.R32G32Sfloat, VKFormatHelper.ToVkVertexFormat(Prowl.Runtime.Graphite.VertexFormat.Float2));
        Assert.Equal(VkFormat.R32G32B32Sfloat, VKFormatHelper.ToVkVertexFormat(Prowl.Runtime.Graphite.VertexFormat.Float3));
        Assert.Equal(VkFormat.R32G32B32A32Sfloat, VKFormatHelper.ToVkVertexFormat(Prowl.Runtime.Graphite.VertexFormat.Float4));
        Assert.Equal(VkFormat.R8G8B8A8Unorm, VKFormatHelper.ToVkVertexFormat(Prowl.Runtime.Graphite.VertexFormat.UByte4Norm));
    }

    [Theory]
    [InlineData(LoadOp.Load, VkAttachmentLoadOp.Load)]
    [InlineData(LoadOp.Clear, VkAttachmentLoadOp.Clear)]
    [InlineData(LoadOp.DontCare, VkAttachmentLoadOp.DontCare)]
    public void ToVkLoadOp_MapsCorrectly(LoadOp input, VkAttachmentLoadOp expected)
    {
        Assert.Equal(expected, VKFormatHelper.ToVkLoadOp(input));
    }

    [Theory]
    [InlineData(StoreOp.Store, VkAttachmentStoreOp.Store)]
    [InlineData(StoreOp.DontCare, VkAttachmentStoreOp.DontCare)]
    public void ToVkStoreOp_MapsCorrectly(StoreOp input, VkAttachmentStoreOp expected)
    {
        Assert.Equal(expected, VKFormatHelper.ToVkStoreOp(input));
    }

    #endregion

    #region RenderPassKey Tests

    [Fact]
    public void RenderPassKey_IdenticalKeys_AreEqual()
    {
        var key1 = new RenderPassKey
        {
            ColorFormats = [TextureFormat.BGRA8Unorm],
            ColorLoadOps = [LoadOp.Clear],
            ColorStoreOps = [StoreOp.Store],
            HasResolve = [false],
            SampleCount = SampleCount.Count1,
            IsPresentTarget = true,
        };

        var key2 = new RenderPassKey
        {
            ColorFormats = [TextureFormat.BGRA8Unorm],
            ColorLoadOps = [LoadOp.Clear],
            ColorStoreOps = [StoreOp.Store],
            HasResolve = [false],
            SampleCount = SampleCount.Count1,
            IsPresentTarget = true,
        };

        Assert.Equal(key1, key2);
        Assert.Equal(key1.GetHashCode(), key2.GetHashCode());
    }

    [Fact]
    public void RenderPassKey_DifferentFormat_AreNotEqual()
    {
        var key1 = new RenderPassKey
        {
            ColorFormats = [TextureFormat.BGRA8Unorm],
            ColorLoadOps = [LoadOp.Clear],
            ColorStoreOps = [StoreOp.Store],
            SampleCount = SampleCount.Count1,
        };

        var key2 = new RenderPassKey
        {
            ColorFormats = [TextureFormat.RGBA8Unorm],
            ColorLoadOps = [LoadOp.Clear],
            ColorStoreOps = [StoreOp.Store],
            SampleCount = SampleCount.Count1,
        };

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void RenderPassKey_DifferentPresentTarget_AreNotEqual()
    {
        var key1 = new RenderPassKey
        {
            ColorFormats = [TextureFormat.BGRA8Unorm],
            ColorLoadOps = [LoadOp.Clear],
            ColorStoreOps = [StoreOp.Store],
            SampleCount = SampleCount.Count1,
            IsPresentTarget = true,
        };

        var key2 = new RenderPassKey
        {
            ColorFormats = [TextureFormat.BGRA8Unorm],
            ColorLoadOps = [LoadOp.Clear],
            ColorStoreOps = [StoreOp.Store],
            SampleCount = SampleCount.Count1,
            IsPresentTarget = false,
        };

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void RenderPassKey_WithDepth_Equals()
    {
        var key1 = new RenderPassKey
        {
            ColorFormats = [TextureFormat.RGBA8Unorm],
            ColorLoadOps = [LoadOp.Clear],
            ColorStoreOps = [StoreOp.Store],
            DepthFormat = TextureFormat.Depth32Float,
            DepthLoadOp = LoadOp.Clear,
            DepthStoreOp = StoreOp.Store,
            SampleCount = SampleCount.Count1,
        };

        var key2 = new RenderPassKey
        {
            ColorFormats = [TextureFormat.RGBA8Unorm],
            ColorLoadOps = [LoadOp.Clear],
            ColorStoreOps = [StoreOp.Store],
            DepthFormat = TextureFormat.Depth32Float,
            DepthLoadOp = LoadOp.Clear,
            DepthStoreOp = StoreOp.Store,
            SampleCount = SampleCount.Count1,
        };

        Assert.Equal(key1, key2);
        Assert.Equal(key1.GetHashCode(), key2.GetHashCode());
    }

    [Fact]
    public void RenderPassKey_DifferentDepthFormat_AreNotEqual()
    {
        var key1 = new RenderPassKey
        {
            ColorFormats = [TextureFormat.RGBA8Unorm],
            ColorLoadOps = [LoadOp.Clear],
            ColorStoreOps = [StoreOp.Store],
            DepthFormat = TextureFormat.Depth32Float,
            DepthLoadOp = LoadOp.Clear,
            DepthStoreOp = StoreOp.Store,
            SampleCount = SampleCount.Count1,
        };

        var key2 = new RenderPassKey
        {
            ColorFormats = [TextureFormat.RGBA8Unorm],
            ColorLoadOps = [LoadOp.Clear],
            ColorStoreOps = [StoreOp.Store],
            DepthFormat = TextureFormat.Depth24PlusStencil8,
            DepthLoadOp = LoadOp.Clear,
            DepthStoreOp = StoreOp.Store,
            SampleCount = SampleCount.Count1,
        };

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void RenderPassKey_MultipleColorAttachments_Equals()
    {
        var key1 = new RenderPassKey
        {
            ColorFormats = [TextureFormat.RGBA8Unorm, TextureFormat.RGBA16Float, TextureFormat.R32Float],
            ColorLoadOps = [LoadOp.Clear, LoadOp.Clear, LoadOp.Clear],
            ColorStoreOps = [StoreOp.Store, StoreOp.Store, StoreOp.Store],
            HasResolve = [false, false, false],
            DepthFormat = TextureFormat.Depth32Float,
            DepthLoadOp = LoadOp.Clear,
            DepthStoreOp = StoreOp.DontCare,
            SampleCount = SampleCount.Count1,
        };

        var key2 = new RenderPassKey
        {
            ColorFormats = [TextureFormat.RGBA8Unorm, TextureFormat.RGBA16Float, TextureFormat.R32Float],
            ColorLoadOps = [LoadOp.Clear, LoadOp.Clear, LoadOp.Clear],
            ColorStoreOps = [StoreOp.Store, StoreOp.Store, StoreOp.Store],
            HasResolve = [false, false, false],
            DepthFormat = TextureFormat.Depth32Float,
            DepthLoadOp = LoadOp.Clear,
            DepthStoreOp = StoreOp.DontCare,
            SampleCount = SampleCount.Count1,
        };

        Assert.Equal(key1, key2);
        Assert.Equal(key1.GetHashCode(), key2.GetHashCode());
    }

    [Fact]
    public void RenderPassKey_DifferentLoadOp_AreNotEqual()
    {
        var key1 = new RenderPassKey
        {
            ColorFormats = [TextureFormat.RGBA8Unorm],
            ColorLoadOps = [LoadOp.Clear],
            ColorStoreOps = [StoreOp.Store],
            SampleCount = SampleCount.Count1,
        };

        var key2 = new RenderPassKey
        {
            ColorFormats = [TextureFormat.RGBA8Unorm],
            ColorLoadOps = [LoadOp.Load],
            ColorStoreOps = [StoreOp.Store],
            SampleCount = SampleCount.Count1,
        };

        Assert.NotEqual(key1, key2);
    }

    #endregion

    #region ResourceBarrier Tests

    [Fact]
    public void ResourceBarrier_StoresStatesCorrectly()
    {
        var barrier = new ResourceBarrier(
            null!,
            ResourceState.RenderTarget,
            ResourceState.ShaderResource);

        Assert.Equal(ResourceState.RenderTarget, barrier.StateBefore);
        Assert.Equal(ResourceState.ShaderResource, barrier.StateAfter);
    }

    [Fact]
    public void ResourceBarrier_UndefinedToShaderResource()
    {
        var barrier = new ResourceBarrier(
            null!,
            ResourceState.Undefined,
            ResourceState.ShaderResource);

        Assert.Equal(ResourceState.Undefined, barrier.StateBefore);
        Assert.Equal(ResourceState.ShaderResource, barrier.StateAfter);
    }

    [Fact]
    public void ResourceBarrier_RenderTargetToPresent()
    {
        var barrier = new ResourceBarrier(
            null!,
            ResourceState.RenderTarget,
            ResourceState.Present);

        Assert.Equal(ResourceState.RenderTarget, barrier.StateBefore);
        Assert.Equal(ResourceState.Present, barrier.StateAfter);
    }

    #endregion

    #region RenderPassLayout Tests

    [Fact]
    public void RenderPassLayout_SingleColor_HasCorrectFormat()
    {
        var layout = new RenderPassLayout([TextureFormat.BGRA8Unorm]);

        Assert.NotNull(layout.ColorFormats);
        Assert.Single(layout.ColorFormats);
        Assert.Equal(TextureFormat.BGRA8Unorm, layout.ColorFormats[0]);
        Assert.Null(layout.DepthStencilFormat);
    }

    [Fact]
    public void RenderPassLayout_WithDepth_HasBothFormats()
    {
        var layout = new RenderPassLayout(
            [TextureFormat.RGBA8Unorm],
            TextureFormat.Depth32Float);

        Assert.Single(layout.ColorFormats);
        Assert.Equal(TextureFormat.RGBA8Unorm, layout.ColorFormats[0]);
        Assert.Equal(TextureFormat.Depth32Float, layout.DepthStencilFormat);
    }

    [Fact]
    public void RenderPassLayout_MultipleColors_PreservesOrder()
    {
        var formats = new[]
        {
            TextureFormat.RGBA8Unorm,
            TextureFormat.RGBA16Float,
            TextureFormat.R32Float,
            TextureFormat.RGBA8Unorm,
        };

        var layout = new RenderPassLayout(formats, TextureFormat.Depth32Float);

        Assert.Equal(4, layout.ColorFormats.Length);
        for (int i = 0; i < formats.Length; i++)
            Assert.Equal(formats[i], layout.ColorFormats[i]);
    }

    #endregion
}

/// <summary>
/// Tests that verify the swapchain blit rendering flow using the GL backend.
/// These validate the same code path (resource barrier, render pass, fullscreen blit)
/// that BlitToSwapchainGraphite uses on Vulkan.
/// </summary>
[Collection("Graphite")]
public class SwapchainBlitFlowTests
{
    private readonly GraphiteTestFixture _fixture;

    public SwapchainBlitFlowTests(GraphiteTestFixture fixture)
    {
        _fixture = fixture;
    }

    private static void ReadBufferData<T>(Prowl.Runtime.Graphite.Buffer buffer, Span<T> destination) where T : unmanaged
    {
        if (buffer is not Prowl.Runtime.Graphite.OpenGL.GLBuffer glBuffer)
            throw new InvalidOperationException("Buffer is not a GLBuffer");

        unsafe
        {
            var gl = ((Prowl.Runtime.Graphite.OpenGL.GLGraphiteDevice)Prowl.Runtime.Graphics.Graphite).GLContext!;
            gl.BindBuffer(Silk.NET.OpenGL.BufferTargetARB.ArrayBuffer, glBuffer.Handle);
            fixed (T* ptr = destination)
            {
                gl.GetBufferSubData(Silk.NET.OpenGL.BufferTargetARB.ArrayBuffer, 0,
                    (nuint)(destination.Length * sizeof(T)), ptr);
            }
            gl.BindBuffer(Silk.NET.OpenGL.BufferTargetARB.ArrayBuffer, 0);
        }
    }

    #region Shared Shader Sources

    private const string BlitVertexShader = @"
#version 430 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aTexCoord;

out vec2 vTexCoord;

void main()
{
    gl_Position = vec4(aPosition, 1.0);
    vTexCoord = aTexCoord;
}
";

    private const string BlitFragmentShader = @"
#version 430 core
in vec2 vTexCoord;
out vec4 FragColor;

layout(binding = 0) uniform sampler2D _MainTex;

void main()
{
    FragColor = texture(_MainTex, vTexCoord);
}
";

    private const string RedFillFragmentShader = @"
#version 430 core
out vec4 FragColor;

void main()
{
    FragColor = vec4(1.0, 0.0, 0.0, 1.0);
}
";

    private const string FullscreenTriVertexShader = @"
#version 430 core

void main()
{
    vec2 positions[3] = vec2[](
        vec2(-1.0, -1.0),
        vec2( 3.0, -1.0),
        vec2(-1.0,  3.0)
    );
    gl_Position = vec4(positions[gl_VertexID], 0.0, 1.0);
}
";

    #endregion

    /// <summary>
    /// Validates the swapchain blit flow: render to an offscreen target,
    /// then blit that target to a second render target (simulating swapchain).
    /// This mirrors BlitToSwapchainGraphite's flow: source → barrier → sample → draw.
    /// </summary>
    [Fact]
    public void BlitFlow_SourceToDestination_BlitsCorrectColor()
    {
        // Step 1: Create "offscreen" source render target (simulates composed output)
        using var sourceTex = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 4, Height = 4, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderTarget | TextureUsage.Sampled | TextureUsage.CopySource,
            SampleCount = SampleCount.Count1,
        });

        // Step 2: Create "swapchain" destination render target
        using var destTex = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 4, Height = 4, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderTarget | TextureUsage.CopySource,
            SampleCount = SampleCount.Count1,
        });

        using var readbackBuffer = _fixture.Device.CreateBuffer(new BufferDescriptor
        {
            SizeInBytes = 4 * 4 * 4, // 4x4 RGBA
            Usage = BufferUsage.CopyDestination,
            MemoryAccess = MemoryAccess.GpuToCpu,
        });

        // Step 3: Fill source with solid red (simulates scene rendering)
        using var fillVs = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.VertexGLSL(FullscreenTriVertexShader));
        using var fillFs = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.FragmentGLSL(RedFillFragmentShader));
        using var fillPipeline = _fixture.Device.CreatePipelineState(new PipelineStateDescriptor
        {
            VertexShader = fillVs,
            FragmentShader = fillFs,
            Topology = Prowl.Runtime.Graphite.PrimitiveTopology.TriangleList,
        });

        using var cmd1 = _fixture.Device.CreateCommandList();
        using var fence1 = _fixture.Device.CreateFence(false);

        cmd1.Begin();
        cmd1.BeginRenderPass(new RenderPassDescriptor
        {
            ColorAttachments =
            [
                RenderPassColorAttachment.Clear(sourceTex, Float4.Zero)
            ]
        });
        cmd1.SetViewport(0, 0, 4, 4);
        cmd1.SetPipeline(fillPipeline);
        cmd1.Draw(3);
        cmd1.EndRenderPass();
        cmd1.End();

        _fixture.Device.SubmitCommands(cmd1, fence1);
        fence1.Wait();

        // Step 4: Blit source → dest (mirrors BlitToSwapchainGraphite)
        using var sampler = _fixture.Device.CreateSampler(SamplerDescriptor.PointClamp);

        using var blitVs = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.VertexGLSL(BlitVertexShader));
        using var blitFs = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.FragmentGLSL(BlitFragmentShader));
        using var blitPipeline = _fixture.Device.CreatePipelineState(new PipelineStateDescriptor
        {
            VertexShader = blitVs,
            FragmentShader = blitFs,
            Topology = Prowl.Runtime.Graphite.PrimitiveTopology.TriangleList,
        });

        using var texBgl = _fixture.Device.CreateBindGroupLayout(new BindGroupLayoutDescriptor(
            BindGroupLayoutEntry.CombinedTextureSampler(0, ShaderStage.Fragment, name: "_MainTex")));
        using var texBindGroup = _fixture.Device.CreateBindGroup(new BindGroupDescriptor(
            texBgl, BindGroupEntry.ForTextureSampler(0, sourceTex, sampler)));

        using var cmd2 = _fixture.Device.CreateCommandList();
        using var fence2 = _fixture.Device.CreateFence(false);

        cmd2.Begin();

        // On Vulkan this is where the ResourceBarrier (RenderTarget → ShaderResource) would go.
        // The GL backend doesn't require explicit barriers, but we issue one for flow correctness.
        cmd2.ResourceBarrier(new ResourceBarrier(sourceTex, ResourceState.RenderTarget, ResourceState.ShaderResource));

        cmd2.BeginRenderPass(new RenderPassDescriptor
        {
            ColorAttachments =
            [
                RenderPassColorAttachment.Clear(destTex, Float4.Zero)
            ]
        });
        cmd2.SetViewport(0, 0, 4, 4);
        cmd2.SetPipeline(blitPipeline);
        cmd2.SetBindGroup(0, texBindGroup);
        cmd2.Draw(3);
        cmd2.EndRenderPass();

        cmd2.CopyTextureToBuffer(new BufferTextureCopy
        {
            Texture = destTex,
            Buffer = readbackBuffer,
            MipLevel = 0, ArrayLayer = 0,
            X = 0, Y = 0, Z = 0,
            Width = 4, Height = 4, Depth = 1,
            BufferOffset = 0,
        });
        cmd2.End();

        _fixture.Device.SubmitCommands(cmd2, fence2);
        fence2.Wait();

        // Read back and verify the center pixel is red
        var pixelData = new byte[4 * 4 * 4];
        ReadBufferData<byte>(readbackBuffer, pixelData);

        // Check pixel at (1,1) — should be red (255, 0, 0, 255)
        int offset = (1 * 4 + 1) * 4;
        Assert.Equal(255, pixelData[offset + 0]); // R
        Assert.Equal(0, pixelData[offset + 1]);   // G
        Assert.Equal(0, pixelData[offset + 2]);   // B
        Assert.Equal(255, pixelData[offset + 3]); // A
    }

    /// <summary>
    /// Validates that clearing a render target to a specific color works correctly,
    /// similar to how the swapchain render pass clears to black before the blit draw.
    /// </summary>
    [Fact]
    public void RenderPass_ClearToColor_ProducesCorrectResult()
    {
        using var renderTarget = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 2, Height = 2, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderTarget | TextureUsage.CopySource,
            SampleCount = SampleCount.Count1,
        });

        using var readbackBuffer = _fixture.Device.CreateBuffer(new BufferDescriptor
        {
            SizeInBytes = 2 * 2 * 4,
            Usage = BufferUsage.CopyDestination,
            MemoryAccess = MemoryAccess.GpuToCpu,
        });

        using var cmd = _fixture.Device.CreateCommandList();
        using var fence = _fixture.Device.CreateFence(false);

        var clearColor = new Float4(0, 0, 1, 1); // Blue

        cmd.Begin();
        cmd.BeginRenderPass(new RenderPassDescriptor
        {
            ColorAttachments =
            [
                RenderPassColorAttachment.Clear(renderTarget, clearColor)
            ]
        });
        cmd.EndRenderPass();

        cmd.CopyTextureToBuffer(new BufferTextureCopy
        {
            Texture = renderTarget,
            Buffer = readbackBuffer,
            MipLevel = 0, ArrayLayer = 0,
            X = 0, Y = 0, Z = 0,
            Width = 2, Height = 2, Depth = 1,
            BufferOffset = 0,
        });
        cmd.End();

        _fixture.Device.SubmitCommands(cmd, fence);
        fence.Wait();

        var pixelData = new byte[2 * 2 * 4];
        ReadBufferData<byte>(readbackBuffer, pixelData);

        // Every pixel should be blue (0, 0, 255, 255)
        for (int i = 0; i < 4; i++)
        {
            int off = i * 4;
            Assert.Equal(0, pixelData[off + 0]);   // R
            Assert.Equal(0, pixelData[off + 1]);   // G
            Assert.Equal(255, pixelData[off + 2]); // B
            Assert.Equal(255, pixelData[off + 3]); // A
        }
    }

    /// <summary>
    /// Validates that a texture with content renders correctly when sampled via
    /// a CombinedTextureSampler bind group — the exact pattern used by the swapchain blit.
    /// </summary>
    [Fact]
    public void BindGroup_CombinedTextureSampler_SamplesCorrectly()
    {
        // Create 1x1 magenta source texture
        using var sourceTexture = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.Sampled,
            SampleCount = SampleCount.Count1,
        });

        byte[] magenta = [255, 0, 255, 255];
        _fixture.Device.UpdateTexture(sourceTexture, new TextureUpdateDescriptor
        {
            X = 0, Y = 0, Z = 0,
            Width = 1, Height = 1, Depth = 1,
            MipLevel = 0, ArrayLayer = 0,
        }, magenta);

        using var sampler = _fixture.Device.CreateSampler(SamplerDescriptor.PointClamp);

        using var renderTarget = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderTarget | TextureUsage.CopySource,
            SampleCount = SampleCount.Count1,
        });

        using var readbackBuffer = _fixture.Device.CreateBuffer(new BufferDescriptor
        {
            SizeInBytes = 4,
            Usage = BufferUsage.CopyDestination,
            MemoryAccess = MemoryAccess.GpuToCpu,
        });

        // Build blit shader and bind group (same pattern as BlitToSwapchainGraphite)
        const string texSampleVs = @"
#version 430 core
out vec2 vTexCoord;
void main()
{
    vec2 positions[3] = vec2[](vec2(-1,-1), vec2(3,-1), vec2(-1,3));
    vec2 texcoords[3] = vec2[](vec2(0,0), vec2(2,0), vec2(0,2));
    gl_Position = vec4(positions[gl_VertexID], 0.0, 1.0);
    vTexCoord = texcoords[gl_VertexID];
}
";
        const string texSampleFs = @"
#version 430 core
in vec2 vTexCoord;
out vec4 FragColor;
layout(binding = 0) uniform sampler2D _MainTex;
void main()
{
    FragColor = texture(_MainTex, vTexCoord);
}
";

        using var vs = _fixture.Device.CreateShaderModule(ShaderModuleDescriptor.VertexGLSL(texSampleVs));
        using var fs = _fixture.Device.CreateShaderModule(ShaderModuleDescriptor.FragmentGLSL(texSampleFs));
        using var pipeline = _fixture.Device.CreatePipelineState(new PipelineStateDescriptor
        {
            VertexShader = vs,
            FragmentShader = fs,
            Topology = Prowl.Runtime.Graphite.PrimitiveTopology.TriangleList,
        });

        using var bgl = _fixture.Device.CreateBindGroupLayout(new BindGroupLayoutDescriptor(
            BindGroupLayoutEntry.CombinedTextureSampler(0, ShaderStage.Fragment, name: "_MainTex")));
        using var bindGroup = _fixture.Device.CreateBindGroup(new BindGroupDescriptor(
            bgl, BindGroupEntry.ForTextureSampler(0, sourceTexture, sampler)));

        using var cmd = _fixture.Device.CreateCommandList();
        using var fence = _fixture.Device.CreateFence(false);

        cmd.Begin();
        cmd.BeginRenderPass(new RenderPassDescriptor
        {
            ColorAttachments = [RenderPassColorAttachment.Clear(renderTarget, Float4.Zero)]
        });
        cmd.SetViewport(0, 0, 1, 1, 0, 1);
        cmd.SetPipeline(pipeline);
        cmd.SetBindGroup(0, bindGroup);
        cmd.Draw(3, 1, 0, 0);
        cmd.EndRenderPass();

        cmd.CopyTextureToBuffer(new BufferTextureCopy
        {
            Texture = renderTarget, Buffer = readbackBuffer,
            MipLevel = 0, ArrayLayer = 0,
            X = 0, Y = 0, Z = 0,
            Width = 1, Height = 1, Depth = 1,
            BufferOffset = 0,
        });
        cmd.End();

        _fixture.Device.SubmitCommands(cmd, fence);
        fence.Wait();

        var pixelData = new byte[4];
        ReadBufferData<byte>(readbackBuffer, pixelData);

        // Should be magenta (255, 0, 255, 255)
        Assert.Equal(255, pixelData[0]); // R
        Assert.Equal(0, pixelData[1]);   // G
        Assert.Equal(255, pixelData[2]); // B
        Assert.Equal(255, pixelData[3]); // A
    }

    }
