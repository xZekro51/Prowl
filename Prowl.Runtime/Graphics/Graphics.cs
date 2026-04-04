// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Runtime.Graphite;
using Prowl.Runtime.Graphite.OpenGL;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

using Silk.NET.OpenGL;

namespace Prowl.Runtime;

/// <summary>
/// Identifies the active rendering context — whether the engine is currently
/// performing game/scene rendering or editor UI rendering.
/// <para>
/// In the editor, scene and game views render through the Graphite
/// abstraction layer using the project's chosen backend (<see cref="GraphicsContext.Game"/>),
/// while the editor chrome (Dear ImGui, Paper UI) always uses the OpenGL
/// backend (<see cref="GraphicsContext.Editor"/>).
/// </para>
/// <para>
/// In standalone (built) games the context stays <see cref="GraphicsContext.Game"/>
/// for the entire frame.
/// </para>
/// </summary>
public enum GraphicsContext
{
    /// <summary>
    /// Game or scene rendering.  Commands go through the Graphite abstraction
    /// and are executed by whichever backend the project settings define.
    /// </summary>
    Game,

    /// <summary>
    /// Editor UI and tool rendering (Dear ImGui, Paper UI).  Always uses
    /// the OpenGL backend regardless of the project's rendering setting.
    /// </summary>
    Editor,
}

public static unsafe class Graphics
{
    private static GraphiteDevice? _graphiteDevice;

    /// <summary>
    /// The Graphite graphics device.
    /// All rendering — both the modern command-list API and legacy immediate-mode
    /// pass-throughs — is routed through this single device.
    /// </summary>
    public static GraphiteDevice Graphite => _graphiteDevice ?? throw new InvalidOperationException("Graphics device not initialized. Call Graphics.Initialize first.");

    /// <summary>
    /// Returns <c>true</c> when the device has been initialized and is ready for use.
    /// </summary>
    public static bool IsGraphiteReady => _graphiteDevice is not null;

    /// <summary>
    /// Active Graphite command buffer for parallel recording during the bridge phase.
    /// Pipeline implementations set this to enable Graphite command recording alongside
    /// legacy GL rendering.  Legacy draw methods (<c>DrawMeshNow</c>, <c>DrawRenderables</c>)
    /// will record parallel Graphite commands when this is set and a render pass is active.
    /// </summary>
    internal static Rendering.RenderCommandBuffer? ActiveGraphiteCmdBuffer { get; set; }

    /// <summary>
    /// Tracks whether the swapchain has been rendered to in the current frame.
    /// When <c>true</c>, subsequent render passes targeting the swapchain should use
    /// <c>LoadOp.Load</c> to preserve existing content instead of <c>LoadOp.Clear</c>.
    /// Reset at the start of each frame by the render loop in <see cref="Game"/>.
    /// </summary>
    internal static bool SwapchainClearedThisFrame { get; set; }

    /// <summary>
    /// Injects a pre-configured <see cref="GraphiteDevice"/> instance.
    /// Intended for unit-testing scenarios where the normal initialization flow is bypassed.
    /// </summary>
    internal static void SetGraphiteDevice(GraphiteDevice device) => _graphiteDevice = device;

    /// <summary>
    /// The current rendering context (editor or game).
    /// Rendering code can check this to branch behavior between editor and game rendering.
    /// </summary>
    public static GraphicsContext ActiveContext { get; set; } = GraphicsContext.Game;

    /// <summary>
    /// The underlying <see cref="GLGraphiteDevice"/> when the OpenGL backend is active.
    /// Used internally by legacy pass-through methods.  Returns <c>null</c> for non-GL backends.
    /// </summary>
    private static GLGraphiteDevice? GLDevice => _graphiteDevice as GLGraphiteDevice;

    /// <summary>
    /// Returns the <see cref="GLGraphiteDevice"/> or throws with a clear message.
    /// Use this instead of <c>GLDevice!</c> to avoid cryptic NullReferenceExceptions
    /// when the Vulkan backend is active.
    /// </summary>
    private static GLGraphiteDevice RequireGLDevice => GLDevice
        ?? throw new InvalidOperationException(
            "This operation requires the OpenGL backend. Legacy GL pass-through methods " +
            "are not available with the current backend. Migrate callers to the Graphite API.");

    /// <summary>
    /// Returns <c>true</c> when the OpenGL backend is active.
    /// Use this to guard legacy GL-only code paths.
    /// </summary>
    public static bool IsOpenGL => GLDevice is not null;

    /// <summary>
    /// Direct access to the Silk.NET OpenGL context.
    /// Only available when using the OpenGL backend.
    /// Provided for backward compatibility with existing OpenGL resource types
    /// (<see cref="GraphicsBuffer"/>, <see cref="GraphicsTexture"/>, etc.).
    /// </summary>
    public static GL GL => GLDevice?.GLContext
        ?? throw new InvalidOperationException("OpenGL context is only available with the OpenGL backend.");

    public static int MaxTextureSize => GLDevice?.MaxTextureSize ?? (int)(_graphiteDevice?.Capabilities.MaxTextureSize ?? 4096);
    public static int MaxCubeMapTextureSize => GLDevice?.MaxCubeMapTextureSize ?? (int)(_graphiteDevice?.Capabilities.MaxTextureSize ?? 4096);
    public static int MaxArrayTextureLayers => GLDevice?.MaxArrayTextureLayers ?? 256;
    public static int MaxFramebufferColorAttachments => GLDevice?.MaxFramebufferColorAttachments ?? (int)(_graphiteDevice?.Capabilities.MaxColorAttachments ?? 8);

    #region Renderable Draw API

    // ============================================================================
    // QUEUED RENDERING API - Unity-style Graphics.DrawMesh/DrawMeshInstanced
    // ============================================================================

    /// <summary>
    /// Queues a single mesh to be rendered by pushing it to the scene's render queue.
    /// The mesh will be rendered during the next frame with the specified material and transform.
    /// </summary>
    /// <param name="scene">Scene to push the renderable to</param>
    /// <param name="mesh">Mesh to render</param>
    /// <param name="transform">World transform matrix</param>
    /// <param name="material">Material to render with</param>
    /// <param name="layer">Layer index for culling and sorting (default: 0)</param>
    /// <param name="properties">Optional per-object property overrides</param>
    public static void DrawMesh(Scene scene, Mesh mesh, Float4x4 transform, Material material, int layer = 0, PropertyState? properties = null)
    {
        if (scene == null || mesh == null || material == null) return;

        var renderable = new MeshRenderable(mesh, material, transform, layer, properties);
        scene.PushRenderable(renderable);
    }

    /// <summary>
    /// Queues multiple instances of a mesh to be rendered with GPU instancing.
    /// Automatically handles batching for large instance counts (>1023 instances).
    /// </summary>
    /// <param name="scene">Scene to push the renderable to</param>
    /// <param name="mesh">Mesh to render</param>
    /// <param name="transforms">Array of world transforms (one per instance)</param>
    /// <param name="material">Material to render with</param>
    /// <param name="worldOrigin">World-space origin for depth sorting (e.g., particle system transform position, terrain chunk center)</param>
    /// <param name="layer">Layer index for culling and sorting (default: 0)</param>
    /// <param name="properties">Optional shared properties for all instances</param>
    /// <param name="bounds">Optional custom bounds for culling. If null, computed from mesh bounds.</param>
    /// <param name="maxBatchSize">Maximum instances per batch (default: 1023)</param>
    public static void DrawMeshInstanced(Scene scene, Mesh mesh, Float4x4[] transforms, Material material, Float3 worldOrigin, int layer = 0, PropertyState? properties = null, AABB? bounds = null, int maxBatchSize = 1023)
    {
        if (scene == null || mesh == null || material == null || transforms == null || transforms.Length == 0) return;

        // Automatic batching for >1023 instances by default
        int remainingInstances = transforms.Length;
        int offset = 0;

        while (remainingInstances > 0)
        {
            int batchSize = Maths.Min(remainingInstances, maxBatchSize);

            // Create instance data for this batch
            var instanceData = new Rendering.InstanceData[batchSize];
            for (int i = 0; i < batchSize; i++)
            {
                instanceData[i] = new Rendering.InstanceData(transforms[offset + i]);
            }

            // Push batch to scene
            var renderable = new InstancedMeshRenderable(mesh, material, instanceData, worldOrigin, layer, properties, bounds);
            scene.PushRenderable(renderable);

            remainingInstances -= batchSize;
            offset += batchSize;
        }
    }

    /// <summary>
    /// Queues multiple instances with per-instance colors.
    /// Automatically handles batching for large instance counts (>1023 instances).
    /// </summary>
    /// <param name="worldOrigin">World-space origin for depth sorting (e.g., particle system transform position, terrain chunk center)</param>
    public static void DrawMeshInstanced(Scene scene, Mesh mesh, Float4x4[] transforms, Material material, Float4[] colors, Float3 worldOrigin, int layer = 0, PropertyState? properties = null, AABB? bounds = null, int maxBatchSize = 1023)
    {
        if (scene == null || mesh == null || material == null || transforms == null || transforms.Length == 0) return;

        // Automatic batching for >1023 instances by default
        int remainingInstances = transforms.Length;
        int offset = 0;

        while (remainingInstances > 0)
        {
            int batchSize = Maths.Min(remainingInstances, maxBatchSize);

            // Create instance data for this batch with colors
            var instanceData = new Rendering.InstanceData[batchSize];
            for (int i = 0; i < batchSize; i++)
            {
                int idx = offset + i;
                Float4 color = idx < colors.Length ? colors[idx] : new Float4(1, 1, 1, 1);
                instanceData[i] = new Rendering.InstanceData(transforms[idx], color);
            }

            // Push batch to scene
            var renderable = new InstancedMeshRenderable(mesh, material, instanceData, worldOrigin, layer, properties, bounds);
            scene.PushRenderable(renderable);

            remainingInstances -= batchSize;
            offset += batchSize;
        }
    }

    /// <summary>
    /// Queues multiple instances with optional per-instance colors and custom data.
    /// This is the most flexible overload for custom per-instance data (UV offsets, lifetimes, etc.)
    /// Automatically handles batching for large instance counts.
    /// </summary>
    /// <param name="scene">Scene to push the renderable to</param>
    /// <param name="mesh">Mesh to render</param>
    /// <param name="transforms">Array of world transforms (one per instance)</param>
    /// <param name="material">Material to render with</param>
    /// <param name="worldOrigin">World-space origin for depth sorting (e.g., particle system transform position, terrain chunk center)</param>
    /// <param name="colors">Optional per-instance colors (RGBA). If null, defaults to white.</param>
    /// <param name="customData">Optional per-instance custom data (4 floats). Useful for UV offsets, lifetimes, etc.</param>
    /// <param name="layer">Layer index for culling and sorting (default: 0)</param>
    /// <param name="properties">Optional shared properties for all instances</param>
    /// <param name="bounds">Optional custom bounds for culling. If null, computed from mesh bounds.</param>
    /// <param name="maxBatchSize">Maximum instances per batch (default: 1023)</param>
    public static void DrawMeshInstanced(
        Scene scene,
        Mesh mesh,
        Float4x4[] transforms,
        Material material,
        Float3 worldOrigin,
        Float4[]? colors = null,
        Float4[]? customData = null,
        int layer = 0,
        PropertyState? properties = null,
        AABB? bounds = null,
        int maxBatchSize = 1023)
    {
        if (scene == null || mesh == null || material == null || transforms == null || transforms.Length == 0) return;

        // Automatic batching for >maxBatchSize instances
        int remainingInstances = transforms.Length;
        int offset = 0;

        while (remainingInstances > 0)
        {
            int batchSize = Maths.Min(remainingInstances, maxBatchSize);

            // Build InstanceData from separate arrays
            var instanceData = new Rendering.InstanceData[batchSize];
            for (int i = 0; i < batchSize; i++)
            {
                int idx = offset + i;
                Float4 color = colors != null && idx < colors.Length ? colors[idx] : new Float4(1, 1, 1, 1);
                Float4 custom = customData != null && idx < customData.Length ? customData[idx] : Float4.Zero;
                instanceData[i] = new Rendering.InstanceData(transforms[idx], color, custom);
            }

            // Push batch to scene
            var renderable = new InstancedMeshRenderable(mesh, material, instanceData, worldOrigin, layer, properties, bounds);
            scene.PushRenderable(renderable);

            remainingInstances -= batchSize;
            offset += batchSize;
        }
    }

    #endregion

    #region Command Buffer

    /// <summary>
    /// Creates a new <see cref="Rendering.RenderCommandBuffer"/> backed by a Graphite
    /// <see cref="Graphite.CommandList"/>.  The command buffer begins recording immediately
    /// and must be submitted via <see cref="Rendering.RenderCommandBuffer.Submit"/> or disposed.
    /// </summary>
    /// <param name="debugName">Optional name shown in GPU profilers.</param>
    public static Rendering.RenderCommandBuffer CreateCommandBuffer(string? debugName = null)
        => new Rendering.RenderCommandBuffer(debugName);

    #endregion

    #region Graphics Backend

    public static GraphicsProgram CurrentProgram => GraphicsProgram.currentProgram;

    /// <summary>
    /// Initializes the graphics subsystem with the specified backend.
    /// Creates a <see cref="GraphiteDevice"/> which now also provides
    /// the legacy immediate-mode API during the migration.
    /// <para>
    /// Ordering: the device reference is assigned and all event subscriptions
    /// are registered <b>before</b> <c>device.Initialize()</c> is called, so
    /// that subscribers receive the initial
    /// <see cref="EventSystem.GraphiteDeviceEvents.OnDeviceReady"/> event fired
    /// at the end of device initialization. If initialization fails the device
    /// reference is cleared so that <see cref="IsGraphiteReady"/> returns
    /// <c>false</c>.
    /// </para>
    /// </summary>
    public static void Initialize(GraphicsBackendType backend, bool debug)
    {
        var device = GraphiteDevice.Create(backend);

        // Assign the device reference BEFORE calling device.Initialize() so
        // that event subscriptions registered below can access Graphics.Graphite
        // when OnDeviceReady fires (which happens inside device.Initialize()).
        _graphiteDevice = device;

        // Register all event subscriptions before the device is fully initialized.
        // Critical ordering: OnDeviceReady fires at the END of device.Initialize(),
        // so subscribers (e.g. PipelineCacheManager) must be registered first.
        InitializeEventSubscriptions();

        try
        {
            device.Initialize(debug ? GraphiteDeviceOptions.Debug : GraphiteDeviceOptions.Default);
        }
        catch
        {
            try { device.Dispose(); } catch { }
            _graphiteDevice = null;
            throw;
        }

        Debug.Log($"[Graphics] Device initialized: {_graphiteDevice.BackendName}");
    }

    /// <summary>
    /// Registers all event-driven subscriptions for GPU resource caches and
    /// frame-level bookkeeping. Called once from <see cref="Initialize"/> before
    /// the device fires <see cref="EventSystem.GraphiteDeviceEvents.OnDeviceReady"/>.
    /// </summary>
    private static void InitializeEventSubscriptions()
    {
        Rendering.PipelineStateCache.InitializeEventSubscriptions();
        Rendering.GraphiteMaterialBinder.InitializeEventSubscriptions();
        Rendering.PipelineCacheManager.InitializeEventSubscriptions();

        // Swap render stats at frame begin (before any rendering) so the
        // previous frame's data is available for display.
        EventSystem.GraphiteDeviceEvents.SubscribeOnGpuFrameBegin(_ =>
        {
            Rendering.RenderStats.Instance.SwapFrames();
            EventSystem.RenderingEvents.InvokeOnRenderStatsReady(new EventSystem.RenderStatsReadyArgs(
                Rendering.RenderStats.Instance.DrawCalls,
                Rendering.RenderStats.Instance.Triangles,
                Rendering.RenderStats.Instance.Vertices));
        }, priority: -90);
    }

    // Backward-compatible cache accessors (delegate to the GL device)
    public static Dictionary<ulong, uint> cachedBlockLocations => RequireGLDevice.CachedBlockLocations;
    public static Dictionary<ulong, int> cachedUniformLocations => RequireGLDevice.CachedUniformLocations;
    public static Dictionary<ulong, int> cachedAttribLocations => RequireGLDevice.CachedAttribLocations;

    [Obsolete("Use CommandList.SetViewport() or CommandList.SetViewportRaw() instead.")]
    public static void Viewport(int x, int y, uint width, uint height)
    {
        GLDevice?.Viewport(x, y, width, height);
        // Bridge phase: mirror viewport and scissor to active Graphite command buffer.
        // Vulkan dynamic state requires both to be set; shadow atlas rendering
        // uses per-tile viewports so scissor must match to avoid bleeding.
        // Use raw viewport (no Y-flip) because this bridge is only reached
        // during shadow rendering on Vulkan, where the atlas is self-contained
        // and the Y-flip would corrupt the shadow UV ↔ depth mapping.
        if (ActiveGraphiteCmdBuffer is { InRenderPass: true } cmd)
        {
            cmd.SetViewportRaw(x, y, width, height);
            cmd.SetScissor(x, y, width, height);
        }
    }

    [Obsolete("Use render pass LoadOp.Clear instead.")]
    public static void Clear(float r, float g, float b, float a, ClearFlags v) => GLDevice?.Clear(r, g, b, a, v);

    [Obsolete("Use PipelineState rasterizer configuration instead.")]
    public static void SetState(RasterizerState state, bool force = false) => GLDevice?.SetState(state, force);

    public static RasterizerState GetState() => GLDevice?.GetState() ?? new RasterizerState();

    #region Buffers

    public static GraphicsBuffer CreateBuffer<T>(BufferType bufferType, T[] data, bool dynamic = false)
    {
        fixed (void* dat = data)
            return new GraphicsBuffer(bufferType, (uint)(data.Length * sizeof(T)), dat, dynamic);
    }

    public static void SetBuffer<T>(GraphicsBuffer buffer, T[] data, bool dynamic = false)
    {
        fixed (void* dat = data)
            buffer!.Set((uint)(data.Length * sizeof(T)), dat, dynamic);
    }

    public static void UpdateBuffer<T>(GraphicsBuffer buffer, uint offsetInBytes, T[] data)
    {
        fixed (void* dat = data)
            buffer!.Update(offsetInBytes, (uint)(data.Length * sizeof(T)), dat);
    }

    public static void BindBuffer(GraphicsBuffer buffer) => GLDevice?.BindBuffer(buffer);

    public static uint GetBlockIndex(GraphicsProgram program, string blockName) => RequireGLDevice.GetBlockIndex(program, blockName);

    public static void BindUniformBuffer(GraphicsProgram program, string blockName, GraphicsBuffer buffer, uint bindingPoint = 0)
        => GLDevice?.BindUniformBuffer(program, blockName, buffer, bindingPoint);

    public static void BindUniformBuffer(GraphicsProgram program, string blockName, Graphite.Buffer graphiteBuffer, uint bindingPoint = 0)
        => GLDevice?.BindUniformBuffer(program, blockName, graphiteBuffer, bindingPoint);

    #endregion

    #region Vertex Arrays

    public static GraphicsVertexArray CreateVertexArray(
        VertexFormat format,
        GraphicsBuffer vertices,
        GraphicsBuffer? indices,
        VertexFormat? instanceFormat = null,
        GraphicsBuffer? instanceBuffer = null)
    {
        return new GraphicsVertexArray(format, vertices, indices, instanceFormat, instanceBuffer);
    }

    [Obsolete("Use CommandList.SetMeshBuffers() instead.")]
    public static void BindVertexArray(GraphicsVertexArray? vertexArrayObject) => GLDevice?.BindVertexArray(vertexArrayObject);

    #endregion


    #region Frame Buffers

    public static GraphicsFrameBuffer CreateFramebuffer(GraphicsFrameBuffer.Attachment[] attachments, uint width, uint height) => new GraphicsFrameBuffer(attachments, width, height);

    [Obsolete("Use CommandList.BeginRenderPass() targeting the swapchain instead.")]
    public static void UnbindFramebuffer() => GLDevice?.UnbindFramebuffer();

    [Obsolete("Use CommandList.BeginRenderPass() instead.")]
    public static void BindFramebuffer(GraphicsFrameBuffer frameBuffer, FBOTarget readFramebuffer = FBOTarget.Framebuffer)
        => GLDevice?.BindFramebuffer(frameBuffer, readFramebuffer);

    public static GraphicsFrameBuffer? GetCurrentFramebuffer(FBOTarget target = FBOTarget.Framebuffer)
        => GLDevice?.GetCurrentFramebuffer(target);

    [Obsolete("Use CommandList.CopyTextureToTexture() instead.")]
    public static void BlitFramebuffer(int srcX, int srcY, int srcWidth, int srcHeight, int destX, int destY, int destWidth, int destHeight, ClearFlags mask, BlitFilter filter)
        => GLDevice?.BlitFramebuffer(srcX, srcY, srcWidth, srcHeight, destX, destY, destWidth, destHeight, mask, filter);

    public static T ReadPixel<T>(int attachment, int x, int y, TextureImageFormat format) where T : unmanaged
        => RequireGLDevice.ReadPixel<T>(attachment, x, y, format);

    #endregion

    #region Shaders

    public static GraphicsProgram CompileProgram(string fragment, string vertex, string geometry) => new GraphicsProgram(fragment, vertex, geometry);
    [Obsolete("Use CommandList.SetPipeline() instead.")]
    public static void BindProgram(GraphicsProgram program) => GLDevice?.BindProgram(program);

    public static int GetUniformLocation(GraphicsProgram program, string name) => RequireGLDevice.GetUniformLocation(program, name);

    public static int GetAttribLocation(GraphicsProgram program, string name) => RequireGLDevice.GetAttribLocation(program, name);

    [Obsolete("Use BindGroup uniforms instead.")]
    public static void SetUniformF(GraphicsProgram program, string name, float value) => GLDevice?.SetUniformF(program, name, value);

    [Obsolete("Use BindGroup uniforms instead.")]
    public static void SetUniformI(GraphicsProgram program, string name, int value) => GLDevice?.SetUniformI(program, name, value);

    [Obsolete("Use BindGroup uniforms instead.")]
    public static void SetUniformV2(GraphicsProgram program, string name, Float2 value) => GLDevice?.SetUniformV2(program, name, value);

    [Obsolete("Use BindGroup uniforms instead.")]
    public static void SetUniformV3(GraphicsProgram program, string name, Float3 value) => GLDevice?.SetUniformV3(program, name, value);

    [Obsolete("Use BindGroup uniforms instead.")]
    public static void SetUniformV4(GraphicsProgram program, string name, Float4 value) => GLDevice?.SetUniformV4(program, name, value);

    [Obsolete("Use BindGroup uniforms instead.")]
    public static void SetUniformMatrix(GraphicsProgram program, string name, bool transpose, Float4x4 matrix)
        => GLDevice?.SetUniformMatrix(program, name, transpose, matrix);
    [Obsolete("Use BindGroup uniforms instead.")]
    public static void SetUniformMatrix(GraphicsProgram program, string name, bool transpose, in float matrix)
        => GLDevice?.SetUniformMatrix(program, name, transpose, in matrix);

    [Obsolete("Use BindGroup uniforms instead.")]
    public static void SetUniformMatrix(GraphicsProgram program, string name, uint count, bool transpose, in float matrix)
        => GLDevice?.SetUniformMatrix(program, name, count, transpose, in matrix);

    [Obsolete("Use CommandList.SetBindGroup() with texture entries instead.")]
    public static void SetUniformTexture(GraphicsProgram program, string name, int slot, GraphicsTexture texture)
        => GLDevice?.SetUniformTexture(program, name, slot, texture);

    #endregion

    #region Textures

    public static GraphicsTexture CreateTexture(TextureType type, TextureImageFormat format) => new GraphicsTexture(type, format);
    public static void SetWrapS(GraphicsTexture texture, TextureWrap wrap) => texture!.SetWrapS(wrap);

    public static void SetWrapT(GraphicsTexture texture, TextureWrap wrap) => texture!.SetWrapT(wrap);
    public static void SetWrapR(GraphicsTexture texture, TextureWrap wrap) => texture!.SetWrapR(wrap);
    public static void SetTextureFilters(GraphicsTexture texture, TextureMin min, TextureMag mag) => texture!.SetTextureFilters(min, mag);
    public static void GenerateMipmap(GraphicsTexture texture) => texture!.GenerateMipmap();

    public static unsafe void GetTexImage(GraphicsTexture texture, int mip, void* data) => texture!.GetTexImage(mip, data);

    public static unsafe void TexImage2D(GraphicsTexture texture, int mip, uint width, uint height, int v2, void* data)
        => texture!.TexImage2D(texture.Target, mip, width, height, v2, data);
    public static unsafe void TexSubImage2D(GraphicsTexture texture, int mip, int x, int y, uint width, uint height, void* data)
        => texture!.TexSubImage2D(texture.Target, mip, x, y, width, height, data);

    public static unsafe void TexImage3D(GraphicsTexture texture, int level, uint width, uint height, uint depth, void* data)
        => texture!.TexImage3D(texture.Target, level, width, height, depth, data);
    public static unsafe void TexSubImage3D(GraphicsTexture texture, int level, int x, int y, int z, uint width, uint height, uint depth, void* data)
        => texture!.TexSubImage3D(texture.Target, level, x, y, z, width, height, depth, data);

    #endregion

    [Obsolete("Use CommandList.Draw() instead.")]
    public static void Draw(Topology primitiveType, uint count) => GLDevice?.Draw(primitiveType, count);

    [Obsolete("Use CommandList.Draw() instead.")]
    public static void Draw(Topology primitiveType, int v, uint count) => GLDevice?.Draw(primitiveType, v, count);

    [Obsolete("Use CommandList.DrawIndexed() instead.")]
    public static unsafe void DrawIndexed(Topology primitiveType, uint indexCount, bool index32bit, void* value)
        => GLDevice?.DrawIndexed(primitiveType, indexCount, index32bit, value);

    [Obsolete("Use CommandList.DrawIndexed() instead.")]
    public static unsafe void DrawIndexed(Topology primitiveType, uint indexCount, int startIndex, int baseVertex, bool index32bit)
        => GLDevice?.DrawIndexed(primitiveType, indexCount, startIndex, baseVertex, index32bit);

    [Obsolete("Use CommandList.DrawIndexedInstanced() instead.")]
    public static unsafe void DrawIndexedInstanced(Topology primitiveType, uint indexCount, uint instanceCount, bool index32bit)
        => GLDevice?.DrawIndexedInstanced(primitiveType, indexCount, instanceCount, index32bit);

    /// <summary>
    /// Invalidates all legacy GL state caches (program, texture binding, etc.).
    /// Must be called after Graphite command lists are submitted, because the
    /// GL backend executes those commands directly—changing the active program,
    /// VAO, textures, and other state—without going through the legacy
    /// cache-aware wrappers (<see cref="GraphicsProgram.Use"/>,
    /// <see cref="GraphicsTexture.Bind"/>, etc.).
    /// </summary>
    [Obsolete("Legacy GL cache invalidation — will be removed after full Graphite migration.")]
    public static void InvalidateLegacyCaches()
    {
        GraphicsProgram.currentProgram = null;
        GraphicsTexture.InvalidateBindCache();
    }

    public static void Dispose()
    {
        Rendering.PipelineStateCache.Clear();
        Runtime.Graphite.ShaderCrossCompiler.Shutdown();

        _graphiteDevice?.Dispose();
        _graphiteDevice = null;
    }

    /// <summary>
    /// Called when a device-lost event is detected. Cleanup of cached GPU state
    /// (pipeline states, material binder resources) is now handled by
    /// <see cref="EventSystem.GraphiteDeviceEvents.OnDeviceLost"/> subscribers.
    /// This method is retained for legacy GL cache invalidation during the migration.
    /// </summary>
    internal static void OnDeviceLost()
    {
        // PipelineStateCache and GraphiteMaterialBinder cleanup is now event-driven
        // via GraphiteDeviceEvents.OnDeviceLost subscribers (see InitializeEventSubscriptions).
        InvalidateLegacyCaches();
    }

    #endregion
}
