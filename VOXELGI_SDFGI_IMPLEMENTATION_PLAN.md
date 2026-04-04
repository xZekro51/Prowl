# VoxelGI & SDFGI Implementation Plan — Prowl Engine

> **Goal:** Add two real-time global illumination techniques — **Voxel Cone Tracing GI (VoxelGI)** and **Signed Distance Field GI (SDFGI)** — as selectable modes within the existing deferred rendering pipeline. Both are toggled from per-scene `GlobalIlluminationParams` on `Scene`, with an optional per-light override on `DirectionalLight` and a UI in the editor.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Phase 0 — Prerequisites & Infrastructure](#2-phase-0--prerequisites--infrastructure)
3. [Phase 1 — Scene-Level GI Configuration](#3-phase-1--scene-level-gi-configuration)
4. [Phase 2 — VoxelGI Implementation](#4-phase-2--voxelgi-implementation)
5. [Phase 3 — SDFGI Implementation](#5-phase-3--sdfgi-implementation)
6. [Phase 4 — Shader Registration & Default Assets](#6-phase-4--shader-registration--default-assets)
7. [Phase 5 — Editor Integration](#7-phase-5--editor-integration)
8. [Phase 6 — Deferred Compose Integration](#8-phase-6--deferred-compose-integration)
9. [Phase 7 — Performance & Quality](#9-phase-7--performance--quality)
10. [Phase 8 — Cleanup & Quality Assurance](#10-phase-8--cleanup--quality-assurance)
11. [File Manifest](#11-file-manifest)
12. [Implementation Order](#12-implementation-order)
13. [Key Technical Decisions](#13-key-technical-decisions)

---

## 1. Architecture Overview

### Where GI fits in the current pipeline

The `DefaultRenderPipeline.Internal_Render` method (file: `Prowl.Runtime/Rendering/DefaultRenderPipeline.cs`) follows this order:

```
Step 0  — Setup variables, prepare camera
Step 1  — Pre-Cull image effects
Step 2  — Camera snapshot + global uniforms
Step 3  — Frustum cull renderables
Step 4  — Pre-Render image effects
Step 5  — Shadow atlas rendering              ← GI voxelization/SDF update inserted AFTER here
Step 6  — GBuffer pass (deferred)
Step 7  — Deferred lighting pass (per-light)   ← GI cone-trace/probe-lookup inserted AFTER here
Step 7.5— DuringLighting image effects (SSPT, GTAO, etc.)
Step 8  — Deferred composition (albedo × lighting + ambient + fog)
Step 9  — AfterLighting image effects
Step 10 — Transparent forward pass
Step 11 — PostProcess image effects
Step 12 — Gizmos
Step 13 — Blit to target/swapchain
Step 14 — Post-render cleanup
```

Both GI techniques inject work at two points:
- **After Step 5 (shadows):** Scene voxelization (VoxelGI) or global SDF + probe update (SDFGI).
- **After Step 7 (deferred lighting):** Cone tracing (VoxelGI) or probe lookup (SDFGI) writes indirect lighting into the light accumulation buffer before composition.

This matches the existing `RenderStage.DuringLighting` integration point used by `SSPTEffect`.

### Key engine types involved

| Type | File | Role |
|------|------|------|
| `DefaultRenderPipeline` | `Prowl.Runtime/Rendering/DefaultRenderPipeline.cs` | Main render loop; orchestrates all passes |
| `DefaultRenderPipelineAsset` | `Prowl.Runtime/Rendering/DefaultRenderPipelineAsset.cs` | Inspector-configurable pipeline settings |
| `RenderPipeline` | `Prowl.Runtime/Rendering/RenderPipeline.cs` | Base class; holds `CameraSnapshot`, culling, draw helpers |
| `Scene` | `Prowl.Runtime/Resources/Scene.cs` | Holds `FogParams`, `AmbientLightParams`, `SkyboxParams` — GI config goes here |
| `DirectionalLight` | `Prowl.Runtime/Components/Lights/DirectionalLight.cs` | Primary light; drives shadow cascades, optional GI override |
| `Light` (base) | `Prowl.Runtime/Components/Lights/Light.cs` | Base light with shadow settings |
| `ImageEffect` | `Prowl.Runtime/Components/Camera.cs` (nested) | Base for post-process-like effects; has `RenderStage` |
| `RenderContext` | `Prowl.Runtime/Rendering/RenderContext.cs` | Passed to image effects; holds GBuffer, light accumulation, scene color |
| `Texture3D` | `Prowl.Runtime/Resources/Texture3D.cs` | Existing 3D texture (legacy GL path) |
| `CommandList` | `Prowl.Runtime/Graphite/Commands/CommandList.cs` | Graphite command recording; has compute dispatch support |
| `ComputePipelineState` | `Prowl.Runtime/Graphite/Resources/PipelineState.cs` | Abstract compute PSO |
| `PropertyState` | `Prowl.Runtime/Rendering/PropertyState.cs` | Global/per-object shader uniforms |
| `GlobalUniforms` | `Prowl.Runtime/Rendering/GlobalUniforms.cs` | Uniform buffer uploaded once per frame |
| `ShadowAtlas` | `Prowl.Runtime/Rendering/ShadowAtlas.cs` | Shadow atlas allocation |
| `RenderTexture` | `Prowl.Runtime/Resources/RenderTexture.cs` | 2D render target with temp-RT pool |
| `DefaultShader` | `Prowl.Runtime/Resources/DefaultAssets.cs` | Enum of embedded shaders |
| `DefaultShaderInclude` | `Prowl.Runtime/Resources/DefaultAssets.cs` | Enum of embedded GLSL includes |
| `RenderingEvents` | `Prowl.Runtime/EventSystem/RenderingEvents.cs` | Event domain for rendering pipeline phases |
| `ProjectSettingsPanel` | `Prowl.Editor/Panels/ProjectSettingsPanel.cs` | Editor settings UI (tabs: Player, Rendering, Scripting Defines) |

---

## 2. Phase 0 — Prerequisites & Infrastructure

### Step 0.1 — Verify Compute Shader Support on Both Backends

Both VoxelGI and SDFGI rely heavily on compute shaders. `CommandList` (line 258-292) already exposes `SetComputePipeline`, `Dispatch`, `DispatchIndirect`, and `MemoryBarrier`. Verify the concrete backends:

**Actions:**
1. Open `Prowl.Runtime/Graphite/OpenGL/GLCommandList.cs` — confirm `DispatchCore` calls `glDispatchCompute`. If OpenGL < 4.3, guard all GI compute paths and fall back gracefully (GI disabled).
2. Open `Prowl.Runtime/Graphite/Vulkan/VKCommandList.cs` — confirm `DispatchCore` calls `vkCmdDispatch`.
3. Verify `GraphiteDevice` subclasses implement `CreateComputePipelineState`.

**Fallback:** If GL lacks compute support, GI is disabled at runtime with `Debug.LogWarning("GI requires compute shader support (OpenGL 4.3+ or Vulkan)")` and the pipeline falls back to ambient-only lighting (current behaviour).

### Step 0.2 — 3D Render-Texture Wrapper for Graphite

`Texture3D` in `Prowl.Runtime/Resources/Texture3D.cs` uses the legacy `Graphics.*` path. Both GI techniques need 3D textures backed by Graphite for compute read/write (image-load-store).

**New file:** `Prowl.Runtime/Rendering/GI/Texture3DRT.cs`

```csharp
namespace Prowl.Runtime.Rendering;

/// <summary>
/// Lightweight 3D render-texture wrapper for compute read/write.
/// Analogous to RenderTexture but for 3D (volumetric) textures.
/// Supports temporary pooling to avoid per-frame allocation.
/// </summary>
public sealed class Texture3DRT : EngineObject
{
    public uint Width { get; }
    public uint Height { get; }
    public uint Depth { get; }
    public TextureImageFormat Format { get; }

    // Legacy GL handle (for OpenGL image-load-store)
    public Texture3D? LegacyTexture { get; }

    // Graphite handle (for Vulkan compute)
    public Graphite.Texture? GraphiteTexture { get; }

    public Texture3DRT(uint width, uint height, uint depth,
                       TextureImageFormat format, bool generateMipmaps = false);
    public void Clear(Float4 clearValue);

    // Temporary pool (mirrors RenderTexture.GetTemporaryRT / ReleaseTemporaryRT)
    public static Texture3DRT GetTemporary(uint w, uint h, uint d,
                                            TextureImageFormat fmt);
    public static void ReleaseTemporary(Texture3DRT rt);

    protected override void OnDispose();
}
```

**Key requirements:**
- On Vulkan: create with `TextureUsage.Storage | TextureUsage.Sampled` so it can be bound as both a storage image (compute write) and a sampled texture (fragment read).
- On OpenGL: use `glTexImage3D` + `glBindImageTexture` for compute, `sampler3D` for fragment reads.
- Support mipmap generation (needed for VoxelGI anisotropic mipmaps).

### Step 0.3 — Verify Storage-Image Bind Group Entries

Compute shaders writing to 3D textures need `imageStore`/`imageLoad` bindings. Check:

**Files to inspect:**
- `Prowl.Runtime/Graphite/Descriptors/BindGroupDescriptor.cs` — does `BindGroupLayoutEntry` support a `StorageTexture` variant?
- `Prowl.Runtime/Graphite/Vulkan/VKBindGroup.cs` — does it create `VK_DESCRIPTOR_TYPE_STORAGE_IMAGE` descriptors?
- `Prowl.Runtime/Graphite/OpenGL/GLBindGroup.cs` (or equivalent) — does it call `glBindImageTexture`?

If not, add:
- `BindGroupLayoutEntry.StorageImage(binding, shaderStage, format, access, name)` factory method
- `BindGroupEntry.ForStorageTexture(binding, texture)` factory method
- Backend implementations for both VK and GL

### Step 0.4 — Add `imageStore` / `imageLoad` Extension to Shader Parser

The shader parser (`Prowl.Runtime/AssetImporting/ShaderParser.cs`) may need to recognize `layout(rXXXX, binding = N) uniform imageXD` for compute shaders. Verify that compute shader GLSL is parsed/compiled correctly through the existing pipeline. If the engine only handles vertex+fragment passes, you'll need:

- A `Compute { ... }` block type in the `.shader` format (alongside existing `Vertex { ... }` and `Fragment { ... }`)
- The shader parser emitting a compute stage when it sees this block
- `ShaderPass` variant compilation supporting compute-only programs

---

## 3. Phase 1 — Scene-Level GI Configuration

### Step 1.1 — Add `GlobalIlluminationParams` to `Scene`

Add a new struct in `Prowl.Runtime/Resources/Scene.cs` at line ~255 (after `SkyboxParams Skybox = new();`), following the exact pattern of `FogParams`, `AmbientLightParams`, and `SkyboxParams`:

```csharp
public struct GlobalIlluminationParams
{
    public enum GIMode
    {
        None,       // No GI (ambient-only, current behavior)
        VoxelGI,    // Voxel Cone Tracing
        SDFGI       // Signed Distance Field GI
    }

    public GIMode Mode = GIMode.None;

    // === Shared settings ===
    public float Intensity = 1.0f;
    public float Distance = 100.0f;       // World-space radius around camera to cover
    public int BounceCount = 1;           // Number of indirect light bounces

    // === VoxelGI-specific ===
    public int VoxelResolution = 256;     // Grid resolution: 64, 128, 256, 512
    public int ConeCount = 6;             // Diffuse cone count: 4, 6, 9, 16
    public float ConeAngle = 0.5f;        // Cone half-angle in radians
    public bool VoxelAO = true;           // Derive ambient occlusion from voxel opacity

    // === SDFGI-specific ===
    public int SDFCascadeCount = 4;       // Number of SDF cascades: 2, 3, 4, 6
    public int SDFProbeResolution = 8;    // Probes per cascade axis: 4, 8, 16
    public float SDFOcclusionBias = 0.01f;
    public float SDFCascadeScale = 2.0f;  // Size multiplier between cascades

    public GlobalIlluminationParams() { }
}

public GlobalIlluminationParams GlobalIllumination = new();
```

**File:** `Prowl.Runtime/Resources/Scene.cs` — insert after line 255 (`public SkyboxParams Skybox = new();`)

### Step 1.2 — Add Optional GI Override to `DirectionalLight`

Add fields to `Prowl.Runtime/Components/Lights/DirectionalLight.cs` after the existing shadow fields (after line 34: `public float ShadowDistance = 100f;`):

```csharp
// === Global Illumination override ===
/// <summary>
/// When true, this directional light's GI settings override the scene's
/// GlobalIlluminationParams. Useful for per-light testing or artistic control.
/// </summary>
public bool OverrideSceneGI = false;

/// <summary>GI mode override (only used when OverrideSceneGI is true).</summary>
public Scene.GlobalIlluminationParams.GIMode GIModeOverride =
    Scene.GlobalIlluminationParams.GIMode.None;

/// <summary>GI intensity override (only used when OverrideSceneGI is true).</summary>
public float GIIntensityOverride = 1.0f;
```

### Step 1.3 — Helper to Resolve Effective GI Settings

Add a static helper in `VoxelGISystem` or a shared `GIUtils` class:

```csharp
public static class GIUtils
{
    /// <summary>
    /// Resolves the effective GI mode and intensity by checking the primary
    /// directional light's override first, then falling back to scene settings.
    /// </summary>
    public static (Scene.GlobalIlluminationParams.GIMode Mode, float Intensity)
        ResolveGISettings(Scene scene, IReadOnlyList<IRenderableLight> lights)
    {
        // Check for directional light override
        foreach (IRenderableLight light in lights)
        {
            if (light is DirectionalLight dirLight && dirLight.OverrideSceneGI)
            {
                return (dirLight.GIModeOverride, dirLight.GIIntensityOverride);
            }
        }

        // Fall back to scene settings
        return (scene.GlobalIllumination.Mode, scene.GlobalIllumination.Intensity);
    }
}
```

**New file:** `Prowl.Runtime/Rendering/GI/GIUtils.cs`

---

## 4. Phase 2 — VoxelGI Implementation

### Overview

Voxel Cone Tracing GI works in four stages:
1. **Voxelize** the scene into a 3D grid (RGBA = radiance + opacity)
2. **Inject** direct lighting from the directional light into lit voxels
3. **Generate** anisotropic mipmaps for directional pre-filtering
4. **Cone trace** — for each screen pixel, trace wide/narrow cones through the mipmap chain to gather indirect diffuse + specular

### Step 2.1 — VoxelGISystem Orchestrator

**New file:** `Prowl.Runtime/Rendering/GI/VoxelGISystem.cs`

```csharp
namespace Prowl.Runtime.Rendering;

/// <summary>
/// Manages the Voxel Cone Tracing GI pipeline.
/// Lifecycle: created by DefaultRenderPipeline, updated per-frame, disposed with pipeline.
/// </summary>
public sealed class VoxelGISystem : IDisposable
{
    // 3D volume textures
    private Texture3DRT? _voxelRadiance;   // RGBA16F — RGB = radiance, A = opacity
    private Texture3DRT? _voxelNormal;     // RGB10A2 — encoded surface normal
    private int _currentResolution;
    private float _currentWorldSize;

    // Materials wrapping the GI shaders
    private Material? _voxelizeMat;
    private Material? _injectLightMat;
    private Material? _mipmapMat;
    private Material? _coneTraceMat;

    // Temporal accumulation
    private RenderTexture? _prevGI;
    private uint _frameIndex;

    /// <summary>
    /// Allocates or reallocates GPU resources when settings change.
    /// </summary>
    public void EnsureResources(int resolution, float worldSize) { ... }

    /// <summary>
    /// Voxelizes opaque geometry into the 3D grid.
    /// Called after shadow rendering, before GBuffer.
    /// Uses 3-axis orthographic projection with imageStore.
    /// </summary>
    public void Voxelize(
        IReadOnlyList<IRenderable> renderables,
        HashSet<int> culledIndices,
        RenderPipeline pipeline,
        RenderPipeline.CameraSnapshot css) { ... }

    /// <summary>
    /// Injects direct illumination from the primary directional light.
    /// Compute dispatch over the entire volume.
    /// </summary>
    public void InjectDirectLight(
        DirectionalLight light,
        RenderPipeline.CameraSnapshot css) { ... }

    /// <summary>
    /// Generates the anisotropic mipmap chain (6-directional filtering).
    /// Compute dispatch per mip level, halving resolution each step.
    /// </summary>
    public void GenerateMipmaps() { ... }

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
        int coneCount) { ... }

    public void Dispose() { ... }
}
```

### Step 2.2 — Voxelization Shader

**New file:** `Prowl.Runtime/Assets/Defaults/VoxelGI_Voxelize.shader`

This is the most complex shader. It uses **geometry-shader-based dominant-axis projection** (the "standard" approach for GPU voxelization):

```
Shader "Default/VoxelGI_Voxelize"

Pass "Voxelize"
{
    Tags { "LightMode" = "Voxelize" }
    Cull None
    ZWrite Off
    ZTest Off
    ColorMask 0     // No color output — writes via imageStore
    Blend Off

    GLSLPROGRAM

    Vertex
    {
        // Transform vertices to world space only (no projection).
        // Output world position + normal + albedo for the geometry shader.
        layout(location = 0) in vec3 vertexPosition;
        layout(location = 1) in vec2 vertexTexCoord;
        layout(location = 2) in vec3 vertexNormal;

        out vec3 v_WorldPos;
        out vec3 v_Normal;
        out vec2 v_TexCoord;

        void main()
        {
            vec4 worldPos = PROWL_MATRIX_M * vec4(vertexPosition, 1.0);
            v_WorldPos = worldPos.xyz;
            v_Normal = normalize(mat3(PROWL_MATRIX_M) * vertexNormal);
            v_TexCoord = vertexTexCoord;
            gl_Position = worldPos; // passed through, geometry shader will project
        }
    }

    Geometry
    {
        // Select dominant axis (X/Y/Z) per triangle for maximum coverage.
        // Project triangle onto the 2D plane of the selected axis.
        layout(triangles) in;
        layout(triangle_strip, max_vertices = 3) out;

        uniform vec3 _VoxelGridCenter;
        uniform float _VoxelGridSize;  // half-extent in world units

        in vec3 v_WorldPos[];
        in vec3 v_Normal[];
        in vec2 v_TexCoord[];

        out vec3 g_WorldPos;
        out vec3 g_Normal;
        out vec2 g_TexCoord;

        void main()
        {
            // Compute face normal to determine dominant axis
            vec3 faceNormal = abs(cross(
                v_WorldPos[1] - v_WorldPos[0],
                v_WorldPos[2] - v_WorldPos[0]));

            // Choose projection axis (largest component of face normal)
            // Project into [-1, 1] NDC on chosen 2D plane
            // ... (standard dominant-axis voxelization)

            for (int i = 0; i < 3; i++)
            {
                g_WorldPos = v_WorldPos[i];
                g_Normal = v_Normal[i];
                g_TexCoord = v_TexCoord[i];
                // gl_Position = project onto dominant 2D axis...
                EmitVertex();
            }
            EndPrimitive();
        }
    }

    Fragment
    {
        // Write voxel data using imageStore.
        // No render target output (ColorMask 0).

        layout(rgba16f, binding = 0) uniform image3D _VoxelRadiance;
        layout(rgb10_a2, binding = 1) uniform image3D _VoxelNormal;

        uniform vec3 _VoxelGridCenter;
        uniform float _VoxelGridSize;
        uniform int _VoxelResolution;
        uniform sampler2D _MainTex; // albedo texture

        in vec3 g_WorldPos;
        in vec3 g_Normal;
        in vec2 g_TexCoord;

        void main()
        {
            // Convert world position to voxel grid coordinates
            vec3 localPos = (g_WorldPos - _VoxelGridCenter) / _VoxelGridSize;
            localPos = localPos * 0.5 + 0.5; // [0, 1]
            ivec3 voxelCoord = ivec3(localPos * float(_VoxelResolution));

            // Bounds check
            if (any(lessThan(voxelCoord, ivec3(0))) ||
                any(greaterThanEqual(voxelCoord, ivec3(_VoxelResolution))))
                discard;

            vec4 albedo = texture(_MainTex, g_TexCoord);
            vec3 normal = normalize(g_Normal) * 0.5 + 0.5;

            // Atomic average via imageStore
            // (use atomic operations or multiple passes for correct averaging)
            imageStore(_VoxelRadiance, voxelCoord, vec4(albedo.rgb, 1.0));
            imageStore(_VoxelNormal, voxelCoord, vec4(normal, 1.0));
        }
    }

    ENDGLSL
}
```

> **Note:** Geometry shaders have limited performance. An alternative for Phase 7 optimization is to use 3 separate rasterization passes (one per axis) without a geometry shader, which may perform better on some GPUs.

### Step 2.3 — Direct Light Injection Shader (Compute)

**New file:** `Prowl.Runtime/Assets/Defaults/VoxelGI_InjectLight.shader`

```
Shader "Default/VoxelGI_InjectLight"

Pass "InjectLight"
{
    Tags { "LightMode" = "Compute" }

    GLSLPROGRAM

    Compute
    {
        layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

        layout(rgba16f, binding = 0) uniform image3D _VoxelRadiance;
        layout(rgba16f, binding = 1) uniform readonly image3D _VoxelAlbedo;

        uniform vec3 _LightDirection;
        uniform vec3 _LightColor;
        uniform float _LightIntensity;
        uniform vec3 _VoxelGridCenter;
        uniform float _VoxelGridSize;
        uniform int _VoxelResolution;

        // Shadow sampling (re-use existing cascade shadow maps)
        uniform sampler2D _ShadowAtlas;
        uniform mat4 _CascadeShadowMatrix0;
        // ... (cascade data)

        void main()
        {
            ivec3 voxelCoord = ivec3(gl_GlobalInvocationID);
            if (any(greaterThanEqual(voxelCoord, ivec3(_VoxelResolution))))
                return;

            vec4 voxelAlbedo = imageLoad(_VoxelAlbedo, voxelCoord);
            if (voxelAlbedo.a < 0.01) return; // empty voxel

            // Convert voxel coord to world position
            vec3 worldPos = _VoxelGridCenter +
                (vec3(voxelCoord) / float(_VoxelResolution) - 0.5) * 2.0 * _VoxelGridSize;

            // Simple N·L shading with shadow lookup
            // ... (sample shadow atlas, compute direct lighting)

            vec3 directLight = voxelAlbedo.rgb * _LightColor * _LightIntensity * NdotL * shadow;
            imageStore(_VoxelRadiance, voxelCoord, vec4(directLight, voxelAlbedo.a));
        }
    }

    ENDGLSL
}
```

### Step 2.4 — Anisotropic Mipmap Generation Shader (Compute)

**New file:** `Prowl.Runtime/Assets/Defaults/VoxelGI_Mipmap.shader`

For proper cone tracing, you need directional pre-filtering. The standard approach stores **6 directional components** (±X, ±Y, ±Z) per mip level. This can be implemented as:

- **Option A:** 6 separate 3D textures, one per direction, each with its own mipmap chain.
- **Option B (simpler):** A single 3D texture with standard isotropic mipmaps. Less accurate but much simpler; adequate for a first pass.

Start with **Option B** for simplicity:

```
Pass "GenerateMipmap"
{
    Tags { "LightMode" = "Compute" }

    GLSLPROGRAM
    Compute
    {
        layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

        layout(rgba16f, binding = 0) uniform readonly image3D _SourceMip;
        layout(rgba16f, binding = 1) uniform writeonly image3D _DestMip;

        uniform int _SourceSize;

        void main()
        {
            ivec3 coord = ivec3(gl_GlobalInvocationID);
            int destSize = _SourceSize / 2;
            if (any(greaterThanEqual(coord, ivec3(destSize)))) return;

            // 2×2×2 box filter
            vec4 sum = vec4(0.0);
            for (int z = 0; z < 2; z++)
            for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
            {
                sum += imageLoad(_SourceMip, coord * 2 + ivec3(x, y, z));
            }
            imageStore(_DestMip, coord, sum / 8.0);
        }
    }
    ENDGLSL
}
```

### Step 2.5 — Cone Tracing Shader (Fullscreen Fragment)

**New file:** `Prowl.Runtime/Assets/Defaults/VoxelGI_ConeTrace.shader`

```
Shader "Default/VoxelGI_ConeTrace"

Pass "ConeTrace"
{
    Tags { "RenderOrder" = "Opaque" }
    Cull None
    ZTest Off
    ZWrite Off
    Blend One One    // Additive blending into light accumulation

    GLSLPROGRAM

    Vertex
    {
        layout(location = 0) in vec3 vertexPosition;
        layout(location = 1) in vec2 vertexTexCoord;
        out vec2 TexCoords;
        void main()
        {
            TexCoords = vertexTexCoord;
            gl_Position = vec4(vertexPosition, 1.0);
        }
    }

    Fragment
    {
        #include "Fragment"

        layout(location = 0) out vec4 outColor;
        in vec2 TexCoords;

        uniform sampler3D _VoxelRadiance;   // Mipmapped radiance volume
        uniform sampler2D _GBufferB;        // Normals
        uniform sampler2D _GBufferC;        // Roughness, Metalness
        uniform sampler2D _CameraDepthTexture;

        uniform vec3 _VoxelGridCenter;
        uniform float _VoxelGridSize;
        uniform int _VoxelResolution;
        uniform float _GIIntensity;
        uniform int _ConeCount;

        // Predefined cone directions (hemisphere around Z+, rotated to world normal)
        // ... (6 or more cone directions in tangent space)

        vec3 WorldPosFromDepth(float depth, vec2 uv) { /* same as DeferredCompose */ }

        vec4 TraceCone(vec3 origin, vec3 direction, float coneAngle)
        {
            vec4 accumColor = vec4(0.0);
            float dist = _VoxelGridSize / float(_VoxelResolution) * 2.0; // start 2 voxels away
            float maxDist = _VoxelGridSize * 2.0;

            while (dist < maxDist && accumColor.a < 0.95)
            {
                vec3 samplePos = origin + direction * dist;

                // Convert to UVW
                vec3 uvw = (samplePos - _VoxelGridCenter) / (_VoxelGridSize * 2.0) + 0.5;
                if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0))))
                    break;

                // Mip level from cone diameter at this distance
                float diameter = 2.0 * dist * tan(coneAngle * 0.5);
                float mipLevel = log2(diameter * float(_VoxelResolution) / (_VoxelGridSize * 2.0));

                vec4 voxelSample = textureLod(_VoxelRadiance, uvw, max(0.0, mipLevel));

                // Front-to-back compositing
                float a = 1.0 - accumColor.a;
                accumColor.rgb += voxelSample.rgb * a * voxelSample.a;
                accumColor.a += voxelSample.a * a;

                // Step forward (larger steps at higher mip = further away)
                dist += diameter * 0.5;
            }
            return accumColor;
        }

        void main()
        {
            float depth = texture(_CameraDepthTexture, TexCoords).r;
            if (depth >= 1.0) { outColor = vec4(0.0); return; }

            vec3 worldPos = WorldPosFromDepth(depth, TexCoords);
            vec4 normalData = texture(_GBufferB, TexCoords);
            vec3 viewNormal = normalData.rgb * 2.0 - 1.0;
            vec3 worldNormal = normalize((inverse(transpose(PROWL_MATRIX_V)) * vec4(viewNormal, 0.0)).xyz);

            // Build tangent frame
            vec3 T, B;
            // ... (compute tangent/bitangent from worldNormal)

            // Trace diffuse cones
            vec3 indirectDiffuse = vec3(0.0);
            for (int i = 0; i < _ConeCount; i++)
            {
                vec3 coneDir = /* tangent-space cone direction rotated to world */ ;
                vec4 cone = TraceCone(worldPos + worldNormal * 0.05, coneDir, 1.0472 /* ~60° */);
                indirectDiffuse += cone.rgb;
            }
            indirectDiffuse /= float(_ConeCount);

            // Optional: trace specular cone (narrow, in reflection direction)
            vec3 viewDir = normalize(worldPos - _WorldSpaceCameraPos);
            vec3 reflDir = reflect(viewDir, worldNormal);
            float roughness = texture(_GBufferC, TexCoords).r;
            float specConeAngle = mix(0.02, 0.5, roughness); // narrow → wide
            vec4 indirectSpecular = TraceCone(worldPos + worldNormal * 0.05, reflDir, specConeAngle);

            outColor = vec4((indirectDiffuse + indirectSpecular.rgb) * _GIIntensity, 0.0);
        }
    }

    ENDGLSL
}
```

### Step 2.6 — Pipeline Integration

Modify `DefaultRenderPipeline.Internal_Render` in `Prowl.Runtime/Rendering/DefaultRenderPipeline.cs`:

**Add field** (in the `#region Pipeline Resources` section, around line 60):
```csharp
private VoxelGISystem? _voxelGI;
private SDFGISystem? _sdfGI;
```

**After Step 5 (shadow rendering)** — insert voxelization (around line 243, after `AssignCameraMatrices`):
```csharp
// 5.2 Voxel GI: voxelize scene for cone tracing
var giParams = css.Scene.GlobalIllumination;
var (giMode, giIntensity) = GIUtils.ResolveGISettings(css.Scene, lights);
if (giMode == Scene.GlobalIlluminationParams.GIMode.VoxelGI)
{
    _voxelGI ??= new VoxelGISystem();
    _voxelGI.EnsureResources(giParams.VoxelResolution, giParams.Distance);
    _voxelGI.Voxelize(renderables, culledRenderableIndices, this, css);

    // Find primary directional light for direct light injection
    DirectionalLight? primaryDirLight = null;
    foreach (IRenderableLight light in lights)
    {
        if (light is DirectionalLight dl) { primaryDirLight = dl; break; }
    }
    if (primaryDirLight != null)
        _voxelGI.InjectDirectLight(primaryDirLight, css);

    _voxelGI.GenerateMipmaps();
}
```

**After Step 7 (deferred lighting)** — insert cone tracing (around line 375, after light accumulation render pass ends):
```csharp
// 7.1 Voxel GI: cone trace indirect lighting into accumulation buffer
if (giMode == Scene.GlobalIlluminationParams.GIMode.VoxelGI && _voxelGI != null)
{
    _voxelGI.ConeTrace(gBuffer, lightAccumulation, css, giIntensity, giParams.ConeCount);
}
```

**Dispose VoxelGI when mode changes or pipeline disposes:**
```csharp
// In OnDispose override
_voxelGI?.Dispose();
_sdfGI?.Dispose();
```

---

## 5. Phase 3 — SDFGI Implementation

### Overview

SDFGI uses a multi-cascade signed distance field (SDF) of the scene plus a grid of irradiance probes updated via SDF ray marching. It's more complex than VoxelGI but offers better performance at large scales and more stable temporal behavior.

**Pipeline:**
1. **Per-mesh SDF generation** (amortized, cached per mesh asset)
2. **Global SDF cascade merge** (per frame — merge object SDFs into world-space cascades)
3. **Irradiance probe update** (per frame, incremental — trace rays through SDF, accumulate SH)
4. **Probe lookup** (fullscreen — trilinearly interpolate probes, apply SDF-occlusion weighting)

### Step 3.1 — SDFGISystem Orchestrator

**New file:** `Prowl.Runtime/Rendering/GI/SDFGISystem.cs`

```csharp
namespace Prowl.Runtime.Rendering;

public sealed class SDFGISystem : IDisposable
{
    private struct SDFCascade
    {
        public Texture3DRT SDFTexture;         // R16F — signed distance
        public Texture3DRT ProbeIrradiance;    // RGBA16F — L1 SH coefficients
        public Texture3DRT ProbeVisibility;    // R8 — mean-distance visibility
        public float WorldSize;                // Half-extent of this cascade
        public Float3 Center;                  // World-space center (follows camera)
    }

    private SDFCascade[]? _cascades;
    private int _cascadeCount;
    private int _probeUpdateOffset;   // Round-robin index for incremental updates

    private Material? _generateSDFMat;
    private Material? _mergeSDFMat;
    private Material? _probeUpdateMat;
    private Material? _probeTraceMat;

    public void EnsureResources(int cascadeCount, float baseSize,
                                 float cascadeScale, int probeResolution) { ... }

    /// <summary>
    /// Merges per-object SDFs into global cascade textures.
    /// Compute dispatch per cascade.
    /// </summary>
    public void UpdateGlobalSDF(
        IReadOnlyList<IRenderable> renderables,
        RenderPipeline.CameraSnapshot css) { ... }

    /// <summary>
    /// Incrementally updates irradiance probes by tracing rays through the SDF.
    /// Each frame updates a subset of probes (round-robin).
    /// </summary>
    public void UpdateProbes(
        IReadOnlyList<IRenderableLight> lights,
        RenderPipeline.CameraSnapshot css) { ... }

    /// <summary>
    /// Fullscreen pass: looks up probe grid for indirect lighting.
    /// Writes additive contribution to light accumulation.
    /// </summary>
    public void TraceGI(
        RenderTexture gBuffer,
        RenderTexture lightAccumulation,
        RenderPipeline.CameraSnapshot css,
        float intensity) { ... }

    public void Dispose() { ... }
}
```

### Step 3.2 — Per-Mesh SDF Cache

**New file:** `Prowl.Runtime/Rendering/GI/MeshSDFCache.cs`

```csharp
namespace Prowl.Runtime.Rendering;

/// <summary>
/// Caches per-mesh signed distance fields. SDFs are generated once per unique
/// mesh and reused across frames. Invalidated when mesh data changes.
/// </summary>
public static class MeshSDFCache
{
    private static readonly Dictionary<int, Texture3DRT> _cache = new();

    /// <summary>Resolution of per-mesh SDFs (typically 32 or 64).</summary>
    public static int MeshSDFResolution { get; set; } = 32;

    public static Texture3DRT GetOrGenerate(Mesh mesh, Material sdfGenMaterial)
    {
        int id = mesh.InstanceID;
        if (_cache.TryGetValue(id, out Texture3DRT? existing) && existing.IsValid())
            return existing;

        // Generate SDF via compute shader (jump-flooding or brute-force ray march)
        Texture3DRT sdf = new Texture3DRT(
            (uint)MeshSDFResolution, (uint)MeshSDFResolution, (uint)MeshSDFResolution,
            TextureImageFormat.Float); // R16F or R32F

        // Dispatch compute:
        // 1. Upload mesh triangle data to SSBO
        // 2. For each voxel, compute min signed distance to all triangles
        // ... (compute dispatch)

        _cache[id] = sdf;
        return sdf;
    }

    public static void Invalidate(int meshInstanceID)
    {
        if (_cache.Remove(meshInstanceID, out Texture3DRT? old))
            old?.Dispose();
    }

    public static void Clear()
    {
        foreach (Texture3DRT sdf in _cache.Values)
            sdf?.Dispose();
        _cache.Clear();
    }
}
```

### Step 3.3 — SDF Generation Shader (Compute)

**New file:** `Prowl.Runtime/Assets/Defaults/SDFGI_GenerateSDF.shader`

Brute-force approach for small meshes (32³):

```
Pass "GenerateMeshSDF"
{
    GLSLPROGRAM
    Compute
    {
        layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

        layout(r16f, binding = 0) uniform writeonly image3D _SDFOutput;

        // Mesh triangle data via SSBO
        struct Triangle { vec3 v0, v1, v2; };
        layout(std430, binding = 1) readonly buffer TriangleBuffer
        {
            Triangle triangles[];
        };

        uniform int _TriangleCount;
        uniform vec3 _BoundsMin;
        uniform vec3 _BoundsMax;
        uniform int _Resolution;

        float PointTriangleDistance(vec3 p, vec3 a, vec3 b, vec3 c) { /* ... */ }

        void main()
        {
            ivec3 coord = ivec3(gl_GlobalInvocationID);
            if (any(greaterThanEqual(coord, ivec3(_Resolution)))) return;

            vec3 worldPos = mix(_BoundsMin, _BoundsMax,
                                (vec3(coord) + 0.5) / float(_Resolution));

            float minDist = 1e10;
            for (int i = 0; i < _TriangleCount; i++)
            {
                float d = PointTriangleDistance(worldPos,
                    triangles[i].v0, triangles[i].v1, triangles[i].v2);
                minDist = min(minDist, d);
            }

            // Sign determination (inside/outside) via normal direction
            // ... (use angle-weighted pseudo-normal method)

            imageStore(_SDFOutput, coord, vec4(minDist));
        }
    }
    ENDGLSL
}
```

### Step 3.4 — Global SDF Cascade Merge Shader (Compute)

**New file:** `Prowl.Runtime/Assets/Defaults/SDFGI_MergeSDF.shader`

```
Pass "MergeSDF"
{
    GLSLPROGRAM
    Compute
    {
        layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

        layout(r16f, binding = 0) uniform writeonly image3D _GlobalSDF;

        // Per-object SDF samplers (array of up to N objects)
        uniform sampler3D _ObjectSDFs[64]; // per-mesh SDFs
        uniform mat4 _ObjectTransforms[64]; // world→local transforms
        uniform vec3 _ObjectBoundsMin[64];
        uniform vec3 _ObjectBoundsMax[64];
        uniform int _ObjectCount;

        uniform vec3 _CascadeCenter;
        uniform float _CascadeSize;
        uniform int _CascadeResolution;

        void main()
        {
            ivec3 coord = ivec3(gl_GlobalInvocationID);
            if (any(greaterThanEqual(coord, ivec3(_CascadeResolution)))) return;

            vec3 worldPos = _CascadeCenter +
                ((vec3(coord) + 0.5) / float(_CascadeResolution) - 0.5) * 2.0 * _CascadeSize;

            float globalDist = _CascadeSize * 2.0; // init to max possible

            for (int i = 0; i < _ObjectCount; i++)
            {
                // Transform world pos to object's local SDF space
                vec3 localPos = (_ObjectTransforms[i] * vec4(worldPos, 1.0)).xyz;
                vec3 uvw = (localPos - _ObjectBoundsMin[i]) /
                           (_ObjectBoundsMax[i] - _ObjectBoundsMin[i]);

                if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0))))
                    continue;

                float objDist = texture(_ObjectSDFs[i], uvw).r;
                globalDist = min(globalDist, objDist);
            }

            imageStore(_GlobalSDF, coord, vec4(globalDist));
        }
    }
    ENDGLSL
}
```

> **Scalability note:** For scenes with many objects (>64), use a spatial acceleration structure (octree/BVH) on CPU to select only nearby objects per cascade cell, or process in multiple compute passes.

### Step 3.5 — Irradiance Probe Update Shader (Compute)

**New file:** `Prowl.Runtime/Assets/Defaults/SDFGI_ProbeUpdate.shader`

Each probe traces N rays through the global SDF, samples direct lighting at hit points, and accumulates L1 spherical harmonics:

```
Pass "UpdateProbes"
{
    GLSLPROGRAM
    Compute
    {
        layout(local_size_x = 64) in; // 1 thread per probe (or per ray)

        // Probe grid storage (SH coefficients packed in 3D texture)
        layout(rgba16f, binding = 0) uniform image3D _ProbeIrradiance;
        layout(r8, binding = 1) uniform image3D _ProbeVisibility;

        // Global SDF for ray marching
        uniform sampler3D _GlobalSDF;
        uniform vec3 _CascadeCenter;
        uniform float _CascadeSize;

        // Probe grid params
        uniform int _ProbeResolution; // probes per axis
        uniform int _RaysPerProbe;    // typically 64 or 128
        uniform int _ProbeUpdateOffset; // for incremental updates
        uniform float _Hysteresis;    // temporal blending (0.95–0.98)

        // Lighting
        uniform vec3 _LightDirection;
        uniform vec3 _LightColor;

        // SH encoding helpers
        vec4 SHEncode(vec3 direction, vec3 radiance) { /* L1 SH */ }

        float MarchSDF(vec3 origin, vec3 dir, float maxDist)
        {
            float t = 0.0;
            for (int i = 0; i < 64; i++)
            {
                vec3 p = origin + dir * t;
                vec3 uvw = (p - _CascadeCenter) / (_CascadeSize * 2.0) + 0.5;
                if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0))))
                    return -1.0; // out of bounds
                float d = texture(_GlobalSDF, uvw).r;
                if (d < 0.001) return t; // hit
                t += d;
                if (t > maxDist) return -1.0;
            }
            return -1.0;
        }

        void main()
        {
            int probeIndex = int(gl_GlobalInvocationID.x) + _ProbeUpdateOffset;
            int totalProbes = _ProbeResolution * _ProbeResolution * _ProbeResolution;
            if (probeIndex >= totalProbes) return;

            // Decode probe grid position
            ivec3 probeCoord = ivec3(
                probeIndex % _ProbeResolution,
                (probeIndex / _ProbeResolution) % _ProbeResolution,
                probeIndex / (_ProbeResolution * _ProbeResolution));

            vec3 probeWorldPos = _CascadeCenter +
                ((vec3(probeCoord) + 0.5) / float(_ProbeResolution) - 0.5) * 2.0 * _CascadeSize;

            // Trace rays and accumulate SH
            vec4 newSH = vec4(0.0);
            for (int r = 0; r < _RaysPerProbe; r++)
            {
                vec3 rayDir = /* Fibonacci hemisphere or random direction */;
                float hitDist = MarchSDF(probeWorldPos, rayDir, _CascadeSize);

                vec3 radiance = vec3(0.0);
                if (hitDist > 0.0)
                {
                    // At hit point, evaluate direct lighting
                    float shadowMarch = MarchSDF(
                        probeWorldPos + rayDir * hitDist + vec3(0.001),
                        -_LightDirection, _CascadeSize);
                    float shadow = shadowMarch < 0.0 ? 1.0 : 0.0;
                    radiance = _LightColor * shadow * max(0.0, dot(-rayDir, _LightDirection));
                }

                newSH += SHEncode(rayDir, radiance);
            }
            newSH /= float(_RaysPerProbe);

            // Temporal blend with previous value
            vec4 prevSH = imageLoad(_ProbeIrradiance, probeCoord);
            vec4 blendedSH = mix(newSH, prevSH, _Hysteresis);
            imageStore(_ProbeIrradiance, probeCoord, blendedSH);
        }
    }
    ENDGLSL
}
```

### Step 3.6 — Probe Lookup Shader (Fullscreen Fragment)

**New file:** `Prowl.Runtime/Assets/Defaults/SDFGI_ProbeTrace.shader`

```
Shader "Default/SDFGI_ProbeTrace"

Pass "ProbeLookup"
{
    Tags { "RenderOrder" = "Opaque" }
    Cull None
    ZTest Off
    ZWrite Off
    Blend One One  // Additive into light accumulation

    GLSLPROGRAM
    Vertex { /* standard fullscreen quad */ }

    Fragment
    {
        #include "Fragment"

        layout(location = 0) out vec4 outColor;
        in vec2 TexCoords;

        // Per-cascade probe grids (up to 4 cascades)
        uniform sampler3D _ProbeIrradiance0;
        uniform sampler3D _ProbeIrradiance1;
        uniform sampler3D _ProbeIrradiance2;
        uniform sampler3D _ProbeIrradiance3;

        uniform vec3 _CascadeCenter[4];
        uniform float _CascadeSize[4];
        uniform int _CascadeCount;
        uniform int _ProbeResolution;

        uniform sampler2D _GBufferB;
        uniform sampler2D _CameraDepthTexture;
        uniform float _GIIntensity;

        vec3 SHDecode(vec4 sh, vec3 normal) { /* Evaluate L1 SH in direction */ }

        vec3 WorldPosFromDepth(float depth, vec2 uv) { /* ... */ }

        void main()
        {
            float depth = texture(_CameraDepthTexture, TexCoords).r;
            if (depth >= 1.0) { outColor = vec4(0.0); return; }

            vec3 worldPos = WorldPosFromDepth(depth, TexCoords);
            vec3 worldNormal = /* decode from GBufferB, same as DeferredCompose */;

            // Find the tightest cascade containing this point
            vec3 irradiance = vec3(0.0);
            for (int c = 0; c < _CascadeCount; c++)
            {
                vec3 localPos = (worldPos - _CascadeCenter[c]) / (_CascadeSize[c] * 2.0) + 0.5;
                if (all(greaterThan(localPos, vec3(0.05))) &&
                    all(lessThan(localPos, vec3(0.95))))
                {
                    vec4 sh = texture(/* cascade c probe grid */, localPos);
                    irradiance = SHDecode(sh, worldNormal);
                    break;
                }
            }

            outColor = vec4(irradiance * _GIIntensity, 0.0);
        }
    }
    ENDGLSL
}
```

### Step 3.7 — Pipeline Integration

Same pattern as VoxelGI in `DefaultRenderPipeline.Internal_Render`:

```csharp
// After Step 5 — SDF GI update
if (giMode == Scene.GlobalIlluminationParams.GIMode.SDFGI)
{
    _sdfGI ??= new SDFGISystem();
    _sdfGI.EnsureResources(giParams.SDFCascadeCount, giParams.Distance,
                            giParams.SDFCascadeScale, giParams.SDFProbeResolution);
    _sdfGI.UpdateGlobalSDF(renderables, css);
    _sdfGI.UpdateProbes(lights, css);
}

// After Step 7 — Probe lookup
if (giMode == Scene.GlobalIlluminationParams.GIMode.SDFGI && _sdfGI != null)
{
    _sdfGI.TraceGI(gBuffer, lightAccumulation, css, giIntensity);
}
```

---

## 6. Phase 4 — Shader Registration & Default Assets

### Step 4.1 — Add to `DefaultShader` enum

**File:** `Prowl.Runtime/Resources/DefaultAssets.cs`, append to the `DefaultShader` enum (after `SDFUI` at line 37):

```csharp
// Global Illumination
VoxelGI_Voxelize,
VoxelGI_InjectLight,
VoxelGI_Mipmap,
VoxelGI_ConeTrace,
SDFGI_GenerateSDF,
SDFGI_MergeSDF,
SDFGI_ProbeUpdate,
SDFGI_ProbeTrace,
```

### Step 4.2 — Add to `DefaultShaderInclude` enum

**File:** `Prowl.Runtime/Resources/DefaultAssets.cs`, append to `DefaultShaderInclude`:

```csharp
GICommon,
```

### Step 4.3 — Create shared GLSL include

**New file:** `Prowl.Runtime/Assets/Defaults/GI_Common.glsl`

Contains:
- Spherical harmonics L1 encode/decode
- 3D texture coordinate transforms (world → voxel, world → cascade)
- SDF ray marching utility
- Cone tracing helper
- Tangent frame construction from normal
- World position reconstruction from depth (shared with `DeferredCompose.shader`)

### Step 4.4 — Register shader file mappings

Find the code that maps `DefaultShader` enum values to embedded resource file paths (likely in `Shader.LoadDefault` or a resource loader). Add entries for all 8 new shaders + 1 new include. Follow the existing pattern.

---

## 7. Phase 5 — Editor Integration

### Step 5.1 — Add "Lighting" Tab to ProjectSettingsPanel

**File:** `Prowl.Editor/Panels/ProjectSettingsPanel.cs`

1. Add `"Lighting"` to the `TabNames` array (line 29-34):
   ```csharp
   private static readonly string[] TabNames =
   [
       "Player",
       "Rendering",
       "Lighting",              // ← NEW
       "Scripting Defines",
   ];
   ```

2. Add case in the switch (line 79-84):
   ```csharp
   case 2: DrawLightingPage(); break;
   case 3: DrawScriptingDefinesPage(); break; // shifted from 2→3
   ```

3. Add `DrawLightingPage()` method:
   ```csharp
   private void DrawLightingPage()
   {
       var scene = Scene.Current;
       if (scene == null)
       {
           ImGui.TextDisabled("No active scene");
           return;
       }

       ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "Global Illumination");
       ImGui.Separator();
       ImGui.Spacing();

       // GI Mode dropdown
       var gi = scene.GlobalIllumination;
       string[] modeNames = Enum.GetNames<Scene.GlobalIlluminationParams.GIMode>();
       int modeIndex = (int)gi.Mode;
       ImGui.Text("GI Mode");
       ImGui.SetNextItemWidth(200 * Game.DpiScale);
       if (ImGui.Combo("##GIMode", ref modeIndex, modeNames, modeNames.Length))
           gi.Mode = (Scene.GlobalIlluminationParams.GIMode)modeIndex;

       // Shared settings
       float intensity = gi.Intensity;
       if (ImGui.SliderFloat("Intensity", ref intensity, 0f, 5f))
           gi.Intensity = intensity;

       float distance = gi.Distance;
       if (ImGui.SliderFloat("Distance", ref distance, 10f, 500f))
           gi.Distance = distance;

       int bounces = gi.BounceCount;
       if (ImGui.SliderInt("Bounces", ref bounces, 1, 4))
           gi.BounceCount = bounces;

       // VoxelGI-specific
       if (gi.Mode == Scene.GlobalIlluminationParams.GIMode.VoxelGI)
       {
           ImGui.Spacing();
           ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "Voxel Cone Tracing");
           ImGui.Separator();

           int[] resOptions = [64, 128, 256, 512];
           // ... resolution combo, cone count slider, voxel AO checkbox
       }

       // SDFGI-specific
       if (gi.Mode == Scene.GlobalIlluminationParams.GIMode.SDFGI)
       {
           ImGui.Spacing();
           ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "SDF Global Illumination");
           ImGui.Separator();

           // ... cascade count combo, probe resolution combo, cascade scale slider
       }

       scene.GlobalIllumination = gi;

       // Ambient settings (moved from wherever they currently live)
       ImGui.Spacing();
       ImGui.Spacing();
       ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "Ambient Light");
       ImGui.Separator();
       // ... (existing ambient mode, color, strength UI)

       ImGui.Spacing();
       ImGui.TextColored(new Vector4(0.7f, 0.8f, 1f, 1f), "Fog");
       ImGui.Separator();
       // ... (existing fog mode, color, density UI)
   }
   ```

### Step 5.2 — DirectionalLight Inspector

The public fields added in Step 1.2 (`OverrideSceneGI`, `GIModeOverride`, `GIIntensityOverride`) will automatically appear in the Inspector since Prowl serializes public fields. The existing inspector infrastructure handles this.

For improved UX, consider adding `[HideInInspector]` on `GIModeOverride` and `GIIntensityOverride` and manually drawing them only when `OverrideSceneGI` is true (via a custom property drawer or inline Inspector logic).

### Step 5.3 — GI Debug Visualization

**New file:** `Prowl.Runtime/Rendering/GI/GIDebugView.cs`

Add debug draw modes accessible from the Scene panel's view options dropdown:

| Mode | What it shows |
|------|---------------|
| `GI_IndirectOnly` | Only the GI contribution (no direct light, no ambient) |
| `GI_VoxelGrid` | Voxel grid as colored cubes (VoxelGI) |
| `GI_SDFSlice` | 2D slice through the SDF cascade (SDFGI) |
| `GI_ProbeGrid` | Probe positions as colored spheres (SDFGI) |

---

## 8. Phase 6 — Deferred Compose Integration

### Step 6.1 — Modify DeferredCompose Shader

**File:** `Prowl.Runtime/Assets/Defaults/DeferredCompose.shader`

Add a `_GIActive` uniform and modify the ambient calculation:

```glsl
uniform float _GIActive; // 0.0 = use ambient, 1.0 = GI replaces ambient

// In main(), replace lines 130-133:
if (shadingMode >= 0.5) {
    vec3 worldNormal = normalize(
        (inverse(transpose(PROWL_MATRIX_V)) * vec4(gbufferB.rgb * 2.0 - 1.0, 0.0)).xyz);

    vec3 ambient;
    if (_GIActive < 0.5) {
        // No GI: use scene ambient as before
        ambient = CalculateAmbient(worldNormal) * albedo * ao * _AmbientStrength;
    } else {
        // GI is active: indirect lighting is already in the accumulation buffer.
        // Apply only a small ambient fill to prevent total darkness in unlit corners.
        ambient = _AmbientColor.rgb * albedo * ao * _AmbientStrength * 0.1;
    }

    color = ambient + lightAccumulation;
}
```

### Step 6.2 — Set `_GIActive` from Pipeline

**File:** `Prowl.Runtime/Rendering/DefaultRenderPipeline.cs`, in the composition section (around line 406):

```csharp
bool giActive = giMode != Scene.GlobalIlluminationParams.GIMode.None;
_deferredCompose.SetFloat("_GIActive", giActive ? 1.0f : 0.0f);
```

---

## 9. Phase 7 — Performance & Quality

### Step 7.1 — Temporal Accumulation & Reprojection

**New file:** `Prowl.Runtime/Rendering/GI/GITemporalFilter.cs`

Both GI techniques produce noisy single-frame results. Temporal filtering greatly improves quality:

```csharp
public sealed class GITemporalFilter : IDisposable
{
    private RenderTexture? _previousGI;
    private Material? _temporalMat;

    /// <summary>
    /// Applies temporal reprojection blending.
    /// Uses motion vectors (prowl_PrevViewProj) to reproject previous frame's GI.
    /// Rejects disoccluded samples via depth/normal comparison.
    /// </summary>
    public void Apply(RenderTexture currentGI, RenderTexture gBuffer,
                      RenderPipeline.CameraSnapshot css, float blendFactor = 0.9f)
    {
        // 1. Reproject previous GI using inverse(current VP) * prevVP
        // 2. Compare depths; reject if delta > threshold
        // 3. Blend: output = lerp(current, reprojected, blendFactor)
        // 4. Copy result to _previousGI for next frame
    }

    public void Dispose() { /* dispose _previousGI, _temporalMat */ }
}
```

### Step 7.2 — Resolution Scaling

Add to `GlobalIlluminationParams`:

```csharp
/// <summary>
/// Resolution scale for GI tracing (0.25 = quarter, 0.5 = half, 1.0 = full).
/// Lower values significantly improve performance at the cost of detail.
/// Combined with bilateral upscale.
/// </summary>
public float ResolutionScale = 0.5f;
```

In cone-trace / probe-lookup passes:
```csharp
int giWidth = (int)(css.PixelWidth * giParams.ResolutionScale);
int giHeight = (int)(css.PixelHeight * giParams.ResolutionScale);
RenderTexture giResult = RenderTexture.GetTemporaryRT(giWidth, giHeight, false, [...]);
// ... render at reduced resolution, then bilateral upscale to full resolution
```

### Step 7.3 — Incremental / Partial Updates

**VoxelGI:**
- Divide the voxel grid into 8 octants. Revoxelize 1-2 octants per frame in a round-robin pattern.
- Camera-near octants get higher priority.
- Only fully revoxelize when the camera moves beyond a threshold or scene changes are detected.

**SDFGI:**
- The probe update shader already uses `_ProbeUpdateOffset` for round-robin.
- Update `_ProbeUpdateOffset` by `totalProbes / 4` each frame, so all probes are refreshed every 4 frames.
- When the camera moves, re-center cascades and invalidate edge probes.

### Step 7.4 — Geometry-Shader-Free Voxelization (Optional Optimization)

Replace the geometry shader in `VoxelGI_Voxelize.shader` with 3 separate render passes (one per axis), each using a simple orthographic projection. This avoids the geometry shader bottleneck on some GPUs (especially integrated GPUs and older AMD hardware).

---

## 10. Phase 8 — Cleanup & Quality Assurance

### Step 8.1 — Resource Disposal

Both `VoxelGISystem` and `SDFGISystem` hold GPU resources. Ensure:

- They implement `IDisposable` (not `EngineObject` — they're internal pipeline helpers, not scene objects).
- `DefaultRenderPipeline.OnDispose()` calls `_voxelGI?.Dispose()` and `_sdfGI?.Dispose()`.
- When GI mode changes at runtime (e.g., user toggles in the editor), dispose the inactive system's resources.
- When resolution/cascade settings change, detect the change in `EnsureResources` and recreate textures.

### Step 8.2 — Event System Integration

**File:** `Prowl.Runtime/EventSystem/RenderingEvents.cs` — add GI-related events:

```csharp
/// <summary>Raised after voxelization or SDF update completes for the frame.</summary>
[EventArgs(typeof(GIUpdateArgs))]
private static readonly EventKey _OnGIDataUpdated = new();

/// <summary>Raised after GI probes have been updated (SDFGI only).</summary>
[EventArgs(typeof(GIUpdateArgs))]
private static readonly EventKey _OnGIProbesUpdated = new();
```

**Event args:**
```csharp
public readonly record struct GIUpdateArgs(
    Scene.GlobalIlluminationParams.GIMode Mode,
    float UpdateTimeMs);
```

### Step 8.3 — Profiler Integration

**File:** `Prowl.Runtime/Profiling/BuiltInProfilerSections.cs` — add in the `Register()` method:

```csharp
// ── Global Illumination ──────────────────────────────────
Profiler.RegisterSection("VoxelGI.Voxelize",
    "GI",
    "Rasterizes opaque geometry into a 3D voxel grid using " +
    "dominant-axis projection and imageStore.");

Profiler.RegisterSection("VoxelGI.InjectLight",
    "GI",
    "Injects direct illumination from the primary directional " +
    "light into occupied voxels via compute shader.");

Profiler.RegisterSection("VoxelGI.Mipmap",
    "GI",
    "Generates the anisotropic mipmap chain of the voxel radiance " +
    "volume for pre-filtered cone tracing.");

Profiler.RegisterSection("VoxelGI.ConeTrace",
    "GI",
    "Traces diffuse and specular cones through the voxel mipmap chain " +
    "to compute per-pixel indirect illumination.");

Profiler.RegisterSection("SDFGI.UpdateSDF",
    "GI",
    "Merges per-object signed distance fields into global SDF cascades " +
    "centered around the camera.");

Profiler.RegisterSection("SDFGI.UpdateProbes",
    "GI",
    "Incrementally updates irradiance probes by tracing rays through " +
    "the global SDF and accumulating spherical harmonics.");

Profiler.RegisterSection("SDFGI.TraceGI",
    "GI",
    "Fullscreen pass that trilinearly interpolates the probe grid to " +
    "compute per-pixel indirect diffuse illumination.");
```

Wrap each GI pass in the orchestrator classes:
```csharp
using (Profiler.Section("VoxelGI.Voxelize"))
{
    // ... voxelization dispatch
}
```

### Step 8.4 — Unit Tests

**New file:** `Prowl.Runtime.Test/Rendering/GI/VoxelGISystemTests.cs`

```csharp
public class VoxelGISystemTests : IDisposable
{
    [Fact]
    public void EnsureResources_CreatesTexturesAtCorrectResolution() { ... }

    [Fact]
    public void WorldToVoxelCoord_MapsCorrectly() { ... }

    [Fact]
    public void Dispose_ReleasesAllResources() { ... }

    public void Dispose() { /* cleanup */ }
}
```

**New file:** `Prowl.Runtime.Test/Rendering/GI/SDFGISystemTests.cs`

```csharp
public class SDFGISystemTests : IDisposable
{
    [Fact]
    public void EnsureResources_CreatesCascadesWithCorrectSizes() { ... }

    [Fact]
    public void ProbeGrid_LayoutMatchesResolution() { ... }

    [Fact]
    public void CascadeScaling_DoublesPerLevel() { ... }

    public void Dispose() { /* cleanup */ }
}
```

**New file:** `Prowl.Runtime.Test/Rendering/GI/MeshSDFCacheTests.cs`

```csharp
public class MeshSDFCacheTests : IDisposable
{
    [Fact]
    public void GetOrGenerate_CachesSameResult() { ... }

    [Fact]
    public void Invalidate_RemovesEntry() { ... }

    [Fact]
    public void Clear_DisposesAll() { ... }

    public void Dispose() { /* cleanup */ }
}
```

### Step 8.5 — Graceful Degradation

When compute shaders aren't available or when the GPU runs out of memory:

```csharp
// In VoxelGISystem.EnsureResources:
if (!Graphics.SupportsComputeShaders)
{
    Debug.LogWarning("VoxelGI requires compute shader support. Falling back to ambient lighting.");
    return;
}

// Memory budget check
long estimatedMemory = resolution * resolution * resolution * 8L * 2; // 2 volumes × RGBA16F
if (estimatedMemory > maxBudgetBytes)
{
    Debug.LogWarning($"VoxelGI at resolution {resolution} exceeds memory budget. Reducing to {resolution/2}.");
    resolution /= 2;
}
```

---

## 11. File Manifest

### New Files — Prowl.Runtime

| File | Purpose |
|------|---------|
| `Rendering/GI/GIUtils.cs` | Shared GI utilities (resolve settings, coordinate helpers) |
| `Rendering/GI/VoxelGISystem.cs` | VoxelGI orchestrator (voxelize → inject → mipmap → cone trace) |
| `Rendering/GI/SDFGISystem.cs` | SDFGI orchestrator (SDF gen → merge → probe update → trace) |
| `Rendering/GI/MeshSDFCache.cs` | Per-mesh SDF caching |
| `Rendering/GI/GITemporalFilter.cs` | Temporal reprojection/accumulation for both techniques |
| `Rendering/GI/GIDebugView.cs` | Debug visualization modes |
| `Rendering/GI/Texture3DRT.cs` | 3D render texture wrapper for Graphite compute |
| `Assets/Defaults/GI_Common.glsl` | Shared GLSL: SH, coord transforms, SDF marching |
| `Assets/Defaults/VoxelGI_Voxelize.shader` | Scene voxelization (vertex + geometry + fragment) |
| `Assets/Defaults/VoxelGI_InjectLight.shader` | Direct light injection (compute) |
| `Assets/Defaults/VoxelGI_Mipmap.shader` | Mipmap generation (compute) |
| `Assets/Defaults/VoxelGI_ConeTrace.shader` | Cone tracing (fullscreen fragment) |
| `Assets/Defaults/SDFGI_GenerateSDF.shader` | Per-mesh SDF generation (compute) |
| `Assets/Defaults/SDFGI_MergeSDF.shader` | Global SDF cascade merge (compute) |
| `Assets/Defaults/SDFGI_ProbeUpdate.shader` | Irradiance probe update (compute) |
| `Assets/Defaults/SDFGI_ProbeTrace.shader` | Probe lookup (fullscreen fragment) |

### Modified Files — Prowl.Runtime

| File | Lines | Change |
|------|-------|--------|
| `Resources/Scene.cs` | ~255 | Add `GlobalIlluminationParams` struct + `GlobalIllumination` field |
| `Components/Lights/DirectionalLight.cs` | ~34 | Add `OverrideSceneGI`, `GIModeOverride`, `GIIntensityOverride` fields |
| `Rendering/DefaultRenderPipeline.cs` | ~60, ~243, ~375 | Add GI system fields; insert voxelize/SDF and cone-trace/probe-lookup passes |
| `Rendering/DefaultRenderPipelineAsset.cs` | ~53 | (Optional) Add GI quality settings |
| `Assets/Defaults/DeferredCompose.shader` | ~130 | Add `_GIActive` uniform; conditional ambient |
| `Resources/DefaultAssets.cs` | ~37, ~77 | Add new `DefaultShader` and `DefaultShaderInclude` enum values |
| `EventSystem/RenderingEvents.cs` | ~23 | Add `_OnGIDataUpdated`, `_OnGIProbesUpdated` events |
| `Profiling/BuiltInProfilerSections.cs` | ~50+ | Register GI profiler sections |

### Modified Files — Prowl.Editor

| File | Lines | Change |
|------|-------|--------|
| `Panels/ProjectSettingsPanel.cs` | ~29, ~79 | Add "Lighting" tab; `DrawLightingPage()` method |

### New Test Files

| File | Purpose |
|------|---------|
| `Prowl.Runtime.Test/Rendering/GI/VoxelGISystemTests.cs` | VoxelGI resource and coordinate tests |
| `Prowl.Runtime.Test/Rendering/GI/SDFGISystemTests.cs` | SDFGI cascade and probe layout tests |
| `Prowl.Runtime.Test/Rendering/GI/MeshSDFCacheTests.cs` | SDF cache lifecycle tests |

---

## 12. Implementation Order

Recommended sequence that builds dependencies bottom-up:

| Step | Phase | Description | Depends On |
|------|-------|-------------|------------|
| 1 | 0.1 | Verify compute shader support on GL + Vulkan | — |
| 2 | 0.2 | `Texture3DRT` wrapper for Graphite | 0.1 |
| 3 | 0.3 | Storage-image bind group entries | 0.1 |
| 4 | 0.4 | Compute shader support in shader parser | 0.1 |
| 5 | 1.1 | `GlobalIlluminationParams` on `Scene` | — |
| 6 | 1.2 | `DirectionalLight` GI override fields | 5 |
| 7 | 1.3 | `GIUtils.ResolveGISettings` | 5, 6 |
| 8 | 4.1-4.4 | Register new `DefaultShader` enums + GLSL include | — |
| 9 | 2.1 | `VoxelGISystem` skeleton | 2, 3, 7, 8 |
| 10 | 2.2 | `VoxelGI_Voxelize.shader` | 4, 8 |
| 11 | 2.3 | `VoxelGI_InjectLight.shader` | 4, 8 |
| 12 | 2.4 | `VoxelGI_Mipmap.shader` | 4, 8 |
| 13 | 2.5 | `VoxelGI_ConeTrace.shader` | 8 |
| 14 | 2.6 | Pipeline integration (VoxelGI) | 9-13 |
| 15 | 6.1-6.2 | `DeferredCompose` `_GIActive` integration | 14 |
| 16 | 5.1 | Editor "Lighting" tab in ProjectSettingsPanel | 5 |
| 17 | 7.1 | `GITemporalFilter` | 14 |
| 18 | 7.2 | Resolution scaling + bilateral upscale | 14 |
| 19 | 3.1 | `SDFGISystem` skeleton | 2, 3, 7, 8 |
| 20 | 3.2 | `MeshSDFCache` | 2 |
| 21 | 3.3 | `SDFGI_GenerateSDF.shader` | 4, 8, 20 |
| 22 | 3.4 | `SDFGI_MergeSDF.shader` | 4, 8 |
| 23 | 3.5 | `SDFGI_ProbeUpdate.shader` | 4, 8 |
| 24 | 3.6 | `SDFGI_ProbeTrace.shader` | 8 |
| 25 | 3.7 | Pipeline integration (SDFGI) | 19-24 |
| 26 | 5.3 | `GIDebugView` | 14, 25 |
| 27 | 8.1 | Disposal audit | 14, 25 |
| 28 | 8.2 | Event system integration | 14, 25 |
| 29 | 8.3 | Profiler sections | 14, 25 |
| 30 | 8.4 | Unit tests | 9, 19, 20 |
| 31 | 8.5 | Graceful degradation | 14, 25 |

---

## 13. Key Technical Decisions

| Decision | Rationale |
|----------|-----------|
| **GI writes to light accumulation buffer** | Matches SSPT's integration point (`RenderStage.DuringLighting`). GI adds to light accumulation before the compose step, so `DeferredCompose` sees the indirect contribution in `_LightAccumulation`. |
| **3D textures via Graphite (not legacy GL)** | Compute read/write requires Graphite's storage-image bindings. Legacy `Texture3D` is GL-only and lacks compute support. |
| **Per-scene GI settings (not per-camera)** | GI structures (voxel grid, SDF cascades, probes) are world-space and expensive. Per-camera would duplicate all GPU memory. Camera just reads the shared result. |
| **DirectionalLight override is optional** | Most users want scene-level control. Power users who need per-light testing or artistic override can enable it explicitly. |
| **VoxelGI implemented before SDFGI** | VoxelGI is better documented in academic literature, has simpler data structures (single 3D texture vs. multi-cascade SDF + probe grid), and is easier to debug visually (render the voxel grid). |
| **L1 SH for SDFGI probes (not octahedral)** | 4 SH coefficients per channel = 12 floats per probe. Fits in one `RGBA16F` texel. Fast to evaluate. Sufficient for low-frequency diffuse GI. |
| **Isotropic mipmaps first (VoxelGI)** | 6-directional anisotropic mipmaps are the ideal but require 6× more memory and complexity. Standard mipmaps work acceptably for diffuse GI and are trivially simpler. Upgrade to anisotropic in a later pass. |
| **Geometry shader voxelization first** | The standard approach. 3-pass axis-aligned alternative is faster on some GPUs but requires more C# orchestration code. Start with the single-pass GS approach, optimize later. |
| **Incremental probe updates (SDFGI)** | Updating all probes every frame is too expensive. Round-robin with hysteresis blending (0.95-0.98) converges within a few frames and stays temporally stable. |
| **Temporal filter shared between both techniques** | Both produce noisy single-frame results. The same reprojection + depth-rejection + blend logic applies to both, just operating on different input textures. |
| **GI disabled by default (`Mode = None`)** | Zero performance cost when not opted in. Existing scenes continue to work unchanged. |

---

## References

- **Voxel Cone Tracing:** Crassin et al., "Interactive Indirect Illumination Using Voxel Cone Tracing" (2011)
- **SDFGI:** Godot Engine implementation, Wright et al., "Radiance Cascades" concepts
- **DDGI (Dynamic Diffuse GI):** Majercik et al., "Dynamic Diffuse Global Illumination with Ray-Traced Irradiance Fields" (2019) — probe update strategy
- **SDF Generation:** Quilez, "Distance Functions" (iquilezles.org)
- **Spherical Harmonics:** Sloan, "Stupid Spherical Harmonics Tricks" (2008)
