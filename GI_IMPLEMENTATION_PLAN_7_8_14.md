# Implementation Plan — GI Issues #7, #8, #14

> **Goal:** Implement SDF merge (real per-object SDF generation & merging), move inline compute GLSL to a maintainable form with `#include` support, and wire up the remaining `GIDebugView` visualization modes — without breaking the Vulkan or OpenGL backends.

---

## Table of Contents

1. [Issue #8 — Inline GLSL Compute Sources](#issue-8--inline-glsl-compute-sources)
2. [Issue #7 — SDF Merge Placeholder → Real SDF Generation](#issue-7--sdf-merge-placeholder--real-sdf-generation)
3. [Issue #14 — GIDebugView Visualization Modes](#issue-14--gidebugview-visualization-modes)
4. [Cross-Cutting Concerns](#cross-cutting-concerns)
5. [Testing Strategy](#testing-strategy)
6. [Risk Matrix](#risk-matrix)

> **Implementation order:** #8 → #7 → #14. Issue #8 unlocks `#include "GICommon"` inside compute shaders, which #7's SDF kernels need. #14 is independent but benefits from correct GI data produced by #7.

---

## Issue #8 — Inline GLSL Compute Sources

### Problem Recap

Five compute shaders are embedded as raw C# string constants:

| Constant | File | Purpose |
|----------|------|---------|
| `VoxelizeClearComputeSource` | `VoxelGISystem.cs:401` | Clear voxel grid |
| `VoxelizeFillComputeSource` | `VoxelGISystem.cs:425` | AABB voxel fill |
| `InjectLightComputeSource` | `VoxelGISystem.cs:468` | Direct light injection |
| `MergeSDFComputeSource` | `SDFGISystem.cs:379` | SDF cascade initialization (placeholder) |
| `ProbeUpdateComputeSource` | `SDFGISystem.cs:410` | Probe irradiance update |

These bypass the engine's `#include` resolution (no access to `GICommon.glsl`), have no hot-reload, and surface SPIR-V compilation errors only at runtime.

### Chosen Approach — Preprocessed Embedded GLSL Strings

The engine's `ShaderParser` only handles vertex/fragment `.shader` files — it has no concept of a compute-only stage (`Shared { } Compute { }`). Adding a full `.compute` asset pipeline with new importer, `ComputeVariant`, and editor support is a significant effort that goes beyond the scope of this fix.

Instead, we will:

1. **Move each inline GLSL source** into its own `.glsl` file under `Prowl.Runtime/Assets/Defaults/Compute/`.
2. **Add a lightweight include preprocessor** in a helper method that reuses the engine's existing `EmbeddedResources.ReadAllText()` and regex-based `#include` resolution pattern (identical to what `ShaderParser` does for vertex/fragment includes).
3. **Load the compute source at kernel creation** via `EmbeddedResources.ReadAllText()` → preprocess → pass to `ComputeKernel`.

This gives us `#include "GICommon"` support, keeps all GLSL in proper files with syntax highlighting, and avoids any changes to `ShaderParser`, `ComputeKernel`, or the asset import pipeline.

### Step-by-Step

#### Step 8.1 — Create compute GLSL source files

Create the following files (auto-embedded by the existing `<EmbeddedResource Include="Assets\Defaults\**" />` glob in `Prowl.Runtime.csproj`):

| New File | Content source |
|----------|---------------|
| `Assets/Defaults/Compute/VoxelGI_Clear.glsl` | `VoxelizeClearComputeSource` constant |
| `Assets/Defaults/Compute/VoxelGI_Fill.glsl` | `VoxelizeFillComputeSource` constant |
| `Assets/Defaults/Compute/VoxelGI_InjectLight.glsl` | `InjectLightComputeSource` constant |
| `Assets/Defaults/Compute/SDFGI_MergeSDF.glsl` | `MergeSDFComputeSource` constant |
| `Assets/Defaults/Compute/SDFGI_ProbeUpdate.glsl` | `ProbeUpdateComputeSource` constant |

Each file is **pure GLSL** (no `#version` — `ComputeKernel` already prepends that). Files may use `#include "GICommon"` where needed.

Example header for `VoxelGI_InjectLight.glsl`:
```glsl
// VoxelGI_InjectLight.glsl — Direct light injection compute shader
// Dispatched by VoxelGISystem.InjectDirectLight()

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(rgba16f, binding = 0) uniform image3D VoxelRadiance;

layout(std140, binding = 1) uniform Params
{
    vec3 _LightDirection;
    float _LightIntensity;
    // ...
};

void main()
{
    // ... (same logic as current inline string)
}
```

#### Step 8.2 — Create `ComputeShaderLoader` helper

Create `Prowl.Runtime/Rendering/Compute/ComputeShaderLoader.cs`:

```csharp
namespace Prowl.Runtime.Rendering.Compute;

/// <summary>
/// Loads embedded compute GLSL sources with #include preprocessing.
/// Reuses the engine's EmbeddedResources for file resolution.
/// </summary>
public static class ComputeShaderLoader
{
    private static readonly Regex s_includeRegex =
        new(@"^\s*#include\s*""(.+?)""\s*$", RegexOptions.Multiline);

    /// <summary>
    /// Loads a compute shader source from embedded resources and resolves #include directives.
    /// </summary>
    /// <param name="resourcePath">
    /// Path relative to Assets/Defaults, e.g. "Compute/VoxelGI_Clear" (no extension).
    /// The ".glsl" extension is appended automatically.
    /// </param>
    public static string Load(string resourcePath)
    {
        string source = EmbeddedResources.ReadAllText(
            $"Assets/Defaults/{resourcePath}.glsl");

        // Recursively resolve #include directives
        return s_includeRegex.Replace(source, match =>
        {
            string includeName = match.Groups[1].Value;
            // Resolve from DefaultShaderInclude embedded resources
            string? includeSource = EmbeddedResources.TryReadAllText(
                $"Assets/Defaults/{includeName}.glsl");

            if (includeSource == null)
            {
                Debug.LogError($"ComputeShaderLoader: Include not found: {includeName}");
                return "";
            }

            // Recursively resolve nested includes
            return s_includeRegex.Replace(includeSource, match2 => /* recurse */);
        });
    }
}
```

Key design decisions:
- The method is **synchronous** and runs once per kernel creation (not per frame).
- Include paths match the existing `DefaultShaderInclude` names (`GICommon`, `ShaderVariables`, etc.).
- No changes to `ComputeKernel` — it still takes a plain GLSL string.

#### Step 8.3 — Update `VoxelGISystem` to use `ComputeShaderLoader`

Replace each inline constant with a `ComputeShaderLoader.Load()` call at kernel creation time:

```csharp
// Before (VoxelGISystem.cs, EnsureResources):
_voxelClearKernel = new ComputeKernel(VoxelizeClearComputeSource, clearLayout, "VoxelGI_Clear");

// After:
string clearSource = ComputeShaderLoader.Load("Compute/VoxelGI_Clear");
_voxelClearKernel = new ComputeKernel(clearSource, clearLayout, "VoxelGI_Clear");
```

Repeat for `_voxelFillKernel` and `_injectLightKernel`.

Remove the `#region Compute Shader Sources` block and all three `private const string` fields.

#### Step 8.4 — Update `SDFGISystem` to use `ComputeShaderLoader`

Same pattern for `_mergeSDFKernel` and `_probeUpdateKernel`. Remove inline GLSL constants.

#### Step 8.5 — Verify embedded resource glob

Check that `Prowl.Runtime.csproj` already includes:
```xml
<EmbeddedResource Include="Assets\Defaults\**" />
```
This covers the new `Assets/Defaults/Compute/*.glsl` files automatically. **No csproj changes needed.**

#### Vulkan Compatibility Notes for #8

- **No behavioral change.** `ComputeKernel` still receives a GLSL string and compiles it to SPIR-V via shaderc. The only difference is the source comes from an embedded resource file instead of a C# constant.
- **`#version` is still prepended by `ComputeKernel`.** The `.glsl` files must NOT contain `#version` directives.
- **`#define PROWL_VULKAN 1`** is still prepended by `ComputeKernel` on Vulkan. The include-resolved source is backend-agnostic.
- **Include resolution runs on the CPU** before compilation. No GPU-side changes.

---

## Issue #7 — SDF Merge Placeholder → Real SDF Generation

### Problem Recap

1. `MeshSDFCache.GetOrGenerate()` allocates a `Texture3DRT` but never populates SDF data.
2. `SDFGISystem.MergeSDFComputeSource` only initializes all voxels to max distance (empty space). It never reads per-object SDFs.
3. `ProbeUpdateComputeSource` does sky-only approximation because the SDF is always empty.

### Architecture Overview

The SDF pipeline has two phases:
1. **Per-mesh SDF generation** (`MeshSDFCache`) — generate a distance field for each unique mesh once, cached by `InstanceID`.
2. **Global SDF merge** (`SDFGISystem.UpdateGlobalSDF`) — for each cascade, iterate over scene objects, transform their per-mesh SDFs into world space, and take the minimum distance at each voxel.

### Step-by-Step

#### Step 7.1 — Implement per-mesh SDF generation compute shader

Create `Assets/Defaults/Compute/SDFGI_GenerateMeshSDF.glsl`:

```glsl
// SDFGI_GenerateMeshSDF.glsl — Brute-force SDF generation from mesh triangles
// Dispatched once per unique mesh by MeshSDFCache.

#include "GICommon"

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(r16f, binding = 0) uniform image3D MeshSDF;

// Triangle data packed as SSBO
layout(std430, binding = 2) readonly buffer TriangleData
{
    vec4 triangles[]; // Each triangle = 3 consecutive vec4 (xyz + padding)
};

layout(std140, binding = 1) uniform Params
{
    vec3  _BoundsMin;      // AABB min (with padding margin)
    float _BoundsExtent;   // Max extent of padded AABB
    int   _SDFResolution;  // Typically 32 or 64
    int   _TriangleCount;  // Number of triangles
};

// Point-triangle unsigned distance (Inigo Quilez's method)
float PointTriangleDistance(vec3 p, vec3 a, vec3 b, vec3 c)
{
    vec3 ba = b - a; vec3 pa = p - a;
    vec3 cb = c - b; vec3 pb = p - b;
    vec3 ac = a - c; vec3 pc = p - c;
    vec3 nor = cross(ba, ac);

    float signCheck = sign(dot(cross(ba, nor), pa))
                    + sign(dot(cross(cb, nor), pb))
                    + sign(dot(cross(ac, nor), pc));

    if (signCheck < 2.0)
    {
        // Outside — distance to nearest edge
        float d1 = dot(ba, clamp(dot(ba, pa) / dot(ba, ba), 0.0, 1.0)) - dot(pa, pa);
        // (use full Quilez implementation)
        return sqrt(min(min(
            dot2(ba * clamp(dot(ba, pa) / dot(ba, ba), 0.0, 1.0) - pa),
            dot2(cb * clamp(dot(cb, pb) / dot(cb, cb), 0.0, 1.0) - pb)),
            dot2(ac * clamp(dot(ac, pc) / dot(ac, ac), 0.0, 1.0) - pc)));
    }
    else
    {
        // Inside triangle — distance to plane
        return sqrt(dot(nor, pa) * dot(nor, pa) / dot(nor, nor));
    }
}

void main()
{
    ivec3 coord = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(coord, ivec3(_SDFResolution))))
        return;

    // Map voxel coordinate to local-space position
    vec3 localPos = _BoundsMin +
        (vec3(coord) + 0.5) / float(_SDFResolution) * _BoundsExtent;

    float minDist = 1e10;
    for (int i = 0; i < _TriangleCount; i++)
    {
        vec3 v0 = triangles[i * 3 + 0].xyz;
        vec3 v1 = triangles[i * 3 + 1].xyz;
        vec3 v2 = triangles[i * 3 + 2].xyz;
        float d = PointTriangleDistance(localPos, v0, v1, v2);
        minDist = min(minDist, d);
    }

    // Normalize distance to SDF extent so values are in [0, 1] range relative to bounds
    float normalizedDist = minDist / _BoundsExtent;

    imageStore(MeshSDF, coord, vec4(normalizedDist, 0.0, 0.0, 0.0));
}
```

**Design notes:**
- Unsigned distance field (UDF) is simpler and sufficient for GI probe queries. Signed distance is harder (requires inside/outside determination via winding number or ray-parity) and can be a future enhancement.
- Brute-force O(voxels × triangles) is acceptable at resolution 32³ with typical meshes (<50K triangles). For 64³ or high-poly meshes, a future BVH or jump-flood optimization can be added.
- Triangle data is passed via SSBO (`std430`), which is supported on both Vulkan and OpenGL 4.3+.

#### Step 7.2 — Add SSBO support to `ComputeDispatcher`

Currently `ComputeDispatcher.Dispatch()` supports:
- `images` — storage textures (binding slot → `Graphite.Texture`)
- `textures` — sampled textures (binding slot → `Graphite.Texture`)
- `uniforms` — `ComputeUniforms` (std140 uniform buffer at a fixed binding)

We need to add:
- `buffers` — storage buffers (binding slot → `Graphite.Buffer`)

**Changes to `ComputeDispatcher.cs`:**

Add a new optional parameter to `Dispatch()`:

```csharp
public static void Dispatch(
    CommandList cmd,
    ComputeKernel kernel,
    uint groupsX, uint groupsY, uint groupsZ,
    ComputeUniforms? uniforms = null,
    (int binding, Graphite.Texture texture)[]? images = null,
    (int binding, Graphite.Texture texture)[]? textures = null,
    (int binding, Graphite.Buffer buffer)[]? storageBuffers = null)  // NEW
```

In the bind group construction loop, add entries for storage buffers:

```csharp
if (storageBuffers != null)
{
    foreach ((int binding, Graphite.Buffer buffer) in storageBuffers)
    {
        entries.Add(new BindGroupEntry
        {
            Binding = (uint)binding,
            Buffer = buffer,
            Offset = 0,
            Size = buffer.Size,
        });
    }
}
```

**Vulkan compatibility:** `VkDescriptorType.STORAGE_BUFFER` is a standard descriptor type. The bind group layout entry `BindGroupLayoutEntry.StorageBuffer(binding, stage, name)` must be added — check if this method exists. If not, add it to `BindGroupLayoutEntry` following the same pattern as `StorageTexture` and `UniformBuffer`.

**Verification:** Check if `BindGroupLayoutEntry` has a `StorageBuffer` factory. If not:

```csharp
// Add to BindGroupLayoutEntry (Prowl.Runtime/Graphite/Descriptors/BindGroupDescriptor.cs)
public static BindGroupLayoutEntry StorageBuffer(uint binding, ShaderStage visibility, string? name = null)
{
    return new BindGroupLayoutEntry
    {
        Binding = binding,
        Visibility = visibility,
        Type = BindingType.StorageBuffer,
        Name = name,
    };
}
```

Also verify that `BindingType.StorageBuffer` exists in the `BindingType` enum. If not, add it. Then ensure the Vulkan backend (`VKBindGroup`) handles this type in `CreateDescriptorSet`.

#### Step 7.3 — Update `MeshSDFCache` to dispatch SDF generation

```csharp
public static Texture3DRT? GetOrGenerate(Resources.Mesh mesh)
{
    int id = mesh.InstanceID;
    if (s_cache.TryGetValue(id, out Texture3DRT? existing) && existing.IsValid())
        return existing;

    if (!Graphics.IsGraphiteReady || !mesh.isReadable)
        return null;

    uint res = (uint)MeshSDFResolution;
    Texture3DRT sdf = new Texture3DRT(res, res, res, TextureImageFormat.Short);

    // Upload triangle data to GPU buffer
    Float3[] verts = mesh.Vertices;
    uint[] indices = mesh.Indices;
    int triCount = indices.Length / 3;

    if (triCount == 0)
    {
        s_cache[id] = sdf;
        return sdf;
    }

    // Pack triangle vertices into vec4[] (xyz + padding)
    float[] triData = new float[triCount * 3 * 4]; // 3 verts × vec4
    for (int t = 0; t < triCount; t++)
    {
        for (int v = 0; v < 3; v++)
        {
            Float3 vert = verts[indices[t * 3 + v]];
            int offset = (t * 3 + v) * 4;
            triData[offset + 0] = vert.X;
            triData[offset + 1] = vert.Y;
            triData[offset + 2] = vert.Z;
            triData[offset + 3] = 0f; // padding
        }
    }

    // Create GPU storage buffer
    Graphite.Buffer triBuf = Graphics.Graphite.CreateBuffer(new BufferDescriptor
    {
        Size = (ulong)(triData.Length * sizeof(float)),
        Usage = BufferUsage.Storage,
        MemoryAccess = MemoryAccess.CpuToGpu,
    });
    // Upload data
    triBuf.SetData(triData);

    // Compute padded AABB
    AABB bounds = mesh.bounds;
    float padding = 0.1f; // 10% margin
    Float3 extent = bounds.Max - bounds.Min;
    float maxExtent = Math.Max(extent.X, Math.Max(extent.Y, extent.Z)) * (1f + padding);
    Float3 boundsMin = bounds.Center - new Float3(maxExtent * 0.5f);

    // Dispatch SDF generation
    RenderCommandBuffer? cmdBuffer = Graphics.ActiveGraphiteCmdBuffer;
    if (cmdBuffer != null)
    {
        CommandList cmd = cmdBuffer.CommandList;

        // Create or get the kernel (cached statically)
        EnsureGenerateKernel();

        s_generateUniforms!.Clear();
        s_generateUniforms.SetVector3("_BoundsMin", boundsMin);
        s_generateUniforms.SetFloat("_BoundsExtent", maxExtent);
        s_generateUniforms.SetInt("_SDFResolution", (int)res);
        s_generateUniforms.SetInt("_TriangleCount", triCount);

        uint groups = ComputeDispatcher.WorkGroupCount((int)res, 4);
        ComputeDispatcher.Dispatch(cmd, s_generateKernel!, groups, groups, groups,
            s_generateUniforms,
            images: [(0, sdf.GraphiteTexture!)],
            storageBuffers: [(2, triBuf)]);
    }

    // Retire the triangle buffer (GPU will read it, then it gets freed)
    GraphiteMaterialBinder.Retire(triBuf);

    s_cache[id] = sdf;
    return sdf;
}
```

**Vulkan compatibility notes:**
- The triangle buffer is created as `CpuToGpu` for direct upload (no staging buffer needed since it's write-once).
- The buffer is retired via `GraphiteMaterialBinder.Retire()` so it is not freed while the GPU is still reading it. The retire mechanism already waits for the fence of the frame that last used the buffer.
- The storage texture (`image3D MeshSDF`) barrier is handled automatically by `ComputeDispatcher.Dispatch()` which transitions images to `General` before dispatch.
- SDF generation is a **one-shot operation** per mesh (cached). It runs inline on the current command buffer during the GI update step.

**Important:** SDF generation must happen **before** `UpdateGlobalSDF` needs the SDF textures. The call sequence in `DefaultRenderPipeline` is:
1. `_sdfGI.EnsureResources(...)` — allocates cascades
2. `_sdfGI.UpdateGlobalSDF(renderables, css)` — this is where per-mesh SDFs are needed

The cleanest approach is to have `UpdateGlobalSDF` call `MeshSDFCache.GetOrGenerate(mesh)` for each renderable. The first frame will trigger generation; subsequent frames use the cache.

#### Step 7.4 — Implement real SDF merge in `MergeSDFComputeSource`

Update the `SDFGI_MergeSDF.glsl` compute shader to:
1. Clear to max distance (as currently).
2. For each object SDF, transform the voxel's world position into the object's local space, sample the per-mesh SDF, and take the minimum distance.

This requires passing per-object data. Two approaches:

**Option A — Multi-dispatch (simpler, chosen):**
Dispatch the merge kernel once per object (similar to how VoxelGI dispatches fill once per renderable). Each dispatch provides the object's transform and mesh SDF texture. The shader reads the current global SDF value, computes the object's SDF contribution, and writes `min(existing, objectSDF)`.

This requires two passes:
1. **Clear pass:** Initialize cascade to max distance (existing behavior).
2. **Per-object pass:** For each renderable with a valid mesh SDF, dispatch a kernel that blends the object's SDF into the global cascade.

Create `Assets/Defaults/Compute/SDFGI_BlendObjectSDF.glsl`:

```glsl
// SDFGI_BlendObjectSDF.glsl — Blend one object's SDF into the global cascade
// Dispatched once per object per cascade by SDFGISystem.UpdateGlobalSDF()

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(r16f, binding = 0) uniform image3D GlobalSDF;
layout(binding = 2) uniform sampler3D ObjectSDF;  // Per-mesh SDF from cache

layout(std140, binding = 1) uniform Params
{
    vec3  _CascadeCenter;
    float _CascadeSize;
    int   _CascadeResolution;
    // Object transform: world position of AABB center + extent
    vec3  _ObjectCenter;   // world-space center of the mesh AABB
    float _ObjectExtent;   // half-extent of the padded AABB (same as _BoundsExtent * 0.5 from generation)
};

void main()
{
    ivec3 coord = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(coord, ivec3(_CascadeResolution))))
        return;

    // Current global SDF distance
    float currentDist = imageLoad(GlobalSDF, coord).r;

    // World position of this cascade voxel
    vec3 worldPos = _CascadeCenter +
        ((vec3(coord) + 0.5) / float(_CascadeResolution) - 0.5) * 2.0 * _CascadeSize;

    // Transform to object's local SDF space [0, 1]
    vec3 localUVW = (worldPos - _ObjectCenter) / (_ObjectExtent * 2.0) + 0.5;

    // Skip if outside the object's SDF volume
    if (any(lessThan(localUVW, vec3(0.0))) || any(greaterThan(localUVW, vec3(1.0))))
        return;

    // Sample object SDF (normalized distance) and convert to world-space distance
    float objectDist = texture(ObjectSDF, localUVW).r * _ObjectExtent * 2.0;

    // Take minimum (union of all objects)
    float mergedDist = min(currentDist, objectDist);
    imageStore(GlobalSDF, coord, vec4(mergedDist, 0.0, 0.0, 0.0));
}
```

**Bind group layout for the blend kernel:**
```csharp
BindGroupLayoutEntry[] blendLayout =
[
    BindGroupLayoutEntry.StorageTexture(0, ShaderStage.Compute, "GlobalSDF"),
    BindGroupLayoutEntry.UniformBuffer(1, ShaderStage.Compute, name: "Params"),
    BindGroupLayoutEntry.SampledTexture(2, ShaderStage.Compute, "ObjectSDF"),
    BindGroupLayoutEntry.Sampler(3, ShaderStage.Compute, "ObjectSDFSampler"),
];
```

**Note:** On Vulkan, the `sampler3D` in GLSL is a combined image-sampler. The bind group must provide both a texture view and a sampler. The `ComputeDispatcher` currently only handles storage images and uniforms. We need to add `textures` support (already present in the `Dispatch` signature based on the code I read — parameter exists but may need the sampler pairing). Verify that `ComputeDispatcher.Dispatch()` `textures` parameter creates a combined image-sampler bind group entry with a default sampler.

If not, the `textures` array entries need a sampler. Check the existing `Dispatch()` code for how `textures` are handled — from the code read earlier, the `textures` parameter creates `BindGroupEntry` with just a texture view. On Vulkan, combined image-samplers also need a sampler. Add a default linear-clamp sampler to `ComputeDispatcher` for this purpose.

#### Step 7.5 — Update `SDFGISystem.UpdateGlobalSDF()` to use real merge

```csharp
public void UpdateGlobalSDF(
    IReadOnlyList<IRenderable> renderables,
    RenderPipeline.CameraSnapshot css)
{
    // ... existing null checks ...

    using (Profiler.Section("SDFGI.UpdateSDF"))
    {
        CommandList cmd = ...;

        // Re-center cascades
        for (int i = 0; i < _cascadeCount; i++)
            _cascades[i].Center = css.CameraPosition;

        for (int c = 0; c < _cascadeCount; c++)
        {
            SDFCascade cascade = _cascades[c];
            Graphite.Texture? sdfTex = cascade.SDFTexture?.GraphiteTexture;
            if (sdfTex == null) continue;

            // Pass 1: Clear cascade to max distance (existing)
            _mergeSDFUniforms!.Clear();
            _mergeSDFUniforms.SetVector3("_CascadeCenter", cascade.Center);
            _mergeSDFUniforms.SetFloat("_CascadeSize", cascade.WorldSize);
            _mergeSDFUniforms.SetInt("_CascadeResolution", 64);
            uint groupSize = ComputeDispatcher.WorkGroupCount(64, 4);
            ComputeDispatcher.Dispatch(cmd, _mergeSDFKernel!, groupSize, groupSize, groupSize,
                _mergeSDFUniforms, images: [(0, sdfTex)]);

            // Pass 2: Blend each object's SDF into the cascade
            for (int i = 0; i < renderables.Count; i++)
            {
                IRenderable renderable = renderables[i];
                renderable.GetCullingData(out bool isRenderable, out AABB bounds);
                if (!isRenderable) continue;

                // Get mesh SDF from cache
                Resources.Mesh? mesh = renderable.GetMesh();
                if (mesh == null) continue;

                Texture3DRT? meshSDF = MeshSDFCache.GetOrGenerate(mesh);
                if (meshSDF.IsNotValid()) continue;

                // Quick reject: skip if object AABB doesn't overlap cascade volume
                Float3 cascadeMin = cascade.Center - new Float3(cascade.WorldSize);
                Float3 cascadeMax = cascade.Center + new Float3(cascade.WorldSize);
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
                _blendSDFUniforms.SetVector3("_ObjectCenter", objectCenter);
                _blendSDFUniforms.SetFloat("_ObjectExtent", objectExtent);

                ComputeDispatcher.Dispatch(cmd, _blendSDFKernel!, groupSize, groupSize, groupSize,
                    _blendSDFUniforms,
                    images: [(0, sdfTex)],
                    textures: [(2, meshSDF!.GraphiteTexture!)]);
            }
        }
    }
}
```

**New fields in `SDFGISystem`:**
```csharp
private ComputeKernel? _blendSDFKernel;
private ComputeUniforms? _blendSDFUniforms;
```

Created in `EnsureResources()`, disposed in `Dispose()`.

#### Step 7.6 — Add `GetMesh()` to `IRenderable`

The `IRenderable` interface needs a way to retrieve the source `Mesh` for SDF generation. Currently it has:
- `GetCullingData(out bool, out AABB)`
- `GetMaterial()`
- `Render(...)`

Add:
```csharp
/// <summary>
/// Returns the source mesh for SDF generation, or null if not mesh-based.
/// </summary>
Resources.Mesh? GetMesh() => null; // Default implementation returns null
```

Implement in `MeshRenderer` (and any other `IRenderable` implementations) to return the actual mesh:
```csharp
public Resources.Mesh? GetMesh() => _mesh?.Res;
```

**Impact:** This is a non-breaking interface change (default implementation returns null). Existing `IRenderable` implementations that don't have meshes (particles, terrain, etc.) will naturally return null and be skipped by the SDF merge.

#### Step 7.7 — Update `ProbeUpdateComputeSource` to march through SDF

The probe update shader currently does sky-only approximation. Once the SDF is populated, update it to:
1. Sample multiple ray directions from each probe.
2. March through the global SDF using `MarchSDF()` from `GICommon.glsl` (now accessible via `#include "GICommon"` thanks to issue #8).
3. If a ray hits geometry, use the directional light color as incoming radiance; if it escapes, use sky color.
4. Encode the accumulated radiance as L1 SH.

Update `Assets/Defaults/Compute/SDFGI_ProbeUpdate.glsl`:

```glsl
#include "GICommon"

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(rgba16f, binding = 0) uniform image3D ProbeIrradiance;
layout(binding = 2) uniform sampler3D GlobalSDF;

layout(std140, binding = 1) uniform Params
{
    vec3  _CascadeCenter;
    float _CascadeSize;
    int   _ProbeResolution;
    int   _ProbeUpdateOffset;
    float _Hysteresis;
    vec3  _LightDirection;
    vec3  _LightColor;
    float _LightIntensity;
    vec3  _SkyColor;
};

void main()
{
    ivec3 probeCoord = ivec3(gl_GlobalInvocationID);
    if (any(greaterThanEqual(probeCoord, ivec3(_ProbeResolution))))
        return;

    vec3 probeWorldPos = _CascadeCenter +
        ((vec3(probeCoord) + 0.5) / float(_ProbeResolution) - 0.5) * 2.0 * _CascadeSize;

    vec4 newSH = vec4(0.0);
    int numRays = 16; // Cosine-weighted hemisphere directions

    for (int r = 0; r < numRays; r++)
    {
        // Generate ray direction (Fibonacci sphere or fixed set)
        vec3 dir = GetSphereDirection(r, numRays); // Helper to distribute directions

        float hitDist = MarchSDF(GlobalSDF, probeWorldPos, dir,
                                  _CascadeCenter, _CascadeSize,
                                  _CascadeSize * 2.0, 64);

        vec3 radiance;
        if (hitDist > 0.0)
        {
            // Hit geometry — use direct light contribution at hit point
            float NdotL = max(0.0, dot(-dir, _LightDirection));
            radiance = _LightColor * _LightIntensity * NdotL * 0.3; // crude approximation
        }
        else
        {
            // No hit — sky radiance
            radiance = _SkyColor * 0.5;
        }

        newSH += SHEncodeL1(dir, radiance);
    }
    newSH /= float(numRays);

    // Temporal blend
    vec4 prevSH = imageLoad(ProbeIrradiance, probeCoord);
    vec4 blendedSH = mix(newSH, prevSH, _Hysteresis);
    imageStore(ProbeIrradiance, probeCoord, blendedSH);
}
```

**Bind group layout update for probe update kernel:**
Add a sampled texture entry for the global SDF:
```csharp
BindGroupLayoutEntry[] probeLayout =
[
    BindGroupLayoutEntry.StorageTexture(0, ShaderStage.Compute, "ProbeIrradiance"),
    BindGroupLayoutEntry.UniformBuffer(1, ShaderStage.Compute, name: "Params"),
    BindGroupLayoutEntry.SampledTexture(2, ShaderStage.Compute, "GlobalSDF"),
    BindGroupLayoutEntry.Sampler(3, ShaderStage.Compute, "GlobalSDFSampler"),
];
```

Update `UpdateProbes()` to pass the cascade's SDF texture:
```csharp
ComputeDispatcher.Dispatch(cmd, _probeUpdateKernel, groupSize, groupSize, groupSize,
    _probeUpdateUniforms,
    images: [(0, probeTex)],
    textures: [(2, cascade.SDFTexture!.GraphiteTexture!)]);
```

#### Vulkan Compatibility Notes for #7

1. **Storage buffer (SSBO) for triangle data:** Requires `VK_DESCRIPTOR_TYPE_STORAGE_BUFFER`. Must verify `VKBindGroup.CreateDescriptorSet()` handles this type. The GL backend uses `GL_SHADER_STORAGE_BUFFER` (available since GL 4.3, which is the minimum for compute).

2. **Image layout transitions:** `ComputeDispatcher.Dispatch()` already transitions storage images to `General` before dispatch and issues a memory barrier after. No additional transitions needed for the SDF textures.

3. **Sampled texture in compute shader:** When a compute shader reads a texture via `sampler3D`, the texture must be in `ShaderReadOnlyOptimal` (Vulkan) or equivalent. The `textures` parameter in `ComputeDispatcher.Dispatch()` should transition these to `ShaderResource` before dispatch. **Verify this behavior** — if not handled, add a transition loop analogous to the existing `images` transition loop.

4. **Buffer retirement:** Triangle buffers are retired via `GraphiteMaterialBinder.Retire()` which ensures they are freed only after the GPU frame completes.

5. **Per-object dispatch count:** Each renderable triggers one `Dispatch()` call per cascade. For 100 objects × 4 cascades = 400 dispatches. Each dispatch has a memory barrier. This is within Vulkan's capabilities but may need profiling. A future optimization could pack multiple objects into an SSBO and process them in a single dispatch.

6. **SDF texture format:** `TextureImageFormat.Short` maps to `R16F` — a single-channel 16-bit float. Sufficient for unsigned distance. Both Vulkan and GL support `r16f` for `imageStore`.

---

## Issue #14 — GIDebugView Visualization Modes

### Problem Recap

`GIDebugView.ActiveMode` supports 5 modes:
- ✅ `None` — normal rendering (works)
- ✅ `IndirectOnly` — only GI contribution, no direct light (implemented in session 1)
- ❌ `VoxelGrid` — colored cubes showing voxelized geometry (VoxelGI only)
- ❌ `SDFSlice` — 2D slice through the SDF cascade (SDFGI only)
- ❌ `ProbeGrid` — probe positions as colored spheres (SDFGI only)

### Approach — Fullscreen Post-Process Visualization Shaders

All three debug modes are implemented as fullscreen blit passes that run **after the GI trace step (7.1)** and **replace** the normal compose output. This avoids modifying the GBuffer or lighting pipeline.

Each mode gets its own shader pass and is triggered from `DefaultRenderPipeline` when `GIDebugView.ActiveMode` matches.

### Step-by-Step

#### Step 14.1 — Create `GI_DebugVoxelGrid.shader`

A fullscreen pass that ray-marches through the voxel grid and renders occupied voxels as colored cubes:

```
Shader "Default/GI_DebugVoxelGrid"

Pass "DebugVoxelGrid"
{
    Tags { "RenderOrder" = "Opaque" }
    Cull None
    ZTest Off
    ZWrite Off

    GLSLPROGRAM

    Vertex { /* standard fullscreen quad */ }

    Fragment
    {
        #include "Fragment"
        #include "GICommon"

        layout(location = 0) out vec4 outColor;
        in vec2 TexCoords;

        uniform sampler3D _VoxelRadiance;
        uniform sampler2D _CameraDepthTexture;
        uniform vec3 _VoxelGridCenter;
        uniform float _VoxelGridSize;
        uniform int _VoxelResolution;

        void main()
        {
            // Reconstruct world position from depth
            float depth = texture(_CameraDepthTexture, TexCoords).r;
            vec3 worldPos = GI_WorldPosFromDepth(depth, TexCoords, PROWL_MATRIX_INV_VP);

            // Map to voxel UVW
            vec3 uvw = WorldToVoxelUVW(worldPos, _VoxelGridCenter, _VoxelGridSize);

            if (!IsInsideVolume(uvw))
            {
                outColor = vec4(0.0, 0.0, 0.0, 1.0);
                return;
            }

            // Sample voxel at this world position (mip 0)
            vec4 voxelData = textureLod(_VoxelRadiance, uvw, 0.0);

            if (voxelData.a < 0.01)
            {
                // Empty voxel — show faint grid lines
                vec3 voxelCoord = uvw * float(_VoxelResolution);
                vec3 gridLine = abs(fract(voxelCoord) - 0.5);
                float edge = 1.0 - smoothstep(0.45, 0.5, min(gridLine.x, min(gridLine.y, gridLine.z)));
                outColor = vec4(vec3(edge * 0.05), 1.0);
            }
            else
            {
                // Occupied voxel — show stored color
                outColor = vec4(voxelData.rgb, 1.0);
            }
        }
    }

    ENDGLSL
}
```

#### Step 14.2 — Create `GI_DebugSDFSlice.shader`

A fullscreen pass that shows a 2D horizontal slice through the SDF volume at the camera's Y position:

```
Shader "Default/GI_DebugSDFSlice"

Pass "DebugSDFSlice"
{
    Tags { "RenderOrder" = "Opaque" }
    Cull None
    ZTest Off
    ZWrite Off

    GLSLPROGRAM

    Vertex { /* standard fullscreen quad */ }

    Fragment
    {
        #include "Fragment"
        #include "GICommon"

        layout(location = 0) out vec4 outColor;
        in vec2 TexCoords;

        uniform sampler3D _SDFCascade0;
        uniform vec3 _CascadeCenter0;
        uniform float _CascadeSize0;
        uniform float _SliceY; // Normalized Y position [0,1] within cascade

        void main()
        {
            // Map screen UV to cascade XZ, use _SliceY for Y
            vec3 uvw = vec3(TexCoords.x, _SliceY, TexCoords.y);

            float dist = texture(_SDFCascade0, uvw).r;

            // Visualize: blue = far from surface, white = near surface, red = very close
            float normalizedDist = clamp(dist / (_CascadeSize0 * 0.5), 0.0, 1.0);

            vec3 color;
            if (normalizedDist < 0.05)
                color = vec3(1.0, 0.2, 0.2); // Near surface — red
            else if (normalizedDist < 0.3)
                color = vec3(1.0, 1.0, 1.0); // Close — white
            else
                color = mix(vec3(0.0, 0.3, 0.8), vec3(0.0, 0.0, 0.1), normalizedDist); // Far — blue gradient

            outColor = vec4(color, 1.0);
        }
    }

    ENDGLSL
}
```

#### Step 14.3 — Create `GI_DebugProbeGrid.shader`

A fullscreen pass that renders probe positions as colored dots by ray-marching small spheres at each probe location:

```
Shader "Default/GI_DebugProbeGrid"

Pass "DebugProbeGrid"
{
    Tags { "RenderOrder" = "Opaque" }
    Cull None
    ZTest Off
    ZWrite Off

    GLSLPROGRAM

    Vertex { /* standard fullscreen quad */ }

    Fragment
    {
        #include "Fragment"
        #include "GICommon"

        layout(location = 0) out vec4 outColor;
        in vec2 TexCoords;

        uniform sampler3D _ProbeIrradiance0;
        uniform sampler2D _CameraDepthTexture;
        uniform vec3 _CascadeCenter0;
        uniform float _CascadeSize0;
        uniform int _ProbeResolution;

        void main()
        {
            // Reconstruct world position
            float depth = texture(_CameraDepthTexture, TexCoords).r;
            vec3 worldPos = GI_WorldPosFromDepth(depth, TexCoords, PROWL_MATRIX_INV_VP);

            // Find nearest probe
            vec3 uvw = WorldToVoxelUVW(worldPos, _CascadeCenter0, _CascadeSize0);

            if (!IsInsideVolume(uvw))
            {
                outColor = vec4(0.0, 0.0, 0.0, 1.0);
                return;
            }

            vec3 probeCoord = uvw * float(_ProbeResolution);
            vec3 nearestProbe = floor(probeCoord) + 0.5;
            float distToProbe = length(probeCoord - nearestProbe);

            // Render probe as a small sphere
            float probeRadius = 0.3; // In probe-grid units
            if (distToProbe < probeRadius)
            {
                // Sample probe irradiance and decode as color
                vec3 probeUVW = nearestProbe / float(_ProbeResolution);
                vec4 sh = texture(_ProbeIrradiance0, probeUVW);
                vec3 irradiance = SHDecodeL1(sh, vec3(0.0, 1.0, 0.0)); // Decode for up direction
                outColor = vec4(irradiance * 2.0, 1.0); // Boost for visibility
            }
            else
            {
                // Background — show scene normally but dimmed
                outColor = vec4(0.0, 0.0, 0.0, 1.0);
            }
        }
    }

    ENDGLSL
}
```

#### Step 14.4 — Add `DefaultShader` enum entries

In `Prowl.Runtime/Resources/DefaultAssets.cs`, add:

```csharp
// GI Debug Visualization
GI_DebugVoxelGrid,
GI_DebugSDFSlice,
GI_DebugProbeGrid,
```

The embedded resource filenames must match: `GI_DebugVoxelGrid.shader`, `GI_DebugSDFSlice.shader`, `GI_DebugProbeGrid.shader`.

#### Step 14.5 — Wire up debug modes in `DefaultRenderPipeline`

Add fields:
```csharp
private Material? _debugVoxelGridMat;
private Material? _debugSDFSliceMat;
private Material? _debugProbeGridMat;
```

After step 7.2 (temporal filter), add:

```csharp
// 7.3 GI Debug Visualization — override output if debug mode is active
GIDebugMode debugMode = GIDebugView.ActiveMode;
if (debugMode == GIDebugMode.VoxelGrid && _voxelGI != null &&
    giMode == Scene.GlobalIlluminationParams.GIMode.VoxelGI)
{
    _debugVoxelGridMat ??= new Material(Shader.LoadDefault(DefaultShader.GI_DebugVoxelGrid));
    // Set uniforms from _voxelGI (grid center, size, resolution, radiance texture)
    SetVoxelDebugUniforms(_debugVoxelGridMat);
    RenderPipeline.Blit(gBuffer, lightAccumulation, _debugVoxelGridMat, 0, false, false);
}
else if (debugMode == GIDebugMode.SDFSlice && _sdfGI != null &&
         giMode == Scene.GlobalIlluminationParams.GIMode.SDFGI)
{
    _debugSDFSliceMat ??= new Material(Shader.LoadDefault(DefaultShader.GI_DebugSDFSlice));
    // Set uniforms: cascade 0 SDF texture, center, size, slice Y
    SetSDFSliceDebugUniforms(_debugSDFSliceMat, css);
    RenderPipeline.Blit(gBuffer, lightAccumulation, _debugSDFSliceMat, 0, false, false);
}
else if (debugMode == GIDebugMode.ProbeGrid && _sdfGI != null &&
         giMode == Scene.GlobalIlluminationParams.GIMode.SDFGI)
{
    _debugProbeGridMat ??= new Material(Shader.LoadDefault(DefaultShader.GI_DebugProbeGrid));
    // Set uniforms: cascade 0 probe irradiance, center, size, resolution
    SetProbeGridDebugUniforms(_debugProbeGridMat);
    RenderPipeline.Blit(gBuffer, lightAccumulation, _debugProbeGridMat, 0, false, false);
}
```

Each `SetXxxDebugUniforms()` helper method sets the material textures and floats, including the Vulkan transition barrier for 3D textures (same pattern as `ConeTrace()` — `ResourceBarrier(UnorderedAccess → ShaderResource)` before the Blit).

#### Step 14.6 — Dispose debug materials

In `DefaultRenderPipeline.OnDispose()`, add:
```csharp
_debugVoxelGridMat?.Dispose();
_debugSDFSliceMat?.Dispose();
_debugProbeGridMat?.Dispose();
```

#### Vulkan Compatibility Notes for #14

1. **3D texture barriers:** Each debug shader reads 3D textures via `sampler3D`. Before the Blit, issue `ResourceBarrier(UnorderedAccess → ShaderResource)` for each 3D texture, same as `ConeTrace` already does. This ensures the Vulkan image layout matches the descriptor's expected layout.

2. **Non-destructive Blit:** Use `preserveContents: false` since debug modes replace the output entirely (not additive). This uses `LoadOp.DontCare` which is cheaper on Vulkan tile-based GPUs.

3. **No new render passes needed.** All debug views use the existing `RenderPipeline.Blit()` fullscreen pass infrastructure, which handles Vulkan render pass begin/end internally.

4. **`PROWL_MATRIX_INV_VP`:** Available via `#include "Fragment"` which includes `ShaderVariables.glsl`. Set globally by `SetupGlobalUniforms()` which runs before the debug passes.

---

## Cross-Cutting Concerns

### `BindGroupLayoutEntry.StorageBuffer` / `BindingType.StorageBuffer`

Before implementing #7, verify these exist in the Graphite abstraction:

1. **`BindingType` enum** (`Prowl.Runtime/Graphite/Descriptors/BindGroupDescriptor.cs`) — must contain `StorageBuffer`.
2. **`BindGroupLayoutEntry.StorageBuffer()`** static factory.
3. **`VKBindGroup.CreateDescriptorSet()`** — must map `StorageBuffer` → `VK_DESCRIPTOR_TYPE_STORAGE_BUFFER`.
4. **`GLBindGroup`** (if it exists) — must handle `GL_SHADER_STORAGE_BUFFER` binding.

If any of these are missing, they must be added as part of step 7.2.

### `ComputeDispatcher.Dispatch()` — `textures` parameter behavior

Verify the existing `textures` parameter creates **combined image-sampler** descriptors on Vulkan (texture view + sampler). If it only creates image views without samplers, add a default linear-clamp `Sampler` to `ComputeDispatcher` that is used for all sampled textures in compute dispatches.

### `IRenderable.GetMesh()` default interface method

Adding a default interface method (`Resources.Mesh? GetMesh() => null;`) requires C# 8+ which is available in .NET 9. This is a **non-breaking change** — existing implementations don't need to be updated unless they want to provide mesh data for SDF generation.

### Memory Budget

| Resource | Size (at default settings) | Notes |
|----------|---------------------------|-------|
| Per-mesh SDF (32³ × R16F) | 64 KB each | Cached; typical scene: 10-50 meshes = 0.6-3.2 MB |
| Per-mesh SDF (64³ × R16F) | 512 KB each | If MeshSDFResolution is increased |
| Triangle upload buffer | Varies | Retired immediately after dispatch |
| Debug materials (3) | ~1 KB each | Negligible |

### Frame Timing Impact

| Operation | Frequency | Estimated GPU Cost |
|-----------|-----------|-------------------|
| SDF generation | Once per mesh (cached) | 1-5 ms per mesh at 32³ |
| SDF merge clear | Once per cascade per frame | <0.1 ms |
| SDF blend per object | Once per object per cascade per frame | ~0.05 ms each |
| Debug visualization | Per frame when active | <0.5 ms |

---

## Testing Strategy

### Unit Tests (no GPU required)

1. **`MeshSDFCacheTests`** — Verify that `GetOrGenerate` returns non-null on second call (cache hit). Verify `Invalidate` disposes and removes. (Existing tests cover this.)

2. **`GIDebugViewTests`** — Verify `ActiveMode` get/set, verify all enum values are distinct. (Existing tests cover basic lifecycle.)

3. **`ComputeShaderLoaderTests`** — New test class:
   - Verify `Load("Compute/VoxelGI_Clear")` returns non-empty string.
   - Verify `#include "GICommon"` is resolved (output contains `SHEncodeL1`).
   - Verify missing include logs error and returns partial source.

### Integration Tests (require Graphite device)

4. **`VoxelGISystemTests`** — Extend existing lifecycle tests to verify `EnsureResources` + `Voxelize` doesn't throw after #8 refactor.

5. **`SDFGISystemTests`** — Extend to verify `UpdateGlobalSDF` with mock renderables that have meshes.

### Manual Verification

6. **Vulkan backend:** Enable VoxelGI/SDFGI in a sample scene, verify no `VK_ERROR_DEVICE_LOST`.
7. **OpenGL backend:** Same scene, verify GI output matches pre-refactor.
8. **Debug modes:** Toggle each `GIDebugMode` in the Scene panel dropdown, verify visualization appears.

---

## Risk Matrix

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `StorageBuffer` descriptor type not in Graphite | Medium | Blocks #7 | Check first; add to Graphite if missing (small, contained change) |
| SSBO not available on target GL version | Low | GL fallback broken | GL 4.3+ required for compute; already a requirement |
| SDF generation too slow for large meshes | Medium | First-frame stutter | Add triangle count limit; defer to background; use lower SDF resolution |
| `textures` in `ComputeDispatcher` missing sampler on Vulkan | Medium | Validation error | Add default sampler to dispatcher |
| Include preprocessor infinite recursion | Low | Stack overflow | Add recursion depth limit (max 16) |
| Debug shader samples 3D texture in wrong layout | Medium | `VK_ERROR_DEVICE_LOST` | Follow exact barrier pattern from `ConeTrace()` |

---

## File Change Summary

### New Files

| File | Purpose |
|------|---------|
| `Assets/Defaults/Compute/VoxelGI_Clear.glsl` | Voxel grid clear compute shader |
| `Assets/Defaults/Compute/VoxelGI_Fill.glsl` | AABB voxel fill compute shader |
| `Assets/Defaults/Compute/VoxelGI_InjectLight.glsl` | Direct light injection compute shader |
| `Assets/Defaults/Compute/SDFGI_MergeSDF.glsl` | SDF cascade initialization compute shader |
| `Assets/Defaults/Compute/SDFGI_BlendObjectSDF.glsl` | Per-object SDF blend compute shader |
| `Assets/Defaults/Compute/SDFGI_GenerateMeshSDF.glsl` | Per-mesh SDF generation compute shader |
| `Assets/Defaults/Compute/SDFGI_ProbeUpdate.glsl` | Probe irradiance update compute shader |
| `Rendering/Compute/ComputeShaderLoader.cs` | Include-preprocessing loader for compute GLSL |
| `Assets/Defaults/GI_DebugVoxelGrid.shader` | Voxel grid debug visualization |
| `Assets/Defaults/GI_DebugSDFSlice.shader` | SDF slice debug visualization |
| `Assets/Defaults/GI_DebugProbeGrid.shader` | Probe grid debug visualization |

### Modified Files

| File | Changes |
|------|---------|
| `VoxelGISystem.cs` | Remove inline GLSL constants; use `ComputeShaderLoader.Load()` |
| `SDFGISystem.cs` | Remove inline GLSL constants; add blend kernel; use `ComputeShaderLoader.Load()`; update `UpdateGlobalSDF()` and `UpdateProbes()` |
| `MeshSDFCache.cs` | Add SDF generation compute dispatch; add static kernel/uniforms fields |
| `DefaultRenderPipeline.cs` | Add debug visualization passes after step 7.2; add debug material fields; dispose in `OnDispose()` |
| `DefaultAssets.cs` | Add `GI_DebugVoxelGrid`, `GI_DebugSDFSlice`, `GI_DebugProbeGrid` to `DefaultShader` enum |
| `ComputeDispatcher.cs` | Add `storageBuffers` parameter to `Dispatch()`; verify `textures` creates combined image-sampler |
| `IRenderable.cs` | Add `GetMesh()` default interface method |
| `MeshRenderer.cs` | Implement `GetMesh()` |
| `BindGroupDescriptor.cs` | Add `StorageBuffer` factory (if missing) |
| `BindingType` enum | Add `StorageBuffer` (if missing) |
| `VKBindGroup.cs` | Handle `StorageBuffer` in descriptor set creation (if missing) |
