// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Vector;

using LegacyVertexFormat = Prowl.Runtime.VertexFormat;

namespace Prowl.Runtime.Graphite;

/// <summary>
/// Options for creating a graphics device.
/// </summary>
public struct GraphiteDeviceOptions
{
    /// <summary>Enable debug/validation layers.</summary>
    public bool EnableDebugLayer;

    public GraphiteDeviceOptions()
    {
        EnableDebugLayer = false;
    }

    public static GraphiteDeviceOptions Default => new();

    public static GraphiteDeviceOptions Debug => new()
    {
        EnableDebugLayer = true,
    };
}

/// <summary>
/// Describes the capabilities of a graphics device.
/// </summary>
public struct DeviceCapabilities
{
    /// <summary>Device/GPU name.</summary>
    public string DeviceName;

    /// <summary>Vendor name.</summary>
    public string VendorName;

    /// <summary>Whether compute shaders are supported.</summary>
    public bool SupportsCompute;

    /// <summary>Whether geometry shaders are supported.</summary>
    public bool SupportsGeometryShaders;

    /// <summary>Whether tessellation is supported.</summary>
    public bool SupportsTessellation;

    /// <summary>Whether multi-draw indirect is supported.</summary>
    public bool SupportsMultiDrawIndirect;

    /// <summary>Whether bindless resources are supported.</summary>
    public bool SupportsBindless;

    /// <summary>Maximum texture size in any dimension.</summary>
    public uint MaxTextureSize;

    /// <summary>Maximum uniform buffer size.</summary>
    public uint MaxUniformBufferSize;

    /// <summary>Maximum storage buffer size.</summary>
    public uint MaxStorageBufferSize;

    /// <summary>Maximum number of bind groups per pipeline.</summary>
    public uint MaxBindGroups;

    /// <summary>Maximum number of samplers per shader stage.</summary>
    public uint MaxSamplersPerStage;

    /// <summary>Maximum number of textures per shader stage.</summary>
    public uint MaxTexturesPerStage;

    /// <summary>Maximum vertex attributes.</summary>
    public uint MaxVertexAttributes;

    /// <summary>Maximum vertex buffer bindings.</summary>
    public uint MaxVertexBuffers;

    /// <summary>Maximum color attachments for a render pass.</summary>
    public uint MaxColorAttachments;

    /// <summary>Maximum compute workgroup size (X).</summary>
    public uint MaxComputeWorkgroupSizeX;

    /// <summary>Maximum compute workgroup size (Y).</summary>
    public uint MaxComputeWorkgroupSizeY;

    /// <summary>Maximum compute workgroup size (Z).</summary>
    public uint MaxComputeWorkgroupSizeZ;

    /// <summary>Maximum compute invocations per workgroup.</summary>
    public uint MaxComputeInvocationsPerWorkgroup;
}

/// <summary>
/// The main graphics device for creating resources and submitting commands.
/// </summary>
public abstract class GraphiteDevice : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Set to <c>true</c> when the GPU reports an unrecoverable error
    /// (e.g. Vulkan <c>ErrorDeviceLost</c>). Rendering code should check
    /// this flag and skip GPU work to prevent cascading failures.
    /// </summary>
    public bool IsDeviceLost { get; protected set; }

    #region Properties

    /// <summary>The backend name (e.g., "OpenGL 4.5", "Vulkan 1.3").</summary>
    public abstract string BackendName { get; }

    /// <summary>The backend type.</summary>
    public abstract GraphicsBackendType BackendType { get; }

    /// <summary>Device capabilities.</summary>
    public abstract DeviceCapabilities Capabilities { get; }

    /// <summary>Current swapchain width.</summary>
    public abstract uint SwapchainWidth { get; }

    /// <summary>Current swapchain height.</summary>
    public abstract uint SwapchainHeight { get; }

    /// <summary>
    /// The number of frames that may be in-flight simultaneously on the GPU.
    /// Render resources (e.g. pooled render textures) must not be reused until
    /// this many frames have elapsed to avoid GPU data races.
    /// Defaults to 1 (OpenGL); Vulkan overrides to match its fence count.
    /// </summary>
    public virtual int FramesInFlight => 1;

    /// <summary>
    /// Index of the current frame slot (0 .. <see cref="FramesInFlight"/>-1).
    /// Used by renderers that need per-frame-in-flight resource sets.
    /// Defaults to 0 (OpenGL has no multi-frame overlap).
    /// </summary>
    public virtual int CurrentFrameIndex => 0;

    #endregion

    #region Initialization

    /// <summary>
    /// Initializes the graphics device.
    /// </summary>
    public abstract void Initialize(GraphiteDeviceOptions options);

    #endregion

    #region Resource Creation

    /// <summary>
    /// Creates a GPU buffer.
    /// </summary>
    public abstract Buffer CreateBuffer(in BufferDescriptor descriptor);

    /// <summary>
    /// Creates a GPU buffer with initial data.
    /// </summary>
    public Buffer CreateBuffer<T>(BufferUsage usage, ReadOnlySpan<T> data, MemoryAccess memoryAccess = MemoryAccess.GpuOnly, string? debugName = null) where T : unmanaged
    {
        var descriptor = new BufferDescriptor
        {
            SizeInBytes = (uint)(data.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>()),
            Usage = usage | BufferUsage.CopyDestination,
            MemoryAccess = memoryAccess,
            DebugName = debugName,
            InitialData = System.Runtime.InteropServices.MemoryMarshal.AsBytes(data).ToArray(),
        };
        return CreateBuffer(in descriptor);
    }

    /// <summary>
    /// Creates a GPU texture.
    /// </summary>
    public abstract Texture CreateTexture(in TextureDescriptor descriptor);

    /// <summary>
    /// Creates a sampler.
    /// </summary>
    public abstract Sampler CreateSampler(in SamplerDescriptor descriptor);

    /// <summary>
    /// Creates a shader module from source.
    /// </summary>
    public abstract ShaderModule CreateShaderModule(in ShaderModuleDescriptor descriptor);

    /// <summary>
    /// Creates a graphics pipeline state.
    /// </summary>
    public abstract PipelineState CreatePipelineState(in PipelineStateDescriptor descriptor);

    /// <summary>
    /// Creates a compute pipeline state.
    /// </summary>
    public abstract ComputePipelineState CreateComputePipelineState(in ComputePipelineStateDescriptor descriptor);

    /// <summary>
    /// Creates a bind group layout.
    /// </summary>
    public abstract BindGroupLayout CreateBindGroupLayout(in BindGroupLayoutDescriptor descriptor);

    /// <summary>
    /// Creates a bind group.
    /// </summary>
    public abstract BindGroup CreateBindGroup(in BindGroupDescriptor descriptor);

    /// <summary>
    /// Creates a fence for synchronization.
    /// </summary>
    public abstract Fence CreateFence(bool signaled = false);

    #endregion

    #region Command List Management

    /// <summary>
    /// Creates a command list for recording commands.
    /// </summary>
    public abstract CommandList CreateCommandList();

    /// <summary>
    /// Submits a command list for execution.
    /// </summary>
    public abstract void SubmitCommands(CommandList commandList);

    /// <summary>
    /// Submits multiple command lists for execution.
    /// </summary>
    public abstract void SubmitCommands(ReadOnlySpan<CommandList> commandLists);

    /// <summary>
    /// Submits a command list and signals a fence when complete.
    /// </summary>
    public abstract void SubmitCommands(CommandList commandList, Fence fence);

    #endregion

    #region Synchronization

    /// <summary>
    /// Waits for a fence to be signaled.
    /// </summary>
    public abstract void WaitForFence(Fence fence);

    /// <summary>
    /// Waits for the GPU to be completely idle.
    /// </summary>
    public abstract void WaitForIdle();

    #endregion

    #region Frame Management

    /// <summary>
    /// Resizes the swapchain.
    /// </summary>
    public abstract void ResizeSwapchain(uint width, uint height);

    /// <summary>
    /// Retrieves the contents of the backend's pipeline cache as a byte array
    /// suitable for persisting to disk. Returns <c>null</c> if the backend
    /// does not support pipeline cache serialization (e.g. OpenGL).
    /// </summary>
    public virtual byte[]? GetPipelineCacheData() => null;

    /// <summary>
    /// Seeds the backend's pipeline cache with previously saved data.
    /// Call after <see cref="Initialize"/> and before first frame rendering
    /// for maximum benefit. No-op on backends that don't support pipeline caches.
    /// </summary>
    public virtual void LoadPipelineCacheData(ReadOnlySpan<byte> data) { }

    #endregion

    #region Resource Updates

    /// <summary>
    /// Allocates a sub-region from a per-frame transient uniform buffer and copies
    /// data into it. Eliminates per-draw buffer creation overhead on backends that
    /// support it (Vulkan). The returned buffer and offset are valid for the
    /// current frame only.
    /// <para>Default implementation creates a regular CpuToGpu buffer.</para>
    /// </summary>
    public virtual (Buffer Buffer, uint Offset) AllocateTransientUniform(ReadOnlySpan<byte> data)
    {
        var desc = new BufferDescriptor
        {
            SizeInBytes = (uint)data.Length,
            Usage = BufferUsage.Uniform,
            MemoryAccess = MemoryAccess.CpuToGpu,
            InitialData = data.ToArray(),
        };
        return (CreateBuffer(in desc), 0);
    }

    /// <summary>
    /// Updates buffer data from the CPU. Blocks until complete.
    /// </summary>
    public abstract void UpdateBuffer<T>(Buffer buffer, uint offsetInBytes, ReadOnlySpan<T> data) where T : unmanaged;

    /// <summary>
    /// Updates texture data from the CPU. Blocks until complete.
    /// </summary>
    public abstract void UpdateTexture(Texture texture, in TextureUpdateDescriptor descriptor, ReadOnlySpan<byte> data);

    /// <summary>
    /// Reads texture data back from the GPU into a CPU buffer. Blocks until complete.
    /// </summary>
    /// <param name="texture">The texture to read from.</param>
    /// <param name="mipLevel">The mip level to read.</param>
    /// <param name="arrayLayer">The array layer to read.</param>
    /// <param name="destination">A span that receives the pixel data. Must be large enough to hold the entire mip level.</param>
    public abstract void ReadbackTexture(Texture texture, uint mipLevel, uint arrayLayer, Span<byte> destination);

    /// <summary>
    /// Generates mipmaps for a texture.
    /// </summary>
    public abstract void GenerateMipmaps(Texture texture);

    #endregion

    #region Swapchain

    /// <summary>
    /// Gets the current swapchain texture for rendering.
    /// </summary>
    public abstract Texture GetSwapchainTexture();

    /// <summary>
    /// Begins a new frame by acquiring the next swapchain image.
    /// Must be called before any rendering commands for the frame.
    /// Returns false if the swapchain is out of date and needs resizing.
    /// </summary>
    public virtual bool BeginFrame() => true;

    /// <summary>
    /// Presents the current frame to the screen.
    /// Must be called after all rendering commands for the frame have been submitted.
    /// Returns false if the swapchain is out of date and needs resizing.
    /// </summary>
    public virtual bool Present() => true;

    /// <summary>
    /// Begins batching upload operations (buffer/texture data transfers) into a
    /// single command submission. Subsequent uploads will be recorded but not
    /// submitted until <see cref="FlushUploadBatch"/> is called, dramatically
    /// reducing per-upload GPU stalls. No-op on backends that don't benefit
    /// from explicit batching (e.g. OpenGL).
    /// </summary>
    public virtual void BeginUploadBatch() { }

    /// <summary>
    /// Submits all uploads batched since the last <see cref="BeginUploadBatch"/>
    /// call and waits for completion. No-op when no batch is active.
    /// </summary>
    public virtual void FlushUploadBatch() { }

    /// <summary>
    /// Number of upload operations staged during the current (or most recent) upload batch.
    /// Returns 0 on backends that do not track uploads.
    /// </summary>
    public virtual int BatchUploadCount => 0;

    /// <summary>
    /// Total bytes staged for upload during the current (or most recent) upload batch.
    /// Returns 0 on backends that do not track uploads.
    /// </summary>
    public virtual long BatchUploadBytes => 0;

    #endregion

    #region GPU Profiling

    /// <summary>
    /// Begins a named GPU timer query. Writes a timestamp into the active command buffer.
    /// Must be paired with a matching <see cref="EndGpuTimerQuery"/> call using the same name.
    /// No-op on backends that do not support GPU timestamp queries.
    /// </summary>
    /// <param name="cmd">The command list currently recording GPU commands.</param>
    /// <param name="name">A unique name identifying this timing section (e.g. "GBuffer", "Lighting").</param>
    public virtual void BeginGpuTimerQuery(CommandList cmd, string name) { }

    /// <summary>
    /// Ends a named GPU timer query. Writes a second timestamp to compute the elapsed duration.
    /// No-op on backends that do not support GPU timestamp queries.
    /// </summary>
    /// <param name="cmd">The command list currently recording GPU commands.</param>
    /// <param name="name">The name used in the matching <see cref="BeginGpuTimerQuery"/> call.</param>
    public virtual void EndGpuTimerQuery(CommandList cmd, string name) { }

    /// <summary>
    /// Returns the GPU timing results from the most recently completed frame.
    /// Results are read back after the GPU fence is signaled, so they are 1–2 frames behind.
    /// Returns an empty array on backends that do not support GPU timestamp queries.
    /// </summary>
    public virtual GpuTimingResult[] GetGpuTimingResults() => [];

    #endregion

    #region Legacy Immediate-Mode API

    // Virtual methods for backward compatibility during the migration from
    // direct GL calls to the Graphite abstraction.  The OpenGL backend
    // overrides these with real GL implementations.  Non-GL backends
    // (Vulkan) inherit the no-op defaults.

    /// <summary>
    /// Whether this backend needs an explicit blit to the swapchain image.
    /// OpenGL renders to the default framebuffer directly; Vulkan needs
    /// an explicit copy/blit from the off-screen render target.
    /// </summary>
    public virtual bool NeedsExplicitSwapchainBlit => false;

    /// <summary>
    /// Returns the native graphics context (e.g. <c>Silk.NET.OpenGL.GL</c>) if available, or <c>null</c>.
    /// Used by subsystems that need raw API access (e.g. Dear ImGui).
    /// </summary>
    public virtual object? NativeContext => null;

    // ── Viewport &amp; Clear ──────────────────────────────────────────

    public virtual void Viewport(int x, int y, uint width, uint height) { }

    public virtual void Clear(float r, float g, float b, float a, ClearFlags v) { }

    // ── Rasterizer State ──────────────────────────────────────────

    public virtual void SetState(RasterizerState state, bool force = false) { }

    public virtual RasterizerState GetState() => new RasterizerState();

    // ── Buffers ───────────────────────────────────────────────────

    public virtual void BindBuffer(GraphicsBuffer buffer) { }

    public virtual uint GetBlockIndex(GraphicsProgram program, string blockName) => 0xFFFFFFFF;

    public virtual void BindUniformBuffer(GraphicsProgram program, string blockName, GraphicsBuffer buffer, uint bindingPoint = 0) { }

    public virtual void BindUniformBuffer(GraphicsProgram program, string blockName, Buffer graphiteBuffer, uint bindingPoint = 0) { }

    // ── Vertex Arrays ─────────────────────────────────────────────

    public virtual void BindVertexArray(GraphicsVertexArray? vertexArrayObject) { }

    // ── Frame Buffers ─────────────────────────────────────────────

    public virtual void UnbindFramebuffer() { }

    public virtual void BindFramebuffer(GraphicsFrameBuffer frameBuffer, FBOTarget target = FBOTarget.Framebuffer) { }

    public virtual GraphicsFrameBuffer? GetCurrentFramebuffer(FBOTarget target = FBOTarget.Framebuffer) => null;

    public virtual void BlitFramebuffer(int srcX, int srcY, int srcWidth, int srcHeight,
        int destX, int destY, int destWidth, int destHeight, ClearFlags mask, BlitFilter filter) { }

    public virtual unsafe T ReadPixel<T>(int attachment, int x, int y, TextureImageFormat format) where T : unmanaged => default;

    // ── Shaders ───────────────────────────────────────────────────

    public virtual void BindProgram(GraphicsProgram program) { }

    public virtual int GetUniformLocation(GraphicsProgram program, string name) => -1;

    public virtual int GetAttribLocation(GraphicsProgram program, string name) => -1;

    public virtual void SetUniformF(GraphicsProgram program, string name, float value) { }

    public virtual void SetUniformI(GraphicsProgram program, string name, int value) { }

    public virtual void SetUniformV2(GraphicsProgram program, string name, Float2 value) { }

    public virtual void SetUniformV3(GraphicsProgram program, string name, Float3 value) { }

    public virtual void SetUniformV4(GraphicsProgram program, string name, Float4 value) { }

    public virtual unsafe void SetUniformMatrix(GraphicsProgram program, string name, uint count, bool transpose, in float matrix) { }

    public virtual void SetUniformMatrix(GraphicsProgram program, string name, bool transpose, Float4x4 matrix) { }

    public virtual void SetUniformMatrix(GraphicsProgram program, string name, bool transpose, in float matrix) { }

    public virtual void SetUniformTexture(GraphicsProgram program, string name, int slot, GraphicsTexture texture) { }

    // ── Drawing ───────────────────────────────────────────────────

    public virtual void Draw(Topology primitiveType, uint count) { }

    public virtual void Draw(Topology primitiveType, int offset, uint count) { }

    public virtual unsafe void DrawIndexed(Topology primitiveType, uint indexCount, bool index32bit, void* value) { }

    public virtual unsafe void DrawIndexed(Topology primitiveType, uint indexCount, int startIndex, int baseVertex, bool index32bit) { }

    public virtual unsafe void DrawIndexedInstanced(Topology primitiveType, uint indexCount, uint instanceCount, bool index32bit) { }

    // ── Capability Accessors ─────────────────────────────────────

    public virtual int MaxTextureSize => (int)(Capabilities.MaxTextureSize);

    public virtual int MaxCubeMapTextureSize => (int)(Capabilities.MaxTextureSize);

    public virtual int MaxArrayTextureLayers => 256;

    public virtual int MaxFramebufferColorAttachments => (int)(Capabilities.MaxColorAttachments);

    // ── Caches ────────────────────────────────────────────────────

    public virtual Dictionary<ulong, uint> CachedBlockLocations { get; } = [];

    public virtual Dictionary<ulong, int> CachedUniformLocations { get; } = [];

    public virtual Dictionary<ulong, int> CachedAttribLocations { get; } = [];

    // ── Legacy Resource Creation ──────────────────────────────────
    // Backend-specific resource creation for legacy engine types.
    // GL backend creates real GL objects; VK returns 0/no-ops.

    public virtual uint CreateLegacyFramebuffer(GraphicsFrameBuffer.Attachment[] attachments, uint width, uint height) => 0;

    public virtual void DeleteLegacyFramebuffer(uint handle) { }

    public virtual uint CompileLegacyProgram(string fragmentSource, string vertexSource, string geometrySource) => 0;

    public virtual void UseLegacyProgram(uint handle) { }

    public virtual void DeleteLegacyProgram(uint handle) { }

    public virtual uint CreateLegacyVertexArray(LegacyVertexFormat format, GraphicsBuffer vertices, GraphicsBuffer? indices,
        LegacyVertexFormat? instanceFormat = null, GraphicsBuffer? instanceBuffer = null) => 0;

    public virtual void DeleteLegacyVertexArray(uint handle) { }

    // ── Legacy Texture Operations ────────────────────────────────
    // Texture state operations that GL sets per-texture but Vulkan handles via Samplers.

    public virtual void LegacySetTextureWrap(GraphicsTexture texture, int axis, TextureWrap wrap) { }

    public virtual void LegacySetTextureFilters(GraphicsTexture texture, TextureMin min, TextureMag mag) { }

    // ── Wireframe Mode ───────────────────────────────────────────

    public virtual void SetWireframeMode(bool enabled) { }

    #endregion

    #region Disposal

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            WaitForIdle();
            DisposeResources();
            GC.SuppressFinalize(this);
        }
    }

    protected abstract void DisposeResources();

    protected void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    ~GraphiteDevice()
    {
        if (!_disposed)
        {
            Debug.LogWarning("GraphiteDevice was not disposed before finalization.");
        }
    }

    #endregion

    #region Factory

    /// <summary>
    /// Creates a graphics device for the specified backend.
    /// </summary>
    public static GraphiteDevice Create(GraphicsBackendType backend)
    {
        return backend switch
        {
            GraphicsBackendType.OpenGL => new OpenGL.GLGraphiteDevice(),
            GraphicsBackendType.Vulkan => new Vulkan.VKGraphiteDevice(),
            _ => throw new ArgumentException($"Unknown backend type: {backend}", nameof(backend)),
        };
    }

    #endregion
}

/// <summary>
/// Represents a single GPU timing measurement for a named render pass section.
/// </summary>
public readonly record struct GpuTimingResult(string Name, double DurationMs);
