// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;

using Prowl.Runtime.Resources;
using Prowl.Vector;

using Graphite = Prowl.Runtime.Graphite;
using Material = Prowl.Runtime.Resources.Material;
using Mesh = Prowl.Runtime.Resources.Mesh;
using Shader = Prowl.Runtime.Resources.Shader;

namespace Prowl.Runtime.Rendering;

public struct ViewerData
{
    public Float3 Position;
    public Float3 Forward;
    public Float3 Up;
    public Float3 Right;

    public ViewerData(DefaultRenderPipeline.CameraSnapshot css)
    {
        Position = css.CameraPosition;
        Forward = css.CameraForward;
        Up = css.CameraUp;
        Right = css.CameraRight;
    }

    public ViewerData(Float3 position, Float3 forward, Float3 right, Float3 up) : this()
    {
        Position = position;
        Forward = forward;
        Right = right;
        Up = up;
    }
}

/// <summary>
/// Default rendering pipeline implementation that handles deferred rendering
/// with a forward pass for transparent geometry, post-processing effects,
/// shadows, and debug visualization.
/// <para>
/// Create instances via <see cref="DefaultRenderPipelineAsset"/> rather than
/// constructing directly.  The asset caches the pipeline and allows
/// inspector-editable configuration of GBuffer formats, shadow atlas size, etc.
/// </para>
/// </summary>
public class DefaultRenderPipeline : RenderPipeline
{
    #region Pipeline Resources (per-instance)

    private Mesh _quadMesh;
    private Mesh _skyDome;
    private Material _defaultMaterial;
    private Material _skybox;
    private Material _gizmo;
    private Material _deferredCompose;

    // Graphite resources for non-GL swapchain blit (Vulkan path)
    private Graphite.Sampler? _graphiteBlitSampler;
    private Graphite.BindGroupLayout? _graphiteBlitTexBGL;
    private Graphite.Texture? _graphiteBlitLastSourceTex;
    private Graphite.BindGroup? _graphiteBlitBindGroup;

    #endregion

    #region Configuration

    /// <summary>
    /// The asset that created and configures this pipeline instance.
    /// </summary>
    public DefaultRenderPipelineAsset Asset { get; }

    /// <summary>
    /// Creates a new <see cref="DefaultRenderPipeline"/> configured by the given asset.
    /// Prefer using <see cref="DefaultRenderPipelineAsset.Pipeline"/> instead of
    /// calling this constructor directly.
    /// </summary>
    public DefaultRenderPipeline(DefaultRenderPipelineAsset asset)
    {
        Asset = asset ?? throw new ArgumentNullException(nameof(asset));
    }

    /// <summary>
    /// Creates a new <see cref="DefaultRenderPipeline"/> with default settings.
    /// </summary>
    public DefaultRenderPipeline() : this(new DefaultRenderPipelineAsset()) { }

    #endregion

    #region Resource Management

    private void ValidateDefaults()
    {
        _quadMesh ??= Mesh.GetFullscreenQuad();
        _defaultMaterial ??= new Material(Shader.LoadDefault(DefaultShader.Standard));
        _skybox ??= new Material(Shader.LoadDefault(DefaultShader.ProceduralSkybox));
        _gizmo ??= new Material(Shader.LoadDefault(DefaultShader.Gizmos));

        // Load deferred shaders
        _deferredCompose ??= new Material(Shader.LoadDefault(DefaultShader.DeferredCompose));

        if (_skyDome.IsNotValid())
        {
            Model skyDomeModel = Model.LoadDefault(DefaultModel.SkyDome) ?? throw new Exception("SkyDome model not found. Please ensure the model is included in the project.");
            _skyDome = skyDomeModel.Meshes[0].Mesh;
        }
    }

    #endregion

    #region Main Rendering

    public override void Render(Camera camera, in RenderingData data)
    {
        ValidateDefaults();

        // Main rendering with correct order of operations
        Internal_Render(camera, data);

        PropertyState.ClearGlobals();

        // Publish per-frame render stats
        RenderStats.Instance.SwapFrames();

        base.Render(camera, in data);
    }

    private Dictionary<RenderStage, List<ImageEffect>> GatherImageEffects(Camera camera)
    {
        var effectsByStage = new Dictionary<RenderStage, List<ImageEffect>>
        {
            { RenderStage.BeforeGBuffer, new List<ImageEffect>() },
            { RenderStage.AfterGBuffer, new List<ImageEffect>() },
            { RenderStage.DuringLighting, new List<ImageEffect>() },
            { RenderStage.AfterLighting, new List<ImageEffect>() },
            { RenderStage.PostProcess, new List<ImageEffect>() }
        };

        foreach (ImageEffect effect in camera.Effects)
        {
            // Get the stage, with backward compatibility for IsOpaqueEffect
            RenderStage stage = effect.Stage;

            #pragma warning disable CS0618 // Type or member is obsolete
            if (effect.IsOpaqueEffect && stage == RenderStage.PostProcess)
            {
                // Backward compatibility: IsOpaqueEffect means AfterLighting
                stage = RenderStage.AfterLighting;
            }
            #pragma warning restore CS0618

            effectsByStage[stage].Add(effect);
        }

        return effectsByStage;
    }

    private void ExecuteImageEffects(RenderContext context, List<ImageEffect> effects)
    {
        if (effects == null || effects.Count == 0)
            return;

        foreach (var effect in effects)
        {
            effect.OnRenderEffect(context);
        }
    }

    #endregion

    #region Scene Rendering

    private void Internal_Render(Camera camera, in RenderingData data)
    {
        // =======================================================
        // 0. Setup variables, and prepare the camera
        bool isHDR = camera.HDR;
        var effectsByStage = GatherImageEffects(camera);
        var allEffects = new List<ImageEffect>();
        foreach (var effects in effectsByStage.Values)
            allEffects.AddRange(effects);

        IReadOnlyList<IRenderableLight> lights = camera.GameObject.Scene.Lights;
        RenderTexture target = camera.UpdateRenderData();

        // Bridge phase: create Graphite command buffer for parallel recording.
        // Commands are recorded alongside legacy GL calls but NOT submitted —
        // this validates the RenderCommandBuffer API and pipeline structure.
        RenderCommandBuffer? graphiteCmd = null;
        if (Graphics.IsGraphiteReady)
        {
            try { graphiteCmd = Graphics.CreateCommandBuffer("DefaultPipeline"); }
            catch { /* Graphite not fully ready */ }
        }
        Graphics.ActiveGraphiteCmdBuffer = graphiteCmd;

        // =======================================================
        // 1. Pre Cull
        foreach (ImageEffect effect in allEffects)
            effect.OnPreCull(camera);

        // =======================================================
        // 2. Take a snapshot of all Camera data
        CameraSnapshot css = new(camera);
        SetupGlobalUniforms(css);

        // =======================================================
        // 3. Cull Renderables based on Snapshot data
        IReadOnlyList<IRenderable> renderables = camera.GameObject.Scene.Renderables;
        HashSet<int> culledRenderableIndices = CullRenderables(renderables, css.WorldFrustum, css.CullingMask);

        // =======================================================
        // 4. Pre Render
        foreach (ImageEffect effect in allEffects)
            effect.OnPreRender(camera);

        // =======================================================
        // 5. Setup Lighting and Shadows
        RenderShadowAtlas(css, lights, renderables);

        // 5.1 Re-Assign camera matrices (The Lighting can modify these)
        AssignCameraMatrices(css.View, css.Projection);

        // =======================================================
        // 6. Create GBuffer for Deferred Rendering
        // GBuffer layout:
        // BufferA: RGB = Albedo, A = Alpha
        // BufferB: RGB = Normal (view space), A = ShadingMode
        // BufferC: R = Roughness, G = Metalness, B = Specular, A = AO
        // BufferD: Custom Data per Shading Mode (e.g., Emissive for Lit mode)
        RenderTexture gBuffer = RenderTexture.GetTemporaryRT((int)css.PixelWidth, (int)css.PixelHeight, true, [
            Asset.GBufferAlbedoFormat, // BufferA - Albedo + Alpha
            Asset.GBufferNormalFormat, // BufferB - Normal + ShadingMode
            Asset.GBufferPBRFormat,    // BufferC - Roughness, Metalness, Specular, AO
            Asset.GBufferCustomFormat, // BufferD - Custom Data (Emissive, etc.)
            ]);

        // Bind GBuffer as the target
        Graphics.BindFramebuffer(gBuffer.frameBuffer);

        // Bridge phase: begin Graphite render pass for GBuffer (clear handled by LoadOp.Clear)
        if (graphiteCmd != null)
        {
            try
            {
                var clearFloat4 = new Float4(
                    (float)camera.ClearColor.R,
                    (float)camera.ClearColor.G,
                    (float)camera.ClearColor.B,
                    (float)camera.ClearColor.A);
                var loadOp = camera.ClearFlags == CameraClearFlags.Nothing
                    ? Graphite.LoadOp.DontCare
                    : Graphite.LoadOp.Clear;
                graphiteCmd.BeginRenderPass(gBuffer, loadOp, clearFloat4, camera.ClearFlags != CameraClearFlags.Nothing);
            }
            catch { /* Graphite render pass setup failed */ }
        }

        // 6.1 Clear GBuffer
        switch (camera.ClearFlags)
        {
            case CameraClearFlags.Skybox:
                Graphics.Clear(
                    (float)camera.ClearColor.R,
                    (float)camera.ClearColor.G,
                    (float)camera.ClearColor.B,
                    (float)camera.ClearColor.A,
                    ClearFlags.Color | ClearFlags.Depth
                );

                if (css.Scene.Skybox.Enabled)
                    RenderSkybox(css);
                break;

            case CameraClearFlags.SolidColor:
                Graphics.Clear(
                    (float)camera.ClearColor.R,
                    (float)camera.ClearColor.G,
                    (float)camera.ClearColor.B,
                    (float)camera.ClearColor.A,
                    ClearFlags.Color | ClearFlags.Depth
                );
                break;

            case CameraClearFlags.Depth:
                Graphics.Clear(0, 0, 0, 0, ClearFlags.Depth);
                break;

            case CameraClearFlags.Nothing:
                // Do not clear anything
                break;
        }

        // 6.2 Draw opaque geometry to GBuffer
        //List<IRenderable> sortFrontToBack = SortRenderables(renderables, culledRenderableIndices, css.CameraPosition, SortMode.FrontToBack);
        DrawRenderables(renderables, "RenderOrder", "Opaque", new ViewerData(css), culledRenderableIndices, false); // Its deffered rendering, overdraw is cheap

        // Bridge phase: end GBuffer Graphite render pass
        if (graphiteCmd?.InRenderPass == true)
        {
            try { graphiteCmd.EndRenderPass(); }
            catch { }
        }

        // =======================================================
        // 7. Deferred Lighting Pass - Render each light's contribution
        // Create light accumulation buffer
        RenderTexture lightAccumulation = RenderTexture.GetTemporaryRT((int)camera.PixelWidth, (int)camera.PixelHeight, false, [
            isHDR ? TextureImageFormat.Short4 : TextureImageFormat.Color4b, // Accumulated lighting
            ]);

        // Set GBuffer textures as global textures for shaders
        PropertyState.SetGlobalTexture("_GBufferA", gBuffer.InternalTextures[0]);
        PropertyState.SetGlobalTexture("_GBufferB", gBuffer.InternalTextures[1]);
        PropertyState.SetGlobalTexture("_GBufferC", gBuffer.InternalTextures[2]);
        PropertyState.SetGlobalTexture("_GBufferD", gBuffer.InternalTextures[3]);
        PropertyState.SetGlobalTexture("_CameraDepthTexture", gBuffer.InternalDepth);

        // Clear light accumulation to black
        Graphics.BindFramebuffer(lightAccumulation.frameBuffer);
        Graphics.Clear(0, 0, 0, 0, ClearFlags.Color);

        // Bridge phase: begin Graphite render pass for light accumulation
        if (graphiteCmd != null)
        {
            try { graphiteCmd.BeginRenderPass(lightAccumulation, Graphite.LoadOp.Clear, Float4.Zero, false); }
            catch { }
        }

        // Render each light's contribution (additive blending)
        foreach (IRenderableLight light in lights)
        {
            if (css.CullingMask.HasLayer(light.GetLayer()) == false)
                continue;

            light.OnRenderLight(gBuffer, lightAccumulation, css);
        }

        // Bridge phase: end lighting Graphite render pass
        if (graphiteCmd?.InRenderPass == true)
        {
            try { graphiteCmd.EndRenderPass(); }
            catch { }
        }

        // =======================================================
        // 7.5. Apply DuringLighting effects (e.g., SSPT, GTAO that need light accumulation)
        if (effectsByStage[RenderStage.DuringLighting].Count > 0)
        {
            var lightingContext = new RenderContext
            {
                GBuffer = gBuffer,
                LightAccumulation = lightAccumulation,
                SceneColor = null, // Not available yet
                Camera = camera,
                Width = (int)css.PixelWidth,
                Height = (int)css.PixelHeight,
                CurrentStage = RenderStage.DuringLighting
            };

            ExecuteImageEffects(lightingContext, effectsByStage[RenderStage.DuringLighting]);
        }

        // =======================================================
        // 8. Deferred Composition Pass - Combine light accumulation with GBuffer
        // Create final composition output
        RenderTexture composedOutput = RenderTexture.GetTemporaryRT((int)camera.PixelWidth, (int)camera.PixelHeight, true, [
            isHDR ? TextureImageFormat.Short4 : TextureImageFormat.Color4b,
            ]);

        // Set GBuffer and light textures for compose shader
        _deferredCompose.SetTexture("_LightAccumulation", lightAccumulation.InternalTextures[0]);
        _deferredCompose.SetTexture("_GBufferA", gBuffer.InternalTextures[0]);
        _deferredCompose.SetTexture("_GBufferB", gBuffer.InternalTextures[1]);
        _deferredCompose.SetTexture("_GBufferD", gBuffer.InternalTextures[3]);
        _deferredCompose.SetTexture("_CameraDepthTexture", gBuffer.InternalDepth);

        // Set fog parameters
        Scene.FogParams fog = css.Scene.Fog;
        Float4 fogParams = Float4.Zero;
        fogParams.X = fog.Density / 1.2011224f; // density/sqrt(ln(2))
        fogParams.Y = fog.Density / 0.693147181f; // ln(2)
        fogParams.Z = -1.0f / (fog.End - fog.Start);
        fogParams.W = fog.End / (fog.End - fog.Start);
        _deferredCompose.SetColor("_FogColor", fog.Color);
        _deferredCompose.SetVector("_FogParams", fogParams);
        _deferredCompose.SetVector("_FogStates", new Float3(
            fog.Mode == Scene.FogParams.FogMode.Linear ? 1 : 0,
            fog.Mode == Scene.FogParams.FogMode.Exponential ? 1 : 0,
            fog.Mode == Scene.FogParams.FogMode.ExponentialSquared ? 1 : 0
        ));

        // Set ambient lighting parameters
        Scene.AmbientLightParams ambient = css.Scene.Ambient;
        _deferredCompose.SetVector("_AmbientMode", new Float2(
            ambient.Mode == Scene.AmbientLightParams.AmbientMode.Uniform ? 1 : 0,
            ambient.Mode == Scene.AmbientLightParams.AmbientMode.Hemisphere ? 1 : 0
        ));
        _deferredCompose.SetColor("_AmbientColor", ambient.Color);
        _deferredCompose.SetColor("_AmbientSkyColor", ambient.SkyColor);
        _deferredCompose.SetColor("_AmbientGroundColor", ambient.GroundColor);
        _deferredCompose.SetFloat("_AmbientStrength", (float)ambient.Strength);

        // Bridge phase: begin Graphite render pass for composition
        if (graphiteCmd != null)
        {
            try { graphiteCmd.BeginRenderPass(composedOutput, Graphite.LoadOp.Clear, Float4.Zero, true); }
            catch { }
        }

        // Perform composition
        Blit(lightAccumulation, composedOutput, _deferredCompose, 0, false, false);

        // Bridge phase: end compose Graphite render pass
        if (graphiteCmd?.InRenderPass == true)
        {
            try { graphiteCmd.EndRenderPass(); }
            catch { }
        }

        // Copy depth from GBuffer to composed output for transparent rendering
        Graphics.BindFramebuffer(gBuffer.frameBuffer, FBOTarget.Read);
        Graphics.BindFramebuffer(composedOutput.frameBuffer, FBOTarget.Draw);
        Graphics.BlitFramebuffer(0, 0, gBuffer.Width, gBuffer.Height, 0, 0, composedOutput.Width, composedOutput.Height, ClearFlags.Depth, BlitFilter.Nearest);

        // Bind composed output for transparent rendering
        Graphics.BindFramebuffer(composedOutput.frameBuffer);

        // =======================================================
        // 9. Apply AfterLighting effects (opaque post-processing)
        if (effectsByStage[RenderStage.AfterLighting].Count > 0)
        {
            var afterLightingContext = new RenderContext
            {
                GBuffer = gBuffer,
                LightAccumulation = lightAccumulation,
                SceneColor = composedOutput,
                Camera = camera,
                Width = (int)css.PixelWidth,
                Height = (int)css.PixelHeight,
                CurrentStage = RenderStage.AfterLighting
            };

            ExecuteImageEffects(afterLightingContext, effectsByStage[RenderStage.AfterLighting]);
        }

        // =======================================================
        // 10. Transparent geometry (Forward rendered on top of composed result)
        // Bridge phase: begin Graphite render pass for forward transparent (load existing content)
        if (graphiteCmd != null)
        {
            try { graphiteCmd.BeginRenderPass(composedOutput, Graphite.LoadOp.Load, null, false); }
            catch { }
        }

        List<IRenderable> sortBackToFront = SortRenderables(renderables, culledRenderableIndices, css.CameraPosition, SortMode.BackToFront);
        DrawRenderables(sortBackToFront, "RenderOrder", "Transparent", new ViewerData(css), null, false);

        // Bridge phase: end forward transparent Graphite render pass
        if (graphiteCmd?.InRenderPass == true)
        {
            try { graphiteCmd.EndRenderPass(); }
            catch { }
        }

        // =======================================================
        // 11. Apply PostProcess effects (final post-processing)
        if (effectsByStage[RenderStage.PostProcess].Count > 0)
        {
            var postProcessContext = new RenderContext
            {
                GBuffer = gBuffer,
                LightAccumulation = lightAccumulation,
                SceneColor = composedOutput,
                Camera = camera,
                Width = (int)css.PixelWidth,
                Height = (int)css.PixelHeight,
                CurrentStage = RenderStage.PostProcess
            };

            ExecuteImageEffects(postProcessContext, effectsByStage[RenderStage.PostProcess]);

            // Effects may have replaced the scene color buffer (e.g., HDR to LDR)
            var replacedRTs = postProcessContext.GetReplacedRTs();
            if (replacedRTs.Count > 0)
            {
                // Update our reference to the new buffer
                composedOutput = postProcessContext.SceneColor;

                // Clean up old buffers
                foreach (var oldRT in replacedRTs)
                {
                    RenderTexture.ReleaseTemporaryRT(oldRT);
                }
            }
        }

        // =======================================================
        // 12. Render Gizmos
        RenderGizmos(css);

        // =======================================================
        // 13. Blit Result to target, If target is null Blit will go to the Screen/Window
        Blit(composedOutput, target, null, 0, false, false);

        // On non-GL backends (Vulkan), the legacy GL Blit above is a no-op.
        // Blit the composed scene output to the swapchain via Graphite so the
        // rendered content is actually visible.  This must happen before the
        // temporary render textures are released back to the pool.
        if (!Graphics.IsOpenGL && target == null)
        {
            try
            {
                BlitToSwapchainGraphite(composedOutput);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DefaultRenderPipeline] Failed to blit to swapchain: {e.Message}");
            }
        }

        // =======================================================
        // 14. Post Render
        foreach (ImageEffect effect in allEffects)
            effect.OnPostRender(camera);

        // =======================================================
        // 15. Cleanup temporary render textures
        RenderTexture.ReleaseTemporaryRT(gBuffer);
        RenderTexture.ReleaseTemporaryRT(lightAccumulation);
        RenderTexture.ReleaseTemporaryRT(composedOutput);

        // Bridge phase: dispose the Graphite command buffer recorded alongside
        // legacy GL calls.  The recorded commands lack bind group bindings and
        // cannot produce correct output yet (Phase 4).
        Graphics.ActiveGraphiteCmdBuffer = null;
        graphiteCmd?.Dispose();

        // Reset bound framebuffer if any is bound
        Graphics.UnbindFramebuffer();
        Graphics.Viewport(0, 0, (uint)Window.InternalWindow.FramebufferSize.X, (uint)Window.InternalWindow.FramebufferSize.Y);
    }

    /// <summary>
    /// Blits the composed scene output to the swapchain using Graphite commands.
    /// Called on non-GL backends (Vulkan) where the legacy GL Blit is a no-op.
    /// </summary>
    private void BlitToSwapchainGraphite(RenderTexture source)
    {
        var device = Graphics.Graphite;
        var swapchainTex = device.GetSwapchainTexture();

        var sourceGraphiteTex = source.frameBuffer.GraphiteColorAttachments is { Length: > 0 }
            ? source.frameBuffer.GraphiteColorAttachments[0]
            : null;
        if (sourceGraphiteTex == null)
            return;

        // Lazy-initialize shared resources
        _graphiteBlitSampler ??= device.CreateSampler(Graphite.SamplerDescriptor.LinearClamp);
        _graphiteBlitTexBGL ??= device.CreateBindGroupLayout(new Graphite.BindGroupLayoutDescriptor(
            Graphite.BindGroupLayoutEntry.CombinedTextureSampler(0, Graphite.ShaderStage.Fragment, name: "_MainTex")));

        // Get or create a bind group for this source texture, disposing the old
        // one when the source changes to avoid accumulating stale GPU resources.
        if (_graphiteBlitLastSourceTex != sourceGraphiteTex)
        {
            _graphiteBlitBindGroup?.Dispose();
            _graphiteBlitBindGroup = device.CreateBindGroup(new Graphite.BindGroupDescriptor(
                _graphiteBlitTexBGL,
                Graphite.BindGroupEntry.ForTextureSampler(0, sourceGraphiteTex, _graphiteBlitSampler)));
            _graphiteBlitLastSourceTex = sourceGraphiteTex;
        }
        var texBindGroup = _graphiteBlitBindGroup!;

        // Resolve the blit shader program
        var blitMat = BlitMaterial;
        var pass = blitMat.Shader.GetPass(0);
        if (!pass.TryGetVariantProgram(blitMat._localKeywords, out var program))
            return;

        // Get the fullscreen quad mesh and its Graphite vertex layout
        var quad = Mesh.GetFullscreenQuad();
        quad.Upload();
        var vao = quad.VertexArrayObject;
        if (vao?.GraphiteVertexLayout == null)
            return;

        // Render pass targeting the swapchain (clear to black, then draw over it)
        var renderPassLayout = new Graphite.RenderPassLayout([swapchainTex.Format]);
        var colorAtt = Graphite.RenderPassColorAttachment.Clear(swapchainTex, Float4.Zero);
        var desc = new Graphite.RenderPassDescriptor
        {
            ColorAttachments = [colorAtt],
        };

        using var cmd = Graphics.CreateCommandBuffer("SwapchainBlit");
        cmd.BeginRenderPass(in desc, renderPassLayout);

        // Vulkan pipelines use dynamic viewport/scissor state — these MUST be set
        // before any draw call or the GPU will read uninitialised dynamic state.
        cmd.SetViewport(0, 0, swapchainTex.Width, swapchainTex.Height);
        cmd.SetScissor(0, 0, swapchainTex.Width, swapchainTex.Height);

        var pipeline = PipelineStateCache.GetOrCreate(
            program, vao.GraphiteVertexLayout.Value, pass.State, Topology.Triangles,
            renderPassLayout, [_graphiteBlitTexBGL]);
        cmd.SetPipeline(pipeline);
        cmd.SetBindGroup(0, texBindGroup);
        cmd.SetMeshBuffers(quad);
        cmd.DrawIndexed((uint)quad.IndexCount);

        cmd.EndRenderPass();
        cmd.Submit();

        Graphics.SwapchainClearedThisFrame = true;
    }

    private void RenderShadowAtlas(CameraSnapshot css, IReadOnlyList<IRenderableLight> lights, IReadOnlyList<IRenderable> renderables)
    {
        // Ensure the shadow atlas texture exists
        ShadowAtlas.TryInitialize();

        // Reset the tile allocator so every frame starts with a full atlas
        ShadowAtlas.Clear();

        var atlas = ShadowAtlas.GetAtlas();

        Graphics.BindFramebuffer(atlas.frameBuffer);
        // Ensure clean state for shadow rendering: depth test/write enabled, no blending.
        // Prevents stale state from image effects or prior passes from corrupting the shadow map.
        Graphics.SetState(new RasterizerState
        {
            DepthTest = true,
            DepthWrite = true,
            Depth = RasterizerState.DepthMode.Lequal,
            DoBlend = false,
            CullFace = RasterizerState.PolyFace.Back,
        }, true);
        Graphics.Clear(0.0f, 0.0f, 0.0f, 1.0f, ClearFlags.Depth);

        // Bridge phase: begin depth-only Graphite render pass for shadow atlas
        var graphiteCmd = Graphics.ActiveGraphiteCmdBuffer;
        if (graphiteCmd != null)
        {
            try { graphiteCmd.BeginDepthOnlyRenderPass(atlas); }
            catch { }
        }

        // Process all lights - each light handles its own shadow rendering
        foreach (IRenderableLight light in lights)
        {
            if (css.CullingMask.HasLayer(light.GetLayer()) == false)
                continue;

            if (light is Light lightComponent)
            {
                lightComponent.RenderShadows(this, css.CameraPosition, renderables);
            }
        }

        // Bridge phase: end shadow atlas Graphite render pass
        if (graphiteCmd?.InRenderPass == true)
        {
            try { graphiteCmd.EndRenderPass(); }
            catch { }
        }
    }

    private void RenderSkybox(CameraSnapshot css)
    {
        // Always set a safe default sun direction to avoid NaN from normalize(vec3(0)) in the shader
        _skybox.SetVector("_SunDir", new Float3(0, -1, 0));

        // Override with the actual directional light direction if one exists
        var sun = css.Scene.Lights.FirstOrDefault(l => l is IRenderableLight rl && rl.GetLightType() == LightType.Directional);
        if (sun != null)
        {
            _skybox.SetVector("_SunDir", sun.GetLightDirection());
        }

        DrawMeshNow(_skyDome, _skybox);
    }

    private void RenderGizmos(CameraSnapshot css)
    {
        Float4x4 vp = css.Projection * css.View;
        (Mesh? wire, Mesh? solid) = Debug.GetGizmoDrawData();

        if (wire.IsValid() || solid.IsValid())
        {
            if (wire.IsValid()) DrawMeshNow(wire, _gizmo);
            if (solid.IsValid()) DrawMeshNow(solid, _gizmo);
        }

#warning TODO: Implement Gizmo Icons rendering

        //List<GizmoBuilder.IconDrawCall> icons = Debug.GetGizmoIcons();
        //if (icons != null)
        //{
        //    buffer.SetMaterial(s_gizmo);
        //
        //    foreach (GizmoBuilder.IconDrawCall icon in icons)
        //    {
        //        Vector3 center = icon.center;
        //        Matrix4x4 billboard = Matrix4x4.CreateBillboard(center, Vector3.zero, css.cameraUp, css.cameraForward);
        //
        //        buffer.SetMatrix("_Matrix_VP", (billboard * vp).ToFloat());
        //        buffer.SetTexture("_MainTex", icon.texture);
        //
        //        buffer.DrawSingle(s_quadMesh);
        //    }
        //}
    }

    #endregion
}
