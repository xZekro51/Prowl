// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.EventSystem;
using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Profiling;
using Prowl.Runtime.Rendering.GI;
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

    // Graphite resources for swapchain blit
    private Graphite.Sampler? _graphiteBlitSampler;
    private Graphite.BindGroupLayout? _graphiteBlitTexBGL;
    private Graphite.Texture? _graphiteBlitLastSourceTex;
    private Graphite.BindGroup? _graphiteBlitBindGroup;

    // Event subscription handle for swapchain recreation cleanup
    private IDisposable? _swapchainSub;

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

        _swapchainSub = GraphiteDeviceEvents.SubscribeOnSwapchainRecreated(_ =>
        {
            _graphiteBlitBindGroup?.Dispose();
            _graphiteBlitBindGroup = null;
            _graphiteBlitLastSourceTex = null;
        }, priority: 0);
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

        base.Render(camera, in data);
    }

    #endregion

    #region Scene Rendering

    private void Internal_Render(Camera camera, in RenderingData data)
    {
        Profiler.BeginSection("Pipeline.Render");

        // Skip rendering entirely if the GPU device has been lost.
        // Attempting to record or submit commands after device lost would
        // produce cascading errors and crash the editor.
        if (Graphics.IsGraphiteReady && Graphics.Graphite.IsDeviceLost)
        {
            Profiler.EndSection();
            return;
        }

        // =======================================================
        // 0. Setup variables, and prepare the camera
        bool isHDR = camera.HDR;

        IReadOnlyList<IRenderableLight> lights = camera.GameObject.Scene.Lights;
        RenderTexture target = camera.UpdateRenderData();

        // Create Graphite command buffer for rendering (used on both backends).
        RenderCommandBuffer? graphiteCmd = null;
        if (Graphics.IsGraphiteReady)
        {
            graphiteCmd = Graphics.CreateCommandBuffer("DefaultPipeline");
        }
        Graphics.ActiveGraphiteCmdBuffer = graphiteCmd;

        // =======================================================
        // 1. Pre Cull
        foreach (ImageEffect effect in camera.Effects)
            effect.OnPreCull(camera);

        // =======================================================
        // 2. Take a snapshot of all Camera data
        CameraSnapshot css = new(camera);
        SetupGlobalUniforms(css);

        RenderingEvents.InvokeOnCameraRenderBegin(new CameraRenderBeginArgs(css.PixelWidth, css.PixelHeight, graphiteCmd));

        // =======================================================
        // 3. Cull Renderables based on Snapshot data
        Profiler.BeginSection("Pipeline.Cull");
        IReadOnlyList<IRenderable> renderables = camera.GameObject.Scene.Renderables;
        HashSet<int> culledRenderableIndices = CullRenderables(renderables, css.WorldFrustum, css.CullingMask);
        Profiler.EndSection();

        // =======================================================
        // 4. Pre Render
        foreach (ImageEffect effect in camera.Effects)
            effect.OnPreRender(camera);

        // =======================================================
        // 5. Setup Lighting and Shadows
        Profiler.BeginSection("Pipeline.Shadows");
        graphiteCmd?.PushDebugGroup("Stage5_ShadowAtlas");
        RenderShadowAtlas(css, lights, renderables);
        graphiteCmd?.PopDebugGroup();
        Profiler.EndSection(); // Pipeline.Shadows

        // 5.1 Re-Assign camera matrices (The Lighting can modify these)
        AssignCameraMatrices(css.View, css.Projection);

        // 5.2 Global Illumination: voxelize scene (VoxelGI) or update SDF/probes (SDFGI)
        Profiler.BeginSection("Pipeline.GI");
        graphiteCmd?.PushDebugGroup("Stage5.2_GlobalIllumination");
        Scene.GlobalIlluminationParams giParams = css.Scene.GlobalIllumination;
        (Scene.GlobalIlluminationParams.GIMode giMode, float giIntensity) = GIUtils.ResolveGISettings(css.Scene, lights);

        RenderingEvents.InvokeOnGIPassBegin(new GIPassBeginArgs(
            giMode, giIntensity, giParams, renderables, culledRenderableIndices, css, lights));

        // Re-assign camera matrices after GI data update (voxelization may modify them)
        AssignCameraMatrices(css.View, css.Projection);

        RenderingEvents.InvokeOnGIPassEnd(new GIPassEndArgs(giMode));
        graphiteCmd?.PopDebugGroup(); // Stage5.2_GlobalIllumination
        Profiler.EndSection(); // Pipeline.GI

        // =======================================================
        // 6. Create GBuffer for Deferred Rendering
        //
        // All three major temporary render textures (gBuffer, lightAccumulation,
        // composedOutput) are wrapped in a try-finally to guarantee they are
        // returned to the pool even when an exception interrupts the pipeline.
        RenderTexture? gBuffer = null;
        RenderTexture? lightAccumulation = null;
        RenderTexture? composedOutput = null;
        try
        {

        Profiler.BeginSection("Pipeline.GBuffer");
        // GBuffer layout:
        // BufferA: RGB = Albedo, A = Alpha
        // BufferB: RGB = Normal (view space), A = ShadingMode
        // BufferC: R = Roughness, G = Metalness, B = Specular, A = AO
        // BufferD: Custom Data per Shading Mode (e.g., Emissive for Lit mode)
        gBuffer = RenderTexture.GetTemporaryRT((int)css.PixelWidth, (int)css.PixelHeight, true, [
            Asset.GBufferAlbedoFormat, // BufferA - Albedo + Alpha
            Asset.GBufferNormalFormat, // BufferB - Normal + ShadingMode
            Asset.GBufferPBRFormat,    // BufferC - Roughness, Metalness, Specular, AO
            Asset.GBufferCustomFormat, // BufferD - Custom Data (Emissive, etc.)
            ]);

        // 6.0 BeforeGBuffer image effects (e.g., screen-space setup from previous frame data)
        RenderingEvents.InvokeOnImageEffectsDispatch(new ImageEffectsDispatchArgs(
            RenderStage.BeforeGBuffer, new RenderContext
            {
                GBuffer = gBuffer,
                Camera = camera,
                Width = (int)css.PixelWidth,
                Height = (int)css.PixelHeight,
                CurrentStage = RenderStage.BeforeGBuffer,
                CommandBuffer = graphiteCmd
            }));

        // Begin Graphite render pass for GBuffer (clear handled by LoadOp.Clear)
        graphiteCmd?.PushDebugGroup("Stage6_GBuffer");
        RenderingEvents.InvokeOnGBufferPassBegin(new GBufferPassArgs(gBuffer, graphiteCmd));
        if (graphiteCmd != null)
        {
            var clearFloat4 = new Float4(
                (float)camera.ClearColor.R,
                (float)camera.ClearColor.G,
                (float)camera.ClearColor.B,
                (float)camera.ClearColor.A);
            var loadOp = camera.ClearFlags == CameraClearFlags.Nothing
                ? Graphite.LoadOp.DontCare
                : Graphite.LoadOp.Clear;
            graphiteCmd.BeginRenderPass(gBuffer, loadOp, clearFloat4, camera.ClearFlags != CameraClearFlags.Nothing, hintNextUsageShaderRead: true);
            graphiteCmd.SetViewport(0, 0, gBuffer.Width, gBuffer.Height);
            graphiteCmd.SetScissor(0, 0, (uint)gBuffer.Width, (uint)gBuffer.Height);
        }

        // Render skybox (works on both backends via DrawMeshNow)
        if (camera.ClearFlags == CameraClearFlags.Skybox && css.Scene.Skybox.Enabled)
            RenderSkybox(css);

        // 6.2 Draw opaque geometry to GBuffer
        //List<IRenderable> sortFrontToBack = SortRenderables(renderables, culledRenderableIndices, css.CameraPosition, SortMode.FrontToBack);
        DrawRenderables(renderables, "RenderOrder", "Opaque", new ViewerData(css), culledRenderableIndices, false); // Its deffered rendering, overdraw is cheap

        // End GBuffer Graphite render pass
        if (graphiteCmd?.InRenderPass == true)
            graphiteCmd.EndRenderPass();

        // Transition GBuffer attachments to ShaderResource for lighting sampling
        TransitionToShaderResource(gBuffer);
        graphiteCmd?.PopDebugGroup(); // Stage6_GBuffer
        RenderingEvents.InvokeOnGBufferPassEnd(new GBufferPassArgs(gBuffer, graphiteCmd));
        Profiler.EndSection(); // Pipeline.GBuffer

        // 6.1 AfterGBuffer image effects (e.g., modifying surface properties before lighting)
        RenderingEvents.InvokeOnImageEffectsDispatch(new ImageEffectsDispatchArgs(
            RenderStage.AfterGBuffer, new RenderContext
            {
                GBuffer = gBuffer,
                Camera = camera,
                Width = (int)css.PixelWidth,
                Height = (int)css.PixelHeight,
                CurrentStage = RenderStage.AfterGBuffer,
                CommandBuffer = graphiteCmd
            }));

        // =======================================================
        // 7. Deferred Lighting Pass - Render each light's contribution
        Profiler.BeginSection("Pipeline.Lighting");
        // Create light accumulation buffer
        lightAccumulation = RenderTexture.GetTemporaryRT((int)camera.PixelWidth, (int)camera.PixelHeight, false, [
            isHDR ? TextureImageFormat.Short4 : TextureImageFormat.Color4b, // Accumulated lighting
            ]);

        // Set GBuffer textures as global textures for shaders
        PropertyState.SetGlobalTexture("_GBufferA", gBuffer.InternalTextures[0]);
        PropertyState.SetGlobalTexture("_GBufferB", gBuffer.InternalTextures[1]);
        PropertyState.SetGlobalTexture("_GBufferC", gBuffer.InternalTextures[2]);
        PropertyState.SetGlobalTexture("_GBufferD", gBuffer.InternalTextures[3]);
        PropertyState.SetGlobalTexture("_CameraDepthTexture", gBuffer.InternalDepth);

        // Begin Graphite render pass for light accumulation
        graphiteCmd?.PushDebugGroup("Stage7_DeferredLighting");
        RenderingEvents.InvokeOnLightingPassBegin(new LightingPassArgs(gBuffer, lightAccumulation, lights.Count, graphiteCmd));
        if (graphiteCmd != null)
        {
            graphiteCmd.BeginRenderPass(lightAccumulation, Graphite.LoadOp.Clear, Float4.Zero, false, hintNextUsageShaderRead: true);
            // Use SetViewportRaw (no Y-flip) for fullscreen passes that sample GBuffer.
            // GBuffer was rendered with Y-flip, storing scene top at texture row 0.
            // Without Y-flip, NDC (-1,-1) maps to framebuffer top, UV (0,0) samples row 0 = scene top.
            graphiteCmd.SetViewportRaw(0, 0, lightAccumulation.Width, lightAccumulation.Height);
            graphiteCmd.SetScissor(0, 0, (uint)lightAccumulation.Width, (uint)lightAccumulation.Height);
        }

        // Render each light's contribution (additive blending)
        int renderedLightCount = 0;
        bool skipDirectLights = GIDebugView.ActiveMode == GIDebugMode.IndirectOnly;
        if (!skipDirectLights)
        {
            foreach (IRenderableLight light in lights)
            {
                if (css.CullingMask.HasLayer(light.GetLayer()) == false)
                    continue;

                light.OnRenderLight(gBuffer, lightAccumulation, css);
                renderedLightCount++;
            }
        }

        // End lighting Graphite render pass
        if (graphiteCmd?.InRenderPass == true)
            graphiteCmd.EndRenderPass();

        // Transition light accumulation to ShaderResource for compose/effects sampling
        TransitionToShaderResource(lightAccumulation);
        graphiteCmd?.PopDebugGroup(); // Stage7_DeferredLighting
        RenderingEvents.InvokeOnLightingPassEnd(new LightingPassArgs(gBuffer, lightAccumulation, renderedLightCount, graphiteCmd));
        Profiler.EndSection(); // Pipeline.Lighting

        // 7.1 Global Illumination: cone trace (VoxelGI) or probe lookup (SDFGI) into light accumulation
        // 7.2 Temporal filtering is applied by GISystemManager via OnGITracePass subscriber
        Profiler.BeginSection("Pipeline.GI.Trace");
        graphiteCmd?.PushDebugGroup("Stage7.1_GIConeTrace");
        RenderingEvents.InvokeOnGITracePass(new GITracePassArgs(
            giMode, giIntensity, giParams.ConeCount, gBuffer, lightAccumulation, css));
        graphiteCmd?.PopDebugGroup(); // Stage7.1_GIConeTrace

        // 7.3 GI Debug Visualization — override output if debug mode is active
        GIDebugMode debugMode = GIDebugView.ActiveMode;
        if (debugMode != GIDebugMode.None)
        {
            RenderingEvents.InvokeOnGIDebugVisualize(new GIDebugVisualizeArgs(
                giMode, debugMode, gBuffer, lightAccumulation, css));
        }
        Profiler.EndSection(); // Pipeline.GI.Trace

        // =======================================================
        // 7.5. Apply DuringLighting effects (e.g., SSPT, GTAO that need light accumulation)
        RenderingEvents.InvokeOnImageEffectsDispatch(new ImageEffectsDispatchArgs(
            RenderStage.DuringLighting, new RenderContext
            {
                GBuffer = gBuffer,
                LightAccumulation = lightAccumulation,
                SceneColor = null, // Not available yet
                Camera = camera,
                Width = (int)css.PixelWidth,
                Height = (int)css.PixelHeight,
                CurrentStage = RenderStage.DuringLighting,
                CommandBuffer = graphiteCmd
            }));

        // =======================================================
        // 8. Deferred Composition Pass - Combine light accumulation with GBuffer
        Profiler.BeginSection("Pipeline.Compose");
        graphiteCmd?.PushDebugGroup("Stage8_Composition");
        // Create final composition output
        composedOutput = RenderTexture.GetTemporaryRT((int)camera.PixelWidth, (int)camera.PixelHeight, true, [
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

        // Suppress ambient when IndirectOnly debug mode is active so only GI
        // contribution is visible in the final output.
        float ambientStrength = skipDirectLights ? 0.0f : (float)ambient.Strength;
        _deferredCompose.SetFloat("_AmbientStrength", ambientStrength);

        // Set GI active flag for composition shader — only suppress ambient
        // when the GI system has actually produced valid indirect lighting data.
        bool giHasData = GISystemManager.HasValidData(giMode);
        _deferredCompose.SetFloat("_GIActive", giHasData ? 1.0f : 0.0f);

        // Begin Graphite render pass for composition
        if (graphiteCmd != null)
        {
            graphiteCmd.BeginRenderPass(composedOutput, Graphite.LoadOp.Clear, Float4.Zero, true, hintNextUsageShaderRead: true);
            // Use SetViewportRaw (no Y-flip) for fullscreen passes that sample previous render targets
            graphiteCmd.SetViewportRaw(0, 0, composedOutput.Width, composedOutput.Height);
            graphiteCmd.SetScissor(0, 0, (uint)composedOutput.Width, (uint)composedOutput.Height);
        }

        // Perform composition
        Blit(lightAccumulation, composedOutput, _deferredCompose, 0, false, false);

        // End compose Graphite render pass
        if (graphiteCmd?.InRenderPass == true)
            graphiteCmd.EndRenderPass();

        // Copy depth from GBuffer to composed output for transparent rendering
        var srcDepth = gBuffer.GraphiteDepthTexture;
        var dstDepth = composedOutput.GraphiteDepthTexture;
        if (srcDepth != null && dstDepth != null && graphiteCmd != null)
        {
            graphiteCmd.ResourceBarriers([
                new Graphite.ResourceBarrier(srcDepth, Graphite.ResourceState.DepthWrite, Graphite.ResourceState.CopySource),
                new Graphite.ResourceBarrier(dstDepth, Graphite.ResourceState.DepthWrite, Graphite.ResourceState.CopyDestination),
            ]);

            graphiteCmd.CopyTextureToTexture(new Graphite.TextureTextureCopy
            {
                Source = srcDepth,
                Destination = dstDepth,
                Width = (uint)gBuffer.Width,
                Height = (uint)gBuffer.Height,
                Depth = 1,
            });

            graphiteCmd.ResourceBarriers([
                new Graphite.ResourceBarrier(srcDepth, Graphite.ResourceState.CopySource, Graphite.ResourceState.ShaderResource),
                new Graphite.ResourceBarrier(dstDepth, Graphite.ResourceState.CopyDestination, Graphite.ResourceState.DepthWrite),
            ]);
        }

        // Transition composedOutput to ShaderResource for AfterLighting effects
        // that may sample it via manually-set textures in Blit(target, mat)
        TransitionToShaderResource(composedOutput);
        graphiteCmd?.PopDebugGroup(); // Stage8_Composition
        RenderingEvents.InvokeOnCompositionComplete(new CompositionCompleteArgs(composedOutput, gBuffer, graphiteCmd));
        Profiler.EndSection(); // Pipeline.Compose

        // =======================================================
        // 9. Apply AfterLighting effects (opaque post-processing)
        RenderingEvents.InvokeOnImageEffectsDispatch(new ImageEffectsDispatchArgs(
            RenderStage.AfterLighting, new RenderContext
            {
                GBuffer = gBuffer,
                LightAccumulation = lightAccumulation,
                SceneColor = composedOutput,
                Camera = camera,
                Width = (int)css.PixelWidth,
                Height = (int)css.PixelHeight,
                CurrentStage = RenderStage.AfterLighting,
                CommandBuffer = graphiteCmd
            }));

        // =======================================================
        // 10. Transparent geometry (Forward rendered on top of composed result)
        Profiler.BeginSection("Pipeline.Transparents");
        graphiteCmd?.PushDebugGroup("Stage10_Transparents");
        RenderingEvents.InvokeOnTransparentPassBegin(new TransparentPassArgs(composedOutput, graphiteCmd));

        // Upload forward lighting globals so transparent shaders can evaluate
        // a single directional light + ambient without reading the GBuffer.
        SetupForwardLightGlobals(lights, css);

        // Begin Graphite render pass for forward transparent (load existing content)
        // Ensure composedOutput is in RenderTarget state for LoadOp.Load
        TransitionToRenderTarget(composedOutput);
        if (graphiteCmd != null)
        {
            graphiteCmd.BeginRenderPass(composedOutput, Graphite.LoadOp.Load, null, false);
            graphiteCmd.SetViewport(0, 0, composedOutput.Width, composedOutput.Height);
            graphiteCmd.SetScissor(0, 0, (uint)composedOutput.Width, (uint)composedOutput.Height);
        }

        List<IRenderable> sortBackToFront = SortRenderables(renderables, culledRenderableIndices, css.CameraPosition, SortMode.BackToFront);
        DrawRenderables(sortBackToFront, "RenderOrder", "Transparent", new ViewerData(css), null, false);

        // End forward transparent Graphite render pass
        if (graphiteCmd?.InRenderPass == true)
            graphiteCmd.EndRenderPass();
        graphiteCmd?.PopDebugGroup(); // Stage10_Transparents
        Profiler.EndSection(); // Pipeline.Transparents

        // Transition composedOutput to ShaderResource for PostProcess effects
        TransitionToShaderResource(composedOutput);

        // =======================================================
        // 11. Apply PostProcess effects (final post-processing)
        Profiler.BeginSection("Pipeline.PostProcess");
        {
            var postProcessContext = new RenderContext
            {
                GBuffer = gBuffer,
                LightAccumulation = lightAccumulation,
                SceneColor = composedOutput,
                Camera = camera,
                Width = (int)css.PixelWidth,
                Height = (int)css.PixelHeight,
                CurrentStage = RenderStage.PostProcess,
                CommandBuffer = graphiteCmd
            };

            RenderingEvents.InvokeOnImageEffectsDispatch(new ImageEffectsDispatchArgs(
                RenderStage.PostProcess, postProcessContext));

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
        Profiler.EndSection(); // Pipeline.PostProcess

        // =======================================================
        // 12. Render Gizmos (needs an active render pass on composedOutput for Vulkan)
        Profiler.BeginSection("Pipeline.Gizmos");
        graphiteCmd?.PushDebugGroup("Stage12_Gizmos");
        // Ensure composedOutput is in RenderTarget state for LoadOp.Load
        TransitionToRenderTarget(composedOutput);
        if (graphiteCmd != null)
        {
            graphiteCmd.BeginRenderPass(composedOutput, Graphite.LoadOp.Load, null, false);
            graphiteCmd.SetViewport(0, 0, composedOutput.Width, composedOutput.Height);
            graphiteCmd.SetScissor(0, 0, (uint)composedOutput.Width, (uint)composedOutput.Height);
        }

        RenderGizmos(css);

        if (graphiteCmd?.InRenderPass == true)
            graphiteCmd.EndRenderPass();
        graphiteCmd?.PopDebugGroup(); // Stage12_Gizmos
        Profiler.EndSection(); // Pipeline.Gizmos

        // =======================================================
        // 13. Camera Render End — fire while the command buffer is still alive
        // so GPU profiler handlers can insert final timestamp queries.
        RenderingEvents.InvokeOnCameraRenderEnd(new CameraRenderEndArgs(target == null, graphiteCmd));

        // =======================================================
        // 14. Blit Result to target, If target is null Blit will go to the Screen/Window
        Profiler.BeginSection("Pipeline.Blit");
        if (target == null)
        {
            // Swapchain blit needs a dedicated command buffer because the
            // swapchain texture is acquired separately. Submit the main
            // pipeline first so composedOutput is ready.
            graphiteCmd?.PushDebugGroup("Stage14_BlitToSwapchain");
            graphiteCmd?.PopDebugGroup();
            Graphics.ActiveGraphiteCmdBuffer = null;
            if (graphiteCmd != null)
            {
                graphiteCmd.Submit();
                graphiteCmd.Dispose();
                graphiteCmd = null;
            }
            BlitToSwapchainGraphite(composedOutput);
        }
        else if (target.IsValid())
        {
            // Append the blit to the main command buffer so all GPU work
            // for this camera is in a single submission. This avoids a
            // data race where a later camera's command buffer overwrites
            // the temporary composedOutput texture before an earlier,
            // separate blit submission finishes reading it.
            graphiteCmd?.PushDebugGroup("Stage14_BlitToTarget");
            BlitToRenderTargetGraphite(composedOutput, target, graphiteCmd);
            graphiteCmd?.PopDebugGroup();
            Graphics.ActiveGraphiteCmdBuffer = null;
            if (graphiteCmd != null)
            {
                graphiteCmd.Submit();
                graphiteCmd.Dispose();
                graphiteCmd = null;
            }
        }
        else
        {
            // No target — just submit the main cmd
            Graphics.ActiveGraphiteCmdBuffer = null;
            if (graphiteCmd != null)
            {
                graphiteCmd.Submit();
                graphiteCmd.Dispose();
                graphiteCmd = null;
            }
        }

        Profiler.EndSection(); // Pipeline.Blit

        // =======================================================
        // 15. Post Render
        foreach (ImageEffect effect in camera.Effects)
            effect.OnPostRender(camera);

        } // end try
        finally
        {
            // =======================================================
            // 16. Cleanup temporary render textures
            // Placed in finally so RTs are returned to the pool even when an
            // exception interrupts the pipeline mid-render.
            if (gBuffer != null) RenderTexture.ReleaseTemporaryRT(gBuffer);
            if (lightAccumulation != null) RenderTexture.ReleaseTemporaryRT(lightAccumulation);
            if (composedOutput != null) RenderTexture.ReleaseTemporaryRT(composedOutput);
        }

        Profiler.EndSection(); // Pipeline.Render
    }

    /// <summary>
    /// Uploads global uniforms for forward-rendered transparent objects.
    /// Exposes the primary directional light and ambient data so transparent
    /// shaders can evaluate simple PBR lighting without the deferred GBuffer.
    /// </summary>
    private static void SetupForwardLightGlobals(IReadOnlyList<IRenderableLight> lights, CameraSnapshot css)
    {
        // Find the primary directional light (first one found)
        DirectionalLight? primaryDir = null;
        foreach (IRenderableLight light in lights)
        {
            if (light is DirectionalLight dl) { primaryDir = dl; break; }
        }

        if (primaryDir != null)
        {
            PropertyState.SetGlobalVector("_ForwardLightDir", primaryDir.Transform.Forward);
            Float3 lightCol = new((float)primaryDir.Color.R, (float)primaryDir.Color.G, (float)primaryDir.Color.B);
            PropertyState.SetGlobalVector("_ForwardLightColor", lightCol * primaryDir.Intensity);
        }
        else
        {
            PropertyState.SetGlobalVector("_ForwardLightDir", new Float3(0f, -1f, 0f));
            PropertyState.SetGlobalVector("_ForwardLightColor", Float3.Zero);
        }

        // Ambient parameters
        Scene.AmbientLightParams ambient = css.Scene.Ambient;
        PropertyState.SetGlobalVector("_ForwardAmbientColor",
            new Float3((float)ambient.Color.R, (float)ambient.Color.G, (float)ambient.Color.B));
        PropertyState.SetGlobalFloat("_ForwardAmbientStrength", (float)ambient.Strength);
    }

    /// <summary>
    /// Blits the composed scene output to the swapchain using Graphite commands.
    /// Called when the camera renders directly to the screen (no render target).
    /// </summary>
    private void BlitToSwapchainGraphite(RenderTexture source)
    {
        var device = Graphics.Graphite;
        var swapchainTex = device.GetSwapchainTexture();

        var sourceGraphiteTex = source.GraphiteColorTextures is { Length: > 0 }
            ? source.GraphiteColorTextures[0]
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

        // Transition the source texture to ShaderReadOnlyOptimal before sampling.
        // Without this barrier the texture layout may still be Undefined or
        // ColorAttachmentOptimal, causing the fragment shader to read garbage.
        cmd.ResourceBarrier(new Graphite.ResourceBarrier(
            sourceGraphiteTex, Graphite.ResourceState.RenderTarget, Graphite.ResourceState.ShaderResource));

        cmd.BeginRenderPass(in desc, renderPassLayout);

        // Vulkan pipelines use dynamic viewport/scissor state — these MUST be set
        // before any draw call or the GPU will read uninitialised dynamic state.
        // Use SetViewportRaw for the swapchain blit to avoid double Y-flip
        // (the source texture was already rendered with Y-flip applied).
        cmd.SetViewportRaw(0, 0, swapchainTex.Width, swapchainTex.Height);
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

    /// <summary>
    /// Blits the composed scene output to a camera render target using Graphite commands.
    /// Called when the camera renders to a texture instead of the swapchain.
    /// </summary>
    private void BlitToRenderTargetGraphite(RenderTexture source, RenderTexture target, RenderCommandBuffer? existingCmd)
    {
        var sourceGraphiteTex = source.GraphiteColorTextures is { Length: > 0 }
            ? source.GraphiteColorTextures[0]
            : null;
        if (sourceGraphiteTex == null)
            return;

        var device = Graphics.Graphite;

        // Lazy-initialize shared resources
        _graphiteBlitSampler ??= device.CreateSampler(Graphite.SamplerDescriptor.LinearClamp);
        _graphiteBlitTexBGL ??= device.CreateBindGroupLayout(new Graphite.BindGroupLayoutDescriptor(
            Graphite.BindGroupLayoutEntry.CombinedTextureSampler(0, Graphite.ShaderStage.Fragment, name: "_MainTex")));

        // Create a bind group for this source texture
        var texBindGroup = device.CreateBindGroup(new Graphite.BindGroupDescriptor(
            _graphiteBlitTexBGL,
            Graphite.BindGroupEntry.ForTextureSampler(0, sourceGraphiteTex, _graphiteBlitSampler)));
        GraphiteMaterialBinder.Retire(texBindGroup);

        // Resolve the blit shader program
        var blitMat = BlitMaterial;
        var pass = blitMat.Shader.GetPass(0);
        if (!pass.TryGetVariantProgram(blitMat._localKeywords, out var program))
            return;

        var quad = Mesh.GetFullscreenQuad();
        quad.Upload();
        var vao = quad.VertexArrayObject;
        if (vao?.GraphiteVertexLayout == null)
            return;

        var renderPassLayout = GraphiteFormatMapper.MapRenderPassLayout(target);

        // Use the existing command buffer if provided, otherwise create a new one
        var cmd = existingCmd ?? Graphics.CreateCommandBuffer("RenderTargetBlit");
        bool ownCmd = existingCmd == null;

        cmd.ResourceBarrier(new Graphite.ResourceBarrier(
            sourceGraphiteTex, Graphite.ResourceState.RenderTarget, Graphite.ResourceState.ShaderResource));

        // clearDepth must be true: this is a color-only blit that doesn't need
        // depth contents.  Using false would set DepthLoadOp=Load which requires
        // initialLayout=DepthStencilAttachmentOptimal, but on the first frame
        // (or after RT resize) the depth texture is still in Undefined layout,
        // causing a Vulkan layout mismatch ? ErrorDeviceLost.
        cmd.BeginRenderPass(target, Graphite.LoadOp.Clear, Float4.Zero, true);
        // Use SetViewportRaw (no Y-flip) for fullscreen blits that sample render targets
        cmd.SetViewportRaw(0, 0, target.Width, target.Height);
        cmd.SetScissor(0, 0, (uint)target.Width, (uint)target.Height);

        var pipeline = PipelineStateCache.GetOrCreate(
            program, vao.GraphiteVertexLayout.Value, pass.State, Topology.Triangles,
            renderPassLayout, [_graphiteBlitTexBGL]);
        cmd.SetPipeline(pipeline);
        cmd.SetBindGroup(0, texBindGroup);
        cmd.SetMeshBuffers(quad);
        cmd.DrawIndexed((uint)quad.IndexCount);

        cmd.EndRenderPass();

        // Transition the target color attachment to ShaderResource so downstream
        // consumers (e.g. ImGui) can sample it without a layout mismatch.
        var targetGraphiteTex = target.GraphiteColorTextures is { Length: > 0 }
            ? target.GraphiteColorTextures[0]
            : null;
        if (targetGraphiteTex != null)
        {
            cmd.ResourceBarrier(new Graphite.ResourceBarrier(
                targetGraphiteTex, Graphite.ResourceState.RenderTarget, Graphite.ResourceState.ShaderResource));
        }

        if (ownCmd)
        {
            cmd.Submit();
            cmd.Dispose();
        }
    }

    /// <summary>
    /// Fallback: clears the swapchain to the camera's clear color when
    /// the scene rendering pipeline cannot be used.
    /// </summary>
    private void ClearSwapchainFallback(Camera camera)
    {
        if (!Graphics.IsGraphiteReady)
            return;

        try
        {
            var device = Graphics.Graphite;
            var swapchainTex = device.GetSwapchainTexture();

            var clearColor = camera.ClearFlags == CameraClearFlags.Nothing
                ? Float4.Zero
                : new Float4(
                    (float)camera.ClearColor.R,
                    (float)camera.ClearColor.G,
                    (float)camera.ClearColor.B,
                    (float)camera.ClearColor.A);

            var colorAtt = Graphite.RenderPassColorAttachment.Clear(swapchainTex, clearColor);
            var desc = new Graphite.RenderPassDescriptor
            {
                ColorAttachments = [colorAtt],
            };
            var renderPassLayout = new Graphite.RenderPassLayout([swapchainTex.Format]);

            using var cmd = Graphics.CreateCommandBuffer("VulkanFallbackClear");
            cmd.BeginRenderPass(in desc, renderPassLayout);
            cmd.SetViewport(0, 0, swapchainTex.Width, swapchainTex.Height);
            cmd.SetScissor(0, 0, swapchainTex.Width, swapchainTex.Height);
            cmd.EndRenderPass();
            cmd.Submit();

            Graphics.SwapchainClearedThisFrame = true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[DefaultRenderPipeline] Vulkan fallback clear failed: {e.Message}");
        }
    }

    private void RenderShadowAtlas(CameraSnapshot css, IReadOnlyList<IRenderableLight> lights, IReadOnlyList<IRenderable> renderables)
    {
        // Ensure the shadow atlas texture exists
        ShadowAtlas.TryInitialize();

        // Reset the tile allocator so every frame starts with a full atlas
        ShadowAtlas.Clear();

        var atlas = ShadowAtlas.GetAtlas();

        // Begin depth-only Graphite render pass for shadow atlas
        var graphiteCmd = Graphics.ActiveGraphiteCmdBuffer;
        if (graphiteCmd != null)
        {
            graphiteCmd.BeginDepthOnlyRenderPass(atlas);
            // Use raw viewport (no Y-flip) for the shadow atlas. The atlas is
            // self-contained: depth is written using the light's VP matrix and
            // sampled in the lighting pass with the same matrix. Applying a
            // Y-flip here would break the UV ? depth mapping.
            graphiteCmd.SetViewportRaw(0, 0, atlas.Width, atlas.Height);
            graphiteCmd.SetScissor(0, 0, (uint)atlas.Width, (uint)atlas.Height);
        }

        // Process all lights - each light handles its own shadow rendering
        foreach (IRenderableLight light in lights)
        {
            if (css.CullingMask.HasLayer(light.GetLayer()) == false)
                continue;

            if (light is Light lightComponent)
            {
                Profiler.BeginSection(Profiler.GetDeepSectionName(lightComponent.GetType(), "Shadows"));
                lightComponent.RenderShadows(this, css.CameraPosition, renderables);
                Profiler.EndSection();
            }
        }

        // End shadow atlas Graphite render pass
        if (graphiteCmd?.InRenderPass == true)
            graphiteCmd.EndRenderPass();

        // Transition shadow atlas depth to ShaderResource for shadow sampling
        TransitionToShaderResource(atlas);
    }

    private void RenderSkybox(CameraSnapshot css)
    {
        // Always set a safe default sun direction to avoid NaN from normalize(vec3(0)) in the shader
        _skybox.SetVector("_SunDir", new Float3(0, -1, 0));

        // Override with the actual directional light direction if one exists (no LINQ allocation)
        IReadOnlyList<IRenderableLight> lights = css.Scene.Lights;
        for (int i = 0; i < lights.Count; i++)
        {
            if (lights[i].GetLightType() == LightType.Directional)
            {
                _skybox.SetVector("_SunDir", lights[i].GetLightDirection());
                break;
            }
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

    public override void OnDispose()
    {
        _swapchainSub?.Dispose();
        _swapchainSub = null;

        _graphiteBlitSampler?.Dispose();
        _graphiteBlitTexBGL?.Dispose();
        _graphiteBlitBindGroup?.Dispose();
    }
}
