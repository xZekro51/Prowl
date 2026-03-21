// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

using Xunit;

using Silk.NET.OpenGL;

using Prowl.Runtime.Graphite;
using Prowl.Runtime.Graphite.OpenGL;
using Prowl.Vector;

using StencilOp = Prowl.Runtime.Graphite.StencilOp;

namespace Prowl.Runtime.Test.Graphite;

/// <summary>
/// Tests that verify rendering pipeline backend functionality is fully working.
/// Covers texture sampling, indexed drawing, scissor, alpha blending, stencil operations,
/// dynamic UBO offsets, instanced rendering, and named UBO bind group linkage.
/// </summary>
[Collection("Graphite")]
public class RenderPipelineBackendTests
{
    private readonly GraphiteTestFixture _fixture;

    public RenderPipelineBackendTests(GraphiteTestFixture fixture)
    {
        _fixture = fixture;
    }

    #region Shared Shader Sources

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

    private const string RedFragmentShader = @"
#version 430 core
out vec4 FragColor;

void main()
{
    FragColor = vec4(1.0, 0.0, 0.0, 1.0);
}
";

    private const string GreenFragmentShader = @"
#version 430 core
out vec4 FragColor;

void main()
{
    FragColor = vec4(0.0, 1.0, 0.0, 1.0);
}
";

    #endregion

    #region Texture Sampling Tests

    [Fact]
    public void Render_TextureSampling_ReadsCorrectColor()
    {
        // Upload a 1x1 cyan texture, sample it in a fragment shader via BindGroup,
        // and verify the output pixel matches.

        const string TexSampleVertexShader = @"
#version 430 core

out vec2 vTexCoord;

void main()
{
    vec2 positions[3] = vec2[](
        vec2(-1.0, -1.0),
        vec2( 3.0, -1.0),
        vec2(-1.0,  3.0)
    );
    vec2 texcoords[3] = vec2[](
        vec2(0.0, 0.0),
        vec2(2.0, 0.0),
        vec2(0.0, 2.0)
    );
    gl_Position = vec4(positions[gl_VertexID], 0.0, 1.0);
    vTexCoord = texcoords[gl_VertexID];
}
";

        const string TexSampleFragmentShader = @"
#version 430 core
in vec2 vTexCoord;
out vec4 FragColor;

layout(binding = 0) uniform sampler2D uTexture;

void main()
{
    FragColor = texture(uTexture, vTexCoord);
}
";

        // Create a 1x1 texture with cyan color (0, 255, 255, 255)
        using var sourceTexture = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.Sampled,
            SampleCount = SampleCount.Count1
        });

        byte[] cyanPixel = [0, 255, 255, 255];
        _fixture.Device.UpdateTexture(sourceTexture, new TextureUpdateDescriptor
        {
            X = 0, Y = 0, Z = 0,
            Width = 1, Height = 1, Depth = 1,
            MipLevel = 0, ArrayLayer = 0
        }, cyanPixel);

        using var sampler = _fixture.Device.CreateSampler(SamplerDescriptor.PointClamp);

        // Render target and readback
        using var renderTarget = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderTarget | TextureUsage.CopySource,
            SampleCount = SampleCount.Count1
        });

        using var readbackBuffer = _fixture.Device.CreateBuffer(new BufferDescriptor
        {
            SizeInBytes = 4,
            Usage = BufferUsage.CopyDestination,
            MemoryAccess = MemoryAccess.GpuToCpu
        });

        // Create shaders and pipeline
        using var vertexShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.VertexGLSL(TexSampleVertexShader));
        using var fragmentShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.FragmentGLSL(TexSampleFragmentShader));

        using var pipeline = _fixture.Device.CreatePipelineState(new PipelineStateDescriptor
        {
            VertexShader = vertexShader,
            FragmentShader = fragmentShader,
            Topology = PrimitiveTopology.TriangleList
        });

        // BindGroup: combined texture+sampler
        var layoutDesc = new BindGroupLayoutDescriptor(
            BindGroupLayoutEntry.CombinedTextureSampler(0, ShaderStage.Fragment)
        );
        using var layout = _fixture.Device.CreateBindGroupLayout(in layoutDesc);

        var bindGroupDesc = new BindGroupDescriptor(
            layout,
            BindGroupEntry.ForTextureSampler(0, sourceTexture, sampler)
        );
        using var bindGroup = _fixture.Device.CreateBindGroup(in bindGroupDesc);

        // Record and execute
        using var cmd = _fixture.Device.CreateCommandList();
        using var fence = _fixture.Device.CreateFence(false);

        cmd.Begin();
        cmd.BeginRenderPass(new RenderPassDescriptor
        {
            ColorAttachments =
            [
                new RenderPassColorAttachment
                {
                    Texture = renderTarget,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearColor = Float4.Zero
                }
            ]
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
            BufferOffset = 0
        });
        cmd.End();

        _fixture.Device.SubmitCommands(cmd, fence);
        fence.Wait();

        var pixelData = new byte[4];
        ReadBufferData<byte>(readbackBuffer, pixelData);

        // Should be cyan (0, 255, 255, 255)
        Assert.Equal(0, pixelData[0]);   // R
        Assert.Equal(255, pixelData[1]); // G
        Assert.Equal(255, pixelData[2]); // B
        Assert.Equal(255, pixelData[3]); // A
    }

    #endregion

    #region Named UBO BindGroup Linkage Tests

    [Fact]
    public void Render_NamedUBO_BindGroupLinkage_Works()
    {
        // Tests Phase A BindGroup linkage: the BindGroupLayoutEntry uses a Name
        // to link to the UBO block name in the shader. The GL backend's
        // SetupBindGroupLinkage should use GetUniformBlockIndex with this name.

        const string NamedUBOFragmentShader = @"
#version 430 core
out vec4 FragColor;

layout(std140) uniform MyMaterial {
    vec4 baseColor;
};

void main()
{
    FragColor = baseColor;
}
";

        using var renderTarget = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderTarget | TextureUsage.CopySource,
            SampleCount = SampleCount.Count1
        });

        using var readbackBuffer = _fixture.Device.CreateBuffer(new BufferDescriptor
        {
            SizeInBytes = 4,
            Usage = BufferUsage.CopyDestination,
            MemoryAccess = MemoryAccess.GpuToCpu
        });

        // Create uniform buffer with yellow color
        var colorData = new float[] { 1.0f, 1.0f, 0.0f, 1.0f }; // Yellow
        using var uniformBuffer = _fixture.Device.CreateBuffer<float>(
            BufferUsage.Uniform, colorData.AsSpan(), MemoryAccess.CpuToGpu);

        using var vertexShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.VertexGLSL(FullscreenTriVertexShader));
        using var fragmentShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.FragmentGLSL(NamedUBOFragmentShader));

        using var pipeline = _fixture.Device.CreatePipelineState(new PipelineStateDescriptor
        {
            VertexShader = vertexShader,
            FragmentShader = fragmentShader,
            Topology = PrimitiveTopology.TriangleList
        });

        // BindGroup with named UBO entry - name must match shader block name "MyMaterial"
        var layoutDesc = new BindGroupLayoutDescriptor(
            BindGroupLayoutEntry.UniformBuffer(0, ShaderStage.Fragment, name: "MyMaterial")
        );
        using var layout = _fixture.Device.CreateBindGroupLayout(in layoutDesc);

        var bindGroupDesc = new BindGroupDescriptor(
            layout,
            BindGroupEntry.ForBuffer(0, uniformBuffer)
        );
        using var bindGroup = _fixture.Device.CreateBindGroup(in bindGroupDesc);

        using var cmd = _fixture.Device.CreateCommandList();
        using var fence = _fixture.Device.CreateFence(false);

        cmd.Begin();
        cmd.BeginRenderPass(new RenderPassDescriptor
        {
            ColorAttachments =
            [
                new RenderPassColorAttachment
                {
                    Texture = renderTarget,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearColor = Float4.Zero
                }
            ]
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
            BufferOffset = 0
        });
        cmd.End();

        _fixture.Device.SubmitCommands(cmd, fence);
        fence.Wait();

        var pixelData = new byte[4];
        ReadBufferData<byte>(readbackBuffer, pixelData);

        // Should be yellow (255, 255, 0, 255)
        Assert.Equal(255, pixelData[0]); // R
        Assert.Equal(255, pixelData[1]); // G
        Assert.Equal(0, pixelData[2]);   // B
        Assert.Equal(255, pixelData[3]); // A
    }

    #endregion

    #region Indexed Draw Tests

    [Fact]
    public void Render_IndexedDraw_DrawsCorrectTriangle()
    {
        // Tests DrawIndexed with an index buffer. Creates 4 vertices (quad corners)
        // and uses indices to draw a fullscreen triangle from 3 of them.

        const string PosVertexShader = @"
#version 430 core
layout(location = 0) in vec2 aPosition;

void main()
{
    gl_Position = vec4(aPosition, 0.0, 1.0);
}
";

        // 4 vertices: quad corners
        var vertices = new float[]
        {
            -1f, -1f,  // 0: bottom-left
             1f, -1f,  // 1: bottom-right
             1f,  1f,  // 2: top-right
            -1f,  1f,  // 3: top-left
        };

        // Two triangles forming a fullscreen quad
        var indices = new ushort[] { 0, 1, 2, 0, 2, 3 };

        using var vertexBuffer = _fixture.Device.CreateBuffer<float>(
            BufferUsage.Vertex, vertices.AsSpan(), MemoryAccess.GpuOnly);
        using var indexBuffer = _fixture.Device.CreateBuffer<ushort>(
            BufferUsage.Index, indices.AsSpan(), MemoryAccess.GpuOnly);

        using var renderTarget = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 2, Height = 2, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderTarget | TextureUsage.CopySource,
            SampleCount = SampleCount.Count1
        });

        using var readbackBuffer = _fixture.Device.CreateBuffer(new BufferDescriptor
        {
            SizeInBytes = 16, // 4 pixels * 4 bytes
            Usage = BufferUsage.CopyDestination,
            MemoryAccess = MemoryAccess.GpuToCpu
        });

        using var vertexShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.VertexGLSL(PosVertexShader));
        using var fragmentShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.FragmentGLSL(RedFragmentShader));

        using var pipeline = _fixture.Device.CreatePipelineState(new PipelineStateDescriptor
        {
            VertexShader = vertexShader,
            FragmentShader = fragmentShader,
            Topology = PrimitiveTopology.TriangleList,
            VertexLayout = new VertexLayoutDescriptor(
                new VertexBufferLayout(8, // 2 floats = 8 bytes
                    new VertexAttribute(0, Prowl.Runtime.Graphite.VertexFormat.Float2, 0))
            )
        });

        using var cmd = _fixture.Device.CreateCommandList();
        using var fence = _fixture.Device.CreateFence(false);

        cmd.Begin();
        cmd.BeginRenderPass(new RenderPassDescriptor
        {
            ColorAttachments =
            [
                new RenderPassColorAttachment
                {
                    Texture = renderTarget,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearColor = Float4.Zero // Clear to black
                }
            ]
        });
        cmd.SetViewport(0, 0, 2, 2, 0, 1);
        cmd.SetPipeline(pipeline);
        cmd.SetVertexBuffer(0, vertexBuffer, 0);
        cmd.SetIndexBuffer(indexBuffer, IndexFormat.Uint16, 0);
        cmd.DrawIndexed(6, 1, 0, 0, 0); // 6 indices = 2 triangles = full quad
        cmd.EndRenderPass();

        cmd.CopyTextureToBuffer(new BufferTextureCopy
        {
            Texture = renderTarget, Buffer = readbackBuffer,
            MipLevel = 0, ArrayLayer = 0,
            X = 0, Y = 0, Z = 0,
            Width = 2, Height = 2, Depth = 1,
            BufferOffset = 0
        });
        cmd.End();

        _fixture.Device.SubmitCommands(cmd, fence);
        fence.Wait();

        var pixelData = new byte[16];
        ReadBufferData<byte>(readbackBuffer, pixelData);

        // All 4 pixels should be red (indexed quad covers entire viewport)
        for (int pixel = 0; pixel < 4; pixel++)
        {
            int offset = pixel * 4;
            Assert.Equal(255, pixelData[offset + 0]); // R
            Assert.Equal(0, pixelData[offset + 1]);   // G
            Assert.Equal(0, pixelData[offset + 2]);   // B
            Assert.Equal(255, pixelData[offset + 3]); // A
        }
    }

    #endregion

    #region Scissor Tests

    [Fact]
    public void Render_Scissor_ClipsToRectangle()
    {
        // Render a fullscreen red triangle to a 4x4 target with scissor set to the
        // bottom-left 2x2 region. Only those 4 pixels should be red; the rest stay black.

        using var renderTarget = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 4, Height = 4, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderTarget | TextureUsage.CopySource,
            SampleCount = SampleCount.Count1
        });

        using var readbackBuffer = _fixture.Device.CreateBuffer(new BufferDescriptor
        {
            SizeInBytes = 64, // 16 pixels * 4 bytes
            Usage = BufferUsage.CopyDestination,
            MemoryAccess = MemoryAccess.GpuToCpu
        });

        using var vertexShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.VertexGLSL(FullscreenTriVertexShader));
        using var fragmentShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.FragmentGLSL(RedFragmentShader));

        using var pipeline = _fixture.Device.CreatePipelineState(new PipelineStateDescriptor
        {
            VertexShader = vertexShader,
            FragmentShader = fragmentShader,
            Topology = PrimitiveTopology.TriangleList
        });

        using var cmd = _fixture.Device.CreateCommandList();
        using var fence = _fixture.Device.CreateFence(false);

        cmd.Begin();
        cmd.BeginRenderPass(new RenderPassDescriptor
        {
            ColorAttachments =
            [
                new RenderPassColorAttachment
                {
                    Texture = renderTarget,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearColor = Float4.Zero // Clear to black
                }
            ]
        });
        cmd.SetViewport(0, 0, 4, 4, 0, 1);
        cmd.SetScissor(0, 0, 2, 2); // Bottom-left 2x2 region
        cmd.SetPipeline(pipeline);
        cmd.Draw(3, 1, 0, 0);
        cmd.EndRenderPass();

        cmd.CopyTextureToBuffer(new BufferTextureCopy
        {
            Texture = renderTarget, Buffer = readbackBuffer,
            MipLevel = 0, ArrayLayer = 0,
            X = 0, Y = 0, Z = 0,
            Width = 4, Height = 4, Depth = 1,
            BufferOffset = 0
        });
        cmd.End();

        _fixture.Device.SubmitCommands(cmd, fence);
        fence.Wait();

        var pixelData = new byte[64];
        ReadBufferData<byte>(readbackBuffer, pixelData);

        // Check bottom-left 2x2 pixels (rows 0-1, cols 0-1) are red
        for (int row = 0; row < 2; row++)
        {
            for (int col = 0; col < 2; col++)
            {
                int offset = (row * 4 + col) * 4;
                Assert.Equal(255, pixelData[offset + 0]); // R
                Assert.Equal(0, pixelData[offset + 1]);   // G
                Assert.Equal(0, pixelData[offset + 2]);   // B
                Assert.Equal(255, pixelData[offset + 3]); // A
            }
        }

        // Check at least one pixel outside the scissor region is still black
        // Top-right corner: row=3, col=3
        int outsideOffset = (3 * 4 + 3) * 4;
        Assert.Equal(0, pixelData[outsideOffset + 0]); // R
        Assert.Equal(0, pixelData[outsideOffset + 1]); // G
        Assert.Equal(0, pixelData[outsideOffset + 2]); // B
        Assert.Equal(0, pixelData[outsideOffset + 3]); // A
    }

    #endregion

    #region Alpha Blend Tests

    [Fact]
    public void Render_AlphaBlend_ProducesCorrectPixel()
    {
        // Clear to blue (0, 0, 255, 255), draw a semi-transparent red (255, 0, 0, 128)
        // with standard alpha blend (SrcAlpha, OneMinusSrcAlpha).
        // Expected result: R ≈ 128, G = 0, B ≈ 127, A ≈ 255

        const string SemiTransparentRedFragmentShader = @"
#version 430 core
out vec4 FragColor;

void main()
{
    // 0.5 alpha red
    FragColor = vec4(1.0, 0.0, 0.0, 0.5);
}
";

        using var renderTarget = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderTarget | TextureUsage.CopySource,
            SampleCount = SampleCount.Count1
        });

        using var readbackBuffer = _fixture.Device.CreateBuffer(new BufferDescriptor
        {
            SizeInBytes = 4,
            Usage = BufferUsage.CopyDestination,
            MemoryAccess = MemoryAccess.GpuToCpu
        });

        using var vertexShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.VertexGLSL(FullscreenTriVertexShader));
        using var fragmentShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.FragmentGLSL(SemiTransparentRedFragmentShader));

        using var pipeline = _fixture.Device.CreatePipelineState(new PipelineStateDescriptor
        {
            VertexShader = vertexShader,
            FragmentShader = fragmentShader,
            Topology = PrimitiveTopology.TriangleList,
            BlendState = new BlendStateDescriptor(BlendAttachment.AlphaBlend)
        });

        using var cmd = _fixture.Device.CreateCommandList();
        using var fence = _fixture.Device.CreateFence(false);

        cmd.Begin();
        cmd.BeginRenderPass(new RenderPassDescriptor
        {
            ColorAttachments =
            [
                new RenderPassColorAttachment
                {
                    Texture = renderTarget,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearColor = new Float4(0.0f, 0.0f, 1.0f, 1.0f) // Blue background
                }
            ]
        });
        cmd.SetViewport(0, 0, 1, 1, 0, 1);
        cmd.SetPipeline(pipeline);
        cmd.Draw(3, 1, 0, 0);
        cmd.EndRenderPass();

        cmd.CopyTextureToBuffer(new BufferTextureCopy
        {
            Texture = renderTarget, Buffer = readbackBuffer,
            MipLevel = 0, ArrayLayer = 0,
            X = 0, Y = 0, Z = 0,
            Width = 1, Height = 1, Depth = 1,
            BufferOffset = 0
        });
        cmd.End();

        _fixture.Device.SubmitCommands(cmd, fence);
        fence.Wait();

        var pixelData = new byte[4];
        ReadBufferData<byte>(readbackBuffer, pixelData);

        // Alpha blend: dst = src * srcAlpha + dst * (1 - srcAlpha)
        // R = 1.0 * 0.5 + 0.0 * 0.5 = 0.5 → ~128
        // G = 0.0 * 0.5 + 0.0 * 0.5 = 0.0 → 0
        // B = 0.0 * 0.5 + 1.0 * 0.5 = 0.5 → ~128
        Assert.InRange(pixelData[0], (byte)120, (byte)135); // R ≈ 128
        Assert.Equal(0, pixelData[1]);                       // G = 0
        Assert.InRange(pixelData[2], (byte)120, (byte)135); // B ≈ 128
    }

    #endregion

    #region Stencil Test

    [Fact]
    public void Render_StencilMask_OnlyDrawsWhereStencilPasses()
    {
        // Pass 1: Draw red fullscreen triangle with stencil write (reference=1, passOp=Replace).
        //         Scissor to bottom-left 2x2, so only that region gets stencil=1.
        // Pass 2: Draw green fullscreen triangle with stencil test (reference=1, compare=Equal).
        //         Only the bottom-left 2x2 (where stencil==1) should turn green.
        //         The rest stays black (cleared color).

        using var renderTarget = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 4, Height = 4, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderTarget | TextureUsage.CopySource,
            SampleCount = SampleCount.Count1
        });

        using var depthStencilTarget = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 4, Height = 4, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.Depth24PlusStencil8,
            Usage = TextureUsage.RenderTarget,
            SampleCount = SampleCount.Count1
        });

        using var readbackBuffer = _fixture.Device.CreateBuffer(new BufferDescriptor
        {
            SizeInBytes = 64,
            Usage = BufferUsage.CopyDestination,
            MemoryAccess = MemoryAccess.GpuToCpu
        });

        using var vertexShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.VertexGLSL(FullscreenTriVertexShader));
        using var redFragShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.FragmentGLSL(RedFragmentShader));
        using var greenFragShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.FragmentGLSL(GreenFragmentShader));

        // Pass 1 pipeline: writes stencil reference=1 on pass, always passes stencil test
        using var stencilWritePipeline = _fixture.Device.CreatePipelineState(new PipelineStateDescriptor
        {
            VertexShader = vertexShader,
            FragmentShader = redFragShader,
            Topology = PrimitiveTopology.TriangleList,
            DepthStencilState = new DepthStencilStateDescriptor
            {
                DepthTestEnable = false,
                DepthWriteEnable = false,
                StencilTestEnable = true,
                StencilReadMask = 0xFF,
                StencilWriteMask = 0xFF,
                StencilFront = new StencilFaceState
                {
                    Compare = CompareFunction.Always,
                    PassOp = StencilOp.Replace,
                    FailOp = StencilOp.Keep,
                    DepthFailOp = StencilOp.Keep
                },
                StencilBack = new StencilFaceState
                {
                    Compare = CompareFunction.Always,
                    PassOp = StencilOp.Replace,
                    FailOp = StencilOp.Keep,
                    DepthFailOp = StencilOp.Keep
                }
            }
        });

        // Pass 2 pipeline: only passes stencil test where stencil==1
        using var stencilTestPipeline = _fixture.Device.CreatePipelineState(new PipelineStateDescriptor
        {
            VertexShader = vertexShader,
            FragmentShader = greenFragShader,
            Topology = PrimitiveTopology.TriangleList,
            DepthStencilState = new DepthStencilStateDescriptor
            {
                DepthTestEnable = false,
                DepthWriteEnable = false,
                StencilTestEnable = true,
                StencilReadMask = 0xFF,
                StencilWriteMask = 0x00, // Don't modify stencil
                StencilFront = new StencilFaceState
                {
                    Compare = CompareFunction.Equal,
                    PassOp = StencilOp.Keep,
                    FailOp = StencilOp.Keep,
                    DepthFailOp = StencilOp.Keep
                },
                StencilBack = new StencilFaceState
                {
                    Compare = CompareFunction.Equal,
                    PassOp = StencilOp.Keep,
                    FailOp = StencilOp.Keep,
                    DepthFailOp = StencilOp.Keep
                }
            }
        });

        using var cmd = _fixture.Device.CreateCommandList();
        using var fence = _fixture.Device.CreateFence(false);

        cmd.Begin();
        cmd.BeginRenderPass(new RenderPassDescriptor
        {
            ColorAttachments =
            [
                new RenderPassColorAttachment
                {
                    Texture = renderTarget,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearColor = Float4.Zero // Black
                }
            ],
            DepthStencilAttachment = new RenderPassDepthStencilAttachment
            {
                Texture = depthStencilTarget,
                DepthLoadOp = LoadOp.Clear,
                DepthStoreOp = StoreOp.DontCare,
                DepthClearValue = 1.0f,
                StencilLoadOp = LoadOp.Clear,
                StencilStoreOp = StoreOp.Store,
                StencilClearValue = 0
            }
        });
        cmd.SetViewport(0, 0, 4, 4, 0, 1);

        // Pass 1: write stencil=1 in bottom-left 2x2 only
        cmd.SetPipeline(stencilWritePipeline);
        cmd.SetStencilReference(1);
        cmd.SetScissor(0, 0, 2, 2);
        cmd.Draw(3, 1, 0, 0);

        // Pass 2: draw green where stencil==1 (reset scissor to full viewport)
        cmd.SetPipeline(stencilTestPipeline);
        cmd.SetStencilReference(1);
        cmd.SetScissor(0, 0, 4, 4);
        cmd.Draw(3, 1, 0, 0);

        cmd.EndRenderPass();

        cmd.CopyTextureToBuffer(new BufferTextureCopy
        {
            Texture = renderTarget, Buffer = readbackBuffer,
            MipLevel = 0, ArrayLayer = 0,
            X = 0, Y = 0, Z = 0,
            Width = 4, Height = 4, Depth = 1,
            BufferOffset = 0
        });
        cmd.End();

        _fixture.Device.SubmitCommands(cmd, fence);
        fence.Wait();

        var pixelData = new byte[64];
        ReadBufferData<byte>(readbackBuffer, pixelData);

        // Bottom-left 2x2 (rows 0-1, cols 0-1) should be green
        for (int row = 0; row < 2; row++)
        {
            for (int col = 0; col < 2; col++)
            {
                int offset = (row * 4 + col) * 4;
                Assert.Equal(0, pixelData[offset + 0]);   // R
                Assert.Equal(255, pixelData[offset + 1]); // G
                Assert.Equal(0, pixelData[offset + 2]);   // B
                Assert.Equal(255, pixelData[offset + 3]); // A
            }
        }

        // Top-right corner (row=3, col=3) should be black (stencil failed, no draw)
        int outsideOffset = (3 * 4 + 3) * 4;
        Assert.Equal(0, pixelData[outsideOffset + 0]); // R
        Assert.Equal(0, pixelData[outsideOffset + 1]); // G
        Assert.Equal(0, pixelData[outsideOffset + 2]); // B
        Assert.Equal(0, pixelData[outsideOffset + 3]); // A
    }

    #endregion

    #region Dynamic UBO Offset Tests

    [Fact]
    public void Render_DynamicUBOOffset_SelectsCorrectData()
    {
        // Create a buffer with two color values at different offsets.
        // Use dynamic UBO offset to select which color is used.
        // Draw 1: offset=0 → red. Draw 2: offset=256 → green.
        // Final pixel should be green (second draw overwrites first).

        const string DynUBOFragmentShader = @"
#version 430 core
out vec4 FragColor;

layout(std140, binding = 0) uniform ColorBlock {
    vec4 color;
};

void main()
{
    FragColor = color;
}
";

        using var renderTarget = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderTarget | TextureUsage.CopySource,
            SampleCount = SampleCount.Count1
        });

        using var readbackBuffer = _fixture.Device.CreateBuffer(new BufferDescriptor
        {
            SizeInBytes = 4,
            Usage = BufferUsage.CopyDestination,
            MemoryAccess = MemoryAccess.GpuToCpu
        });

        // Create a buffer with two colors at 256-byte aligned offsets
        // Most GPUs require UBO dynamic offsets to be aligned to 256 bytes
        var bufferData = new byte[512];
        var red = new float[] { 1.0f, 0.0f, 0.0f, 1.0f };
        var green = new float[] { 0.0f, 1.0f, 0.0f, 1.0f };
        System.Buffer.BlockCopy(red, 0, bufferData, 0, 16);     // offset 0: red
        System.Buffer.BlockCopy(green, 0, bufferData, 256, 16); // offset 256: green

        using var uniformBuffer = _fixture.Device.CreateBuffer<byte>(
            BufferUsage.Uniform, bufferData.AsSpan(), MemoryAccess.CpuToGpu);

        using var vertexShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.VertexGLSL(FullscreenTriVertexShader));
        using var fragmentShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.FragmentGLSL(DynUBOFragmentShader));

        using var pipeline = _fixture.Device.CreatePipelineState(new PipelineStateDescriptor
        {
            VertexShader = vertexShader,
            FragmentShader = fragmentShader,
            Topology = PrimitiveTopology.TriangleList
        });

        // BindGroup with dynamic offset
        var layoutDesc = new BindGroupLayoutDescriptor(
            BindGroupLayoutEntry.UniformBuffer(0, ShaderStage.Fragment, hasDynamicOffset: true)
        );
        using var layout = _fixture.Device.CreateBindGroupLayout(in layoutDesc);

        var bindGroupDesc = new BindGroupDescriptor(
            layout,
            BindGroupEntry.ForBuffer(0, uniformBuffer, 0, 16) // base offset 0, size 16 bytes
        );
        using var bindGroup = _fixture.Device.CreateBindGroup(in bindGroupDesc);

        using var cmd = _fixture.Device.CreateCommandList();
        using var fence = _fixture.Device.CreateFence(false);

        cmd.Begin();
        cmd.BeginRenderPass(new RenderPassDescriptor
        {
            ColorAttachments =
            [
                new RenderPassColorAttachment
                {
                    Texture = renderTarget,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearColor = Float4.Zero
                }
            ]
        });
        cmd.SetViewport(0, 0, 1, 1, 0, 1);
        cmd.SetPipeline(pipeline);

        // Draw 1: offset=0 → reads red
        ReadOnlySpan<uint> redOffset = [0];
        cmd.SetBindGroup(0, bindGroup, redOffset);
        cmd.Draw(3, 1, 0, 0);

        // Draw 2: offset=256 → reads green (overwrites red)
        ReadOnlySpan<uint> greenOffset = [256];
        cmd.SetBindGroup(0, bindGroup, greenOffset);
        cmd.Draw(3, 1, 0, 0);

        cmd.EndRenderPass();

        cmd.CopyTextureToBuffer(new BufferTextureCopy
        {
            Texture = renderTarget, Buffer = readbackBuffer,
            MipLevel = 0, ArrayLayer = 0,
            X = 0, Y = 0, Z = 0,
            Width = 1, Height = 1, Depth = 1,
            BufferOffset = 0
        });
        cmd.End();

        _fixture.Device.SubmitCommands(cmd, fence);
        fence.Wait();

        var pixelData = new byte[4];
        ReadBufferData<byte>(readbackBuffer, pixelData);

        // Final pixel should be green (second draw overwrites first)
        Assert.Equal(0, pixelData[0]);   // R
        Assert.Equal(255, pixelData[1]); // G
        Assert.Equal(0, pixelData[2]);   // B
        Assert.Equal(255, pixelData[3]); // A
    }

    #endregion

    #region Instanced Rendering Tests

    [Fact]
    public void Render_InstancedDraw_UsesPerInstanceData()
    {
        // Draw 2 instances of a fullscreen triangle. The second instance writes green.
        // Since both instances overlap the same pixel, the last one (green) wins.

        const string InstancedVertexShader = @"
#version 430 core
layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec4 aInstanceColor;

out vec4 vColor;

void main()
{
    gl_Position = vec4(aPosition, 0.0, 1.0);
    vColor = aInstanceColor;
}
";
        const string InstancedFragmentShader = @"
#version 430 core
in vec4 vColor;
out vec4 FragColor;

void main()
{
    FragColor = vColor;
}
";

        // Vertex data: fullscreen triangle
        var vertices = new float[]
        {
            -1f, -1f,
             3f, -1f,
            -1f,  3f,
        };

        // Per-instance data: 2 instances with different colors
        var instanceData = new float[]
        {
            1f, 0f, 0f, 1f, // Instance 0: red
            0f, 1f, 0f, 1f, // Instance 1: green
        };

        using var vertexBuffer = _fixture.Device.CreateBuffer<float>(
            BufferUsage.Vertex, vertices.AsSpan(), MemoryAccess.GpuOnly);
        using var instanceBuffer = _fixture.Device.CreateBuffer<float>(
            BufferUsage.Vertex, instanceData.AsSpan(), MemoryAccess.GpuOnly);

        using var renderTarget = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderTarget | TextureUsage.CopySource,
            SampleCount = SampleCount.Count1
        });

        using var readbackBuffer = _fixture.Device.CreateBuffer(new BufferDescriptor
        {
            SizeInBytes = 4,
            Usage = BufferUsage.CopyDestination,
            MemoryAccess = MemoryAccess.GpuToCpu
        });

        using var vertexShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.VertexGLSL(InstancedVertexShader));
        using var fragmentShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.FragmentGLSL(InstancedFragmentShader));

        using var pipeline = _fixture.Device.CreatePipelineState(new PipelineStateDescriptor
        {
            VertexShader = vertexShader,
            FragmentShader = fragmentShader,
            Topology = PrimitiveTopology.TriangleList,
            VertexLayout = new VertexLayoutDescriptor(
                new VertexBufferLayout(8, VertexStepMode.Vertex,
                    new VertexAttribute(0, Prowl.Runtime.Graphite.VertexFormat.Float2, 0)),
                new VertexBufferLayout(16, VertexStepMode.Instance,
                    new VertexAttribute(1, Prowl.Runtime.Graphite.VertexFormat.Float4, 0))
            )
        });

        using var cmd = _fixture.Device.CreateCommandList();
        using var fence = _fixture.Device.CreateFence(false);

        cmd.Begin();
        cmd.BeginRenderPass(new RenderPassDescriptor
        {
            ColorAttachments =
            [
                new RenderPassColorAttachment
                {
                    Texture = renderTarget,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearColor = Float4.Zero
                }
            ]
        });
        cmd.SetViewport(0, 0, 1, 1, 0, 1);
        cmd.SetPipeline(pipeline);
        cmd.SetVertexBuffer(0, vertexBuffer, 0);
        cmd.SetVertexBuffer(1, instanceBuffer, 0);
        cmd.Draw(3, 2, 0, 0); // 3 vertices, 2 instances
        cmd.EndRenderPass();

        cmd.CopyTextureToBuffer(new BufferTextureCopy
        {
            Texture = renderTarget, Buffer = readbackBuffer,
            MipLevel = 0, ArrayLayer = 0,
            X = 0, Y = 0, Z = 0,
            Width = 1, Height = 1, Depth = 1,
            BufferOffset = 0
        });
        cmd.End();

        _fixture.Device.SubmitCommands(cmd, fence);
        fence.Wait();

        var pixelData = new byte[4];
        ReadBufferData<byte>(readbackBuffer, pixelData);

        // Instance 1 (green) draws last, so pixel should be green
        Assert.Equal(0, pixelData[0]);   // R
        Assert.Equal(255, pixelData[1]); // G
        Assert.Equal(0, pixelData[2]);   // B
        Assert.Equal(255, pixelData[3]); // A
    }

    #endregion

    #region Multiple BindGroup Tests

    [Fact]
    public void Render_MultipleBindGroups_BothContributeToOutput()
    {
        // Tests using two bind groups simultaneously:
        // Group 0: UBO with a color multiplier
        // Group 1: UBO with a base color
        // Fragment shader multiplies them together.

        const string MultiBindFragmentShader = @"
#version 430 core
out vec4 FragColor;

layout(std140, binding = 0) uniform Multiplier {
    vec4 multiplier;
};

layout(std140, binding = 1) uniform BaseColor {
    vec4 base_color;
};

void main()
{
    FragColor = multiplier * base_color;
}
";

        using var renderTarget = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderTarget | TextureUsage.CopySource,
            SampleCount = SampleCount.Count1
        });

        using var readbackBuffer = _fixture.Device.CreateBuffer(new BufferDescriptor
        {
            SizeInBytes = 4,
            Usage = BufferUsage.CopyDestination,
            MemoryAccess = MemoryAccess.GpuToCpu
        });

        // Multiplier = (0.5, 1.0, 0.5, 1.0)
        var multiplierData = new float[] { 0.5f, 1.0f, 0.5f, 1.0f };
        using var multiplierBuffer = _fixture.Device.CreateBuffer<float>(
            BufferUsage.Uniform, multiplierData.AsSpan(), MemoryAccess.CpuToGpu);

        // Base color = (1.0, 1.0, 1.0, 1.0) (white)
        var baseColorData = new float[] { 1.0f, 1.0f, 1.0f, 1.0f };
        using var baseColorBuffer = _fixture.Device.CreateBuffer<float>(
            BufferUsage.Uniform, baseColorData.AsSpan(), MemoryAccess.CpuToGpu);

        using var vertexShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.VertexGLSL(FullscreenTriVertexShader));
        using var fragmentShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.FragmentGLSL(MultiBindFragmentShader));

        using var pipeline = _fixture.Device.CreatePipelineState(new PipelineStateDescriptor
        {
            VertexShader = vertexShader,
            FragmentShader = fragmentShader,
            Topology = PrimitiveTopology.TriangleList
        });

        // BindGroup 0: Multiplier
        var layout0Desc = new BindGroupLayoutDescriptor(
            BindGroupLayoutEntry.UniformBuffer(0, ShaderStage.Fragment)
        );
        using var layout0 = _fixture.Device.CreateBindGroupLayout(in layout0Desc);
        var bg0Desc = new BindGroupDescriptor(layout0, BindGroupEntry.ForBuffer(0, multiplierBuffer));
        using var bindGroup0 = _fixture.Device.CreateBindGroup(in bg0Desc);

        // BindGroup 1: BaseColor
        var layout1Desc = new BindGroupLayoutDescriptor(
            BindGroupLayoutEntry.UniformBuffer(1, ShaderStage.Fragment)
        );
        using var layout1 = _fixture.Device.CreateBindGroupLayout(in layout1Desc);
        var bg1Desc = new BindGroupDescriptor(layout1, BindGroupEntry.ForBuffer(1, baseColorBuffer));
        using var bindGroup1 = _fixture.Device.CreateBindGroup(in bg1Desc);

        using var cmd = _fixture.Device.CreateCommandList();
        using var fence = _fixture.Device.CreateFence(false);

        cmd.Begin();
        cmd.BeginRenderPass(new RenderPassDescriptor
        {
            ColorAttachments =
            [
                new RenderPassColorAttachment
                {
                    Texture = renderTarget,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearColor = Float4.Zero
                }
            ]
        });
        cmd.SetViewport(0, 0, 1, 1, 0, 1);
        cmd.SetPipeline(pipeline);
        cmd.SetBindGroup(0, bindGroup0);
        cmd.SetBindGroup(1, bindGroup1);
        cmd.Draw(3, 1, 0, 0);
        cmd.EndRenderPass();

        cmd.CopyTextureToBuffer(new BufferTextureCopy
        {
            Texture = renderTarget, Buffer = readbackBuffer,
            MipLevel = 0, ArrayLayer = 0,
            X = 0, Y = 0, Z = 0,
            Width = 1, Height = 1, Depth = 1,
            BufferOffset = 0
        });
        cmd.End();

        _fixture.Device.SubmitCommands(cmd, fence);
        fence.Wait();

        var pixelData = new byte[4];
        ReadBufferData<byte>(readbackBuffer, pixelData);

        // Result = (0.5, 1.0, 0.5, 1.0) * (1.0, 1.0, 1.0, 1.0) = (0.5, 1.0, 0.5, 1.0)
        // R ≈ 128, G = 255, B ≈ 128, A = 255
        Assert.InRange(pixelData[0], (byte)120, (byte)135); // R ≈ 128
        Assert.Equal(255, pixelData[1]);                     // G = 255
        Assert.InRange(pixelData[2], (byte)120, (byte)135); // B ≈ 128
        Assert.Equal(255, pixelData[3]);                     // A = 255
    }

    #endregion

    #region Buffer Update and Re-render Tests

    [Fact]
    public void Render_UpdateUniformBuffer_ReflectsNewColor()
    {
        // Tests that updating a uniform buffer between frames changes the rendered output.
        // Draw 1: UBO contains red → pixel is red.
        // Update UBO to blue.
        // Draw 2: UBO now contains blue → pixel is blue.

        const string UBOColorFragmentShader = @"
#version 430 core
out vec4 FragColor;

layout(std140, binding = 0) uniform ColorBlock {
    vec4 color;
};

void main()
{
    FragColor = color;
}
";

        using var renderTarget = _fixture.Device.CreateTexture(new TextureDescriptor
        {
            Dimension = TextureDimension.Texture2D,
            Width = 1, Height = 1, Depth = 1,
            MipLevels = 1, ArrayLayers = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.RenderTarget | TextureUsage.CopySource,
            SampleCount = SampleCount.Count1
        });

        using var readbackBuffer = _fixture.Device.CreateBuffer(new BufferDescriptor
        {
            SizeInBytes = 4,
            Usage = BufferUsage.CopyDestination,
            MemoryAccess = MemoryAccess.GpuToCpu
        });

        var redColor = new float[] { 1.0f, 0.0f, 0.0f, 1.0f };
        using var uniformBuffer = _fixture.Device.CreateBuffer<float>(
            BufferUsage.Uniform, redColor.AsSpan(), MemoryAccess.CpuToGpu);

        using var vertexShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.VertexGLSL(FullscreenTriVertexShader));
        using var fragmentShader = _fixture.Device.CreateShaderModule(
            ShaderModuleDescriptor.FragmentGLSL(UBOColorFragmentShader));

        using var pipeline = _fixture.Device.CreatePipelineState(new PipelineStateDescriptor
        {
            VertexShader = vertexShader,
            FragmentShader = fragmentShader,
            Topology = PrimitiveTopology.TriangleList
        });

        var layoutDesc = new BindGroupLayoutDescriptor(
            BindGroupLayoutEntry.UniformBuffer(0, ShaderStage.Fragment)
        );
        using var layout = _fixture.Device.CreateBindGroupLayout(in layoutDesc);

        var bindGroupDesc = new BindGroupDescriptor(layout, BindGroupEntry.ForBuffer(0, uniformBuffer));
        using var bindGroup = _fixture.Device.CreateBindGroup(in bindGroupDesc);

        // --- Draw 1: red ---
        {
            using var cmd = _fixture.Device.CreateCommandList();
            using var fence = _fixture.Device.CreateFence(false);

            cmd.Begin();
            cmd.BeginRenderPass(new RenderPassDescriptor
            {
                ColorAttachments =
                [
                    new RenderPassColorAttachment
                    {
                        Texture = renderTarget,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = Float4.Zero
                    }
                ]
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
                BufferOffset = 0
            });
            cmd.End();

            _fixture.Device.SubmitCommands(cmd, fence);
            fence.Wait();

            var pixelData = new byte[4];
            ReadBufferData<byte>(readbackBuffer, pixelData);

            Assert.Equal(255, pixelData[0]); // R
            Assert.Equal(0, pixelData[1]);   // G
            Assert.Equal(0, pixelData[2]);   // B
            Assert.Equal(255, pixelData[3]); // A
        }

        // --- Update UBO to blue ---
        var blueColor = new float[] { 0.0f, 0.0f, 1.0f, 1.0f };
        _fixture.Device.UpdateBuffer<float>(uniformBuffer, 0, blueColor);

        // --- Draw 2: blue ---
        {
            using var cmd = _fixture.Device.CreateCommandList();
            using var fence = _fixture.Device.CreateFence(false);

            cmd.Begin();
            cmd.BeginRenderPass(new RenderPassDescriptor
            {
                ColorAttachments =
                [
                    new RenderPassColorAttachment
                    {
                        Texture = renderTarget,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearColor = Float4.Zero
                    }
                ]
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
                BufferOffset = 0
            });
            cmd.End();

            _fixture.Device.SubmitCommands(cmd, fence);
            fence.Wait();

            var pixelData = new byte[4];
            ReadBufferData<byte>(readbackBuffer, pixelData);

            Assert.Equal(0, pixelData[0]);   // R
            Assert.Equal(0, pixelData[1]);   // G
            Assert.Equal(255, pixelData[2]); // B
            Assert.Equal(255, pixelData[3]); // A
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Reads buffer data back to CPU using GL directly.
    /// </summary>
    private unsafe void ReadBufferData<T>(Prowl.Runtime.Graphite.Buffer buffer, Span<T> destination) where T : unmanaged
    {
        if (buffer is not GLBuffer glBuffer)
            throw new InvalidOperationException("Buffer is not a GLBuffer");

        _fixture.GL.BindBuffer(BufferTargetARB.ArrayBuffer, glBuffer.Handle);

        fixed (T* ptr = destination)
        {
            _fixture.GL.GetBufferSubData(BufferTargetARB.ArrayBuffer, 0,
                (nuint)(destination.Length * sizeof(T)), ptr);
        }

        _fixture.GL.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    #endregion
}
