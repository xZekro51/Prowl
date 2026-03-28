// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using ImGuiNET;

using Prowl.Runtime;
using Prowl.Runtime.Graphite;
using Prowl.Runtime.Rendering;
using Prowl.Vector;

using Graphite = Prowl.Runtime.Graphite;

namespace Prowl.ImGuiIntegration;

/// <summary>
/// Graphite-based Dear ImGui renderer that works on both OpenGL and Vulkan backends.
/// Replaces the <c>Silk.NET.OpenGL.Extensions.ImGui.ImGuiController</c> rendering
/// functionality with the engine's Graphite abstraction layer.
/// </summary>
public sealed class ImGuiRendererGraphite : IDisposable
{
    // ── Shaders (GLSL 450, cross-compiled to SPIR-V for Vulkan) ─────
    private const string VertexShaderSource = """
        #version 450
        layout(set=0, binding=0) uniform Uniforms { mat4 projection; };
        layout(location=0) in vec2 aPos;
        layout(location=1) in vec2 aUV;
        layout(location=2) in vec4 aColor;
        layout(location=0) out vec2 vUV;
        layout(location=1) out vec4 vColor;
        void main() {
            vUV = aUV;
            vColor = aColor;
            gl_Position = projection * vec4(aPos, 0.0, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 450
        layout(set=1, binding=0) uniform sampler2D sTexture;
        layout(location=0) in vec2 vUV;
        layout(location=1) in vec4 vColor;
        layout(location=0) out vec4 fragColor;
        void main() {
            fragColor = vColor * texture(sTexture, vUV);
        }
        """;

    // ── Graphite resources ──────────────────────────────────────────
    private Graphite.Buffer? _vertexBuffer;
    private Graphite.Buffer? _indexBuffer;
    private Graphite.Texture? _fontTexture;
    private Sampler? _fontSampler;
    private BindGroupLayout? _uboBGL;
    private BindGroupLayout? _textureBGL;
    private BindGroupLayout[]? _bindGroupLayouts;
    private BindGroup? _uboBindGroup;
    private BindGroup? _fontTextureBindGroup;
    private Graphite.Buffer? _projectionUBO;
    private ShaderModule? _vertexShaderModule;
    private ShaderModule? _fragmentShaderModule;
    private PipelineState? _pipeline;

    // ── Vertex layout ───────────────────────────────────────────────
    private VertexLayoutDescriptor _vertexLayout;

    // ── Per-frame texture bind group cache ───────────────────────────
    private readonly Dictionary<nint, BindGroup> _textureBindGroups = new();

    // ── Texture registration for ImGui.Image() ──────────────────────
    private readonly Dictionary<nint, Graphite.Texture> _registeredTextures = new();

    // ── Font atlas texture ID ───────────────────────────────────────
    private nint _fontAtlasTexId;

    // ── State tracking ──────────────────────────────────────────────
    private bool _disposed;
    private int _fbWidth;
    private int _fbHeight;

    /// <summary>
    /// Singleton instance for use by the texture registry.
    /// Set when <see cref="Initialize"/> is called.
    /// </summary>
    internal static ImGuiRendererGraphite? Instance { get; private set; }

    /// <summary>
    /// Initialises Graphite resources (shaders, buffers, pipeline, font atlas).
    /// </summary>
    public void Initialize(int fbWidth, int fbHeight)
    {
        Instance = this;
        _fbWidth = fbWidth;
        _fbHeight = fbHeight;

        var device = Graphics.Graphite;

        // ── Shader modules ──────────────────────────────────────
        if (Graphics.IsOpenGL)
        {
            _vertexShaderModule = device.CreateShaderModule(
                ShaderModuleDescriptor.VertexGLSL(VertexShaderSource));
            _fragmentShaderModule = device.CreateShaderModule(
                ShaderModuleDescriptor.FragmentGLSL(FragmentShaderSource));
        }
        else
        {
            // Vulkan requires SPIR-V — compile GLSL at runtime via shaderc.
            byte[] vertSpirv = ShaderCrossCompiler.CompileGLSLToSPIRV(
                VertexShaderSource, ShaderStage.Vertex, "ImGui.vert");
            byte[] fragSpirv = ShaderCrossCompiler.CompileGLSLToSPIRV(
                FragmentShaderSource, ShaderStage.Fragment, "ImGui.frag");

            _vertexShaderModule = device.CreateShaderModule(
                ShaderModuleDescriptor.VertexSPIRV(vertSpirv));
            _fragmentShaderModule = device.CreateShaderModule(
                ShaderModuleDescriptor.FragmentSPIRV(fragSpirv));
        }

        // ── Vertex layout: ImDrawVert = 20 bytes ────────────────
        // float2 pos (0), float2 uv (8), u8x4 color normalised (16)
        _vertexLayout = new VertexLayoutDescriptor(
            new VertexBufferLayout(20,
                new VertexAttribute(0, Graphite.VertexFormat.Float2, 0),
                new VertexAttribute(1, Graphite.VertexFormat.Float2, 8),
                new VertexAttribute(2, Graphite.VertexFormat.UByte4Norm, 16)));

        // ── Buffers (will grow as needed) ────────────────────────
        _vertexBuffer = device.CreateBuffer(BufferDescriptor.Vertex(64 * 1024, dynamic: true));
        _indexBuffer = device.CreateBuffer(BufferDescriptor.Index(64 * 1024, dynamic: true));

        // ── UBO for projection matrix (single mat4 = 64 bytes) ──
        _projectionUBO = device.CreateBuffer(BufferDescriptor.Uniform(64));

        // ── Bind group layouts ──────────────────────────────────
        _uboBGL = device.CreateBindGroupLayout(new BindGroupLayoutDescriptor(
            BindGroupLayoutEntry.UniformBuffer(0, ShaderStage.Vertex, name: "Uniforms")));

        _textureBGL = device.CreateBindGroupLayout(new BindGroupLayoutDescriptor(
            BindGroupLayoutEntry.CombinedTextureSampler(0, ShaderStage.Fragment, name: "sTexture")));

        _bindGroupLayouts = [_uboBGL, _textureBGL];

        // ── UBO bind group ──────────────────────────────────────
        _uboBindGroup = device.CreateBindGroup(new BindGroupDescriptor(
            _uboBGL,
            BindGroupEntry.ForBuffer(0, _projectionUBO, 0, 64)));

        // ── Default sampler (linear, clamp) ─────────────────────
        _fontSampler = device.CreateSampler(SamplerDescriptor.LinearClamp);

        // ── Upload font atlas ───────────────────────────────────
        RebuildFontAtlas();
    }

    /// <summary>
    /// Rebuilds and uploads the ImGui font atlas to the GPU.
    /// Call after adding fonts to <c>ImGui.GetIO().Fonts</c>.
    /// </summary>
    public unsafe void RebuildFontAtlas()
    {
        var device = Graphics.Graphite;
        var io = ImGui.GetIO();

        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytesPerPixel);

        // Dispose old font texture and bind group
        _fontTextureBindGroup?.Dispose();
        _fontTexture?.Dispose();

        _fontTexture = device.CreateTexture(TextureDescriptor.Texture2D(
            (uint)width, (uint)height,
            TextureFormat.RGBA8Unorm,
            TextureUsage.Sampled | TextureUsage.CopyDestination));

        device.UpdateTexture(_fontTexture,
            TextureUpdateDescriptor.FullMip((uint)width, (uint)height),
            new ReadOnlySpan<byte>(pixels.ToPointer(), width * height * bytesPerPixel));

        // Create bind group for font texture
        _fontTextureBindGroup = device.CreateBindGroup(new BindGroupDescriptor(
            _textureBGL!,
            BindGroupEntry.ForTextureSampler(0, _fontTexture, _fontSampler!)));

        // Register the font atlas and tell ImGui about it
        _fontAtlasTexId = RegisterTextureInternal(_fontTexture, _fontTextureBindGroup);
        io.Fonts.SetTexID(_fontAtlasTexId);
        io.Fonts.ClearTexData();
    }

    /// <summary>
    /// Updates the projection matrix and display size for a new frame.
    /// Must be called before <c>ImGui.NewFrame()</c>.
    /// </summary>
    public void NewFrame(int fbWidth, int fbHeight)
    {
        _fbWidth = fbWidth;
        _fbHeight = fbHeight;
    }

    /// <summary>
    /// Renders the ImGui draw data using Graphite commands.
    /// Call after <c>ImGui.Render()</c>.
    /// </summary>
    public unsafe void RenderDrawData(ImDrawDataPtr drawData)
    {
        if (!drawData.Valid || drawData.CmdListsCount == 0)
            return;

        if (!Graphics.IsGraphiteReady || _vertexShaderModule == null || _fragmentShaderModule == null)
            return;

        var device = Graphics.Graphite;
        int fbWidth = (int)(drawData.DisplaySize.X * drawData.FramebufferScale.X);
        int fbHeight = (int)(drawData.DisplaySize.Y * drawData.FramebufferScale.Y);
        if (fbWidth <= 0 || fbHeight <= 0)
            return;

        // ── Upload projection matrix ────────────────────────────
        float L = drawData.DisplayPos.X;
        float R = drawData.DisplayPos.X + drawData.DisplaySize.X;
        float T = drawData.DisplayPos.Y;
        float B = drawData.DisplayPos.Y + drawData.DisplaySize.Y;

        // Float4x4 stores columns (c0–c3). The scalar constructor treats
        // parameters as m_row_col and transposes into column-major layout,
        // so the translation terms must appear in the *last column* of the
        // visual layout (not the last row).
        // Using CreateOrthoOffCenter (same as PaperRenderer) avoids any
        // layout confusion and produces a correct column-major matrix.
        Float4x4 projection = Float4x4.CreateOrthoOffCenter(L, R, B, T, -1, 1);

        device.UpdateBuffer(_projectionUBO!, 0, new ReadOnlySpan<Float4x4>(ref projection));

        // ── Ensure pipeline is created ──────────────────────────
        var swapchainTex = device.GetSwapchainTexture();
        var renderPassLayout = new RenderPassLayout([swapchainTex.Format]);
        EnsurePipeline(renderPassLayout);

        // ── Begin render pass targeting swapchain ────────────────
        using var cmd = new RenderCommandBuffer("ImGui");

        var colorAtt = Graphics.SwapchainClearedThisFrame || Graphics.IsOpenGL
            ? RenderPassColorAttachment.Load(swapchainTex)
            : RenderPassColorAttachment.Clear(swapchainTex, new Float4(0, 0, 0, 1));
        var rpDesc = new RenderPassDescriptor
        {
            ColorAttachments = [colorAtt],
        };
        cmd.BeginRenderPass(in rpDesc, renderPassLayout);

        // Mark swapchain as rendered to
        Graphics.SwapchainClearedThisFrame = true;

        cmd.SetPipeline(_pipeline!);
        cmd.SetViewport(0, 0, fbWidth, fbHeight);
        cmd.SetBindGroup(0, _uboBindGroup!);

        // ── Upload all draw-list vertex/index data into contiguous buffers ──
        // All data must be present before the command buffer is submitted,
        // because CpuToGpu writes happen on the CPU immediately while the
        // GPU reads occur later during execution.
        uint totalVtxBytes = 0;
        uint totalIdxBytes = 0;
        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            ImDrawListPtr cmdList = drawData.CmdLists[n];
            totalVtxBytes += (uint)(cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>());
            totalIdxBytes += (uint)(cmdList.IdxBuffer.Size * sizeof(ushort));
        }

        EnsureBufferSize(ref _vertexBuffer, totalVtxBytes, BufferUsage.Vertex | BufferUsage.CopyDestination);
        EnsureBufferSize(ref _indexBuffer, totalIdxBytes, BufferUsage.Index | BufferUsage.CopyDestination);

        uint vtxOffset = 0;
        uint idxOffset = 0;
        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            ImDrawListPtr cmdList = drawData.CmdLists[n];
            uint vtxSize = (uint)(cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>());
            uint idxSize = (uint)(cmdList.IdxBuffer.Size * sizeof(ushort));

            device.UpdateBuffer(_vertexBuffer!, vtxOffset,
                new ReadOnlySpan<byte>(cmdList.VtxBuffer.Data.ToPointer(), (int)vtxSize));
            device.UpdateBuffer(_indexBuffer!, idxOffset,
                new ReadOnlySpan<byte>(cmdList.IdxBuffer.Data.ToPointer(), (int)idxSize));

            vtxOffset += vtxSize;
            idxOffset += idxSize;
        }

        cmd.SetVertexBuffer(0, _vertexBuffer!);
        cmd.SetIndexBuffer(_indexBuffer!, IndexFormat.Uint16);

        // ── Iterate draw lists ──────────────────────────────────
        System.Numerics.Vector2 clipOff = drawData.DisplayPos;
        System.Numerics.Vector2 clipScale = drawData.FramebufferScale;

        int globalVtxOffset = 0;
        uint globalIdxOffset = 0;
        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            ImDrawListPtr cmdList = drawData.CmdLists[n];

            for (int cmdIdx = 0; cmdIdx < cmdList.CmdBuffer.Size; cmdIdx++)
            {
                ImDrawCmdPtr pcmd = cmdList.CmdBuffer[cmdIdx];

                if (pcmd.UserCallback != IntPtr.Zero)
                {
                    // User callback — not handled in this implementation
                    continue;
                }

                // Clip rect
                System.Numerics.Vector4 clipRect;
                clipRect.X = (pcmd.ClipRect.X - clipOff.X) * clipScale.X;
                clipRect.Y = (pcmd.ClipRect.Y - clipOff.Y) * clipScale.Y;
                clipRect.Z = (pcmd.ClipRect.Z - clipOff.X) * clipScale.X;
                clipRect.W = (pcmd.ClipRect.W - clipOff.Y) * clipScale.Y;

                if (clipRect.X >= fbWidth || clipRect.Y >= fbHeight || clipRect.Z < 0 || clipRect.W < 0)
                    continue;

                int scissorX = Math.Max(0, (int)clipRect.X);
                int scissorY = Math.Max(0, (int)clipRect.Y);
                uint scissorW = (uint)Math.Min(fbWidth - scissorX, (int)(clipRect.Z - clipRect.X));
                uint scissorH = (uint)Math.Min(fbHeight - scissorY, (int)(clipRect.W - clipRect.Y));
                if (scissorW == 0 || scissorH == 0)
                    continue;

                cmd.SetScissor(scissorX, scissorY, scissorW, scissorH);

                // Resolve texture bind group
                BindGroup? texBindGroup = ResolveTextureBindGroup(pcmd.TextureId);
                if (texBindGroup != null)
                    cmd.SetBindGroup(1, texBindGroup);

                cmd.DrawIndexed(pcmd.ElemCount, 1, pcmd.IdxOffset + globalIdxOffset, (int)pcmd.VtxOffset + globalVtxOffset);
            }

            globalVtxOffset += cmdList.VtxBuffer.Size;
            globalIdxOffset += (uint)cmdList.IdxBuffer.Size;
        }

        cmd.EndRenderPass();
        cmd.Submit();
    }

    // ── Texture registration ────────────────────────────────────────

    /// <summary>
    /// Registers a Graphite texture for use with <c>ImGui.Image()</c>.
    /// Returns an opaque <c>nint</c> texture ID.
    /// </summary>
    public nint RegisterTexture(Graphite.Texture texture)
    {
        if (texture == null)
            return 0;

        // Check if already registered
        foreach (var kvp in _registeredTextures)
        {
            if (kvp.Value == texture)
                return kvp.Key;
        }

        // Create a bind group for this texture
        var bindGroup = Graphics.Graphite.CreateBindGroup(new BindGroupDescriptor(
            _textureBGL!,
            BindGroupEntry.ForTextureSampler(0, texture, _fontSampler!)));

        return RegisterTextureInternal(texture, bindGroup);
    }

    /// <summary>
    /// Unregisters a texture previously registered via <see cref="RegisterTexture"/>.
    /// </summary>
    public void UnregisterTexture(nint id)
    {
        if (_textureBindGroups.TryGetValue(id, out var bg))
        {
            // Don't dispose the font bind group
            if (id != _fontAtlasTexId)
                bg.Dispose();
            _textureBindGroups.Remove(id);
        }
        _registeredTextures.Remove(id);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Instance = null;

        _pipeline?.Dispose();
        _vertexShaderModule?.Dispose();
        _fragmentShaderModule?.Dispose();

        foreach (var kvp in _textureBindGroups)
            kvp.Value?.Dispose();
        _textureBindGroups.Clear();
        _registeredTextures.Clear();

        _uboBindGroup?.Dispose();
        _projectionUBO?.Dispose();
        _uboBGL?.Dispose();
        _textureBGL?.Dispose();

        _fontTexture?.Dispose();
        _fontSampler?.Dispose();

        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
    }

    // ── Private helpers ─────────────────────────────────────────────

    private nint RegisterTextureInternal(Graphite.Texture texture, BindGroup bindGroup)
    {
        nint id = (nint)(texture.GetHashCode() ^ bindGroup.GetHashCode());
        // Ensure uniqueness
        while (_textureBindGroups.ContainsKey(id))
            id++;

        _textureBindGroups[id] = bindGroup;
        _registeredTextures[id] = texture;
        return id;
    }

    private BindGroup? ResolveTextureBindGroup(nint textureId)
    {
        if (_textureBindGroups.TryGetValue(textureId, out var bg))
            return bg;
        return null;
    }

    private void EnsurePipeline(RenderPassLayout renderPassLayout)
    {
        if (_pipeline != null)
            return;

        var descriptor = new PipelineStateDescriptor
        {
            VertexShader = _vertexShaderModule,
            FragmentShader = _fragmentShaderModule,
            VertexLayout = _vertexLayout,
            Topology = PrimitiveTopology.TriangleList,
            RasterizerState = RasterizerStateDescriptor.NoCull,
            DepthStencilState = DepthStencilStateDescriptor.NoDepth,
            BlendState = new BlendStateDescriptor(BlendAttachment.AlphaBlend),
            RenderPassLayout = renderPassLayout,
            BindGroupLayouts = _bindGroupLayouts,
            DebugName = "ImGui",
        };

        _pipeline = Graphics.Graphite.CreatePipelineState(in descriptor);
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
}
