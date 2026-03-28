// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Vector;

using Silk.NET.Core.Native;
using Silk.NET.OpenGL;

using LegacyVertexFormat = Prowl.Runtime.VertexFormat;

namespace Prowl.Runtime.Graphite.OpenGL;

/// <summary>
/// OpenGL implementation of the Graphite graphics device.
/// Also provides the legacy immediate-mode rendering API that existing engine code
/// relies on (viewport, clear, state management, uniforms, draw calls).
/// This unified device replaces both the old <c>OpenGLGraphicsDevice</c> and the
/// Graphite <c>GLGraphiteDevice</c> — there is now a single device object.
/// </summary>
public class GLGraphiteDevice : GraphiteDevice
{
    // OpenGL constants not defined in Silk.NET GetPName enum
    private const int GL_MAX_SHADER_STORAGE_BLOCK_SIZE = 0x90DE;
    private const int GL_MAX_COMPUTE_WORK_GROUP_SIZE = 0x91BF;
    private const int GL_MAX_COMPUTE_WORK_GROUP_INVOCATIONS = 0x90EB;

    /// <summary>
    /// The Silk.NET OpenGL context.
    /// Used by both the Graphite command-list backend and the legacy
    /// immediate-mode rendering path during the migration.
    /// </summary>
    public GL GLContext { get; internal set; } = null!;

    internal GL GL => GLContext;

    private DeviceCapabilities _capabilities;
    private int _maxCubeMapTextureSize;
    private int _maxArrayTextureLayers;
    private bool _initialized;
    private uint _swapchainWidth;
    private uint _swapchainHeight;

    // ── Legacy State Tracking ─────────────────────────────────────
    private bool _depthTest = true;
    private bool _depthWrite = true;
    private RasterizerState.DepthMode _depthMode = RasterizerState.DepthMode.Lequal;
    private bool _doBlend = true;
    private RasterizerState.Blending _blendSrc = RasterizerState.Blending.SrcAlpha;
    private RasterizerState.Blending _blendDst = RasterizerState.Blending.OneMinusSrcAlpha;
    private RasterizerState.BlendMode _blendEquation = RasterizerState.BlendMode.Add;
    private RasterizerState.PolyFace _cullFace = RasterizerState.PolyFace.Back;
    private RasterizerState.WindingOrder _winding = RasterizerState.WindingOrder.CW;

    // Framebuffer tracking
    private GraphicsFrameBuffer? _currentFramebuffer;
    private GraphicsFrameBuffer? _currentReadFramebuffer;
    private GraphicsFrameBuffer? _currentDrawFramebuffer;

    // Uniform/attribute caches
    public override Dictionary<ulong, uint> CachedBlockLocations { get; } = [];
    public override Dictionary<ulong, int> CachedUniformLocations { get; } = [];
    public override Dictionary<ulong, int> CachedAttribLocations { get; } = [];

    public override string BackendName => $"OpenGL {GL.GetStringS(StringName.Version)}";
    public override GraphicsBackendType BackendType => GraphicsBackendType.OpenGL;
    public override DeviceCapabilities Capabilities => _capabilities;
    public override uint SwapchainWidth => _swapchainWidth;
    public override uint SwapchainHeight => _swapchainHeight;
    public override bool NeedsExplicitSwapchainBlit => false;
    public override object? NativeContext => GLContext;

    public override void Initialize(GraphiteDeviceOptions options)
    {
        ThrowIfDisposed();
        if (_initialized)
            throw new InvalidOperationException("Device is already initialized.");

        // If GLContext was not pre-set (e.g. by a test fixture), acquire it from the window.
        GLContext ??= GL.GetApi(Window.InternalWindow);

        // Smooth lines
        GLContext.Enable(EnableCap.LineSmooth);

        // Query capabilities
        _capabilities = QueryCapabilities();
        _initialized = true;

        if (options.EnableDebugLayer)
        {
            unsafe
            {
                if (OperatingSystem.IsWindows())
                {
                    GLContext.DebugMessageCallback(DebugCallback, null);
                    GLContext.Enable(EnableCap.DebugOutput);
                    GLContext.Enable(EnableCap.DebugOutputSynchronous);
                }
            }
        }
    }

    private static void DebugCallback(GLEnum source, GLEnum type, int id, GLEnum severity, int length, nint message, nint userParam)
    {
        string? msg = SilkMarshal.PtrToString(message, NativeStringEncoding.UTF8);
        if (type == GLEnum.DebugTypeError || type == GLEnum.DebugTypeUndefinedBehavior)
            Debug.LogError($"OpenGL Error: {msg}");
        else if (type == GLEnum.DebugTypePerformance || type == GLEnum.DebugTypeMarker || type == GLEnum.DebugTypePortability)
            Debug.LogWarning($"OpenGL Warning: {msg}");
        //else
        //    Debug.Log($"OpenGL Message: {msg}");
    }

    private DeviceCapabilities QueryCapabilities()
    {
        GL.GetInteger(GetPName.MaxTextureSize, out int maxTexSize);
        GL.GetInteger(GetPName.MaxUniformBlockSize, out int maxUboSize);
        GL.GetInteger((GetPName)GL_MAX_SHADER_STORAGE_BLOCK_SIZE, out int maxSsboSize);
        GL.GetInteger(GetPName.MaxVertexAttribs, out int maxVertexAttribs);
        GL.GetInteger(GetPName.MaxColorAttachments, out int maxColorAttachments);

        // Legacy capability queries needed by existing engine code
        GL.GetInteger(GetPName.MaxCubeMapTextureSize, out int maxCubeMap);
        GL.GetInteger(GetPName.MaxArrayTextureLayers, out int maxArrayLayers);
        _maxCubeMapTextureSize = maxCubeMap;
        _maxArrayTextureLayers = maxArrayLayers;

        // Compute shader limits - use safe defaults
        // These are common limits for OpenGL 4.3+ compute shaders
        int maxWorkgroupX = 1024, maxWorkgroupY = 1024, maxWorkgroupZ = 64, maxWorkgroupInvocations = 1024;
        try
        {
            Span<int> workgroupSizes = stackalloc int[3];
            GL.GetInteger((GetPName)GL_MAX_COMPUTE_WORK_GROUP_SIZE, workgroupSizes);
            maxWorkgroupX = workgroupSizes[0];
            maxWorkgroupY = workgroupSizes[1];
            maxWorkgroupZ = workgroupSizes[2];
            GL.GetInteger((GetPName)GL_MAX_COMPUTE_WORK_GROUP_INVOCATIONS, out maxWorkgroupInvocations);
        }
        catch (Exception ex)
        {
            // Compute shaders might not be supported on older OpenGL versions - use defaults
            Debug.LogWarning($"Failed to query compute shader limits, using defaults: {ex.Message}");
        }

        return new DeviceCapabilities
        {
            DeviceName = GL.GetStringS(StringName.Renderer) ?? "Unknown",
            VendorName = GL.GetStringS(StringName.Vendor) ?? "Unknown",
            SupportsCompute = true,
            SupportsGeometryShaders = true,
            SupportsTessellation = true,
            SupportsMultiDrawIndirect = true,
            SupportsBindless = false, // Would need to check for extension
            MaxTextureSize = (uint)maxTexSize,
            MaxUniformBufferSize = (uint)maxUboSize,
            MaxStorageBufferSize = (uint)maxSsboSize,
            MaxBindGroups = 8,
            MaxSamplersPerStage = 16,
            MaxTexturesPerStage = 16,
            MaxVertexAttributes = (uint)maxVertexAttribs,
            MaxVertexBuffers = 16,
            MaxColorAttachments = (uint)maxColorAttachments,
            MaxComputeWorkgroupSizeX = (uint)maxWorkgroupX,
            MaxComputeWorkgroupSizeY = (uint)maxWorkgroupY,
            MaxComputeWorkgroupSizeZ = (uint)maxWorkgroupZ,
            MaxComputeInvocationsPerWorkgroup = (uint)maxWorkgroupInvocations,
        };
    }

    #region Resource Creation

    public override Buffer CreateBuffer(in BufferDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new GLBuffer(this, in descriptor);
    }

    public override Texture CreateTexture(in TextureDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new GLTexture(this, in descriptor);
    }

    public override Sampler CreateSampler(in SamplerDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new GLSampler(this, in descriptor);
    }

    public override ShaderModule CreateShaderModule(in ShaderModuleDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new GLShaderModule(this, in descriptor);
    }

    public override PipelineState CreatePipelineState(in PipelineStateDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new GLPipelineState(this, in descriptor);
    }

    public override ComputePipelineState CreateComputePipelineState(in ComputePipelineStateDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new GLComputePipelineState(this, in descriptor);
    }

    public override BindGroupLayout CreateBindGroupLayout(in BindGroupLayoutDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new GLBindGroupLayout(this, in descriptor);
    }

    public override BindGroup CreateBindGroup(in BindGroupDescriptor descriptor)
    {
        ThrowIfDisposed();
        return new GLBindGroup(this, in descriptor);
    }

    public override Fence CreateFence(bool signaled = false)
    {
        ThrowIfDisposed();
        return new GLFence(this, signaled);
    }

    #endregion

    #region Command List Management

    public override CommandList CreateCommandList()
    {
        ThrowIfDisposed();
        return new GLCommandList(this);
    }

    public override void SubmitCommands(CommandList commandList)
    {
        ThrowIfDisposed();
        if (commandList is GLCommandList glCmd)
        {
            glCmd.Execute();
        }
        else
        {
            throw new ArgumentException("Command list is not a GL command list.", nameof(commandList));
        }
    }

    public override void SubmitCommands(ReadOnlySpan<CommandList> commandLists)
    {
        ThrowIfDisposed();
        foreach (var cmd in commandLists)
        {
            SubmitCommands(cmd);
        }
    }

    public override void SubmitCommands(CommandList commandList, Fence fence)
    {
        SubmitCommands(commandList);
        if (fence is GLFence glFence)
        {
            glFence.InsertFence();
        }
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
        GL.Finish();
    }

    #endregion

    #region Frame Management

    public override void ResizeSwapchain(uint width, uint height)
    {
        ThrowIfDisposed();
        _swapchainWidth = width;
        _swapchainHeight = height;
        // OpenGL swapchain resize is handled by the windowing system
    }

    #endregion

    #region Resource Updates

    public override unsafe void UpdateBuffer<T>(Buffer buffer, uint offsetInBytes, ReadOnlySpan<T> data)
    {
        ThrowIfDisposed();
        if (buffer is GLBuffer glBuffer)
        {
            glBuffer.Update(offsetInBytes, data);
        }
    }

    public override void UpdateTexture(Texture texture, in TextureUpdateDescriptor descriptor, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        if (texture is GLTexture glTexture)
        {
            glTexture.Update(in descriptor, data);
        }
    }

    public override void ReadbackTexture(Texture texture, uint mipLevel, uint arrayLayer, Span<byte> destination)
    {
        ThrowIfDisposed();
        if (texture is GLTexture glTexture)
        {
            glTexture.Read(mipLevel, destination);
        }
    }

    public override void GenerateMipmaps(Texture texture)
    {
        ThrowIfDisposed();
        if (texture is GLTexture glTexture)
        {
            glTexture.GenerateMipmaps();
        }
    }

    #endregion

    #region Swapchain

    public override Texture GetSwapchainTexture()
    {
        ThrowIfDisposed();
        // OpenGL's default framebuffer doesn't give us a texture handle
        // We return a special "null texture" that represents the default framebuffer
        return GLSwapchainTexture.Instance;
    }

    #endregion

    protected override void DisposeResources()
    {
        GLContext?.Dispose();
    }

    #region Legacy Immediate-Mode API

    // ── Viewport & Clear ──────────────────────────────────────────

    public override void Viewport(int x, int y, uint width, uint height)
        => GLContext.Viewport(x, y, width, height);

    public override void Clear(float r, float g, float b, float a, ClearFlags v)
    {
        GLContext.ClearColor(r, g, b, a);

        bool needRestoreDepthWrite = false;
        if (v.HasFlag(ClearFlags.Depth) && !_depthWrite)
        {
            GLContext.DepthMask(true);
            needRestoreDepthWrite = true;
        }

        ClearBufferMask clearBufferMask = 0;
        if (v.HasFlag(ClearFlags.Color))
            clearBufferMask |= ClearBufferMask.ColorBufferBit;
        if (v.HasFlag(ClearFlags.Depth))
            clearBufferMask |= ClearBufferMask.DepthBufferBit;
        if (v.HasFlag(ClearFlags.Stencil))
            clearBufferMask |= ClearBufferMask.StencilBufferBit;
        GLContext.Clear(clearBufferMask);

        if (needRestoreDepthWrite)
            GLContext.DepthMask(false);
    }

    // ── Rasterizer State ──────────────────────────────────────────

    public override void SetState(RasterizerState state, bool force = false)
    {
        if (_depthTest != state.DepthTest || force)
        {
            if (state.DepthTest) GLContext.Enable(EnableCap.DepthTest);
            else GLContext.Disable(EnableCap.DepthTest);
            _depthTest = state.DepthTest;
        }

        if (_depthWrite != state.DepthWrite || force)
        {
            GLContext.DepthMask(state.DepthWrite);
            _depthWrite = state.DepthWrite;
        }

        if (_depthMode != state.Depth || force)
        {
            GLContext.DepthFunc(DepthModeToGL(state.Depth));
            _depthMode = state.Depth;
        }

        if (_doBlend != state.DoBlend || force)
        {
            if (state.DoBlend) GLContext.Enable(EnableCap.Blend);
            else GLContext.Disable(EnableCap.Blend);
            _doBlend = state.DoBlend;
        }

        if (_blendSrc != state.BlendSrc || _blendDst != state.BlendDst || force)
        {
            GLContext.BlendFunc(BlendingToGL(state.BlendSrc), BlendingToGL(state.BlendDst));
            _blendSrc = state.BlendSrc;
            _blendDst = state.BlendDst;
        }

        if (_blendEquation != state.Blend || force)
        {
            GLContext.BlendEquation(BlendModeToGL(state.Blend));
            _blendEquation = state.Blend;
        }

        if (_cullFace != state.CullFace || force)
        {
            if (state.CullFace != RasterizerState.PolyFace.None)
            {
                GLContext.Enable(EnableCap.CullFace);
                GLContext.CullFace(CullFaceToGL(state.CullFace));
            }
            else
                GLContext.Disable(EnableCap.CullFace);
            _cullFace = state.CullFace;
        }

        if (_winding != state.Winding || force)
        {
            GLContext.FrontFace(WindingToGL(state.Winding));
            _winding = state.Winding;
        }
    }

    public override RasterizerState GetState() => new()
    {
        DepthTest = _depthTest,
        DepthWrite = _depthWrite,
        Depth = _depthMode,
        DoBlend = _doBlend,
        BlendSrc = _blendSrc,
        BlendDst = _blendDst,
        Blend = _blendEquation,
        CullFace = _cullFace,
    };

    // ── Buffers ───────────────────────────────────────────────────

    public override void BindBuffer(GraphicsBuffer buffer)
    {
        if (buffer.GraphiteBuffer is GLBuffer glBuf)
            GLContext.BindBuffer(glBuf.GetTarget(), glBuf.Handle);
    }

    public override uint GetBlockIndex(GraphicsProgram program, string blockName)
    {
        ulong key = CombineKey(program.ID, blockName);
        if (CachedBlockLocations.TryGetValue(key, out uint loc))
            return loc;

        BindProgram(program);
        uint newLoc = GLContext.GetUniformBlockIndex(program.Handle, blockName);
        CachedBlockLocations[key] = newLoc;
        return newLoc;
    }

    public override void BindUniformBuffer(GraphicsProgram program, string blockName, GraphicsBuffer buffer, uint bindingPoint = 0)
    {
        uint blockIndex = GetBlockIndex(program, blockName);
        if (blockIndex == 0xFFFFFFFF) return; // GL_INVALID_INDEX

        BindProgram(program);
        GLContext.UniformBlockBinding(program.Handle, blockIndex, bindingPoint);
        if (buffer.GraphiteBuffer is GLBuffer glBuf)
            GLContext.BindBufferBase(BufferTargetARB.UniformBuffer, bindingPoint, glBuf.Handle);
    }

    // ── Vertex Arrays ─────────────────────────────────────────────

    public override void BindVertexArray(GraphicsVertexArray? vertexArrayObject)
    {
        uint handle = vertexArrayObject is GraphicsVertexArray vao ? vao.Handle : 0u;
        GLContext.BindVertexArray(handle);
    }

    // ── Frame Buffers ─────────────────────────────────────────────

    public override void UnbindFramebuffer()
    {
        GLContext.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _currentFramebuffer = null;
        _currentReadFramebuffer = null;
        _currentDrawFramebuffer = null;
    }

    public override void BindFramebuffer(GraphicsFrameBuffer frameBuffer, FBOTarget target = FBOTarget.Framebuffer)
    {
        FramebufferTarget glTarget = target switch
        {
            FBOTarget.Read => FramebufferTarget.ReadFramebuffer,
            FBOTarget.Draw => FramebufferTarget.DrawFramebuffer,
            FBOTarget.Framebuffer => FramebufferTarget.Framebuffer,
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        GLContext.BindFramebuffer(glTarget, frameBuffer!.Handle);

        switch (target)
        {
            case FBOTarget.Read:
                _currentReadFramebuffer = frameBuffer;
                break;
            case FBOTarget.Draw:
                _currentDrawFramebuffer = frameBuffer;
                break;
            case FBOTarget.Framebuffer:
                _currentFramebuffer = frameBuffer;
                _currentReadFramebuffer = frameBuffer;
                _currentDrawFramebuffer = frameBuffer;
                break;
        }
        Viewport(0, 0, frameBuffer.Width, frameBuffer.Height);
    }

    public override GraphicsFrameBuffer? GetCurrentFramebuffer(FBOTarget target = FBOTarget.Framebuffer)
    {
        return target switch
        {
            FBOTarget.Read => _currentReadFramebuffer,
            FBOTarget.Draw => _currentDrawFramebuffer,
            _ => _currentFramebuffer,
        };
    }

    public override void BlitFramebuffer(int srcX, int srcY, int srcWidth, int srcHeight,
        int destX, int destY, int destWidth, int destHeight, ClearFlags mask, BlitFilter filter)
    {
        ClearBufferMask clearBufferMask = 0;
        if (mask.HasFlag(ClearFlags.Color)) clearBufferMask |= ClearBufferMask.ColorBufferBit;
        if (mask.HasFlag(ClearFlags.Depth)) clearBufferMask |= ClearBufferMask.DepthBufferBit;
        if (mask.HasFlag(ClearFlags.Stencil)) clearBufferMask |= ClearBufferMask.StencilBufferBit;

        BlitFramebufferFilter blitFilter = filter switch
        {
            BlitFilter.Nearest => BlitFramebufferFilter.Nearest,
            BlitFilter.Linear => BlitFramebufferFilter.Linear,
            _ => throw new ArgumentOutOfRangeException(nameof(filter)),
        };
        GLContext.BlitFramebuffer(srcX, srcY, srcWidth, srcHeight, destX, destY, destWidth, destHeight, clearBufferMask, blitFilter);
    }

    public override unsafe T ReadPixel<T>(int attachment, int x, int y, TextureImageFormat format)
    {
        GLContext.ReadBuffer((ReadBufferMode)((int)ReadBufferMode.ColorAttachment0 + attachment));
        GetTextureFormatEnums(format, out _, out PixelType pixelType, out PixelFormat pixelFormat);
        return GLContext.ReadPixels<T>(x, y, 1, 1, pixelFormat, pixelType);
    }

    // ── Shaders ───────────────────────────────────────────────────

    public override void BindProgram(GraphicsProgram program)
    {
        if (GraphicsProgram.currentProgram?.Handle == program.Handle) return;
        GLContext.UseProgram(program.Handle);
        GraphicsProgram.currentProgram = program;
    }

    public override int GetUniformLocation(GraphicsProgram program, string name)
    {
        ulong key = CombineKey(program.ID, name);
        if (CachedUniformLocations.TryGetValue(key, out int loc))
            return loc;

        BindProgram(program);
        int newLoc = GLContext.GetUniformLocation(program.Handle, name);
        CachedUniformLocations[key] = newLoc;
        return newLoc;
    }

    public override int GetAttribLocation(GraphicsProgram program, string name)
    {
        ulong key = CombineKey(program.ID, name);
        if (CachedAttribLocations.TryGetValue(key, out int loc))
            return loc;

        BindProgram(program);
        int newLoc = GLContext.GetAttribLocation(program.Handle, name);
        CachedAttribLocations[key] = newLoc;
        return newLoc;
    }

    public override void SetUniformF(GraphicsProgram program, string name, float value)
    {
        int loc = GetUniformLocation(program, name);
        if (loc == -1) return;
        BindProgram(program);
        GLContext.Uniform1(loc, value);
    }

    public override void SetUniformI(GraphicsProgram program, string name, int value)
    {
        int loc = GetUniformLocation(program, name);
        if (loc == -1) return;
        BindProgram(program);
        GLContext.Uniform1(loc, value);
    }

    public override void SetUniformV2(GraphicsProgram program, string name, Float2 value)
    {
        int loc = GetUniformLocation(program, name);
        if (loc == -1) return;
        BindProgram(program);
        GLContext.Uniform2(loc, value);
    }

    public override void SetUniformV3(GraphicsProgram program, string name, Float3 value)
    {
        int loc = GetUniformLocation(program, name);
        if (loc == -1) return;
        BindProgram(program);
        GLContext.Uniform3(loc, value);
    }

    public override void SetUniformV4(GraphicsProgram program, string name, Float4 value)
    {
        int loc = GetUniformLocation(program, name);
        if (loc == -1) return;
        BindProgram(program);
        GLContext.Uniform4(loc, value);
    }

    public override unsafe void SetUniformMatrix(GraphicsProgram program, string name, uint count, bool transpose, in float matrix)
    {
        int loc = GetUniformLocation(program, name);
        if (loc == -1) return;
        BindProgram(program);
        GLContext.UniformMatrix4(loc, count, transpose, in matrix);
    }

    public override void SetUniformMatrix(GraphicsProgram program, string name, bool transpose, Float4x4 matrix)
    {
        Float4x4 fMat = matrix;
        SetUniformMatrix(program, name, 1, transpose, in fMat.c0.X);
    }

    public override void SetUniformMatrix(GraphicsProgram program, string name, bool transpose, in float matrix)
        => SetUniformMatrix(program, name, 1, transpose, in matrix);

    public override void SetUniformTexture(GraphicsProgram program, string name, int slot, GraphicsTexture texture)
    {
        int loc = GetUniformLocation(program, name);
        if (loc == -1) return;
        BindProgram(program);
        GLContext.ActiveTexture((TextureUnit)((uint)TextureUnit.Texture0 + slot));
        if (texture.GraphiteTexture is GLTexture glTex)
            GLContext.BindTexture(glTex.Target, glTex.Handle);
        GLContext.Uniform1(loc, slot);
    }

    // ── Drawing ───────────────────────────────────────────────────

    public override void Draw(Topology primitiveType, uint count)
        => Draw(primitiveType, 0, count);

    public override void Draw(Topology primitiveType, int offset, uint count)
    {
        PrimitiveType mode = TopologyToGL(primitiveType);
        GLContext.DrawArrays(mode, offset, count);
    }

    public override unsafe void DrawIndexed(Topology primitiveType, uint indexCount, bool index32bit, void* value)
    {
        PrimitiveType mode = TopologyToGL(primitiveType);
        GLContext.DrawElements(mode, indexCount, index32bit ? DrawElementsType.UnsignedInt : DrawElementsType.UnsignedShort, value);
    }

    public override unsafe void DrawIndexed(Topology primitiveType, uint indexCount, int startIndex, int baseVertex, bool index32bit)
    {
        PrimitiveType mode = TopologyToGL(primitiveType);
        DrawElementsType format = index32bit ? DrawElementsType.UnsignedInt : DrawElementsType.UnsignedShort;
        int formatSize = index32bit ? sizeof(uint) : sizeof(ushort);
        GLContext.DrawElementsBaseVertex(mode, indexCount, format, (void*)(startIndex * formatSize), baseVertex);
    }

    public override unsafe void DrawIndexedInstanced(Topology primitiveType, uint indexCount, uint instanceCount, bool index32bit)
    {
        GLContext.GetInteger(GetPName.VertexArrayBinding, out int currentVAO);
        if (currentVAO == 0)
            throw new InvalidOperationException("DrawIndexedInstanced called with no VAO bound!");

        PrimitiveType mode = TopologyToGL(primitiveType);
        DrawElementsType format = index32bit ? DrawElementsType.UnsignedInt : DrawElementsType.UnsignedShort;
        GLContext.DrawElementsInstanced(mode, indexCount, format, null, instanceCount);
    }

    // ── Capability Accessors ─────────────────────────────────────

    public override int MaxTextureSize => (int)_capabilities.MaxTextureSize;
    public override int MaxCubeMapTextureSize => _maxCubeMapTextureSize;
    public override int MaxArrayTextureLayers => _maxArrayTextureLayers;
    public override int MaxFramebufferColorAttachments => (int)_capabilities.MaxColorAttachments;

    #endregion

    #region Legacy Resource Creation

    public override uint CreateLegacyFramebuffer(GraphicsFrameBuffer.Attachment[] attachments, uint width, uint height)
    {
        uint fbo = GLContext.GenFramebuffer();
        if (fbo == 0) throw new Exception("[FrameBuffer] Failed to generate new FrameBuffer.");

        GLContext.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);

        int colorAttachmentCount = 0;
        for (int i = 0; i < attachments.Length; i++)
        {
            // Extract GL texture handle from Graphite resource
            uint texHandle = attachments[i].Texture?.GraphiteTexture?.NativeHandle ?? 0;
            TextureTarget texTarget = (attachments[i].Texture?.GraphiteTexture is GLTexture gt)
                ? gt.Target : TextureTarget.Texture2D;

            if (!attachments[i].IsDepth)
            {
                GLContext.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                    FramebufferAttachment.ColorAttachment0 + colorAttachmentCount,
                    texTarget, texHandle, 0);
                colorAttachmentCount++;
            }
            else
            {
                GLContext.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                    FramebufferAttachment.DepthAttachment,
                    TextureTarget.Texture2D, texHandle, 0);
            }
        }

        if (colorAttachmentCount > 0)
        {
            GLEnum[] bufs = new GLEnum[colorAttachmentCount];
            for (int i = 0; i < colorAttachmentCount; i++)
                bufs[i] = GLEnum.ColorAttachment0 + i;
            GLContext.DrawBuffers((uint)colorAttachmentCount, bufs);
        }
        else
        {
            GLContext.DrawBuffer(GLEnum.None);
            GLContext.ReadBuffer(GLEnum.None);
        }

        if (GLContext.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new Exception("RenderTexture: FrameBuffer object creation failed.");

        GLContext.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return fbo;
    }

    public override void DeleteLegacyFramebuffer(uint handle)
    {
        if (handle != 0) GLContext.DeleteFramebuffer(handle);
    }

    public override uint CompileLegacyProgram(string fragmentSource, string vertexSource, string geometrySource)
    {
        uint program = GLContext.CreateProgram();

        if (!string.IsNullOrEmpty(fragmentSource))
            CompileAndAttachShader(program, ShaderType.FragmentShader, fragmentSource, "Fragment");

        if (!string.IsNullOrEmpty(vertexSource))
            CompileAndAttachShader(program, ShaderType.VertexShader, vertexSource, "Vertex");

        if (!string.IsNullOrEmpty(geometrySource))
            CompileAndAttachShader(program, ShaderType.GeometryShader, geometrySource, "Geometry");

        GLContext.LinkProgram(program);
        GLContext.GetProgramInfoLog(program, out string info);
        GLContext.GetProgram(program, ProgramPropertyARB.LinkStatus, out int statusCode);
        if (statusCode != 1)
        {
            GLContext.DeleteProgram(program);
            throw new InvalidOperationException("Failed to Link Shader Program.\n" + info + "\nStatus Code: " + statusCode);
        }

        GLContext.Flush();
        return program;
    }

    private void CompileAndAttachShader(uint program, ShaderType type, string source, string label)
    {
        uint shader = GLContext.CreateShader(type);
        GLContext.ShaderSource(shader, source);
        GLContext.CompileShader(shader);
        GLContext.GetShaderInfoLog(shader, out string info);
        GLContext.GetShader(shader, ShaderParameterName.CompileStatus, out int statusCode);
        if (statusCode != 1)
        {
            GLContext.DeleteShader(shader);
            GLContext.DeleteProgram(program);
            throw new InvalidOperationException($"Failed to Compile {label} Shader Source.\n{info}\nStatus Code: {statusCode}");
        }
        GLContext.AttachShader(program, shader);
        GLContext.DeleteShader(shader);
    }

    public override void UseLegacyProgram(uint handle) => GLContext.UseProgram(handle);

    public override void DeleteLegacyProgram(uint handle)
    {
        if (handle != 0) GLContext.DeleteProgram(handle);
    }

    public override unsafe uint CreateLegacyVertexArray(LegacyVertexFormat format, GraphicsBuffer vertices, GraphicsBuffer? indices,
        LegacyVertexFormat? instanceFormat = null, GraphicsBuffer? instanceBuffer = null)
    {
        uint vao = GLContext.GenVertexArray();
        if (vao == 0) throw new Exception("Failed to create VAO.");

        GLContext.BindVertexArray(vao);

        uint vertHandle = (vertices.GraphiteBuffer as GLBuffer)?.Handle ?? 0;
        GLContext.BindBuffer(BufferTargetARB.ArrayBuffer, vertHandle);
        BindVertexFormat(format);

        if (instanceFormat != null && instanceBuffer != null)
        {
            uint instHandle = (instanceBuffer.GraphiteBuffer as GLBuffer)?.Handle ?? 0;
            GLContext.BindBuffer(BufferTargetARB.ArrayBuffer, instHandle);
            BindVertexFormat(instanceFormat);
        }

        if (indices != null)
        {
            uint idxHandle = (indices.GraphiteBuffer as GLBuffer)?.Handle ?? 0;
            GLContext.BindBuffer(BufferTargetARB.ElementArrayBuffer, idxHandle);
        }

        GLContext.BindVertexArray(0);
        return vao;
    }

    private unsafe void BindVertexFormat(LegacyVertexFormat format)
    {
        for (int i = 0; i < format.Elements.Length; i++)
        {
            LegacyVertexFormat.Element element = format.Elements[i];
            uint index = element.Semantic;
            GLContext.EnableVertexAttribArray(index);
            int offset = element.Offset;

            if (element.Type == LegacyVertexFormat.VertexType.Float)
                GLContext.VertexAttribPointer(index, element.Count, (GLEnum)element.Type, element.Normalized, (uint)format.Size, (void*)offset);
            else
                GLContext.VertexAttribIPointer(index, element.Count, (GLEnum)element.Type, (uint)format.Size, (void*)offset);

            if (element.Divisor > 0)
                GLContext.VertexAttribDivisor(index, (uint)element.Divisor);
        }
    }

    public override void DeleteLegacyVertexArray(uint handle)
    {
        if (handle != 0) GLContext.DeleteVertexArray(handle);
    }

    public override void LegacySetTextureWrap(GraphicsTexture texture, int axis, TextureWrap wrap)
    {
        if (texture.GraphiteTexture is not GLTexture glTex) return;
        GLContext.BindTexture(glTex.Target, glTex.Handle);
        GLEnum wrapMode = wrap switch
        {
            TextureWrap.Repeat => GLEnum.Repeat,
            TextureWrap.ClampToEdge => GLEnum.ClampToEdge,
            TextureWrap.MirroredRepeat => GLEnum.MirroredRepeat,
            TextureWrap.ClampToBorder => GLEnum.ClampToBorder,
            _ => throw new ArgumentException("Invalid texture wrap mode", nameof(wrap)),
        };
        GLEnum param = axis switch
        {
            0 => GLEnum.TextureWrapS,
            1 => GLEnum.TextureWrapT,
            2 => GLEnum.TextureWrapR,
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
        GLContext.TexParameter(glTex.Target, param, (int)wrapMode);
    }

    public override void LegacySetTextureFilters(GraphicsTexture texture, TextureMin min, TextureMag mag)
    {
        if (texture.GraphiteTexture is not GLTexture glTex) return;
        GLContext.BindTexture(glTex.Target, glTex.Handle);
        GLEnum minFilter = min switch
        {
            TextureMin.Nearest => GLEnum.Nearest,
            TextureMin.Linear => GLEnum.Linear,
            TextureMin.NearestMipmapNearest => GLEnum.NearestMipmapNearest,
            TextureMin.LinearMipmapNearest => GLEnum.LinearMipmapNearest,
            TextureMin.NearestMipmapLinear => GLEnum.NearestMipmapLinear,
            TextureMin.LinearMipmapLinear => GLEnum.LinearMipmapLinear,
            _ => throw new ArgumentException("Invalid texture min filter", nameof(min)),
        };
        GLEnum magFilter = mag switch
        {
            TextureMag.Nearest => GLEnum.Nearest,
            TextureMag.Linear => GLEnum.Linear,
            _ => throw new ArgumentException("Invalid texture mag filter", nameof(mag)),
        };
        GLContext.TexParameter(glTex.Target, GLEnum.TextureMinFilter, (int)minFilter);
        GLContext.TexParameter(glTex.Target, GLEnum.TextureMagFilter, (int)magFilter);
    }

    public override void SetWireframeMode(bool enabled)
    {
        GLContext.PolygonMode(GLEnum.FrontAndBack, enabled ? GLEnum.Line : GLEnum.Fill);
    }

    #endregion

    #region GL Texture Format Mapping

    /// <summary>
    /// Maps a legacy <see cref="TextureImageFormat"/> to OpenGL pixel format enums.
    /// Moved from <c>GraphicsTexture</c> to keep GL types inside the Graphite layer.
    /// </summary>
    internal static void GetTextureFormatEnums(TextureImageFormat imageFormat, out InternalFormat pixelInternalFormat, out PixelType pixelType, out PixelFormat pixelFormat)
    {
        pixelType = imageFormat switch
        {
            TextureImageFormat.Color4b => PixelType.UnsignedByte,
            TextureImageFormat.Byte => PixelType.UnsignedByte,
            TextureImageFormat.Float => PixelType.Float,
            TextureImageFormat.Float2 => PixelType.Float,
            TextureImageFormat.Float3 => PixelType.Float,
            TextureImageFormat.Float4 => PixelType.Float,
            TextureImageFormat.Short => PixelType.Short,
            TextureImageFormat.Short2 => PixelType.Short,
            TextureImageFormat.Short3 => PixelType.Short,
            TextureImageFormat.Short4 => PixelType.Short,
            TextureImageFormat.Int => PixelType.Int,
            TextureImageFormat.Int2 => PixelType.Int,
            TextureImageFormat.Int3 => PixelType.Int,
            TextureImageFormat.Int4 => PixelType.Int,
            TextureImageFormat.UnsignedShort => PixelType.UnsignedShort,
            TextureImageFormat.UnsignedShort2 => PixelType.UnsignedShort,
            TextureImageFormat.UnsignedShort3 => PixelType.UnsignedShort,
            TextureImageFormat.UnsignedShort4 => PixelType.UnsignedShort,
            TextureImageFormat.UnsignedInt => PixelType.UnsignedInt,
            TextureImageFormat.UnsignedInt2 => PixelType.UnsignedInt,
            TextureImageFormat.UnsignedInt3 => PixelType.UnsignedInt,
            TextureImageFormat.UnsignedInt4 => PixelType.UnsignedInt,
            TextureImageFormat.Depth16f => PixelType.Float,
            TextureImageFormat.Depth24f => PixelType.Float,
            TextureImageFormat.Depth32f => PixelType.Float,
            TextureImageFormat.Depth24Stencil8 => (PixelType)GLEnum.UnsignedInt248,
            _ => throw new ArgumentException("Invalid TextureImageFormat", nameof(imageFormat)),
        };

        pixelInternalFormat = imageFormat switch
        {
            TextureImageFormat.Color4b => InternalFormat.Rgba8,
            TextureImageFormat.Byte => InternalFormat.R8ui,
            TextureImageFormat.Float => InternalFormat.R32f,
            TextureImageFormat.Float2 => InternalFormat.RG32f,
            TextureImageFormat.Float3 => InternalFormat.Rgb32f,
            TextureImageFormat.Float4 => InternalFormat.Rgba32f,
            TextureImageFormat.Short => InternalFormat.R16f,
            TextureImageFormat.Short2 => InternalFormat.RG16f,
            TextureImageFormat.Short3 => InternalFormat.Rgb16f,
            TextureImageFormat.Short4 => InternalFormat.Rgba16f,
            TextureImageFormat.Int => InternalFormat.R32i,
            TextureImageFormat.Int2 => InternalFormat.RG32i,
            TextureImageFormat.Int3 => InternalFormat.Rgb32i,
            TextureImageFormat.Int4 => InternalFormat.Rgba32i,
            TextureImageFormat.UnsignedShort => InternalFormat.R16f,
            TextureImageFormat.UnsignedShort2 => InternalFormat.RG16f,
            TextureImageFormat.UnsignedShort3 => InternalFormat.Rgb16f,
            TextureImageFormat.UnsignedShort4 => InternalFormat.Rgba16f,
            TextureImageFormat.UnsignedInt => InternalFormat.R32ui,
            TextureImageFormat.UnsignedInt2 => InternalFormat.RG32ui,
            TextureImageFormat.UnsignedInt3 => InternalFormat.Rgb32ui,
            TextureImageFormat.UnsignedInt4 => InternalFormat.Rgba32ui,
            TextureImageFormat.Depth16f => InternalFormat.DepthComponent16,
            TextureImageFormat.Depth24f => InternalFormat.DepthComponent24,
            TextureImageFormat.Depth32f => InternalFormat.DepthComponent32f,
            TextureImageFormat.Depth24Stencil8 => InternalFormat.Depth24Stencil8,
            _ => throw new ArgumentException("Invalid TextureImageFormat", nameof(imageFormat)),
        };

        pixelFormat = imageFormat switch
        {
            TextureImageFormat.Color4b => PixelFormat.Rgba,
            TextureImageFormat.Byte => PixelFormat.RedInteger,
            TextureImageFormat.Short => PixelFormat.Red,
            TextureImageFormat.Short2 => PixelFormat.RG,
            TextureImageFormat.Short3 => PixelFormat.Rgb,
            TextureImageFormat.Short4 => PixelFormat.Rgba,
            TextureImageFormat.Float => PixelFormat.Red,
            TextureImageFormat.Float2 => PixelFormat.RG,
            TextureImageFormat.Float3 => PixelFormat.Rgb,
            TextureImageFormat.Float4 => PixelFormat.Rgba,
            TextureImageFormat.Int => PixelFormat.RgbaInteger,
            TextureImageFormat.Int2 => PixelFormat.RGInteger,
            TextureImageFormat.Int3 => PixelFormat.RgbInteger,
            TextureImageFormat.Int4 => PixelFormat.RgbaInteger,
            TextureImageFormat.UnsignedShort => PixelFormat.Red,
            TextureImageFormat.UnsignedShort2 => PixelFormat.RG,
            TextureImageFormat.UnsignedShort3 => PixelFormat.Rgb,
            TextureImageFormat.UnsignedShort4 => PixelFormat.Rgba,
            TextureImageFormat.UnsignedInt => PixelFormat.RedInteger,
            TextureImageFormat.UnsignedInt2 => PixelFormat.RGInteger,
            TextureImageFormat.UnsignedInt3 => PixelFormat.RgbInteger,
            TextureImageFormat.UnsignedInt4 => PixelFormat.RgbaInteger,
            TextureImageFormat.Depth16f => PixelFormat.DepthComponent,
            TextureImageFormat.Depth24f => PixelFormat.DepthComponent,
            TextureImageFormat.Depth32f => PixelFormat.DepthComponent,
            TextureImageFormat.Depth24Stencil8 => PixelFormat.DepthStencil,
            _ => throw new ArgumentException("Invalid TextureImageFormat", nameof(imageFormat)),
        };
    }

    #endregion

    #region GL Enum Conversions

    private static PrimitiveType TopologyToGL(Topology t) => t switch
    {
        Topology.Points => PrimitiveType.Points,
        Topology.Lines => PrimitiveType.Lines,
        Topology.LineLoop => PrimitiveType.LineLoop,
        Topology.LineStrip => PrimitiveType.LineStrip,
        Topology.Triangles => PrimitiveType.Triangles,
        Topology.TriangleStrip => PrimitiveType.TriangleStrip,
        Topology.TriangleFan => PrimitiveType.TriangleFan,
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    private static DepthFunction DepthModeToGL(RasterizerState.DepthMode m) => m switch
    {
        RasterizerState.DepthMode.Never => DepthFunction.Never,
        RasterizerState.DepthMode.Less => DepthFunction.Less,
        RasterizerState.DepthMode.Equal => DepthFunction.Equal,
        RasterizerState.DepthMode.Lequal => DepthFunction.Lequal,
        RasterizerState.DepthMode.Greater => DepthFunction.Greater,
        RasterizerState.DepthMode.Notequal => DepthFunction.Notequal,
        RasterizerState.DepthMode.Gequal => DepthFunction.Gequal,
        RasterizerState.DepthMode.Always => DepthFunction.Always,
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    private static BlendingFactor BlendingToGL(RasterizerState.Blending b) => b switch
    {
        RasterizerState.Blending.Zero => BlendingFactor.Zero,
        RasterizerState.Blending.One => BlendingFactor.One,
        RasterizerState.Blending.SrcColor => BlendingFactor.SrcColor,
        RasterizerState.Blending.OneMinusSrcColor => BlendingFactor.OneMinusSrcColor,
        RasterizerState.Blending.DstColor => BlendingFactor.DstColor,
        RasterizerState.Blending.OneMinusDstColor => BlendingFactor.OneMinusDstColor,
        RasterizerState.Blending.SrcAlpha => BlendingFactor.SrcAlpha,
        RasterizerState.Blending.OneMinusSrcAlpha => BlendingFactor.OneMinusSrcAlpha,
        RasterizerState.Blending.DstAlpha => BlendingFactor.DstAlpha,
        RasterizerState.Blending.OneMinusDstAlpha => BlendingFactor.OneMinusDstAlpha,
        RasterizerState.Blending.ConstantColor => BlendingFactor.ConstantColor,
        RasterizerState.Blending.OneMinusConstantColor => BlendingFactor.OneMinusConstantColor,
        RasterizerState.Blending.ConstantAlpha => BlendingFactor.ConstantAlpha,
        RasterizerState.Blending.OneMinusConstantAlpha => BlendingFactor.OneMinusConstantAlpha,
        RasterizerState.Blending.SrcAlphaSaturate => BlendingFactor.SrcAlphaSaturate,
        _ => throw new ArgumentOutOfRangeException(nameof(b)),
    };

    private static BlendEquationModeEXT BlendModeToGL(RasterizerState.BlendMode m) => m switch
    {
        RasterizerState.BlendMode.Add => BlendEquationModeEXT.FuncAdd,
        RasterizerState.BlendMode.Subtract => BlendEquationModeEXT.FuncSubtract,
        RasterizerState.BlendMode.ReverseSubtract => BlendEquationModeEXT.FuncReverseSubtract,
        RasterizerState.BlendMode.Min => BlendEquationModeEXT.Min,
        RasterizerState.BlendMode.Max => BlendEquationModeEXT.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    private static TriangleFace CullFaceToGL(RasterizerState.PolyFace f) => f switch
    {
        RasterizerState.PolyFace.Front => TriangleFace.Front,
        RasterizerState.PolyFace.Back => TriangleFace.Back,
        RasterizerState.PolyFace.FrontAndBack => TriangleFace.FrontAndBack,
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    private static FrontFaceDirection WindingToGL(RasterizerState.WindingOrder w) => w switch
    {
        RasterizerState.WindingOrder.CW => FrontFaceDirection.CW,
        RasterizerState.WindingOrder.CCW => FrontFaceDirection.Ccw,
        _ => throw new ArgumentOutOfRangeException(nameof(w)),
    };

    /// <summary>
    /// Combines a program ID and string hash into a unique ulong key for caching.
    /// </summary>
    private static ulong CombineKey(int programId, string name)
    {
        uint nameHash = unchecked((uint)name.GetHashCode());
        return ((ulong)programId << 32) | nameHash;
    }

    #endregion
}

/// <summary>
/// Special texture type representing the OpenGL default framebuffer.
/// </summary>
internal class GLSwapchainTexture : Texture
{
    public static readonly GLSwapchainTexture Instance = new();

    private GLSwapchainTexture()
    {
        Dimension = TextureDimension.Texture2D;
        Width = 0; // Will be set dynamically
        Height = 0;
        Depth = 1;
        MipLevels = 1;
        ArrayLayers = 1;
        Format = TextureFormat.RGBA8Unorm;
        Usage = TextureUsage.RenderTarget;
        SampleCount = SampleCount.Count1;
    }

    protected override void DisposeResources()
    {
        // Singleton, never disposed
    }
}
