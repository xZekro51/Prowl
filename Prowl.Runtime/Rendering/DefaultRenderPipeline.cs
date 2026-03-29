// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

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

    // Reusable per-frame collections to avoid GC pressure in GatherImageEffects
    private readonly Dictionary<RenderStage, List<ImageEffect>> _reusableEffectsByStage = new()
    {
        { RenderStage.BeforeGBuffer, new List<ImageEffect>() },
        { RenderStage.AfterGBuffer, new List<ImageEffect>() },
        { RenderStage.DuringLighting, new List<ImageEffect>() },
        { RenderStage.AfterLighting, new List<ImageEffect>() },
        { RenderStage.PostProcess, new List<ImageEffect>() }
    };
    private readonly List<ImageEffect> _reusableAllEffects = [];

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

        // Clear reusable effect buffers after rendering
        _reusableAllEffects.Clear();
        foreach (var list in _reusableEffectsByStage.Values)
            list.Clear();

        PropertyState.ClearGlobals();

        // Publish per-frame render stats
        RenderStats.Instance.SwapFrames();

        base.Render(camera, in data);
    }

    private Dictionary<RenderStage, List<ImageEffect>> GatherImageEffects(Camera camera)
    {
        // Clear reusable lists instead of allocating new ones
        foreach (var list in _reusableEffectsByStage.Values)
            list.Clear();

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

            _reusableEffectsByStage[stage].Add(effect);
        }

        return _reusableEffectsByStage;
    }

    private void ExecuteImageEffects(RenderContext context, List<ImageEffect> effects)
    {
        if (effects == null || effects.Count == 0)
            return;

        foreach (var effect in effects)
        {
            try
            {
                effect.OnRenderEffect(context);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Image effect {effect.GetType().Name} threw: {ex}");
            }
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
        _reusableAllEffects.Clear();
        foreach (var effects in effectsByStage.Values)
            _reusableAllEffects.AddRange(effects);
        var allEffects = _reusableAllEffects;

        IReadOnlyList<IRenderableLight> lights = camera.GameObject.Scene.Lights;
        RenderTexture target = camera.UpdateRenderData();

        // Create Graphite command buffer for rendering.
        // On Vulkan this is the primary rendering path; on OpenGL it records
        // parallel commands alongside legacy GL calls.
        RenderCommandBuffer? graphiteCmd = null;
        if (Graphics.IsGraphiteReady)
        {
            graphiteCmd = Graphics.CreateCommandBuffer("DefaultPipeline");
        }
        Graphics.ActiveGraphiteCmdBuffer = graphiteCmd;
        bool isVulkan = !Graphics.IsOpenGL;

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
        if (!isVulkan)
            Graphics.BindFramebuffer(gBuffer.frameBuffer);

        // Begin Graphite render pass for GBuffer (clear handled by LoadOp.Clear)
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
            graphiteCmd.BeginRenderPass(gBuffer, loadOp, clearFloat4, camera.ClearFlags != CameraClearFlags.Nothing);
            graphiteCmd.SetViewport(0, 0, gBuffer.Width, gBuffer.Height);
            graphiteCmd.SetScissor(0, 0, (uint)gBuffer.Width, (uint)gBuffer.Height);
        }

        // 6.1 Clear GBuffer (on Vulkan, LoadOp.Clear already handles this)
        if (!isVulkan)
        {
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
                    break;
            }
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
        if (!isVulkan)
        {
            Graphics.BindFramebuffer(lightAccumulation.frameBuffer);
            Graphics.Clear(0, 0, 0, 0, ClearFlags.Color);
        }

        // Begin Graphite render pass for light accumulation
        if (graphiteCmd != null)
        {
            graphiteCmd.BeginRenderPass(lightAccumulation, Graphite.LoadOp.Clear, Float4.Zero, false);
            // Use SetViewportRaw (no Y-flip) for fullscreen passes that sample GBuffer.
            // GBuffer was rendered with Y-flip, storing scene top at texture row 0.
            // Without Y-flip, NDC (-1,-1) maps to framebuffer top, UV (0,0) samples row 0 = scene top.
            graphiteCmd.SetViewportRaw(0, 0, lightAccumulation.Width, lightAccumulation.Height);
            graphiteCmd.SetScissor(0, 0, (uint)lightAccumulation.Width, (uint)lightAccumulation.Height);
        }

        // Render each light's contribution (additive blending)
        int renderedLightCount = 0;
        foreach (IRenderableLight light in lights)
        {
            if (css.CullingMask.HasLayer(light.GetLayer()) == false)
                continue;

            light.OnRenderLight(gBuffer, lightAccumulation, css);
            renderedLightCount++;
        }

        // End lighting Graphite render pass
        if (graphiteCmd?.InRenderPass == true)
            graphiteCmd.EndRenderPass();

        // Transition light accumulation to ShaderResource for compose/effects sampling
        TransitionToShaderResource(lightAccumulation);

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

        // Begin Graphite render pass for composition
        if (graphiteCmd != null)
        {
            graphiteCmd.BeginRenderPass(composedOutput, Graphite.LoadOp.Clear, Float4.Zero, true);
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
        if (isVulkan)
        {
            // Vulkan: copy depth texture via Graphite command
            var srcDepth = gBuffer.frameBuffer.GraphiteDepthAttachment;
            var dstDepth = composedOutput.frameBuffer.GraphiteDepthAttachment;
            if (srcDepth != null && dstDepth != null && graphiteCmd != null)
            {
                graphiteCmd.ResourceBarrier(new Graphite.ResourceBarrier(
                    srcDepth, Graphite.ResourceState.DepthWrite, Graphite.ResourceState.CopySource));
                graphiteCmd.ResourceBarrier(new Graphite.ResourceBarrier(
                    dstDepth, Graphite.ResourceState.DepthWrite, Graphite.ResourceState.CopyDestination));

                graphiteCmd.CopyTextureToTexture(new Graphite.TextureTextureCopy
                {
                    Source = srcDepth,
                    Destination = dstDepth,
                    Width = (uint)gBuffer.Width,
                    Height = (uint)gBuffer.Height,
                    Depth = 1,
                });

                graphiteCmd.ResourceBarrier(new Graphite.ResourceBarrier(
                    srcDepth, Graphite.ResourceState.CopySource, Graphite.ResourceState.ShaderResource));
                graphiteCmd.ResourceBarrier(new Graphite.ResourceBarrier(
                    dstDepth, Graphite.ResourceState.CopyDestination, Graphite.ResourceState.DepthWrite));
            }
        }
        else
        {
            Graphics.BindFramebuffer(gBuffer.frameBuffer, FBOTarget.Read);
            Graphics.BindFramebuffer(composedOutput.frameBuffer, FBOTarget.Draw);
            Graphics.BlitFramebuffer(0, 0, gBuffer.Width, gBuffer.Height, 0, 0, composedOutput.Width, composedOutput.Height, ClearFlags.Depth, BlitFilter.Nearest);
        }

        // Bind composed output for transparent rendering
        if (!isVulkan)
            Graphics.BindFramebuffer(composedOutput.frameBuffer);

        // Transition composedOutput to ShaderResource for AfterLighting effects
        // that may sample it via manually-set textures in Blit(target, mat)
        TransitionToShaderResource(composedOutput);

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

        // Transition composedOutput to ShaderResource for PostProcess effects
        TransitionToShaderResource(composedOutput);

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
        // 12. Render Gizmos (needs an active render pass on composedOutput for Vulkan)
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

        // =======================================================
        // 13. Blit Result to target, If target is null Blit will go to the Screen/Window
        if (isVulkan)
        {
            if (target == null)
            {
                // Swapchain blit needs a dedicated command buffer because the
                // swapchain texture is acquired separately. Submit the main
                // pipeline first so composedOutput is ready.
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
                BlitToRenderTargetGraphite(composedOutput, target, graphiteCmd);
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
                // No target and no swapchain blit — just submit the main cmd
                Graphics.ActiveGraphiteCmdBuffer = null;
                if (graphiteCmd != null)
                {
                    graphiteCmd.Submit();
                    graphiteCmd.Dispose();
                    graphiteCmd = null;
                }
            }
        }
        else
        {
            Graphics.ActiveGraphiteCmdBuffer = null;
            if (graphiteCmd != null)
            {
                graphiteCmd.Submit();
                graphiteCmd.Dispose();
                graphiteCmd = null;
            }
            Blit(composedOutput, target, null, 0, false, false);
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

        // Reset bound framebuffer if any is bound
        if (!isVulkan)
        {
            Graphics.UnbindFramebuffer();
            Graphics.Viewport(0, 0, (uint)Window.InternalWindow.FramebufferSize.X, (uint)Window.InternalWindow.FramebufferSize.Y);
        }
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
    /// Called on Vulkan when the camera renders to a texture instead of the swapchain.
    /// </summary>
    private void BlitToRenderTargetGraphite(RenderTexture source, RenderTexture target, RenderCommandBuffer? existingCmd)
    {
        var sourceGraphiteTex = source.frameBuffer.GraphiteColorAttachments is { Length: > 0 }
            ? source.frameBuffer.GraphiteColorAttachments[0]
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

        var renderPassLayout = GraphiteFormatMapper.MapRenderPassLayout(target.frameBuffer);

        // Use the existing command buffer if provided, otherwise create a new one
        var cmd = existingCmd ?? Graphics.CreateCommandBuffer("RenderTargetBlit");
        bool ownCmd = existingCmd == null;

        cmd.ResourceBarrier(new Graphite.ResourceBarrier(
            sourceGraphiteTex, Graphite.ResourceState.RenderTarget, Graphite.ResourceState.ShaderResource));

        cmd.BeginRenderPass(target, Graphite.LoadOp.Clear, Float4.Zero, false);
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
        var targetGraphiteTex = target.frameBuffer.GraphiteColorAttachments is { Length: > 0 }
            ? target.frameBuffer.GraphiteColorAttachments[0]
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
    /// Vulkan fallback: clears the swapchain to the camera's clear color when
    /// the legacy GL scene rendering pipeline cannot be used.
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

        bool isVulkanShadow = !Graphics.IsOpenGL;

        if (!isVulkanShadow)
        {
            Graphics.BindFramebuffer(atlas.frameBuffer);
            // Ensure clean state for shadow rendering: depth test/write enabled, no blending.
            Graphics.SetState(new RasterizerState
            {
                DepthTest = true,
                DepthWrite = true,
                Depth = RasterizerState.DepthMode.Lequal,
                DoBlend = false,
                CullFace = RasterizerState.PolyFace.Back,
            }, true);
            Graphics.Clear(0.0f, 0.0f, 0.0f, 1.0f, ClearFlags.Depth);
        }

        // Begin depth-only Graphite render pass for shadow atlas
        var graphiteCmd = Graphics.ActiveGraphiteCmdBuffer;
        if (graphiteCmd != null)
        {
            graphiteCmd.BeginDepthOnlyRenderPass(atlas);
            // Use raw viewport (no Y-flip) for the shadow atlas. The atlas is
            // self-contained: depth is written using the light's VP matrix and
            // sampled in the lighting pass with the same matrix. Applying a
            // Y-flip here would break the UV ↔ depth mapping.
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
                lightComponent.RenderShadows(this, css.CameraPosition, renderables);
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
}
