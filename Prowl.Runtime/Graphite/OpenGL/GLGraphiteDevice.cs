// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Vector;

using Silk.NET.Core.Native;
using Silk.NET.OpenGL;

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
    public Dictionary<ulong, uint> CachedBlockLocations { get; } = [];
    public Dictionary<ulong, int> CachedUniformLocations { get; } = [];
    public Dictionary<ulong, int> CachedAttribLocations { get; } = [];

    public override string BackendName => $"OpenGL {GL.GetStringS(StringName.Version)}";
    public override GraphicsBackendType BackendType => GraphicsBackendType.OpenGL;
    public override DeviceCapabilities Capabilities => _capabilities;
    public override uint SwapchainWidth => _swapchainWidth;
    public override uint SwapchainHeight => _swapchainHeight;

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
        MaxCubeMapTextureSize = maxCubeMap;
        MaxArrayTextureLayers = maxArrayLayers;

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

    public void Viewport(int x, int y, uint width, uint height)
        => GLContext.Viewport(x, y, width, height);

    public void Clear(float r, float g, float b, float a, ClearFlags v)
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

    public void SetState(RasterizerState state, bool force = false)
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

    public RasterizerState GetState() => new()
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

    public void BindBuffer(GraphicsBuffer buffer)
        => GLContext.BindBuffer(buffer.Target, buffer.Handle);

    public uint GetBlockIndex(GraphicsProgram program, string blockName)
    {
        ulong key = CombineKey(program.ID, blockName);
        if (CachedBlockLocations.TryGetValue(key, out uint loc))
            return loc;

        BindProgram(program);
        uint newLoc = GLContext.GetUniformBlockIndex(program.Handle, blockName);
        CachedBlockLocations[key] = newLoc;
        return newLoc;
    }

    public void BindUniformBuffer(GraphicsProgram program, string blockName, GraphicsBuffer buffer, uint bindingPoint = 0)
    {
        uint blockIndex = GetBlockIndex(program, blockName);
        if (blockIndex == 0xFFFFFFFF) return; // GL_INVALID_INDEX

        BindProgram(program);
        GLContext.UniformBlockBinding(program.Handle, blockIndex, bindingPoint);
        GLContext.BindBufferBase(BufferTargetARB.UniformBuffer, bindingPoint, buffer!.Handle);
    }

    // ── Vertex Arrays ─────────────────────────────────────────────

    public void BindVertexArray(GraphicsVertexArray? vertexArrayObject)
    {
        uint handle = vertexArrayObject is GraphicsVertexArray vao ? vao.Handle : 0u;
        GLContext.BindVertexArray(handle);
    }

    // ── Frame Buffers ─────────────────────────────────────────────

    public void UnbindFramebuffer()
    {
        GLContext.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _currentFramebuffer = null;
        _currentReadFramebuffer = null;
        _currentDrawFramebuffer = null;
    }

    public void BindFramebuffer(GraphicsFrameBuffer frameBuffer, FBOTarget target = FBOTarget.Framebuffer)
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

    public GraphicsFrameBuffer? GetCurrentFramebuffer(FBOTarget target = FBOTarget.Framebuffer)
    {
        return target switch
        {
            FBOTarget.Read => _currentReadFramebuffer,
            FBOTarget.Draw => _currentDrawFramebuffer,
            _ => _currentFramebuffer,
        };
    }

    public void BlitFramebuffer(int srcX, int srcY, int srcWidth, int srcHeight,
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

    public unsafe T ReadPixel<T>(int attachment, int x, int y, TextureImageFormat format) where T : unmanaged
    {
        GLContext.ReadBuffer((ReadBufferMode)((int)ReadBufferMode.ColorAttachment0 + attachment));
        GraphicsTexture.GetTextureFormatEnums(format, out _, out PixelType pixelType, out PixelFormat pixelFormat);
        return GLContext.ReadPixels<T>(x, y, 1, 1, pixelFormat, pixelType);
    }

    // ── Shaders ───────────────────────────────────────────────────

    public void BindProgram(GraphicsProgram program)
        => program!.Use();

    public int GetUniformLocation(GraphicsProgram program, string name)
    {
        ulong key = CombineKey(program.ID, name);
        if (CachedUniformLocations.TryGetValue(key, out int loc))
            return loc;

        BindProgram(program);
        int newLoc = GLContext.GetUniformLocation(program.Handle, name);
        CachedUniformLocations[key] = newLoc;
        return newLoc;
    }

    public int GetAttribLocation(GraphicsProgram program, string name)
    {
        ulong key = CombineKey(program.ID, name);
        if (CachedAttribLocations.TryGetValue(key, out int loc))
            return loc;

        BindProgram(program);
        int newLoc = GLContext.GetAttribLocation(program.Handle, name);
        CachedAttribLocations[key] = newLoc;
        return newLoc;
    }

    public void SetUniformF(GraphicsProgram program, string name, float value)
    {
        int loc = GetUniformLocation(program, name);
        if (loc == -1) return;
        BindProgram(program);
        GLContext.Uniform1(loc, value);
    }

    public void SetUniformI(GraphicsProgram program, string name, int value)
    {
        int loc = GetUniformLocation(program, name);
        if (loc == -1) return;
        BindProgram(program);
        GLContext.Uniform1(loc, value);
    }

    public void SetUniformV2(GraphicsProgram program, string name, Float2 value)
    {
        int loc = GetUniformLocation(program, name);
        if (loc == -1) return;
        BindProgram(program);
        GLContext.Uniform2(loc, value);
    }

    public void SetUniformV3(GraphicsProgram program, string name, Float3 value)
    {
        int loc = GetUniformLocation(program, name);
        if (loc == -1) return;
        BindProgram(program);
        GLContext.Uniform3(loc, value);
    }

    public void SetUniformV4(GraphicsProgram program, string name, Float4 value)
    {
        int loc = GetUniformLocation(program, name);
        if (loc == -1) return;
        BindProgram(program);
        GLContext.Uniform4(loc, value);
    }

    public unsafe void SetUniformMatrix(GraphicsProgram program, string name, uint count, bool transpose, in float matrix)
    {
        int loc = GetUniformLocation(program, name);
        if (loc == -1) return;
        BindProgram(program);
        GLContext.UniformMatrix4(loc, count, transpose, in matrix);
    }

    public void SetUniformMatrix(GraphicsProgram program, string name, bool transpose, Float4x4 matrix)
    {
        Float4x4 fMat = matrix;
        SetUniformMatrix(program, name, 1, transpose, in fMat.c0.X);
    }

    public void SetUniformMatrix(GraphicsProgram program, string name, bool transpose, in float matrix)
        => SetUniformMatrix(program, name, 1, transpose, in matrix);

    public void SetUniformTexture(GraphicsProgram program, string name, int slot, GraphicsTexture texture)
    {
        int loc = GetUniformLocation(program, name);
        if (loc == -1) return;
        BindProgram(program);
        GLContext.ActiveTexture((TextureUnit)((uint)TextureUnit.Texture0 + slot));
        texture.Bind();
        GLContext.Uniform1(loc, slot);
    }

    // ── Drawing ───────────────────────────────────────────────────

    public void Draw(Topology primitiveType, uint count)
        => Draw(primitiveType, 0, count);

    public void Draw(Topology primitiveType, int offset, uint count)
    {
        PrimitiveType mode = TopologyToGL(primitiveType);
        GLContext.DrawArrays(mode, offset, count);
    }

    public unsafe void DrawIndexed(Topology primitiveType, uint indexCount, bool index32bit, void* value)
    {
        PrimitiveType mode = TopologyToGL(primitiveType);
        GLContext.DrawElements(mode, indexCount, index32bit ? DrawElementsType.UnsignedInt : DrawElementsType.UnsignedShort, value);
    }

    public unsafe void DrawIndexed(Topology primitiveType, uint indexCount, int startIndex, int baseVertex, bool index32bit)
    {
        PrimitiveType mode = TopologyToGL(primitiveType);
        DrawElementsType format = index32bit ? DrawElementsType.UnsignedInt : DrawElementsType.UnsignedShort;
        int formatSize = index32bit ? sizeof(uint) : sizeof(ushort);
        GLContext.DrawElementsBaseVertex(mode, indexCount, format, (void*)(startIndex * formatSize), baseVertex);
    }

    public unsafe void DrawIndexedInstanced(Topology primitiveType, uint indexCount, uint instanceCount, bool index32bit)
    {
        GLContext.GetInteger(GetPName.VertexArrayBinding, out int currentVAO);
        if (currentVAO == 0)
            throw new InvalidOperationException("DrawIndexedInstanced called with no VAO bound!");

        PrimitiveType mode = TopologyToGL(primitiveType);
        DrawElementsType format = index32bit ? DrawElementsType.UnsignedInt : DrawElementsType.UnsignedShort;
        GLContext.DrawElementsInstanced(mode, indexCount, format, null, instanceCount);
    }

    // ── Capability Accessors ─────────────────────────────────────

    public int MaxTextureSize => (int)_capabilities.MaxTextureSize;
    public int MaxCubeMapTextureSize { get; private set; }
    public int MaxArrayTextureLayers { get; private set; }
    public int MaxFramebufferColorAttachments => (int)_capabilities.MaxColorAttachments;

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
