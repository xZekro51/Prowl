// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Runtime.Graphite;
using Prowl.Runtime.Profiling;
using Prowl.Runtime.Rendering.Compute;
using Prowl.Runtime.Resources;
using Prowl.Vector;

using Material = Prowl.Runtime.Resources.Material;
using Shader = Prowl.Runtime.Resources.Shader;

namespace Prowl.Runtime.Rendering.GI;

/// <summary>
/// Manages the Signed Distance Field Global Illumination pipeline.
/// Uses multi-cascade SDFs and irradiance probes for indirect lighting.
/// Implements <see cref="IGISystem"/> for event-driven dispatch via <see cref="GISystemManager"/>.
/// </summary>
public sealed class SDFGISystem : IGISystem
{
    private struct SDFCascade
    {
        public Texture3DRT? SDFTexture;         // R16F — signed distance
        public Texture3DRT? ProbeIrradiance;    // RGBA16F — L1 SH coefficients
        public Texture3DRT? ProbeVisibility;    // R8/Short — mean-distance visibility
        public float WorldSize;                 // Half-extent of this cascade
        public Float3 Center;                   // World-space center (follows camera)
    }

    private SDFCascade[]? _cascades;
    private int _cascadeCount;
    private int _probeResolution;
    private int _probeUpdateOffset;   // Round-robin index for incremental updates

    private Material? _probeTraceMat;

    // Compute kernels
    private ComputeKernel? _mergeSDFKernel;
    private ComputeUniforms? _mergeSDFUniforms;
    private ComputeKernel? _blendSDFKernel;
    private ComputeUniforms? _blendSDFUniforms;
    private ComputeKernel? _probeUpdateKernel;
    private ComputeUniforms? _probeUpdateUniforms;
    private Graphite.Sampler? _computeSampler;

    private bool _disposed;

    // Debug visualization materials
    private Material? _debugSDFSliceMat;
    private Material? _debugProbeGridMat;

    /// <summary>
    /// Indicates whether the GI system has produced valid indirect lighting data.
    /// This remains false until compute dispatch is fully integrated and probe textures
    /// are actually populated. Used by the pipeline to decide whether to suppress ambient.
    /// </summary>
    public bool HasValidData { get; private set; }

    /// <inheritdoc />
    public Scene.GlobalIlluminationParams.GIMode SupportedMode => Scene.GlobalIlluminationParams.GIMode.SDFGI;

    // Public accessors for debug visualization
    public int CascadeCount => _cascadeCount;
    public int ProbeResolution => _probeResolution;

    public Graphite.Texture? GetCascadeSDFTexture(int cascade)
        => _cascades != null && cascade < _cascadeCount ? _cascades[cascade].SDFTexture?.GraphiteTexture : null;

    public Graphite.Texture? GetCascadeProbeTexture(int cascade)
        => _cascades != null && cascade < _cascadeCount ? _cascades[cascade].ProbeIrradiance?.GraphiteTexture : null;

    public Float3 GetCascadeCenter(int cascade)
        => _cascades != null && cascade < _cascadeCount ? _cascades[cascade].Center : Float3.Zero;

    public float GetCascadeSize(int cascade)
        => _cascades != null && cascade < _cascadeCount ? _cascades[cascade].WorldSize : 0f;

    /// <summary>
    /// Allocates or reallocates GPU resources when settings change.
    /// </summary>
    public void EnsureResources(int cascadeCount, float baseSize,
                                 float cascadeScale, int probeResolution)
    {
        if (_disposed)
            return;

        if (!Graphics.IsGraphiteReady)
        {
            Debug.LogWarning("SDFGI requires Graphite device. GI disabled.");
            return;
        }

        // Check if resources need reallocation
        if (_cascades != null && _cascadeCount == cascadeCount && _probeResolution == probeResolution)
            return;

        DisposeCascades();

        _cascadeCount = cascadeCount;
        _probeResolution = probeResolution;
        _cascades = new SDFCascade[cascadeCount];

        float currentSize = baseSize;
        uint sdfRes = 64; // SDF resolution per cascade

        for (int i = 0; i < cascadeCount; i++)
        {
            uint probeRes = (uint)probeResolution;

            _cascades[i] = new SDFCascade
            {
                SDFTexture = new Texture3DRT(sdfRes, sdfRes, sdfRes, TextureImageFormat.Short),
                ProbeIrradiance = new Texture3DRT(probeRes, probeRes, probeRes, TextureImageFormat.Short4),
                ProbeVisibility = new Texture3DRT(probeRes, probeRes, probeRes, TextureImageFormat.Short),
                WorldSize = currentSize,
                Center = Float3.Zero,
            };

            currentSize *= cascadeScale;
        }

        // Create materials for rasterization passes
        _probeTraceMat ??= new Material(Shader.LoadDefault(DefaultShader.SDFGI_ProbeTrace));

        // Create compute kernels
        if (_mergeSDFKernel == null || !_mergeSDFKernel.IsValid)
        {
            _mergeSDFKernel?.Dispose();

            BindGroupLayoutEntry[] mergeLayout =
            [
                BindGroupLayoutEntry.StorageTexture(0, ShaderStage.Compute, "GlobalSDF"),
                BindGroupLayoutEntry.UniformBuffer(1, ShaderStage.Compute, name: "Params"),
            ];
            string mergeSource = ComputeShaderLoader.Load("Compute/SDFGI_MergeSDF");
            _mergeSDFKernel = new ComputeKernel(mergeSource, mergeLayout, "SDFGI_MergeSDF");
        }

        if (_blendSDFKernel == null || !_blendSDFKernel.IsValid)
        {
            _blendSDFKernel?.Dispose();

            BindGroupLayoutEntry[] blendLayout =
            [
                BindGroupLayoutEntry.StorageTexture(0, ShaderStage.Compute, "GlobalSDF"),
                BindGroupLayoutEntry.UniformBuffer(1, ShaderStage.Compute, name: "Params"),
                BindGroupLayoutEntry.CombinedTextureSampler(2, ShaderStage.Compute, "ObjectSDF"),
            ];
            string blendSource = ComputeShaderLoader.Load("Compute/SDFGI_BlendObjectSDF");
            _blendSDFKernel = new ComputeKernel(blendSource, blendLayout, "SDFGI_BlendObjectSDF");
        }

        if (_probeUpdateKernel == null || !_probeUpdateKernel.IsValid)
        {
            _probeUpdateKernel?.Dispose();

            BindGroupLayoutEntry[] probeLayout =
            [
                BindGroupLayoutEntry.StorageTexture(0, ShaderStage.Compute, "ProbeIrradiance"),
                BindGroupLayoutEntry.UniformBuffer(1, ShaderStage.Compute, name: "Params"),
                BindGroupLayoutEntry.CombinedTextureSampler(2, ShaderStage.Compute, "GlobalSDF"),
            ];
            string probeSource = ComputeShaderLoader.Load("Compute/SDFGI_ProbeUpdate");
            _probeUpdateKernel = new ComputeKernel(probeSource, probeLayout, "SDFGI_ProbeUpdate");
        }

        _computeSampler ??= Graphics.Graphite.CreateSampler(Graphite.SamplerDescriptor.LinearClamp);
        _mergeSDFUniforms ??= new ComputeUniforms();
        _blendSDFUniforms ??= new ComputeUniforms();
        _probeUpdateUniforms ??= new ComputeUniforms();
    }

    /// <summary>
    /// Merges per-object SDFs into global cascade textures.
    /// Compute dispatch per cascade: first clears to max distance, then blends
    /// each object's per-mesh SDF into the cascade.
    /// </summary>
    public void UpdateGlobalSDF(
        IReadOnlyList<IRenderable> renderables,
        RenderPipeline.CameraSnapshot css)
    {
        if (_cascades == null || _mergeSDFKernel == null || !_mergeSDFKernel.IsValid)
            return;

        using (Profiler.Section("SDFGI.UpdateSDF"))
        {
            RenderCommandBuffer? cmdBuffer = Graphics.ActiveGraphiteCmdBuffer;
            if (cmdBuffer == null)
                return;

            CommandList cmd = cmdBuffer.CommandList;

            // Re-center cascades on camera position
            for (int i = 0; i < _cascadeCount; i++)
            {
                _cascades[i].Center = css.CameraPosition;
            }

            // For each cascade, clear then blend per-object SDFs
            for (int c = 0; c < _cascadeCount; c++)
            {
                SDFCascade cascade = _cascades[c];
                Graphite.Texture? sdfTex = cascade.SDFTexture?.GraphiteTexture;
                if (sdfTex == null)
                    continue;

                // Pass 1: Clear cascade to max distance
                _mergeSDFUniforms!.Clear();
                _mergeSDFUniforms.SetVector3("_CascadeCenter", cascade.Center);
                _mergeSDFUniforms.SetFloat("_CascadeSize", cascade.WorldSize);
                _mergeSDFUniforms.SetInt("_CascadeResolution", 64);

                uint groupSize = ComputeDispatcher.WorkGroupCount(64, 4);

                ComputeDispatcher.Dispatch(
                    cmd,
                    _mergeSDFKernel,
                    groupSize, groupSize, groupSize,
                    _mergeSDFUniforms,
                    images: [(0, sdfTex)]);

                // Pass 2: Blend each object's SDF into the cascade
                if (_blendSDFKernel != null && _blendSDFKernel.IsValid && _computeSampler != null)
                {
                    Float3 cascadeMin = cascade.Center - new Float3(cascade.WorldSize);
                    Float3 cascadeMax = cascade.Center + new Float3(cascade.WorldSize);

                    for (int i = 0; i < renderables.Count; i++)
                    {
                        IRenderable renderable = renderables[i];
                        renderable.GetCullingData(out bool isRenderable, out AABB bounds);
                        if (!isRenderable)
                            continue;

                        // Get mesh SDF from cache
                        Resources.Mesh? mesh = renderable.GetMesh();
                        if (mesh == null)
                            continue;

                        Texture3DRT? meshSDF = MeshSDFCache.GetOrGenerate(mesh);
                        if (meshSDF.IsNotValid() || meshSDF!.GraphiteTexture == null)
                            continue;

                        // Quick reject: skip if object AABB doesn't overlap cascade volume
                        if (bounds.Max.X < cascadeMin.X || bounds.Min.X > cascadeMax.X ||
                            bounds.Max.Y < cascadeMin.Y || bounds.Min.Y > cascadeMax.Y ||
                            bounds.Max.Z < cascadeMin.Z || bounds.Min.Z > cascadeMax.Z)
                            continue;

                        Float3 objectCenter = bounds.Center;
                        Float3 extent = bounds.Max - bounds.Min;
                        float objectExtent = Math.Max(extent.X, Math.Max(extent.Y, extent.Z)) * 0.5f * 1.1f;

                        _blendSDFUniforms!.Clear();
                        _blendSDFUniforms.SetVector3("_CascadeCenter", cascade.Center);
                        _blendSDFUniforms.SetFloat("_CascadeSize", cascade.WorldSize);
                        _blendSDFUniforms.SetInt("_CascadeResolution", 64);
                        // Pad to align _ObjectCenter at offset 32 (16-byte boundary for vec3)
                        _blendSDFUniforms.SetFloat("_Padding0", 0f);
                        _blendSDFUniforms.SetFloat("_Padding1", 0f);
                        _blendSDFUniforms.SetFloat("_Padding2", 0f);
                        _blendSDFUniforms.SetVector3("_ObjectCenter", objectCenter);
                        _blendSDFUniforms.SetFloat("_ObjectExtent", objectExtent);

                        ComputeDispatcher.Dispatch(cmd, _blendSDFKernel, groupSize, groupSize, groupSize,
                            _blendSDFUniforms,
                            images: [(0, sdfTex)],
                            textures: [(2, meshSDF.GraphiteTexture, _computeSampler)]);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Incrementally updates irradiance probes by tracing rays through the SDF.
    /// Each frame updates a subset of probes (round-robin).
    /// </summary>
    public void UpdateProbes(
        IReadOnlyList<IRenderableLight> lights,
        RenderPipeline.CameraSnapshot css)
    {
        if (_cascades == null || _probeUpdateKernel == null || !_probeUpdateKernel.IsValid)
            return;

        using (Profiler.Section("SDFGI.UpdateProbes"))
        {
            RenderCommandBuffer? cmdBuffer = Graphics.ActiveGraphiteCmdBuffer;
            if (cmdBuffer == null)
                return;

            CommandList cmd = cmdBuffer.CommandList;

            int totalProbes = _probeResolution * _probeResolution * _probeResolution;
            int probesPerFrame = Math.Max(1, totalProbes / 4); // Update 1/4 each frame

            // Find primary directional light data
            Float3 lightDir = Float3.UnitY; // default fallback
            Float3 lightColor = Float3.One;
            float lightIntensity = 1.0f;
            foreach (IRenderableLight light in lights)
            {
                if (light.GetLightType() == LightType.Directional)
                {
                    lightDir = light.GetLightDirection();
                    if (light is Light lightComponent)
                    {
                        lightColor = new Float3(
                            (float)lightComponent.Color.R,
                            (float)lightComponent.Color.G,
                            (float)lightComponent.Color.B);
                        lightIntensity = lightComponent.Intensity;
                    }
                    break;
                }
            }

            for (int c = 0; c < _cascadeCount; c++)
            {
                SDFCascade cascade = _cascades[c];
                Graphite.Texture? probeTex = cascade.ProbeIrradiance?.GraphiteTexture;
                Graphite.Texture? sdfTex = cascade.SDFTexture?.GraphiteTexture;
                if (probeTex == null)
                    continue;

                _probeUpdateUniforms!.Clear();
                // Order MUST match the GLSL std140 struct layout:
                //   vec3  _CascadeCenter;     // offset 0
                //   float _CascadeSize;       // offset 12 (packs after vec3)
                //   int   _ProbeResolution;   // offset 16
                //   int   _ProbeUpdateOffset; // offset 20
                //   float _Hysteresis;        // offset 24
                //   (pad to 32)
                //   vec3  _LightDirection;    // offset 32 (16-byte aligned)
                //   float _LightIntensity;    // offset 44
                //   vec3  _LightColor;        // offset 48
                //   float _Padding0;          // offset 60
                //   vec3  _SkyColor;          // offset 64
                _probeUpdateUniforms.SetVector3("_CascadeCenter", cascade.Center);
                _probeUpdateUniforms.SetFloat("_CascadeSize", cascade.WorldSize);
                _probeUpdateUniforms.SetInt("_ProbeResolution", _probeResolution);
                _probeUpdateUniforms.SetInt("_ProbeUpdateOffset", _probeUpdateOffset);
                _probeUpdateUniforms.SetFloat("_Hysteresis", 0.95f);
                _probeUpdateUniforms.SetVector3("_LightDirection", lightDir);
                _probeUpdateUniforms.SetFloat("_LightIntensity", lightIntensity);
                _probeUpdateUniforms.SetVector3("_LightColor", lightColor);
                _probeUpdateUniforms.SetFloat("_Padding0", 0f);
                _probeUpdateUniforms.SetVector3("_SkyColor", new Float3(0.5f, 0.7f, 1.0f));

                uint groupSize = ComputeDispatcher.WorkGroupCount(_probeResolution, 4);

                if (sdfTex != null && _computeSampler != null)
                {
                    ComputeDispatcher.Dispatch(
                        cmd,
                        _probeUpdateKernel,
                        groupSize, groupSize, groupSize,
                        _probeUpdateUniforms,
                        images: [(0, probeTex)],
                        textures: [(2, sdfTex, _computeSampler)]);
                }
                else
                {
                    ComputeDispatcher.Dispatch(
                        cmd,
                        _probeUpdateKernel,
                        groupSize, groupSize, groupSize,
                        _probeUpdateUniforms,
                        images: [(0, probeTex)]);
                }
            }

            _probeUpdateOffset = (_probeUpdateOffset + probesPerFrame) % totalProbes;
            HasValidData = true;
        }
    }

    /// <summary>
    /// Fullscreen pass: looks up probe grid for indirect lighting.
    /// Writes additive contribution to light accumulation.
    /// </summary>
    public void TraceGI(
        RenderTexture gBuffer,
        RenderTexture lightAccumulation,
        RenderPipeline.CameraSnapshot css,
        float intensity)
    {
        if (_cascades == null || _probeTraceMat.IsNotValid())
            return;

        using (Profiler.Section("SDFGI.TraceGI"))
        {
            // Set GBuffer textures
            _probeTraceMat!.SetTexture("_GBufferB", gBuffer.InternalTextures[1]);
            _probeTraceMat.SetTexture("_CameraDepthTexture", gBuffer.InternalDepth);

            // Set cascade data and bind probe textures
            _probeTraceMat.SetInt("_CascadeCount", _cascadeCount);
            _probeTraceMat.SetInt("_ProbeResolution", _probeResolution);
            _probeTraceMat.SetFloat("_GIIntensity", intensity);

            for (int c = 0; c < _cascadeCount && c < 4; c++)
            {
                SDFCascade cascade = _cascades[c];
                _probeTraceMat.SetVector($"_CascadeCenter{c}", cascade.Center);
                _probeTraceMat.SetFloat($"_CascadeSize{c}", cascade.WorldSize);

                // Bind probe irradiance 3D texture for sampling.
                Graphite.Texture? probeTex = cascade.ProbeIrradiance?.GraphiteTexture;
                if (probeTex != null)
                {
                    // Use the material system so the binder can create proper
                    // bind group entries for the sampler3D uniform.
                    _probeTraceMat.SetRawGraphiteTexture($"_ProbeIrradiance{c}", probeTex);
                }
            }

            // Transition probe irradiance textures from General layout
            // (after compute dispatch) to ShaderReadOnly for fragment shader sampling.
            {
                RenderCommandBuffer? cb = Graphics.ActiveGraphiteCmdBuffer;
                if (cb != null && !cb.InRenderPass)
                {
                    for (int t = 0; t < _cascadeCount && t < 4; t++)
                    {
                        Graphite.Texture? tex = _cascades[t].ProbeIrradiance?.GraphiteTexture;
                        if (tex != null)
                        {
                            cb.ResourceBarrier(new ResourceBarrier(
                                tex, ResourceState.UnorderedAccess, ResourceState.ShaderResource));
                        }
                    }
                }
            }

            // Fullscreen blit with additive blending into light accumulation
            RenderPipeline.Blit(gBuffer, lightAccumulation, _probeTraceMat, 0, false, false, default, preserveContents: true);
        }
    }

    #region IGISystem Implementation

    /// <inheritdoc />
    void IGISystem.EnsureResources(Scene.GlobalIlluminationParams giParams)
    {
        EnsureResources(giParams.SDFCascadeCount, giParams.Distance,
            giParams.SDFCascadeScale, giParams.SDFProbeResolution);
    }

    /// <inheritdoc />
    void IGISystem.UpdateData(GIDataUpdateContext context)
    {
        UpdateGlobalSDF(context.Renderables, context.CameraSnapshot);
        UpdateProbes(context.Lights, context.CameraSnapshot);
    }

    /// <inheritdoc />
    void IGISystem.Trace(GITraceContext context)
    {
        TraceGI(context.GBuffer, context.LightAccumulation, context.CameraSnapshot,
            context.GIIntensity);
    }

    /// <inheritdoc />
    public void RenderDebugVisualization(GIDebugMode debugMode, RenderTexture gBuffer,
        RenderTexture lightAccumulation, RenderPipeline.CameraSnapshot css)
    {
        if (debugMode == GIDebugMode.SDFSlice)
            RenderDebugSDFSlice(gBuffer, lightAccumulation, css);
        else if (debugMode == GIDebugMode.ProbeGrid)
            RenderDebugProbeGrid(gBuffer, lightAccumulation, css);
    }

    private void RenderDebugSDFSlice(RenderTexture gBuffer, RenderTexture lightAccumulation,
        RenderPipeline.CameraSnapshot css)
    {
        _debugSDFSliceMat ??= new Material(Shader.LoadDefault(DefaultShader.GI_DebugSDFSlice));

        Graphite.Texture? sdfTex = GetCascadeSDFTexture(0);
        if (sdfTex == null)
            return;

        _debugSDFSliceMat.SetRawGraphiteTexture("_SDFCascade0", sdfTex);
        _debugSDFSliceMat.SetVector("_CascadeCenter0", GetCascadeCenter(0));
        _debugSDFSliceMat.SetFloat("_CascadeSize0", GetCascadeSize(0));

        // Compute slice Y: map camera Y into [0,1] within cascade
        Float3 cascadeCenter = GetCascadeCenter(0);
        float cascadeSize = GetCascadeSize(0);
        float sliceY = (css.CameraPosition.Y - cascadeCenter.Y) / (cascadeSize * 2.0f) + 0.5f;
        sliceY = Math.Clamp(sliceY, 0.0f, 1.0f);
        _debugSDFSliceMat.SetFloat("_SliceY", sliceY);

        TransitionComputeTextureForSampling(sdfTex);

        RenderPipeline.Blit(gBuffer, lightAccumulation, _debugSDFSliceMat, 0, false, false);
    }

    private void RenderDebugProbeGrid(RenderTexture gBuffer, RenderTexture lightAccumulation,
        RenderPipeline.CameraSnapshot css)
    {
        _debugProbeGridMat ??= new Material(Shader.LoadDefault(DefaultShader.GI_DebugProbeGrid));

        Graphite.Texture? probeTex = GetCascadeProbeTexture(0);
        if (probeTex == null)
            return;

        _debugProbeGridMat.SetRawGraphiteTexture("_ProbeIrradiance0", probeTex);
        _debugProbeGridMat.SetTexture("_CameraDepthTexture", gBuffer.InternalDepth);
        _debugProbeGridMat.SetVector("_CascadeCenter0", GetCascadeCenter(0));
        _debugProbeGridMat.SetFloat("_CascadeSize0", GetCascadeSize(0));
        _debugProbeGridMat.SetInt("_ProbeResolution", _probeResolution);

        TransitionComputeTextureForSampling(probeTex);

        RenderPipeline.Blit(gBuffer, lightAccumulation, _debugProbeGridMat, 0, false, false);
    }

    private static void TransitionComputeTextureForSampling(Graphite.Texture texture)
    {
        Rendering.RenderCommandBuffer? cb = Graphics.ActiveGraphiteCmdBuffer;
        if (cb != null && !cb.InRenderPass)
        {
            cb.ResourceBarrier(new ResourceBarrier(
                texture, ResourceState.UnorderedAccess, ResourceState.ShaderResource));
        }
    }

    #endregion

    private void DisposeCascades()
    {
        if (_cascades == null)
            return;

        for (int i = 0; i < _cascades.Length; i++)
        {
            _cascades[i].SDFTexture?.Dispose();
            _cascades[i].ProbeIrradiance?.Dispose();
            _cascades[i].ProbeVisibility?.Dispose();
        }

        _cascades = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeCascades();

        _mergeSDFKernel?.Dispose();
        _mergeSDFKernel = null;
        _mergeSDFUniforms?.Dispose();
        _mergeSDFUniforms = null;

        _blendSDFKernel?.Dispose();
        _blendSDFKernel = null;
        _blendSDFUniforms?.Dispose();
        _blendSDFUniforms = null;

        _probeUpdateKernel?.Dispose();
        _probeUpdateKernel = null;
        _probeUpdateUniforms?.Dispose();
        _probeUpdateUniforms = null;

        _computeSampler?.Dispose();
        _computeSampler = null;

        _probeTraceMat?.Dispose();
        _probeTraceMat = null;

        _debugSDFSliceMat?.Dispose();
        _debugSDFSliceMat = null;

        _debugProbeGridMat?.Dispose();
        _debugProbeGridMat = null;
    }
}
