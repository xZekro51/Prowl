// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Prowl.Runtime.Graphite;
using Prowl.Runtime.Profiling;
using Prowl.Runtime.Rendering.Shaders;
using Prowl.Runtime.Resources;
using Prowl.Vector;

using IndexFormat = Prowl.Runtime.Resources.IndexFormat;

namespace Prowl.Runtime.Rendering;

public struct RenderingData
{
    public bool DisplayGizmo;
    public Float4x4 GridMatrix;
    public Color GridColor;
    public Float3 GridSizes;
}

/// <summary>
/// Interface for all renderable objects in the scene.
/// Supports both single-instance and GPU-instanced rendering through a unified API.
/// </summary>
public interface IRenderable
{
    public Material GetMaterial();
    public int GetLayer();

    /// <summary>
    /// Gets the world-space position of this renderable (typically the transform position).
    /// Used for depth sorting (e.g., back-to-front sorting for transparent objects).
    /// </summary>
    public Float3 GetPosition();

    /// <summary>
    /// Gets the rendering data for this renderable.
    /// </summary>
    /// <param name="viewer">Camera viewing data for culling/LOD</param>
    /// <param name="properties">Shader properties (per-object or shared for instances)</param>
    /// <param name="mesh">Mesh to render</param>
    /// <param name="model">Model matrix (only used for single-instance rendering)</param>
    /// <param name="instanceData">Instance data array for GPU instancing, or null for single-instance rendering</param>
    public void GetRenderingData(ViewerData viewer, out PropertyState properties, out Mesh mesh, out Float4x4 model, out InstanceData[]? instanceData);

    public void GetCullingData(out bool isRenderable, out AABB bounds);

    /// <summary>
    /// Returns the source mesh for SDF generation, or null if not mesh-based.
    /// </summary>
    public Mesh? GetMesh() => null;

    /// <summary>
    /// Gets the sub-mesh index to render. Return -1 to render all indices (the default).
    /// When &gt;= 0, the renderer will use the corresponding <see cref="SubMeshDescriptor"/>
    /// from the mesh to draw only that subset of the index buffer.
    /// </summary>
    public int GetSubMeshIndex() => -1;
}

public enum LightType
{
    Directional,
    Spot,
    Point,
    //Area
}

public interface IRenderableLight
{
    public int GetLightID();
    public int GetLayer();
    public LightType GetLightType();
    public Float3 GetLightPosition();
    public Float3 GetLightDirection();
    public bool DoCastShadows();

    /// <summary>
    /// Renders the light's contribution to the scene.
    /// Similar to ImageEffect.OnRenderImage, lights control their own drawing.
    /// </summary>
    /// <param name="gBuffer">GBuffer containing scene geometry data</param>
    /// <param name="destination">Destination render texture to draw light contribution to</param>
    /// <param name="css">Camera snapshot containing view/projection matrices and other camera data</param>
    public void OnRenderLight(RenderTexture gBuffer, RenderTexture destination, RenderPipeline.CameraSnapshot css);
}

public abstract class RenderPipeline : EngineObject
{
    /// <summary>
    /// The global default render pipeline asset.
    /// When a <see cref="Camera"/> has no per-camera <see cref="Camera.PipelineAsset"/>
    /// or <see cref="Camera.Pipeline"/>, the pipeline created by this asset is used.
    /// Set to <c>null</c> to fall back to a <see cref="DefaultRenderPipelineAsset"/>
    /// with default settings.
    /// </summary>
    public static RenderPipelineAsset? ActivePipelineAsset { get; set; }

    private static DefaultRenderPipelineAsset? s_fallbackAsset;

    /// <summary>
    /// Resolves the <see cref="RenderPipeline"/> to use for a camera, checking
    /// (in order): per-camera pipeline instance, per-camera asset, global asset, fallback default.
    /// </summary>
    internal static RenderPipeline Resolve(Camera camera)
    {
        // 1. Explicit pipeline instance on the camera
        if (camera.Pipeline is not null)
            return camera.Pipeline;

        // 2. Per-camera asset
        if (camera.PipelineAsset is not null)
            return camera.PipelineAsset.Pipeline;

        // 3. Global asset
        if (ActivePipelineAsset is not null)
            return ActivePipelineAsset.Pipeline;

        // 4. Fallback default
        s_fallbackAsset ??= new DefaultRenderPipelineAsset();
        return s_fallbackAsset.Pipeline;
    }

    public struct CameraSnapshot(Camera camera)
    {
        public Scene Scene = camera.Scene;

        public Float3 CameraPosition = camera.Transform.Position;
        public Float3 CameraRight = camera.Transform.Right;
        public Float3 CameraUp = camera.Transform.Up;
        public Float3 CameraForward = camera.Transform.Forward;
        public LayerMask CullingMask = camera.CullingMask;
        public CameraClearFlags ClearFlags = camera.ClearFlags;
        public float NearClipPlane = camera.NearClipPlane;
        public float FarClipPlane = camera.FarClipPlane;
        public uint PixelWidth = camera.PixelWidth;
        public uint PixelHeight = camera.PixelHeight;
        public float Aspect = camera.Aspect;
        public Float4x4 View = camera.ViewMatrix;
        public Float4x4 ViewInverse = camera.ViewMatrix.Invert();
        public Float4x4 Projection = camera.ProjectionMatrix;
        public Float4x4 PreviousViewProj = camera.PreviousViewProjectionMatrix;
        public Frustum WorldFrustum = Frustum.FromMatrix(camera.ProjectionMatrix * camera.ViewMatrix);
        public DepthTextureMode DepthTextureMode = camera.DepthTextureMode; // Flags, Can be None, Normals, MotionVectors
    }

    public HashSet<int> ActiveObjectIds { get => s_activeObjectIds; set => s_activeObjectIds = value; }

    private Dictionary<int, Float4x4> s_prevModelMatrices = [];
    private HashSet<int> s_activeObjectIds = [];
    private const int CLEANUP_INTERVAL_FRAMES = 120; // Clean up every 120 frames
    private int s_framesSinceLastCleanup = 0;

    // Reusable per-frame collections to avoid GC pressure
    private readonly HashSet<int> _reusableCulledSet = [];
    private readonly List<(IRenderable renderable, float distSq)> _reusableSortPairs = [];
    private readonly List<IRenderable> _reusableSortResult = [];
    private readonly List<RenderBatch> _reusableBatches = [];
    private readonly Dictionary<(ulong, int, Mesh, int), int> _reusableBatchLookup = [];
    private readonly List<int> _reusableKeyBuffer = [];

    private void CleanupUnusedModelMatrices()
    {
        // Increment frame counter
        s_framesSinceLastCleanup++;

        // Only perform cleanup at specified interval
        if (s_framesSinceLastCleanup < CLEANUP_INTERVAL_FRAMES)
            return;

        s_framesSinceLastCleanup = 0;

        // Remove all matrices that weren't used in this frame (no LINQ allocation)
        _reusableKeyBuffer.Clear();
        foreach (var key in s_prevModelMatrices.Keys)
        {
            if (!ActiveObjectIds.Contains(key))
                _reusableKeyBuffer.Add(key);
        }

        foreach (int key in _reusableKeyBuffer)
            s_prevModelMatrices.Remove(key);

        // Clear the active IDs set for next frame
        ActiveObjectIds.Clear();
    }

    private void TrackModelMatrix(int objectId, Float4x4 currentModel)
    {
        // Mark this object ID as active this frame
        ActiveObjectIds.Add(objectId);

        // Store current model matrix for next frame
        if (s_prevModelMatrices.TryGetValue(objectId, out Float4x4 prevModel))
            PropertyState.SetGlobalMatrix("prowl_PrevObjectToWorld", prevModel);
        else
            PropertyState.SetGlobalMatrix("prowl_PrevObjectToWorld", currentModel); // First frame, use current matrix

        s_prevModelMatrices[objectId] = currentModel;
    }

    public virtual void Render(Camera camera, in RenderingData data)
    {
        // Clean up unused matrices after rendering
        CleanupUnusedModelMatrices();
    }

    public HashSet<int> CullRenderables(IReadOnlyList<IRenderable> renderables, Frustum? worldFrustum, LayerMask cullingMask)
    {
        _reusableCulledSet.Clear();
        for (int renderIndex = 0; renderIndex < renderables.Count; renderIndex++)
        {
            IRenderable renderable = renderables[renderIndex];

            if (worldFrustum != null && CullRenderable(renderable, worldFrustum.Value))
            {
                _reusableCulledSet.Add(renderIndex);
                continue;
            }

            if (cullingMask.HasLayer(renderable.GetLayer()) == false)
            {
                _reusableCulledSet.Add(renderIndex);
                continue;
            }
        }
        return _reusableCulledSet;
    }

    public bool CullRenderable(IRenderable renderable, Frustum cameraFrustum)
    {
        renderable.GetCullingData(out bool isRenderable, out AABB bounds);

        return !isRenderable || !cameraFrustum.Intersects(bounds);
    }

    public enum SortMode
    {
        FrontToBack,
        BackToFront
    }

    /// <summary>
    /// Sorts renderables by distance from camera and returns a new sorted list.
    /// FrontToBack: Nearest objects first (optimal for opaque objects - early Z rejection)
    /// BackToFront: Farthest objects first (required for transparent objects - correct alpha blending)
    /// </summary>
    public List<IRenderable> SortRenderables(IReadOnlyList<IRenderable> renderables, HashSet<int> culledRenderableIndices, Float3 cameraPosition, SortMode mode)
    {
        int count = renderables?.Count ?? 0;
        _reusableSortPairs.Clear();
        _reusableSortResult.Clear();
        if (count == 0)
            return _reusableSortResult;

        // Collect only non-culled renderables
        for (int i = 0; i < count; i++)
        {
            if (culledRenderableIndices != null && culledRenderableIndices.Contains(i))
                continue;

            var renderable = renderables[i];
            float distSq = Float3.DistanceSquared(renderable.GetPosition(), cameraPosition);
            _reusableSortPairs.Add((renderable, distSq));
        }

        // Sort by distance squared (avoid sqrt)
        _reusableSortPairs.Sort((a, b) => mode switch
        {
            SortMode.FrontToBack => a.distSq.CompareTo(b.distSq),
            SortMode.BackToFront => b.distSq.CompareTo(a.distSq),
            _ => 0
        });

        // Extract sorted renderables into result list
        for (int i = 0; i < _reusableSortPairs.Count; i++)
            _reusableSortResult.Add(_reusableSortPairs[i].renderable);

        return _reusableSortResult;
    }

    public void SetupGlobalUniforms(CameraSnapshot css)
    {
        // Set View Rect
        //buffer.SetViewports((int)(camera.Viewrect.x * target.Width), (int)(camera.Viewrect.y * target.Height), (int)(camera.Viewrect.width * target.Width), (int)(camera.Viewrect.height * target.Height), 0, 1000);

        GlobalUniforms.SetPrevViewProj(css.PreviousViewProj);

        // Setup Default Uniforms for this frame
        // Camera
        GlobalUniforms.SetWorldSpaceCameraPos(css.CameraPosition);
        GlobalUniforms.SetProjectionParams(new Float4(1.0f, css.NearClipPlane, css.FarClipPlane, 1.0f / css.FarClipPlane));
        GlobalUniforms.SetScreenParams(new Float4(css.PixelWidth, css.PixelHeight, 1.0f + 1.0f / css.PixelWidth, 1.0f + 1.0f / css.PixelHeight));

        // Time
        GlobalUniforms.SetTime(new Float4(Time.TimeSinceStartup * 0.5f, Time.TimeSinceStartup, Time.TimeSinceStartup * 2, Time.FrameCount));
        GlobalUniforms.SetSinTime(new Float4(Maths.Sin(Time.TimeSinceStartup / 8), Maths.Sin(Time.TimeSinceStartup / 4), Maths.Sin(Time.TimeSinceStartup / 2), Maths.Sin(Time.TimeSinceStartup)));
        GlobalUniforms.SetCosTime(new Float4(Maths.Cos(Time.TimeSinceStartup / 8), Maths.Cos(Time.TimeSinceStartup / 4), Maths.Cos(Time.TimeSinceStartup / 2), Maths.Cos(Time.TimeSinceStartup)));
        GlobalUniforms.SetDeltaTime(new Float4(Time.DeltaTime, 1.0f / Time.DeltaTime, Time.SmoothDeltaTime, 1.0f / Time.SmoothDeltaTime));

        // Graphics API parameters
        // x = 1.0 on Vulkan (need to flip UV Y for NDC conversion in fullscreen passes), 0.0 on OpenGL
        GlobalUniforms.SetGraphicsParams(new Float4(Graphics.IsOpenGL ? 0.0f : 1.0f, 0, 0, 0));

        // Upload the global uniform buffer
        GlobalUniforms.Upload();
    }

    public void AssignCameraMatrices(Float4x4 view, Float4x4 projection)
    {
        GlobalUniforms.SetMatrixV(view);
        GlobalUniforms.SetMatrixIV(view.Invert());
        GlobalUniforms.SetMatrixP(projection);
        GlobalUniforms.SetMatrixVP(projection * view);

        // Upload the global uniform buffer
        GlobalUniforms.Upload();
    }

    #region Immediate Rendering (DrawMeshNow & Blit)

    /// <summary>
    /// Begins rendering to a <see cref="RenderTexture"/> target.
    /// On OpenGL, binds the framebuffer and optionally clears it.
    /// On Vulkan, begins a Graphite render pass with the appropriate load op.
    /// Must be paired with <see cref="EndRenderToTarget"/>.
    /// </summary>
    /// <param name="target">The render texture to render into.</param>
    /// <param name="clear">Whether to clear the target.</param>
    /// <param name="clearColor">The clear color (defaults to black).</param>
    public static void BeginRenderToTarget(RenderTexture target, bool clear = true, Color? clearColor = null)
    {
        if (!target.IsValid()) return;

        if (!Graphics.IsOpenGL
            && Graphics.ActiveGraphiteCmdBuffer is { InRenderPass: false } cmd)
        {
            var loadOp = clear ? Graphite.LoadOp.Clear : Graphite.LoadOp.DontCare;
            var col = clearColor ?? Color.Black;
            var clearFloat4 = new Float4((float)col.R, (float)col.G, (float)col.B, (float)col.A);
            cmd.BeginRenderPass(target, loadOp, clearFloat4, clear);
            cmd.SetViewport(0, 0, target.Width, target.Height);
            cmd.SetScissor(0, 0, (uint)target.Width, (uint)target.Height);
        }
        else
        {
            Graphics.BindFramebuffer(target.frameBuffer);
            if (clear)
            {
                var col = clearColor ?? Color.Black;
                Graphics.Clear((float)col.R, (float)col.G, (float)col.B, (float)col.A,
                    ClearFlags.Color | ClearFlags.Depth);
            }
        }
    }

    /// <summary>
    /// Ends rendering to the current target started by <see cref="BeginRenderToTarget"/>.
    /// On Vulkan, ends the active Graphite render pass and optionally transitions the
    /// target's attachments to <see cref="Graphite.ResourceState.ShaderResource"/>.
    /// </summary>
    /// <param name="target">If provided, transitions the target's attachments to shader-readable state.</param>
    public static void EndRenderToTarget(RenderTexture? target = null)
    {
        if (!Graphics.IsOpenGL
            && Graphics.ActiveGraphiteCmdBuffer is { InRenderPass: true } cmd)
        {
            cmd.EndRenderPass();
            if (target != null)
                TransitionToShaderResource(target);
        }
    }

    /// <summary>
    /// Transitions all color and depth attachments of a <see cref="RenderTexture"/> from
    /// render-target / depth-write state to <see cref="Graphite.ResourceState.ShaderResource"/>
    /// so they can be sampled in subsequent passes.
    /// Must be called outside a render pass.  No-ops on OpenGL.
    /// </summary>
    internal static void TransitionToShaderResource(RenderTexture target)
    {
        if (Graphics.IsOpenGL) return;
        if (Graphics.ActiveGraphiteCmdBuffer is not { InRenderPass: false } cmd) return;
        if (!target.IsValid()) return;

        var colorAttachments = target.GraphiteColorTextures;
        if (colorAttachments != null)
        {
            foreach (var tex in colorAttachments)
            {
                if (tex != null)
                    cmd.ResourceBarrier(new Graphite.ResourceBarrier(
                        tex, Graphite.ResourceState.RenderTarget, Graphite.ResourceState.ShaderResource));
            }
        }

        var depthAttachment = target.GraphiteDepthTexture;
        if (depthAttachment != null)
            cmd.ResourceBarrier(new Graphite.ResourceBarrier(
                depthAttachment, Graphite.ResourceState.DepthWrite, Graphite.ResourceState.ShaderResource));
    }

    /// <summary>
    /// Transitions all color attachments of a <see cref="RenderTexture"/> back from
    /// <see cref="Graphite.ResourceState.ShaderResource"/> to <see cref="Graphite.ResourceState.RenderTarget"/>
    /// so the texture can be used as a render target with <see cref="LoadOp.Load"/>.
    /// Must be called outside a render pass.  No-ops on OpenGL or if already in the correct state.
    /// </summary>
    internal static void TransitionToRenderTarget(RenderTexture target)
    {
        if (Graphics.IsOpenGL) return;
        if (Graphics.ActiveGraphiteCmdBuffer is not { InRenderPass: false } cmd) return;
        if (!target.IsValid()) return;

        var colorAttachments = target.GraphiteColorTextures;
        if (colorAttachments != null)
        {
            foreach (var tex in colorAttachments)
            {
                if (tex != null)
                    cmd.ResourceBarrier(new Graphite.ResourceBarrier(
                        tex, Graphite.ResourceState.ShaderResource, Graphite.ResourceState.RenderTarget));
            }
        }

        var depthAttachment = target.GraphiteDepthTexture;
        if (depthAttachment != null)
            cmd.ResourceBarrier(new Graphite.ResourceBarrier(
                depthAttachment, Graphite.ResourceState.ShaderResource, Graphite.ResourceState.DepthWrite));
    }

    /// <summary>
    /// Immediately draws a mesh without queuing. Used internally by the render pipeline.
    /// For queued rendering, use Graphics.DrawMesh() instead.
    /// </summary>
    public static void DrawMeshNow(Mesh mesh, Material mat, int passIndex = 0)
    {
        if (mesh.VertexCount <= 0) return;

        // Mesh data can vary between meshes, so we need to let the shader know which attributes are in use
        mat.SetKeyword("HAS_NORMALS", mesh.HasNormals);
        mat.SetKeyword("HAS_TANGENTS", mesh.HasTangents);
        mat.SetKeyword("HAS_UV", mesh.HasUV);
        mat.SetKeyword("HAS_UV2", mesh.HasUV2);
        mat.SetKeyword("HAS_COLORS", mesh.HasColors || mesh.HasColors32);
        mat.SetKeyword("HAS_BONEINDICES", mesh.HasBoneIndices);
        mat.SetKeyword("HAS_BONEWEIGHTS", mesh.HasBoneWeights);
        mat.SetKeyword("SKINNED", mesh.HasBoneIndices && mesh.HasBoneWeights);

        Shaders.ShaderPass pass = mat.Shader.GetPass(passIndex);

        if (!pass.TryGetVariantProgram(mat._localKeywords, out GraphicsProgram? variant))
            throw new System.Exception($"Failed to set shader pass {pass.Name}. No variant found for the current keyword state.");

        // Upload mesh data to GPU - required for BOTH OpenGL and Vulkan paths
        // to ensure VertexArrayObject and GraphiteVertexLayout are initialized.
        mesh.Upload();

        if (!Graphics.IsOpenGL)
        {
            // Vulkan path: use Graphite command buffer exclusively
            RecordGraphiteDraw(mesh, pass, variant, mat._properties);
            return;
        }

        Graphics.SetState(pass.State);

        PropertyState.Apply(mat._properties, variant);

        unsafe
        {
            Graphics.BindVertexArray(mesh.VertexArrayObject);
            Graphics.DrawIndexed(mesh.MeshTopology, (uint)mesh.IndexCount, mesh.IndexFormat == IndexFormat.UInt32, null);
            Graphics.BindVertexArray(null);
        }

        // Bridge phase: record parallel Graphite draw command
        RecordGraphiteDraw(mesh, pass, variant, mat._properties);
    }

    /// <summary>
    /// Records a parallel Graphite draw command during the bridge phase.
    /// On Vulkan, this creates and binds a full bind group from material properties.
    /// Only records if <see cref="Graphics.ActiveGraphiteCmdBuffer"/> is set
    /// and a render pass is currently active.
    /// </summary>
    private static void RecordGraphiteDraw(
        Mesh mesh, Shaders.ShaderPass pass, GraphicsProgram variant,
        PropertyState? materialProps = null,
        PropertyState? instanceProps = null,
        Float4x4? objectToWorld = null,
        Float4x4? worldToObject = null)
    {
        if (Graphics.ActiveGraphiteCmdBuffer is not { InRenderPass: true } cmd)
            return;

        var vao = mesh.VertexArrayObject;
        if (vao?.GraphiteVertexLayout == null ||
            variant.GraphiteVertexModule == null ||
            variant.GraphiteFragmentModule == null)
            return;

        // Get bind group layout for pipeline creation
        var bgl = variant.GetOrCreateBindGroupLayout();
        var layouts = bgl != null ? new[] { bgl } : null;

        cmd.SetMaterialPipeline(variant, vao.GraphiteVertexLayout!.Value, pass.State, mesh.MeshTopology, layouts);

        // Create and bind descriptor set from material/instance properties
        if (variant.Reflection != null && bgl != null)
        {
            var bindGroup = GraphiteMaterialBinder.CreateBindGroup(
                variant.Reflection, bgl, materialProps, instanceProps, objectToWorld, worldToObject);
            if (bindGroup != null)
                cmd.SetBindGroup(0, bindGroup);
        }

        cmd.DrawMeshIndexed(mesh);
    }

    private static Shader? s_blitShader;
    private static Material? s_blitMaterial;
    public static Material BlitMaterial
    {
        get
        {
            if (s_blitShader.IsNotValid())
                s_blitShader = Shader.LoadDefault(DefaultShader.Blit);

            if (s_blitMaterial.IsNotValid())
                s_blitMaterial = new Material(s_blitShader);

            return s_blitMaterial;
        }
    }

    public static void Blit(Texture2D source, Material? mat = null, int pass = 0)
    {
        mat ??= BlitMaterial;
        mat.SetTexture("_MainTex", source);
        Blit(mat, pass);
    }

    public static void Blit(RenderTexture source, RenderTexture target, Material? mat = null, int pass = 0, bool clearDepth = false, bool clearColor = false, Color color = default, bool preserveContents = false)
    {
        mat ??= BlitMaterial;

        // Self-blit (source == target) is a read-write hazard on Vulkan:
        // the render pass writes the image while the fragment shader reads it.
        // Resolve by copying source to a temporary RT and sampling from that.
        if (source == target && !Graphics.IsOpenGL && source.IsValid())
        {
            var formats = new TextureImageFormat[source.InternalTextures.Length];
            for (int i = 0; i < formats.Length; i++)
                formats[i] = source.InternalTextures[i].ImageFormat;
            var temp = RenderTexture.GetTemporaryRT(source.Width, source.Height, false, formats);
            Blit(source, temp);                  // source → temp (default material, simple copy)
            mat.SetTexture("_MainTex", temp.MainTexture);
            Blit(target, mat, pass, clearDepth, clearColor, color, preserveContents); // temp → target via composite material
            RenderTexture.ReleaseTemporaryRT(temp);
            return;
        }

        mat.SetTexture("_MainTex", source.MainTexture);

        // On Vulkan, transition the source texture to ShaderResource so the
        // blit shader can sample from it.  The barrier is a no-op if the
        // texture is already in the correct layout.
        // Only do this outside an active render pass (resource barriers are
        // not allowed inside render passes).  When called within an active
        // render pass (e.g., during lighting), the textures are already in
        // the correct layout.
        if (!Graphics.IsOpenGL
            && Graphics.ActiveGraphiteCmdBuffer is { InRenderPass: false }
            && source.IsValid())
        {
            TransitionToShaderResource(source);
        }

        Blit(target, mat, pass, clearDepth, clearColor, color, preserveContents);
    }

    public static void Blit(Texture2D source, RenderTexture target, Material? mat = null, int pass = 0, bool clearDepth = false, bool clearColor = false, Color color = default, bool preserveContents = false)
    {
        mat ??= BlitMaterial;
        mat.SetTexture("_MainTex", source);
        Blit(target, mat, pass, clearDepth, clearColor, color, preserveContents);
    }

    public static void Blit(RenderTexture target, Material? mat = null, int pass = 0, bool clearDepth = false, bool clearColor = false, Color color = default, bool preserveContents = false)
    {
        mat ??= BlitMaterial;

        // Vulkan path: when called outside an active render pass (e.g., from image effects),
        // we must begin/end our own Graphite render pass around the draw call.
        bool isVulkan = !Graphics.IsOpenGL;
        if (isVulkan
            && Graphics.ActiveGraphiteCmdBuffer is { InRenderPass: false } cmd
            && target.IsValid())
        {
            // DontCare is the safe default for fullscreen blits that overwrite every pixel.
            // Load is only needed when the target already has valid content that must be
            // preserved (e.g. additive GI blending into the light accumulation buffer).
            var loadOp = clearColor ? LoadOp.Clear : (preserveContents ? LoadOp.Load : LoadOp.DontCare);
            var clearFloat4 = clearColor
                ? new Float4((float)color.R, (float)color.G, (float)color.B, (float)color.A)
                : Float4.Zero;

            // Ensure color+depth attachments are in the layouts expected by the render pass.
            // After a prior TransitionToShaderResource (e.g. self-blit, post-process chain)
            // the attachments may still be in ShaderReadOnlyOptimal, which is incompatible
            // with LoadOp.Load's initialLayout (ColorAttachmentOptimal / DepthStencilAttachmentOptimal).
            if (preserveContents)
                TransitionToRenderTarget(target);

            cmd.BeginRenderPass(target, loadOp, clearFloat4, clearDepth);
            // Use SetViewportRaw (no Y-flip) for fullscreen blits.
            // The GBuffer was rendered with Y-flip which stored scene top at texture row 0.
            // Without Y-flip here, NDC (-1,-1) maps to framebuffer top, and UV (0,0) samples
            // texture row 0 (scene top), so the image is correctly oriented.
            cmd.SetViewportRaw(0, 0, target.Width, target.Height);
            cmd.SetScissor(0, 0, (uint)target.Width, (uint)target.Height);

            Blit(mat, pass);

            cmd.EndRenderPass();
            TransitionToShaderResource(target);
            return;
        }

        // GL path (or Vulkan with an already-active render pass)
        if (target.IsValid())
        {
            Graphics.BindFramebuffer(target.frameBuffer);
        }
        else
        {
            Graphics.UnbindFramebuffer();
            Graphics.Viewport(0, 0, (uint)Window.InternalWindow.FramebufferSize.X, (uint)Window.InternalWindow.FramebufferSize.Y);
        }
        if (clearDepth || clearColor)
        {
            ClearFlags clear = 0;
            if (clearDepth) clear |= ClearFlags.Depth;
            if (clearColor) clear |= ClearFlags.Color;
            Graphics.Clear((float)color.R, (float)color.G, (float)color.B, (float)color.A, clear | ClearFlags.Stencil);
        }
        Blit(mat, pass);
    }

    public static void Blit(Material? mat = null, int pass = 0)
    {
        mat ??= BlitMaterial;
        DrawMeshNow(Mesh.GetFullscreenQuad(), mat, pass);
    }

    #endregion

    /// <summary>
    /// Represents a render batch: a group of objects sharing the same material, mesh, and shader pass.
    /// Batching reduces GPU state changes by binding material uniforms once for all objects in the batch.
    /// </summary>
    private struct RenderBatch
    {
        public Material Material;      // Shared material for all objects in this batch
        public Mesh Mesh;              // Shared mesh for all objects in this batch
        public int PassIndex;          // Shader pass index
        public ulong MaterialHash;     // Hash of material uniforms (for sorting/grouping)
        public int SortKey;            // Sort order based on tag value + offset
        public int SubMeshIndex;       // Sub-mesh index (-1 = draw all indices)
        public List<int> RenderableIndices;  // Indices of objects in this batch
        public bool IsInstanced;       // True if this batch uses GPU instancing
        public int InstancedRenderableIndex;  // Index of the instanced renderable (if IsInstanced is true)
    }

    /// <summary>
    /// Renders all given objects with optimized batching. Objects are grouped by (material, mesh, pass)
    /// to minimize GPU state changes. This achieves:
    /// - Material uniforms bound once per batch (instead of per object)
    /// - Mesh data uploaded once per batch
    /// - Shader variant selected once per batch
    /// - Per-object uniforms still bound individually
    ///
    /// Performance: 100 objects with same material = 1 material bind (vs 100 without batching)
    /// </summary>
    public void DrawRenderables(IReadOnlyList<IRenderable> renderables, string shaderTag, string tagValue, ViewerData viewer, HashSet<int> culledRenderableIndices, bool updatePreviousMatrices)
    {
        bool hasRenderOrder = !string.IsNullOrWhiteSpace(shaderTag);
        bool hasSortOffsets = false;

        // ========== PHASE 1: Build Batches ==========
        // Group renderables by (material hash, shader pass, mesh) for efficient rendering
        Profiler.BeginSection("DrawRenderables.Build");
        _reusableBatches.Clear();
        _reusableBatchLookup.Clear();

        for (int renderIndex = 0; renderIndex < renderables.Count; renderIndex++)
        {
            // Skip culled objects
            if (culledRenderableIndices?.Contains(renderIndex) ?? false)
                continue;

            IRenderable renderable = renderables[renderIndex];

            Material material = renderable.GetMaterial();
            if (material.Shader.IsNotValid()) continue;

            // Get rendering data to determine if this is instanced or single-instance rendering
            renderable.GetRenderingData(viewer, out PropertyState _, out Mesh mesh, out Float4x4 _, out InstanceData[]? instanceData);
            if (mesh == null || mesh.VertexCount <= 0) continue;

            // Handle instanced renderables - add to batches with proper sorting (instanceData != null)
            if (instanceData != null && instanceData.Length > 0)
            {
                // Get material hash for batching
                ulong instancedMaterialHash = material.GetStateHash();

                // Find ALL shader passes matching the requested tag and add to batches
                int instancedPassIndex = -1;
                foreach (ShaderPass pass in material.Shader.Passes)
                {
                    instancedPassIndex++;

                    if (hasRenderOrder && !pass.HasTag(shaderTag, tagValue))
                        continue;

                    // Compute sort key for this pass (same as non-instanced)
                    int sortKey = hasRenderOrder ? instancedPassIndex + pass.GetTagSortOffset(shaderTag) : instancedPassIndex;
                    hasSortOffsets |= sortKey != instancedPassIndex;

                    // Create batch for instanced renderable
                    // Each instanced renderable gets its own batch since it draws all instances in one call
                    RenderBatch newBatch = new()
                    {
                        Material = material,
                        Mesh = mesh,
                        PassIndex = instancedPassIndex,
                        MaterialHash = instancedMaterialHash,
                        SortKey = sortKey,
                        IsInstanced = true,
                        InstancedRenderableIndex = renderIndex,
                        RenderableIndices = null  // Not used for instanced batches
                    };
                    _reusableBatches.Add(newBatch);
                }
                continue;
            }

            // Get material hash for batching - materials with identical uniforms will batch together
            ulong materialHash = material.GetStateHash();
            int subMeshIndex = renderable.GetSubMeshIndex();

            // Find ALL shader passes matching the requested tag (e.g., "Opaque", "Transparent", "ShadowCaster")
            // Multi-pass rendering: materials can have multiple passes with the same tag (e.g., terrain with many texture layers)
            int passIndex = -1;
            foreach (ShaderPass pass in material.Shader.Passes)
            {
                passIndex++;

                if (hasRenderOrder && !pass.HasTag(shaderTag, tagValue))
                    continue;


                // Found matching pass - add to appropriate batch
                // Batch key: (material hash, pass index, mesh, submesh) ensures each pass+submesh gets its own batch
                var batchKey = (materialHash, passIndex, mesh, subMeshIndex);
                if (_reusableBatchLookup.TryGetValue(batchKey, out int batchIndex))
                {
                    // Batch already exists - add this object to it
                    _reusableBatches[batchIndex].RenderableIndices.Add(renderIndex);
                }
                else
                {
                    // Compute sort key for this pass
                    int sortKey = hasRenderOrder ? passIndex + pass.GetTagSortOffset(shaderTag) : passIndex;
                    hasSortOffsets |= sortKey != passIndex;

                    // Create new batch for this unique material+pass+mesh+submesh combination
                    RenderBatch newBatch = new()
                    {
                        Material = material,
                        Mesh = mesh,
                        PassIndex = passIndex,
                        MaterialHash = materialHash,
                        SortKey = sortKey,
                        SubMeshIndex = subMeshIndex,
                        RenderableIndices = new() { renderIndex }
                    };
                    _reusableBatchLookup[batchKey] = _reusableBatches.Count;
                    _reusableBatches.Add(newBatch);
                }

                // Continue to next pass - materials can have multiple passes with the same tag
                // They will execute in order they appear in the shader file (Pass 0 → Pass 1 → Pass 2, etc.)
            }
        }

        // Sort batches by their sort key (respects tag offsets like "Transparent+1000")
        if (hasSortOffsets)
        {
            _reusableBatches.Sort((a, b) => a.SortKey.CompareTo(b.SortKey));
        }

        RenderStats.Instance.SetRenderableCount(renderables.Count);
        Profiler.EndSection(); // DrawRenderables.Build

        // ========== PHASE 2: Draw Batches ==========
        // For each batch, bind state once then draw all objects in that batch
        Profiler.BeginSection("DrawRenderables.Draw");
        foreach (RenderBatch batch in _reusableBatches)
        {
            // Handle instanced batches separately
            if (batch.IsInstanced)
            {
                IRenderable instancedRenderable = renderables[batch.InstancedRenderableIndex];
                DrawInstancedRenderablePass(instancedRenderable, batch.Material, batch.Mesh, batch.PassIndex, viewer);
                continue;
            }

            Material material = batch.Material;
            Mesh mesh = batch.Mesh;
            int passIndex = batch.PassIndex;
            int batchSubMeshIndex = batch.SubMeshIndex;
            RenderTexture grabRT = null;

            // Resolve the index range for this batch (full mesh or a single sub-mesh)
            int drawIndexCount;
            int drawFirstIndex;
            if (batchSubMeshIndex >= 0 && batchSubMeshIndex < mesh.SubMeshCount)
            {
                SubMeshDescriptor subDesc = mesh.GetSubMesh(batchSubMeshIndex);
                drawIndexCount = subDesc.IndexCount;
                drawFirstIndex = subDesc.IndexStart;
            }
            else
            {
                drawIndexCount = mesh.IndexCount;
                drawFirstIndex = 0;
            }

            // Configure shader keywords based on mesh attributes (normals, UVs, skinning, etc.)
            // Since all objects in the batch share the same mesh, this is done once per batch
            material.SetKeyword("HAS_NORMALS", mesh.HasNormals);
            material.SetKeyword("HAS_TANGENTS", mesh.HasTangents);
            material.SetKeyword("HAS_UV", mesh.HasUV);
            material.SetKeyword("HAS_UV2", mesh.HasUV2);
            material.SetKeyword("HAS_COLORS", mesh.HasColors || mesh.HasColors32);
            material.SetKeyword("HAS_BONEINDICES", mesh.HasBoneIndices);
            material.SetKeyword("HAS_BONEWEIGHTS", mesh.HasBoneWeights);
            material.SetKeyword("SKINNED", mesh.HasBoneIndices && mesh.HasBoneWeights);

            // Get shader pass and compiled variant for current keyword state
            ShaderPass pass = material.Shader.GetPass(passIndex);
            if (!pass.TryGetVariantProgram(material._localKeywords, out GraphicsProgram? variantNullable) || variantNullable == null)
                continue;

            GraphicsProgram variant = variantNullable;

            bool isVulkan = !Graphics.IsOpenGL;

            // Handle GrabTexture if this pass requests it
            // NOTE: GrabTexture captures whatever is currently bound, so this works for any render target
            if (pass.HasGrabTexture && !isVulkan)
            {
                // Get the currently bound framebuffer so we can restore it
                GraphicsFrameBuffer? currentFB = Graphics.GetCurrentFramebuffer(FBOTarget.Draw);

                if (currentFB != null)
                {
                    // Get framebuffer dimensions from the current render target
                    int fbWidth = (int)currentFB.Width;
                    int fbHeight = (int)currentFB.Height;

                    // Create temporary RT for grabbed texture
                    grabRT = RenderTexture.GetTemporaryRT(fbWidth, fbHeight, false, [TextureImageFormat.Color4b]);

                    // Setup blit: currentFB (read) -> grabRT (draw)
                    Graphics.BindFramebuffer(currentFB, FBOTarget.Read);
                    Graphics.BindFramebuffer(grabRT.frameBuffer, FBOTarget.Draw);
                    Graphics.BlitFramebuffer(0, 0, fbWidth, fbHeight, 0, 0, fbWidth, fbHeight, ClearFlags.Color, BlitFilter.Nearest);

                    // Restore the original framebuffer (for both read and draw)
                    Graphics.BindFramebuffer(currentFB, FBOTarget.Framebuffer);

                    // Set as global texture for this and subsequent passes
                    PropertyState.SetGlobalTexture(pass.GrabTextureName, grabRT.MainTexture);
                }
            }

            if (!isVulkan)
            {
                // Bind GlobalUniforms buffer (contains camera matrices, time, lighting data, etc.)
                // This is done per-batch because each shader variant is a separate GPU program object,
                // and uniform buffer bindings are per-program in OpenGL.
                Graphite.Buffer? globalBuffer = GlobalUniforms.GetBuffer();
                if (globalBuffer != null)
                {
                    Graphics.BindUniformBuffer(variant, "GlobalUniforms", globalBuffer, 0);
                }

                // Apply global properties (lighting, fog, shadow maps, etc.)
                GraphicsProgram.UniformCache cache = variant.uniformCache;
                int texSlot = 0;
                PropertyState.ApplyGlobals(variant, cache, ref texSlot);

                // *** BATCHING OPTIMIZATION: Bind material uniforms ONCE for entire batch ***
                PropertyState.ApplyMaterialUniforms(material._properties, variant, ref texSlot);

                // Set render state (depth test, blend mode, cull mode, etc.) once per batch
                Graphics.SetState(pass.State);
            }

            int texSlotForGraphite = 0; // Graphite handles textures via bind groups, not slots

            // Upload mesh data to GPU once per batch (shared by all objects)
            mesh.Upload();

            // Graphite pipeline and mesh buffers setup for this batch
            bool graphiteBatchActive = false;
            BindGroupLayout? batchBindGroupLayout = null;
            if (Graphics.ActiveGraphiteCmdBuffer is { InRenderPass: true } graphiteCmd)
            {
                var vao = mesh.VertexArrayObject;
                if (vao?.GraphiteVertexLayout != null &&
                    variant.GraphiteVertexModule != null &&
                    variant.GraphiteFragmentModule != null)
                {
                    batchBindGroupLayout = variant.GetOrCreateBindGroupLayout();
                    var layouts = batchBindGroupLayout != null ? new[] { batchBindGroupLayout } : null;
                    graphiteCmd.SetMaterialPipeline(variant, vao.GraphiteVertexLayout!.Value, pass.State, mesh.MeshTopology, layouts);
                    graphiteCmd.SetMeshBuffers(mesh);
                    graphiteBatchActive = true;
                }
            }

            // ========== PHASE 3: Draw Objects in Batch ==========
            // Material/mesh state is already bound - only per-object uniforms change
            foreach (int renderIndex in batch.RenderableIndices)
            {
                IRenderable renderable = renderables[renderIndex];

                // Get per-object data (transform, instance properties)
                renderable.GetRenderingData(viewer, out PropertyState properties, out Mesh _, out Float4x4 model, out InstanceData[]? _);

                // Track model matrix for motion vectors (used in temporal effects like TAA)
                int instanceId = properties.GetInt("_ObjectID");
                if (updatePreviousMatrices && instanceId != 0)
                    TrackModelMatrix(instanceId, model);

                // Compute inverse model matrix once (used by both GL and Graphite paths)
                var fModel = (Float4x4)model;
                var fModelInv = fModel.Invert();

                if (!isVulkan)
                {
                    // Apply instance-specific uniforms (tint colors, bone matrices, etc.)
                    int instanceTexSlot = texSlotForGraphite;
                    PropertyState.ApplyInstanceUniforms(properties, variant, ref instanceTexSlot);

                    // Directly bind per-object transform uniforms
                    Graphics.SetUniformMatrix(variant, "prowl_ObjectToWorld", false, fModel);
                    Graphics.SetUniformMatrix(variant, "prowl_WorldToObject", false, fModelInv);

                    // Execute draw call (mesh VAO already uploaded, just bind and draw)
                    unsafe
                    {
                        Graphics.BindVertexArray(mesh.VertexArrayObject);
                        if (drawFirstIndex == 0)
                            Graphics.DrawIndexed(mesh.MeshTopology, (uint)drawIndexCount, mesh.IndexFormat == IndexFormat.UInt32, null);
                        else
                            Graphics.DrawIndexed(mesh.MeshTopology, (uint)drawIndexCount, drawFirstIndex, 0, mesh.IndexFormat == IndexFormat.UInt32);
                        Graphics.BindVertexArray(null);
                    }
                }

                // Record Graphite draw command (primary on Vulkan, parallel on GL)
                if (graphiteBatchActive)
                {
                    if (variant.Reflection != null && batchBindGroupLayout != null)
                    {
                        var bindGroup = GraphiteMaterialBinder.CreateBindGroup(
                            variant.Reflection, batchBindGroupLayout,
                            material._properties, properties,
                            fModel, fModelInv);
                        if (bindGroup != null)
                            Graphics.ActiveGraphiteCmdBuffer!.SetBindGroup(0, bindGroup);
                    }
                    Graphics.ActiveGraphiteCmdBuffer!.DrawIndexed((uint)drawIndexCount, 1, (uint)drawFirstIndex);
                }

                RenderStats.Instance.AddDrawCall(mesh.VertexCount, drawIndexCount);
            }

            RenderStats.Instance.AddBatch();

            // Release grab texture RT if used
            if (grabRT != null)
            {
                PropertyState.SetGlobalTexture(pass.GrabTextureName, null);
                RenderTexture.ReleaseTemporaryRT(grabRT);
                grabRT = null;
            }
        }
        Profiler.EndSection(); // DrawRenderables.Draw
    }

    /// <summary>
    /// Draws a specific pass of an instanced renderable with GPU instancing.
    /// Uses DrawIndexedInstanced to draw multiple instances in a single draw call.
    /// The mesh's cached VAO system is used for optimal performance.
    /// </summary>
    private void DrawInstancedRenderablePass(IRenderable renderable, Material material, Mesh mesh, int passIndex, ViewerData viewer)
    {
        // Get rendering data (mesh, properties, instance data)
        renderable.GetRenderingData(viewer, out PropertyState sharedProperties, out Mesh _, out Float4x4 __, out InstanceData[]? instanceData);

        if (instanceData == null || instanceData.Length == 0)
            return;

        // Get instanced VAO from mesh (creates and caches on first use)
        GraphicsVertexArray vao = mesh.GetOrCreateInstanceVAO(instanceData, instanceData.Length);

        if (vao == null)
            return;

        // Get rendering info from mesh
        int instanceCount = instanceData.Length;
        int indexCount = mesh.IndexCount;
        bool useIndex32 = mesh.IndexFormat == IndexFormat.UInt32;

        // Enable GPU instancing keyword
        material.SetKeyword("GPU_INSTANCING", true);

        // Get the specific shader pass
        Shaders.ShaderPass pass = material.Shader.GetPass(passIndex);

        // Get shader variant
        if (!pass.TryGetVariantProgram(material._localKeywords, out GraphicsProgram? variantNullable) || variantNullable == null)
        {
            material.SetKeyword("GPU_INSTANCING", false);
            return;
        }

        GraphicsProgram variant = variantNullable;
        bool isVulkan = !Graphics.IsOpenGL;

        if (!isVulkan)
        {
            // Bind GlobalUniforms buffer
            Graphite.Buffer? globalBuffer = GlobalUniforms.GetBuffer();
            if (globalBuffer != null)
            {
                Graphics.BindUniformBuffer(variant, "GlobalUniforms", globalBuffer, 0);
            }

            // Apply global properties
            GraphicsProgram.UniformCache cache = variant.uniformCache;
            int texSlot = 0;
            PropertyState.ApplyGlobals(variant, cache, ref texSlot);

            // Apply material uniforms
            PropertyState.ApplyMaterialUniforms(material._properties, variant, ref texSlot);

            // Apply shared instance properties
            int instanceTexSlot = texSlot;
            PropertyState.ApplyInstanceUniforms(sharedProperties, variant, ref instanceTexSlot);

            // Set render state
            Graphics.SetState(pass.State);

            // Draw with TRUE GPU instancing!
            unsafe
            {
                Graphics.BindVertexArray(vao);
                Graphics.DrawIndexedInstanced(
                    Topology.Triangles,
                    (uint)indexCount,
                    (uint)instanceCount,
                    useIndex32
                );
                Graphics.BindVertexArray(null);
            }
        }

        // Record Graphite instanced draw (primary on Vulkan, parallel on GL)
        if (Graphics.ActiveGraphiteCmdBuffer is { InRenderPass: true } graphiteCmd)
        {
            var meshVao = mesh.VertexArrayObject;
            if (meshVao?.GraphiteVertexLayout != null &&
                variant.GraphiteVertexModule != null &&
                variant.GraphiteFragmentModule != null)
            {
                var bgl = variant.GetOrCreateBindGroupLayout();
                var layouts = bgl != null ? new[] { bgl } : null;
                graphiteCmd.SetMaterialPipeline(variant, meshVao.GraphiteVertexLayout!.Value, pass.State, mesh.MeshTopology, layouts);

                if (variant.Reflection != null && bgl != null)
                {
                    var bindGroup = GraphiteMaterialBinder.CreateBindGroup(
                        variant.Reflection, bgl, material._properties, sharedProperties);
                    if (bindGroup != null)
                        graphiteCmd.SetBindGroup(0, bindGroup);
                }

                graphiteCmd.SetMeshBuffers(mesh);
                graphiteCmd.DrawIndexed((uint)indexCount, (uint)instanceCount);
            }
        }

        RenderStats.Instance.AddDrawCall(mesh.VertexCount * instanceCount, indexCount * instanceCount);
        RenderStats.Instance.AddBatch();

        material.SetKeyword("GPU_INSTANCING", false);
    }
}
