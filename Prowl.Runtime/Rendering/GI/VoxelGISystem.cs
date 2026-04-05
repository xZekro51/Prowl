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
/// Manages the Voxel Cone Tracing GI pipeline.
/// Implements <see cref="IGISystem"/> for event-driven dispatch via <see cref="GISystemManager"/>.
/// </summary>
public sealed class VoxelGISystem : IGISystem
{
    // 3D volume textures
    private Texture3DRT? _voxelRadiance;   // RGBA16F — RGB = radiance, A = opacity
    private int _currentResolution;
    private float _currentWorldSize;

    // Materials wrapping the GI shaders
    private Material? _coneTraceMat;

    // Compute kernels for GPU operations
    private ComputeKernel? _voxelClearKernel;
    private ComputeUniforms? _voxelClearUniforms;
    private ComputeKernel? _voxelFillKernel;
    private ComputeUniforms? _voxelFillUniforms;
    private ComputeKernel? _injectLightKernel;
    private ComputeUniforms? _injectLightUniforms;

    private bool _disposed;

    // Debug visualization material
    private Material? _debugVoxelGridMat;

    /// <summary>
    /// Indicates whether the GI system has produced valid indirect lighting data.
    /// This remains false until compute dispatch is fully integrated and textures
    /// are actually populated. Used by the pipeline to decide whether to suppress ambient.
    /// </summary>
    public bool HasValidData { get; private set; }

    /// <inheritdoc />
    public Scene.GlobalIlluminationParams.GIMode SupportedMode => Scene.GlobalIlluminationParams.GIMode.VoxelGI;

    // Public accessors for debug visualization
    public Graphite.Texture? RadianceTexture => _voxelRadiance?.GraphiteTexture;
    public int Resolution => _currentResolution;
    public float WorldSize => _currentWorldSize;

    /// <summary>
    /// Allocates or reallocates GPU resources when settings change.
    /// </summary>
    public void EnsureResources(int resolution, float worldSize)
    {
        if (_disposed)
            return;

        if (!Graphics.IsGraphiteReady)
        {
            Debug.LogWarning("VoxelGI requires Graphite device. GI disabled.");
            return;
        }

        // Check if we need to reallocate
        if (_voxelRadiance.IsValid() && _currentResolution == resolution &&
            Math.Abs(_currentWorldSize - worldSize) < 0.01f)
            return;

        // Dispose old resources
        DisposeVolumes();

        _currentResolution = resolution;
        _currentWorldSize = worldSize;

        uint res = (uint)resolution;

        // Allocate volume textures
        _voxelRadiance = new Texture3DRT(res, res, res, TextureImageFormat.Short4, true);

        // Create cone trace material
        _coneTraceMat ??= new Material(Shader.LoadDefault(DefaultShader.VoxelGI_ConeTrace));

        // Create compute kernel for voxel grid clear
        if (_voxelClearKernel == null || !_voxelClearKernel.IsValid)
        {
            _voxelClearKernel?.Dispose();
            BindGroupLayoutEntry[] clearLayout =
            [
                BindGroupLayoutEntry.StorageTexture(0, ShaderStage.Compute, "VoxelRadiance"),
                BindGroupLayoutEntry.UniformBuffer(1, ShaderStage.Compute, name: "Params"),
            ];
            string clearSource = ComputeShaderLoader.Load("Compute/VoxelGI_Clear");
            _voxelClearKernel = new ComputeKernel(clearSource, clearLayout, "VoxelGI_Clear");
        }

        // Create compute kernel for voxel AABB fill
        if (_voxelFillKernel == null || !_voxelFillKernel.IsValid)
        {
            _voxelFillKernel?.Dispose();
            BindGroupLayoutEntry[] fillLayout =
            [
                BindGroupLayoutEntry.StorageTexture(0, ShaderStage.Compute, "VoxelRadiance"),
                BindGroupLayoutEntry.UniformBuffer(1, ShaderStage.Compute, name: "Params"),
            ];
            string fillSource = ComputeShaderLoader.Load("Compute/VoxelGI_Fill");
            _voxelFillKernel = new ComputeKernel(fillSource, fillLayout, "VoxelGI_Fill");
        }

        _voxelClearUniforms ??= new ComputeUniforms();
        _voxelFillUniforms ??= new ComputeUniforms();

        // Create compute kernel for light injection
        if (_injectLightKernel == null || !_injectLightKernel.IsValid)
        {
            _injectLightKernel?.Dispose();

            BindGroupLayoutEntry[] layoutEntries =
            [
                BindGroupLayoutEntry.StorageTexture(0, ShaderStage.Compute, "VoxelRadiance"),
                BindGroupLayoutEntry.UniformBuffer(1, ShaderStage.Compute, name: "Params"),
            ];

            string injectSource = ComputeShaderLoader.Load("Compute/VoxelGI_InjectLight");
            _injectLightKernel = new ComputeKernel(injectSource, layoutEntries, "VoxelGI_InjectLight");
        }

        _injectLightUniforms ??= new ComputeUniforms();
    }

    /// <summary>
    /// Voxelizes opaque geometry into the 3D grid using compute-based AABB fill.
    /// Clears the grid and then fills voxels that overlap each renderable's bounding box.
    /// Works on both Vulkan and OpenGL without requiring a render pass.
    /// </summary>
    public void Voxelize(
        IReadOnlyList<IRenderable> renderables,
        HashSet<int> culledIndices,
        RenderPipeline.CameraSnapshot css)
    {
        if (_voxelRadiance.IsNotValid() ||
            _voxelClearKernel == null || !_voxelClearKernel.IsValid ||
            _voxelFillKernel == null || !_voxelFillKernel.IsValid)
            return;

        Graphite.Texture? radianceTex = _voxelRadiance!.GraphiteTexture;
        if (radianceTex == null)
            return;

        using (Profiler.Section("VoxelGI.Voxelize"))
        {
            RenderCommandBuffer? cmdBuffer = Graphics.ActiveGraphiteCmdBuffer;
            if (cmdBuffer == null)
                return;

            CommandList cmd = cmdBuffer.CommandList;
            uint groupSize = ComputeDispatcher.WorkGroupCount(_currentResolution, 4);

            // Step 1: Clear the voxel grid to zero
            _voxelClearUniforms!.Clear();
            _voxelClearUniforms.SetInt("_VoxelResolution", _currentResolution);

            ComputeDispatcher.Dispatch(cmd, _voxelClearKernel, groupSize, groupSize, groupSize,
                _voxelClearUniforms, images: [(0, radianceTex)]);

            // Step 2: Fill voxels for each renderable's bounding box
            Float3 gridMin = css.CameraPosition - new Float3(_currentWorldSize);
            Float3 gridMax = css.CameraPosition + new Float3(_currentWorldSize);

            for (int i = 0; i < renderables.Count; i++)
            {
                if (culledIndices?.Contains(i) ?? false)
                    continue;

                IRenderable renderable = renderables[i];
                renderable.GetCullingData(out bool isRenderable, out AABB bounds);
                if (!isRenderable)
                    continue;

                // Quick reject: skip if AABB is entirely outside the voxel grid
                if (bounds.Max.X < gridMin.X || bounds.Min.X > gridMax.X ||
                    bounds.Max.Y < gridMin.Y || bounds.Min.Y > gridMax.Y ||
                    bounds.Max.Z < gridMin.Z || bounds.Min.Z > gridMax.Z)
                    continue;

                // Get albedo color from material properties
                Color albedoColor = renderable.GetMaterial()._properties.GetColor("_Color");
                Float4 albedo = new((float)albedoColor.R, (float)albedoColor.G, (float)albedoColor.B, 1.0f);

                _voxelFillUniforms!.Clear();
                // Order MUST match the GLSL std140 struct layout:
                //   vec3  _VoxelGridCenter;  // offset 0
                //   float _VoxelGridSize;    // offset 12 (packs after vec3)
                //   int   _VoxelResolution;  // offset 16
                //   (pad to 32)
                //   vec3  _AABBMin;          // offset 32
                //   (pad to 48)
                //   vec3  _AABBMax;          // offset 48
                //   (pad to 64)
                //   vec4  _Albedo;           // offset 64
                _voxelFillUniforms.SetVector3("_VoxelGridCenter", css.CameraPosition);
                _voxelFillUniforms.SetFloat("_VoxelGridSize", _currentWorldSize);
                _voxelFillUniforms.SetInt("_VoxelResolution", _currentResolution);
                _voxelFillUniforms.SetVector3("_AABBMin", bounds.Min);
                _voxelFillUniforms.SetVector3("_AABBMax", bounds.Max);
                _voxelFillUniforms.SetVector4("_Albedo", albedo);

                ComputeDispatcher.Dispatch(cmd, _voxelFillKernel, groupSize, groupSize, groupSize,
                    _voxelFillUniforms, images: [(0, radianceTex)]);
            }
        }
    }

    /// <summary>
    /// Injects direct illumination from the primary directional light.
    /// Compute dispatch over the entire volume.
    /// </summary>
    public void InjectDirectLight(
        DirectionalLight light,
        RenderPipeline.CameraSnapshot css)
    {
        if (_voxelRadiance.IsNotValid() || _injectLightKernel == null || !_injectLightKernel.IsValid)
            return;

        Graphite.Texture? radianceTex = _voxelRadiance!.GraphiteTexture;
        if (radianceTex == null)
            return;

        using (Profiler.Section("VoxelGI.InjectLight"))
        {
            // Get the command list from the active command buffer
            RenderCommandBuffer? cmdBuffer = Graphics.ActiveGraphiteCmdBuffer;
            if (cmdBuffer == null)
                return;

            CommandList cmd = cmdBuffer.CommandList;

            // Set uniform values
            _injectLightUniforms!.Clear();
            // Order MUST match the GLSL std140 struct layout in the compute shader:
            //   vec3  _LightDirection;   // offset 0
            //   float _LightIntensity;   // offset 12 (packs after vec3)
            //   vec3  _LightColor;       // offset 16
            //   float _VoxelGridSize;    // offset 28 (packs after vec3)
            //   vec3  _VoxelGridCenter;  // offset 32
            //   int   _VoxelResolution;  // offset 44 (packs after vec3)
            _injectLightUniforms.SetVector3("_LightDirection", light.Transform.Forward);
            _injectLightUniforms.SetFloat("_LightIntensity", (float)light.Intensity);
            Float3 lightColor = new((float)light.Color.R, (float)light.Color.G, (float)light.Color.B);
            _injectLightUniforms.SetVector3("_LightColor", lightColor);
            _injectLightUniforms.SetFloat("_VoxelGridSize", _currentWorldSize);
            _injectLightUniforms.SetVector3("_VoxelGridCenter", css.CameraPosition);
            _injectLightUniforms.SetInt("_VoxelResolution", _currentResolution);

            // Dispatch compute: 4x4x4 workgroup size
            uint groupSize = ComputeDispatcher.WorkGroupCount(_currentResolution, 4);

            ComputeDispatcher.Dispatch(
                cmd,
                _injectLightKernel,
                groupSize, groupSize, groupSize,
                _injectLightUniforms,
                images: [(0, radianceTex)]);
        }
    }

    /// <summary>
    /// Generates the mipmap chain for the voxel radiance volume.
    /// </summary>
    public void GenerateMipmaps()
    {
        if (_voxelRadiance.IsNotValid())
            return;

        Graphite.Texture? radianceTex = _voxelRadiance!.GraphiteTexture;
        if (radianceTex == null || !_voxelRadiance.HasMipmaps)
            return;

        using (Profiler.Section("VoxelGI.Mipmap"))
        {
            RenderCommandBuffer? cmdBuffer = Graphics.ActiveGraphiteCmdBuffer;
            if (cmdBuffer == null)
                return;

            CommandList cmd = cmdBuffer.CommandList;

            // Use hardware mipmap generation
            cmd.GenerateMipmaps(radianceTex);

            HasValidData = true;
        }
    }

    /// <summary>
    /// Cone traces indirect lighting for every screen pixel.
    /// Reads GBuffer normals + depth, samples the mipmap chain.
    /// Writes additive contribution to lightAccumulation.
    /// </summary>
    public void ConeTrace(
        RenderTexture gBuffer,
        RenderTexture lightAccumulation,
        RenderPipeline.CameraSnapshot css,
        float intensity,
        int coneCount)
    {
        if (_voxelRadiance.IsNotValid() || _coneTraceMat.IsNotValid())
            return;

        using (Profiler.Section("VoxelGI.ConeTrace"))
        {
            // Set cone trace uniforms
            _coneTraceMat!.SetTexture("_GBufferB", gBuffer.InternalTextures[1]);
            _coneTraceMat.SetTexture("_GBufferC", gBuffer.InternalTextures[2]);
            _coneTraceMat.SetTexture("_CameraDepthTexture", gBuffer.InternalDepth);

            _coneTraceMat.SetVector("_VoxelGridCenter", css.CameraPosition);
            _coneTraceMat.SetFloat("_VoxelGridSize", _currentWorldSize);
            _coneTraceMat.SetInt("_VoxelResolution", _currentResolution);
            _coneTraceMat.SetFloat("_GIIntensity", intensity);
            _coneTraceMat.SetInt("_ConeCount", coneCount);

            // Bind the 3D voxel radiance texture for sampling in the cone trace shader.
            Graphite.Texture? radianceTex = _voxelRadiance!.GraphiteTexture;
            if (radianceTex != null)
            {
                // Use the material system so the binder can create proper
                // bind group entries for the sampler3D uniform.
                _coneTraceMat.SetRawGraphiteTexture("_VoxelRadiance", radianceTex);
            }

            // Transition the 3D radiance texture from General layout
            // (after compute dispatch and mipmap generation) to ShaderReadOnly
            // so the descriptor's ImageLayout matches the actual image layout.
            if (radianceTex != null)
            {
                RenderCommandBuffer? cb = Graphics.ActiveGraphiteCmdBuffer;
                if (cb != null && !cb.InRenderPass)
                {
                    cb.ResourceBarrier(new ResourceBarrier(
                        radianceTex, ResourceState.UnorderedAccess, ResourceState.ShaderResource));
                }
            }

            // Fullscreen blit with additive blending into light accumulation
            RenderPipeline.Blit(gBuffer, lightAccumulation, _coneTraceMat, 0, false, false, default, preserveContents: true);
        }
    }

    #region IGISystem Implementation

    /// <inheritdoc />
    void IGISystem.EnsureResources(Scene.GlobalIlluminationParams giParams)
    {
        EnsureResources(giParams.VoxelResolution, giParams.Distance);
    }

    /// <inheritdoc />
    void IGISystem.UpdateData(GIDataUpdateContext context)
    {
        Voxelize(context.Renderables, context.CulledRenderableIndices, context.CameraSnapshot);

        // Find primary directional light for direct light injection
        DirectionalLight? primaryDirLight = null;
        foreach (IRenderableLight light in context.Lights)
        {
            if (light is DirectionalLight dl) { primaryDirLight = dl; break; }
        }
        if (primaryDirLight != null)
            InjectDirectLight(primaryDirLight, context.CameraSnapshot);

        GenerateMipmaps();
    }

    /// <inheritdoc />
    void IGISystem.Trace(GITraceContext context)
    {
        ConeTrace(context.GBuffer, context.LightAccumulation, context.CameraSnapshot,
            context.GIIntensity, context.ConeCount);
    }

    /// <inheritdoc />
    public void RenderDebugVisualization(GIDebugMode debugMode, RenderTexture gBuffer,
        RenderTexture lightAccumulation, RenderPipeline.CameraSnapshot css)
    {
        if (debugMode != GIDebugMode.VoxelGrid)
            return;

        _debugVoxelGridMat ??= new Material(Shader.LoadDefault(DefaultShader.GI_DebugVoxelGrid));

        Graphite.Texture? radianceTex = RadianceTexture;
        if (radianceTex == null)
            return;

        _debugVoxelGridMat.SetRawGraphiteTexture("_VoxelRadiance", radianceTex);
        _debugVoxelGridMat.SetTexture("_CameraDepthTexture", gBuffer.InternalDepth);
        _debugVoxelGridMat.SetVector("_VoxelGridCenter", css.CameraPosition);
        _debugVoxelGridMat.SetFloat("_VoxelGridSize", _currentWorldSize);
        _debugVoxelGridMat.SetInt("_VoxelResolution", _currentResolution);

        // Vulkan: transition radiance texture to ShaderResource before sampling
        TransitionComputeTextureForSampling(radianceTex);

        RenderPipeline.Blit(gBuffer, lightAccumulation, _debugVoxelGridMat, 0, false, false);
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

    private void DisposeVolumes()
    {
        if (_voxelRadiance.IsValid())
        {
            _voxelRadiance.Dispose();
            _voxelRadiance = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        DisposeVolumes();

        _voxelClearKernel?.Dispose();
        _voxelClearKernel = null;
        _voxelClearUniforms?.Dispose();
        _voxelClearUniforms = null;

        _voxelFillKernel?.Dispose();
        _voxelFillKernel = null;
        _voxelFillUniforms?.Dispose();
        _voxelFillUniforms = null;

        _injectLightKernel?.Dispose();
        _injectLightKernel = null;
        _injectLightUniforms?.Dispose();
        _injectLightUniforms = null;

        _coneTraceMat?.Dispose();
        _coneTraceMat = null;

        _debugVoxelGridMat?.Dispose();
        _debugVoxelGridMat = null;
    }

    }
