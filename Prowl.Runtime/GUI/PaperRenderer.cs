// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Prowl.Quill;
using Prowl.Runtime.Graphite;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Rendering.Shaders;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Runtime.GUI;

public class PaperRenderer : ICanvasRenderer
{
    /// <summary>
    /// UBO data layout matching the UIUniforms block in UI.shader (std140-compatible).
    /// All fields are vec4/mat4-sized to guarantee C#/std140 alignment compatibility.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    private struct UIUniformsData
    {
        public Float4x4 Projection;       // mat4
        public Float4x4 ScissorMat;       // mat4
        public Float4 ScissorExtPad;      // vec4 — xy = scissorExt
        public Float4x4 BrushMat;         // mat4
        public Float4 BrushTypePad;       // vec4 — x = brushType (as float)
        public Float4 BrushColor1;        // vec4
        public Float4 BrushColor2;        // vec4
        public Float4 BrushParams;        // vec4
        public Float4 BrushParams2Pad;    // vec4 — xy = brushParams2
    }

    // UBO ring buffer constants
    private static readonly unsafe uint UboStructSize = (uint)sizeof(UIUniformsData);
    private const uint UboAlignment = 256;
    private static readonly uint UboSlotSize = ((UboStructSize + UboAlignment - 1) / UboAlignment) * UboAlignment;
    private const int MaxDrawsPerFrame = 512;
    private static readonly uint UboRingBufferSize = UboSlotSize * MaxDrawsPerFrame;

    // Graphite resources
    private Graphite.Buffer? _vertexBuffer;
    private Graphite.Buffer? _indexBuffer;
    private Graphite.Buffer? _uboRingBuffer;
    private BindGroupLayout? _uboBindGroupLayout;
    private BindGroupLayout? _textureBindGroupLayout;
    private BindGroupLayout[]? _bindGroupLayouts;
    private BindGroup? _uboBindGroup;

    // Shader program (kept for module references used by PipelineStateCache)
    private GraphicsProgram? _shaderProgram;

    // Default white texture and sampler
    private Texture2D? _defaultTexture;
    private Graphite.Sampler? _defaultSampler;

    // Per-drawcall texture bind groups, cached by Graphite texture reference
    private readonly Dictionary<Graphite.Texture, BindGroup> _textureBindGroups = new();

    // Vertex layout for pipeline creation
    private VertexLayoutDescriptor _vertexLayout;

    // View properties
    private Float4x4 _projection;
    private int _viewportWidth;
    private int _viewportHeight;

    // CPU staging buffer for the UBO ring
    private byte[]? _uboStagingBuffer;

    /// <summary>
    /// The render target to render into. Set this before Paper.EndFrame() is called.
    /// When <c>null</c>, renders to the swapchain (screen).
    /// </summary>
    public RenderTexture? RenderTarget { get; set; }

    /// <summary>
    /// When <c>true</c>, the next flush will clear the render target instead of
    /// preserving existing content (LoadOp.Clear vs LoadOp.Load).  Automatically
    /// reset to <c>false</c> after the flush.  Use this for off-screen canvases
    /// that need a fresh clear each frame (e.g. <see cref="WorldCanvas"/>).
    /// </summary>
    public bool ShouldClear { get; set; }

    public void Initialize(int width, int height)
    {
        InitializeShaders();

        if (Graphics.IsGraphiteReady)
            InitializeGraphiteResources();

        UpdateProjection(width, height);
    }

    public void UpdateProjection(int width, int height)
    {
        _projection = Float4x4.CreateOrthoOffCenter(0, width, height, 0, -1, 1);
        _viewportWidth = width;
        _viewportHeight = height;
    }

    public void Cleanup()
    {
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _uboRingBuffer?.Dispose();
        _uboBindGroup?.Dispose();
        _uboBindGroupLayout?.Dispose();
        _textureBindGroupLayout?.Dispose();

        foreach (var bg in _textureBindGroups.Values)
            bg?.Dispose();
        _textureBindGroups.Clear();

        _shaderProgram?.Dispose();
        _defaultTexture?.Dispose();
        _defaultSampler?.Dispose();

        _vertexBuffer = null;
        _indexBuffer = null;
        _uboRingBuffer = null;
        _uboBindGroup = null;
        _uboBindGroupLayout = null;
        _textureBindGroupLayout = null;
        _bindGroupLayouts = null;
        _shaderProgram = null;
        _defaultTexture = null;
        _defaultSampler = null;
        _uboStagingBuffer = null;
    }

    private void InitializeShaders()
    {
        var shader = Shader.LoadDefault(DefaultShader.UI);
        if (shader.IsNotValid())
        {
            Debug.LogError("Failed to load UI shader. Make sure 'UI.shader' exists in Assets/Defaults folder.");
            return;
        }

        ShaderPass pass = shader.GetPass(0);
        if (!pass.TryGetVariantProgram(null, out _shaderProgram))
        {
            Debug.LogError("Failed to compile UI shader.");
            return;
        }
    }

    private void InitializeGraphiteResources()
    {
        var device = Graphics.Graphite;

        // Create vertex buffer (will grow as needed)
        _vertexBuffer = device.CreateBuffer(BufferDescriptor.Vertex(4096, dynamic: true));

        // Create index buffer (will grow as needed)
        _indexBuffer = device.CreateBuffer(BufferDescriptor.Index(4096, dynamic: true));

        // Create UBO ring buffer for per-drawcall uniforms
        _uboRingBuffer = device.CreateBuffer(BufferDescriptor.Uniform(UboRingBufferSize));

        // CPU staging buffer
        _uboStagingBuffer = new byte[UboRingBufferSize];

        // Vertex layout: vec2 position, vec2 texcoord, vec4 color = 32 bytes/vertex
        _vertexLayout = new VertexLayoutDescriptor(
            new VertexBufferLayout(32,
                new VertexAttribute(0, Graphite.VertexFormat.Float2, 0),
                new VertexAttribute(1, Graphite.VertexFormat.Float2, 8),
                new VertexAttribute(2, Graphite.VertexFormat.Float4, 16)));

        // Bind group layout for UBO (group 0) — with dynamic offset for ring buffer
        _uboBindGroupLayout = device.CreateBindGroupLayout(new BindGroupLayoutDescriptor(
            BindGroupLayoutEntry.UniformBuffer(0, ShaderStage.AllGraphics,
                hasDynamicOffset: true, name: "UIUniforms")));

        // Bind group layout for texture (group 1)
        _textureBindGroupLayout = device.CreateBindGroupLayout(new BindGroupLayoutDescriptor(
            BindGroupLayoutEntry.CombinedTextureSampler(0, ShaderStage.Fragment,
                name: "texture0")));

        _bindGroupLayouts = [_uboBindGroupLayout, _textureBindGroupLayout];

        // Create the UBO bind group (references the ring buffer; offset is dynamic)
        _uboBindGroup = device.CreateBindGroup(new BindGroupDescriptor(
            _uboBindGroupLayout,
            BindGroupEntry.ForBuffer(0, _uboRingBuffer, 0, UboStructSize)));

        // Default sampler for UI textures (linear filtering, clamp-to-edge for UI)
        _defaultSampler = device.CreateSampler(SamplerDescriptor.LinearClamp);

        // Default 1×1 white texture
        _defaultTexture = new Texture2D(1, 1);
        byte[] pixelData = [255, 255, 255, 255];
        _defaultTexture.SetData(new Memory<byte>(pixelData), 0, 0, 1, 1);
    }

    private static Float4 ToVector4(Color32 color)
    {
        return new Float4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
    }

    public object CreateTexture(uint width, uint height)
    {
        return new Texture2D(width, height);
    }

    public Int2 GetTextureSize(object texture)
    {
        if (texture is not Texture2D tkTexture)
            throw new ArgumentException("Invalid texture type");

        return new Int2((int)tkTexture.Width, (int)tkTexture.Height);
    }

    public void SetTextureData(object texture, IntRect bounds, byte[] data)
    {
        if (texture is not Texture2D tkTexture)
            throw new ArgumentException("Invalid texture type");

        tkTexture.SetData(new Memory<byte>(data), bounds.Min.X, bounds.Min.Y,
            (uint)bounds.Size.X, (uint)bounds.Size.Y);

        // Invalidate cached bind group for this texture since its content changed
        var graphiteTex = tkTexture.Handle?.GraphiteTexture;
        if (graphiteTex != null)
            _textureBindGroups.Remove(graphiteTex);
    }

    public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls)
    {
        if (drawCalls.Count == 0)
            return;

        if (_shaderProgram == null || !Graphics.IsGraphiteReady || _uboRingBuffer == null)
            return;

        var device = Graphics.Graphite;
        int drawCount = Math.Min(drawCalls.Count, MaxDrawsPerFrame);

        // === 1. Upload vertex data ===
        if (canvas.Vertices.Count > 0)
        {
            float[] packedVertexData = new float[canvas.Vertices.Count * 8];
            for (int i = 0; i < canvas.Vertices.Count; i++)
            {
                Vertex vertex = canvas.Vertices[i];
                packedVertexData[i * 8 + 0] = vertex.x;
                packedVertexData[i * 8 + 1] = vertex.y;
                packedVertexData[i * 8 + 2] = vertex.u;
                packedVertexData[i * 8 + 3] = vertex.v;
                packedVertexData[i * 8 + 4] = vertex.r / 255f;
                packedVertexData[i * 8 + 5] = vertex.g / 255f;
                packedVertexData[i * 8 + 6] = vertex.b / 255f;
                packedVertexData[i * 8 + 7] = vertex.a / 255f;
            }

            uint vertexDataSize = (uint)(packedVertexData.Length * sizeof(float));
            EnsureBufferSize(ref _vertexBuffer, vertexDataSize, BufferUsage.Vertex | BufferUsage.CopyDestination);
            device.UpdateBuffer<float>(_vertexBuffer!, 0, packedVertexData.AsSpan());
        }

        // === 2. Upload index data ===
        if (canvas.Indices.Count > 0)
        {
            uint[] indices = [.. canvas.Indices];
            uint indexDataSize = (uint)(indices.Length * sizeof(uint));
            EnsureBufferSize(ref _indexBuffer, indexDataSize, BufferUsage.Index | BufferUsage.CopyDestination);
            device.UpdateBuffer<uint>(_indexBuffer!, 0, indices.AsSpan());
        }

        // === 3. Pack all drawcall UBO data into the ring buffer ===
        for (int i = 0; i < drawCount; i++)
        {
            var drawCall = drawCalls[i];
            drawCall.GetScissor(out Float4x4 scissor, out Float2 scissorExt);

            var uboData = new UIUniformsData
            {
                Projection = _projection,
                ScissorMat = scissor,
                ScissorExtPad = new Float4(scissorExt.X, scissorExt.Y, 0, 0),
                BrushMat = drawCall.Brush.BrushMatrix,
                BrushTypePad = new Float4((int)drawCall.Brush.Type, 0, 0, 0),
                BrushColor1 = ToVector4(drawCall.Brush.Color1),
                BrushColor2 = ToVector4(drawCall.Brush.Color2),
                BrushParams = new Float4(
                    drawCall.Brush.Point1.X, drawCall.Brush.Point1.Y,
                    drawCall.Brush.Point2.X, drawCall.Brush.Point2.Y),
                BrushParams2Pad = new Float4(
                    drawCall.Brush.CornerRadii, drawCall.Brush.Feather, 0, 0),
            };

            uint offset = UboSlotSize * (uint)i;
            unsafe
            {
                fixed (byte* dst = &_uboStagingBuffer![offset])
                {
                    *(UIUniformsData*)dst = uboData;
                }
            }
        }

        // Upload the entire populated region to the GPU in one call
        uint totalUboBytes = UboSlotSize * (uint)drawCount;
        device.UpdateBuffer<byte>(_uboRingBuffer, 0,
            new ReadOnlySpan<byte>(_uboStagingBuffer, 0, (int)totalUboBytes));

        // === 4. Record and submit Graphite commands ===
        using var cmd = new RenderCommandBuffer("PaperRenderer");

        // Begin render pass
        Graphite.Texture? swapchainTex = null;
        bool clearRT = ShouldClear;
        ShouldClear = false;
        if (RenderTarget != null)
        {
            if (clearRT)
            {
                // LoadOp.Clear uses initialLayout=Undefined, so no pre-barrier needed.
                cmd.BeginRenderPass(RenderTarget, LoadOp.Clear, clearDepth: true);
            }
            else
            {
                // Ensure target attachments are in RenderTarget state for LoadOp.Load.
                // The main pipeline or a prior frame may have left them in ShaderResource.
                if (!Graphics.IsOpenGL)
                {
                    var colorAttachments = RenderTarget.frameBuffer.GraphiteColorAttachments;
                    if (colorAttachments != null)
                    {
                        foreach (var tex in colorAttachments)
                        {
                            if (tex != null)
                                cmd.ResourceBarrier(new Graphite.ResourceBarrier(
                                    tex, ResourceState.ShaderResource, ResourceState.RenderTarget));
                        }
                    }
                }
                cmd.BeginRenderPass(RenderTarget, LoadOp.Load, clearDepth: false);
            }
        }
        else
        {
            swapchainTex = device.GetSwapchainTexture();
            // Load to preserve content already rendered to the swapchain this frame
            // (the scene pipeline clears it to the camera color on Vulkan, and the
            // legacy GL path renders directly to the default framebuffer on OpenGL).
            // If no prior pass touched the swapchain (e.g. no cameras in the scene),
            // Clear to black so the Vulkan image transitions from Undefined layout.
            var colorAtt = Graphics.SwapchainClearedThisFrame || Graphics.IsOpenGL
                ? RenderPassColorAttachment.Load(swapchainTex)
                : RenderPassColorAttachment.Clear(swapchainTex, new Float4(0, 0, 0, 1));
            var desc = new RenderPassDescriptor
            {
                ColorAttachments = [colorAtt],
            };
            cmd.BeginRenderPass(in desc, new RenderPassLayout([swapchainTex.Format]));
        }

        // Resolve pipeline (cached, with bind group layouts for GL UBO/sampler linkage)
        var uiState = new RasterizerState
        {
            DepthTest = false,
            DoBlend = true,
            BlendSrc = RasterizerState.Blending.One,
            BlendDst = RasterizerState.Blending.OneMinusSrcAlpha,
            Blend = RasterizerState.BlendMode.Add,
            CullFace = RasterizerState.PolyFace.None,
        };

        var renderPassLayout = RenderTarget != null
            ? GraphiteFormatMapper.MapRenderPassLayout(RenderTarget.frameBuffer)
            : new RenderPassLayout([swapchainTex!.Format]);

        var pipeline = PipelineStateCache.GetOrCreate(
            _shaderProgram, _vertexLayout, uiState, Topology.Triangles,
            renderPassLayout, _bindGroupLayouts);

        cmd.SetPipeline(pipeline);
        cmd.SetViewport(0, 0, _viewportWidth, _viewportHeight);

        // Bind vertex and index buffers
        cmd.SetVertexBuffer(0, _vertexBuffer!);
        cmd.SetIndexBuffer(_indexBuffer!, Graphite.IndexFormat.Uint32);

        // === 5. Per-drawcall: set bind groups and draw ===
        int indexOffset = 0;
        for (int i = 0; i < drawCount; i++)
        {
            var drawCall = drawCalls[i];

            // UBO bind group with dynamic offset into the ring buffer
            uint uboOffset = UboSlotSize * (uint)i;
            Span<uint> offsets = stackalloc uint[] { uboOffset };
            cmd.SetBindGroup(0, _uboBindGroup!, offsets);

            // Texture bind group
            Texture2D texture = (drawCall.Texture as Texture2D) ?? _defaultTexture!;
            var texBindGroup = GetOrCreateTextureBindGroup(texture);
            if (texBindGroup != null)
                cmd.SetBindGroup(1, texBindGroup);

            // Draw
            cmd.DrawIndexed((uint)drawCall.ElementCount, 1, (uint)indexOffset);
            indexOffset += drawCall.ElementCount;
        }

        cmd.EndRenderPass();

        // Transition RT back to ShaderResource so downstream consumers
        // (e.g. ImGui) can sample it without a Vulkan layout mismatch.
        if (RenderTarget != null && !Graphics.IsOpenGL)
        {
            var colorAttachments = RenderTarget.frameBuffer.GraphiteColorAttachments;
            if (colorAttachments != null)
            {
                foreach (var tex in colorAttachments)
                {
                    if (tex != null)
                        cmd.ResourceBarrier(new Graphite.ResourceBarrier(
                            tex, ResourceState.RenderTarget, ResourceState.ShaderResource));
                }
            }
        }

        cmd.Submit();
    }

    private BindGroup? GetOrCreateTextureBindGroup(Texture2D texture)
    {
        var graphiteTex = texture.Handle?.GraphiteTexture;
        if (graphiteTex == null || _defaultSampler == null)
            return null;

        if (_textureBindGroups.TryGetValue(graphiteTex, out var cached))
            return cached;

        var bindGroup = Graphics.Graphite.CreateBindGroup(new BindGroupDescriptor(
            _textureBindGroupLayout!,
            BindGroupEntry.ForTextureSampler(0, graphiteTex, _defaultSampler)));

        _textureBindGroups[graphiteTex] = bindGroup;
        return bindGroup;
    }

    private static void EnsureBufferSize(ref Graphite.Buffer? buffer, uint requiredSize, BufferUsage usage)
    {
        if (buffer != null && buffer.SizeInBytes >= requiredSize)
            return;

        uint newSize = Math.Max(requiredSize, (buffer?.SizeInBytes ?? 4096u) * 2);
        buffer?.Dispose();
        buffer = Graphics.Graphite.CreateBuffer(new BufferDescriptor(
            newSize, usage, MemoryAccess.CpuToGpu));
    }

    public void Dispose()
    {
        Cleanup();
    }
}
