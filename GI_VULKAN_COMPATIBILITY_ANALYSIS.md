# GI System — Vulkan Compatibility Analysis

## Executive Summary

The Global Illumination (GI) subsystem (`Prowl.Runtime/Rendering/GI/`) has **multiple critical and moderate Vulkan compatibility issues** that will cause `VK_ERROR_DEVICE_LOST` when GI modes (`VoxelGI` or `SDFGI`) are enabled. Additionally, several design problems render the GI system non-functional even on OpenGL. The default GI mode is `GIMode.None`, so these issues only surface when a user (or a directional light override) explicitly enables GI.

> **Note:** The editor crash at `DefaultRenderPipeline.Internal_Render` line 677 may or may not be GI-related, depending on whether the active scene has GI enabled. This document focuses exclusively on GI↔Vulkan conflicts.

---

## Critical Issues (Cause `VK_ERROR_DEVICE_LOST`)

### 1. `ComputeUniforms` GPU Buffer Write-After-Read Hazard

**File:** `Prowl.Runtime/Rendering/Compute/ComputeDispatcher.cs` (lines 82–106)
**Affects:** `VoxelGISystem.InjectDirectLight`, `SDFGISystem.UpdateGlobalSDF`, `SDFGISystem.UpdateProbes`

**Problem:**
`ComputeUniforms.Upload()` writes uniform data to a `CpuToGpu` mapped buffer via direct `MemoryCopy` (no staging, no fence). The same `ComputeUniforms` instance is reused every frame (`_injectLightUniforms`, `_mergeSDFUniforms`, `_probeUpdateUniforms`). Each frame calls `Clear()` → `SetXxx(...)` → `Upload()` which overwrites the same GPU memory.

On Vulkan, the previous frame's compute dispatch may still be in-flight reading this buffer when the CPU overwrites it. There is **no fence wait or double-buffering** protecting the uniform buffer. This is a classic write-after-read (WAR) hazard that causes undefined behavior, often manifesting as `VK_ERROR_DEVICE_LOST`.

**Contrast with the main pipeline:** The main rendering path uses `VKUniformRingBuffer` (a per-frame ring buffer that resets after fence wait in `BeginFrame`), which is safe. `ComputeUniforms` bypasses this mechanism entirely.

**Fix Plan:**
- **Option A (minimal):** Double-buffer the `ComputeUniforms` GPU buffer — maintain one buffer per frame slot (`MaxFramesInFlight = 2`), cycling between them in sync with `GraphiteDevice.BeginFrame`. The CPU writes to slot N while the GPU reads from slot N-1.
- **Option B (better):** Allocate compute uniform data from the existing `VKUniformRingBuffer` instead of managing a separate buffer. This piggybacks on the engine's frame-sync model automatically.
- **Option C (simplest):** Recreate the GPU buffer each frame and retire the old one via `GraphiteMaterialBinder.Retire()`. This avoids reuse entirely at the cost of a small per-frame allocation.

---

### 2. Voxelization Draws Nothing on Vulkan (and GL) — No Render Target

**File:** `Prowl.Runtime/Rendering/GI/VoxelGISystem.cs` (lines 117–159)
**Affects:** `VoxelGISystem.Voxelize`

**Problem:**
`Voxelize()` calls `pipeline.DrawRenderables(renderables, "LightMode", "Voxelize", ...)` three times (one per projection axis). This has **two compounding failures**:

1. **No render pass is active.** `DrawRenderables` only records Graphite draw calls when `Graphics.ActiveGraphiteCmdBuffer is { InRenderPass: true }`. Since `Voxelize` never calls `BeginRenderPass`, no draw commands are recorded on Vulkan. On GL, it also never calls `Graphics.BindFramebuffer`, so draws go to whatever was last bound (incorrect).

2. **No standard material has the `LightMode = Voxelize` tag.** Only `VoxelGI_Voxelize.shader` declares this tag. `DrawRenderables` filters by tag match against each renderable's material. Since scene renderables use `Standard.shader` (which has `RenderOrder = Opaque` and `LightMode = ShadowCaster` passes, but no `Voxelize` pass), every renderable is skipped. The voxel grid is never populated.

**Result:** The voxel radiance volume remains empty (all zeros). Subsequent `InjectDirectLight` reads zeros, `GenerateMipmaps` runs on empty data, and `ConeTrace` samples an empty volume — producing no visible GI contribution. This is a **functional failure**, not a crash.

**Fix Plan:**
- Voxelization requires rendering scene geometry with a **custom voxelization material override**, not the scene's own materials. The pipeline should temporarily replace each renderable's material with `_voxelizeMat` during the voxelization pass, similar to how shadow rendering works.
- On Vulkan, the voxelization technique (dominant-axis orthographic rasterization with `imageStore`) requires a **dummy render target** (1×1 or resolution-matched) with a render pass, or a **compute-shader-based voxelization** approach that doesn't need a render pass at all.
- The `_voxelNormal` texture is allocated but never written to or read from.

---

### 3. `GenerateMipmaps` on Command List Leaves Texture in Wrong Layout for Sampling

**File:** `Prowl.Runtime/Graphite/Vulkan/VKCommandList.cs` (lines in `GenerateMipmapsCore`)
**Affects:** `VoxelGISystem.GenerateMipmaps` → `VoxelGISystem.ConeTrace`

**Problem:**
`VKCommandList.GenerateMipmapsCore` ends by transitioning all mip levels to `ImageLayout.General`. This is correct for subsequent compute access. However, `VoxelGISystem.ConeTrace` (line 299) then issues a `ResourceBarrier(UnorderedAccess → ShaderResource)`, which the engine translates to a transition from `General` → `ShaderReadOnlyOptimal`.

The transition itself is correct, **but there is a subtle issue**: `ResourceBarrierCore` in `VKCommandList` uses the per-mip **tracked layout** (`_mipLayouts[]`) to determine the `OldLayout`. `GenerateMipmapsCore` updates tracked layouts to `General` for all mips. The subsequent `ResourceBarrier` from `ConeTrace` transitions the whole image from tracked `General` → `ShaderReadOnlyOptimal`, which is valid.

**However**, this transition only uses `baseMipLevel=0, levelCount=1` (single mip) because `ResourceBarrierCore` operates on mip 0 by default. **The other mip levels remain tracked as `General` but the VkImageMemoryBarrier only transitions mip 0.** When the fragment shader samples with `textureLod(_VoxelRadiance, uvw, mipLevel)` at mip levels > 0, those mips are in `General` layout while the descriptor expects `ShaderReadOnlyOptimal`.

This mismatch can cause `VK_ERROR_DEVICE_LOST` on strict Vulkan drivers.

**Fix Plan:**
- `ConeTrace` should transition **all mip levels** to `ShaderReadOnlyOptimal`, not just the base level. Use a whole-image barrier that covers `baseMipLevel=0, levelCount=mipLevels`.
- Alternatively, modify `ResourceBarrierCore` to always transition all mips when dealing with storage textures, or add an overload that accepts mip range parameters.
- Alternatively, leave the texture in `General` (which is valid for both compute and sampling) and ensure the bind group's `ImageLayout` is set to `General` instead of `ShaderReadOnlyOptimal`.

---

### 4. `Texture3DRT` Missing `CopySource` Usage Prevents Layout Transitions

**File:** `Prowl.Runtime/Rendering/GI/Texture3DRT.cs` (lines 59–70)

**Problem:**
`Texture3DRT.CreateGPUResources` creates textures with:
```csharp
Usage = TextureUsage.Storage | TextureUsage.Sampled
      | TextureUsage.CopyDestination | TextureUsage.CopySource
```
This is actually correct — `CopySource` is included. **No issue here upon closer inspection.** However, the `GenerateMipmaps` path uses `CmdBlitImage` which requires `TransferSrc` and `TransferDst` usage bits. On Vulkan, `TextureUsage.CopySource` maps to `VK_IMAGE_USAGE_TRANSFER_SRC_BIT` and `CopyDestination` maps to `VK_IMAGE_USAGE_TRANSFER_DST_BIT`, so this is correctly handled.

**(Informational — no fix needed.)**

---

### 5. Dual-Use `lightAccumulation` Render Target Layout Conflict During GI

**File:** `Prowl.Runtime/Rendering/DefaultRenderPipeline.cs` (lines 424–435)
**Affects:** ConeTrace/TraceGI writing to `lightAccumulation`

**Problem:**
At line 425, `lightAccumulation` is transitioned to `ShaderResource` after the lighting render pass ends. Then at line 428–435, `ConeTrace` or `TraceGI` calls `RenderPipeline.Blit(gBuffer, lightAccumulation, mat, preserveContents: true)`.

Inside `Blit`, the `preserveContents` path calls `TransitionToRenderTarget(target)` which transitions `lightAccumulation`'s color attachment from `ShaderReadOnlyOptimal` → `ColorAttachmentOptimal`. Then `BeginRenderPass` uses `LoadOp.Load` with `initialLayout = ColorAttachmentOptimal`.

**This is actually correct** — the transitions are properly sequenced. However, there's a nuance: `Blit` also tries to `TransitionToShaderResource(source)` on `gBuffer` if outside a render pass. Since GBuffer was already transitioned at line 375, this is a no-op barrier.

The **real problem** is that `ConeTrace` sets `_VoxelRadiance` via `SetRawGraphiteTexture`, then calls `Blit` which starts its own render pass. Inside `Blit` → `DrawMeshNow` → `RecordGraphiteDraw`, the material binder creates a bind group that includes the `_VoxelRadiance` texture. The bind group descriptor expects `ShaderReadOnlyOptimal`, and the barrier at line 299 transitioned it. **But `Blit` may have already started the render pass before the barrier's effect is visible**, because `ResourceBarrier` and `BeginRenderPass` are recorded into the same command buffer — the barrier must be issued *before* the render pass begins.

Looking at the code flow more carefully:
1. Line 299: `cb.ResourceBarrier(radianceTex, UnorderedAccess → ShaderResource)` ← outside render pass ✓
2. Line 305: `RenderPipeline.Blit(gBuffer, lightAccumulation, _coneTraceMat, 0, false, false, default, preserveContents: true)`
   - Inside Blit: `TransitionToRenderTarget(target)` ← outside render pass ✓
   - Inside Blit: `cmd.BeginRenderPass(target, ...)` ← starts render pass ✓
   - Inside Blit: `DrawMeshNow(...)` ← inside render pass, creates bind group ✓

The ordering is actually correct — the barrier at step 1 is recorded before the render pass at step 2. **No issue with barrier ordering.**

**(Informational — no fix needed for the layout sequencing, but see issue #3 for the per-mip barrier problem.)**

---

## Moderate Issues (Functional Failures, Not Crashes)

### 6. `VoxelGI_Voxelize.shader` Uses `imageStore` but Has No `image3D` Declaration

**File:** `Prowl.Runtime/Assets/Defaults/VoxelGI_Voxelize.shader`

**Problem:**
The voxelization shader's fragment shader writes to a regular `outColor` output (a render target attachment), not to a 3D image via `imageStore`. The intended voxelization technique (conservative rasterization with `imageStore`) requires:
- `layout(rgba16f, binding = N) uniform image3D VoxelRadiance;`
- `imageStore(VoxelRadiance, voxelCoord, color);`
- No color attachment output (or a dummy one)

The current shader computes voxel coordinates and checks bounds, but then writes to `outColor` — a 2D render target attachment — which doesn't write to the 3D voxel grid. The voxel grid remains empty.

**Fix Plan:**
- Rewrite the voxelize fragment shader to use `imageStore` into the `_voxelRadiance` 3D texture.
- Bind the voxel radiance texture as a storage image during voxelization.
- Use a dummy 1×1 render target (or viewport trick) since Vulkan requires a render pass.
- Consider using compute-based voxelization instead of rasterization-based.

---

### 7. `SDFGISystem.UpdateGlobalSDF` Only Initializes — Never Merges Per-Object SDFs

**File:** `Prowl.Runtime/Rendering/GI/SDFGISystem.cs` (lines 376–401)

**Problem:**
The `MergeSDFComputeSource` shader initializes each voxel with `_CascadeSize * 2.0` (maximum distance = empty space). The comment says "Per-object SDF merging would sample each object's SDF here and take the minimum distance" but this is not implemented. Every frame, the SDF volume is reset to empty.

The `MeshSDFCache.GetOrGenerate` allocates textures but never actually generates SDF data (the compute dispatch is commented as "deferred until compute pipeline support is fully integrated").

**Result:** SDFGI probe updates march through an empty SDF → probes never see geometry → indirect lighting is wrong (sky-only approximation from the placeholder code in `ProbeUpdateComputeSource`).

**Fix Plan:**
- Implement the SDF merge compute shader to iterate over scene meshes, read their per-mesh SDFs from `MeshSDFCache`, and take the minimum distance.
- Implement the SDF generation compute pass in `MeshSDFCache` using ray-triangle intersection or a jump-flood algorithm.

---

### 8. Compute Shaders Use Inline GLSL Strings — Not Loaded Through Shader Asset Pipeline

**Files:** `VoxelGISystem.cs` (line 353), `SDFGISystem.cs` (lines 376, 407)

**Problem:**
The GI compute shaders are embedded as raw GLSL strings (e.g., `InjectLightComputeSource`, `MergeSDFComputeSource`, `ProbeUpdateComputeSource`). The `ComputeKernel` constructor prepends `#version 450` (Vulkan) or `#version 430` (GL) and compiles at runtime.

This bypasses the engine's shader asset pipeline, which means:
- No hot-reload during development
- No include resolution (can't use `#include "GICommon"`)
- No keyword variant support
- SPIR-V compilation errors are only caught at runtime
- The `Fragment` include and `ShaderVariables.glsl` globals (`PROWL_MATRIX_VP`, etc.) are not available

Meanwhile, the rasterization GI shaders (`VoxelGI_ConeTrace.shader`, `SDFGI_ProbeTrace.shader`) **do** use the asset pipeline and `#include "Fragment"` / `#include "GICommon"`.

**Fix Plan:**
- Move compute shader sources to `.compute` shader assets or embed them with proper include preprocessing.
- Use the engine's `ComputeVariant` / `ComputeShaderImporter` infrastructure if available, or at minimum run the include preprocessor before compilation.

---

### 9. `_voxelNormal` Texture Is Allocated But Never Used

**File:** `Prowl.Runtime/Rendering/GI/VoxelGISystem.cs` (line 83)

**Problem:**
```csharp
_voxelNormal = new Texture3DRT(res, res, res, TextureImageFormat.Short4, false);
```
This allocates a full `256³ × RGBA16F` texture (128 MB at 256 resolution) that is never written to, never read from, and never bound to any shader. It's pure GPU memory waste.

**Fix Plan:**
- Remove the allocation until voxelization actually writes normal data.
- If normals are needed for anisotropic voxel cone tracing, implement the write path first.

---

### 10. `_linearSampler` Created But Never Used

**File:** `Prowl.Runtime/Rendering/GI/VoxelGISystem.cs` (lines 106–109)

**Problem:**
A linear clamp sampler is created in `EnsureResources` but is never bound to any shader or passed to any bind group. The cone trace shader uses `textureLod(_VoxelRadiance, uvw, mipLevel)` which requires a combined image-sampler descriptor. The sampler comes from the material system's default sampler (anisotropic 16x), not this `_linearSampler`.

**Fix Plan:**
- Remove `_linearSampler` or use it when binding `_VoxelRadiance` for cone tracing.
- For volume textures, linear clamp is typically more appropriate than anisotropic.

---

### 11. `Texture3DRT` Pool Has No Frame-Age Safety

**File:** `Prowl.Runtime/Rendering/GI/Texture3DRT.cs` (lines 79–126)

**Problem:**
The `GetTemporary` / `ReleaseTemporary` pool returns textures immediately without tracking frame age. In contrast, `RenderTexture.GetTemporaryRT` enforces a `minFrameAge` check to ensure returned textures aren't still in use by the GPU.

If a `Texture3DRT` is released and immediately reacquired in the same or next frame, the GPU may still be reading it from the previous frame's command buffer.

**Fix Plan:**
- Add frame-age tracking like `RenderTexture.GetTemporaryRT`, or at minimum mark released textures with the current frame number and require N frames to elapse before reuse.
- Alternatively, remove the pool if `Texture3DRT` instances are long-lived (the current GI systems create them once and hold them for the pipeline's lifetime).

---

### 12. Material Disposal Leak in GI Systems

**Files:** `VoxelGISystem.cs`, `SDFGISystem.cs`

**Problem:**
`_voxelizeMat`, `_coneTraceMat` (VoxelGI) and `_probeTraceMat` (SDFGI) are created with `new Material(...)` but never disposed in `Dispose()`. Only volume textures and compute kernels are cleaned up.

**Fix Plan:**
- Dispose all materials in the `Dispose()` method.

---

### 13. `GITemporalFilter` Is Incomplete — Placeholder Only

**File:** `Prowl.Runtime/Rendering/GI/GITemporalFilter.cs` (lines 25–47)

**Problem:**
The `Apply` method contains only a comment describing the intended temporal blending algorithm. It allocates a history buffer on the first frame but never actually performs any blending, reprojection, or copy. The class is never instantiated by the pipeline.

**(Informational — not a Vulkan issue, but a completeness gap.)**

---

### 14. `GIDebugView` Mode Is Never Checked by the Pipeline

**File:** `Prowl.Runtime/Rendering/GI/GIDebugView.cs`

**Problem:**
`GIDebugView.ActiveMode` is exposed for editor UI but never read by `DefaultRenderPipeline`. Debug visualization modes (IndirectOnly, VoxelGrid, SDFSlice, ProbeGrid) are not implemented.

**(Informational — not a Vulkan issue.)**

---

## Fix Priority and Ordering

| Priority | Issue | Impact | Effort |
|----------|-------|--------|--------|
| **P0** | #1 — ComputeUniforms WAR hazard | Crash (VK_ERROR_DEVICE_LOST) | Medium |
| **P0** | #3 — Per-mip barrier in ConeTrace | Crash (VK_ERROR_DEVICE_LOST) | Low |
| **P1** | #2 — Voxelization non-functional | GI produces no data | High |
| **P1** | #6 — Voxelize shader has no imageStore | GI produces no data | Medium |
| **P1** | #7 — SDF merge is placeholder | SDFGI produces no data | High |
| **P2** | #12 — Material disposal leak | GPU memory leak | Low |
| **P2** | #9 — Unused voxel normal texture | 128 MB GPU memory waste | Trivial |
| **P2** | #10 — Unused linear sampler | Minor resource waste | Trivial |
| **P2** | #11 — Pool frame-age safety | Potential GPU data race | Low |
| **P3** | #8 — Inline GLSL compute sources | Developer experience | Medium |
| **P3** | #13 — Temporal filter placeholder | Quality feature gap | High |
| **P3** | #14 — Debug view not wired up | Debug feature gap | Medium |

---

## Recommended Fix Plan

### Phase 1: Make GI Safe on Vulkan (P0 fixes)

1. **Fix `ComputeUniforms` buffer synchronization** — switch to per-frame buffer allocation via `GraphiteMaterialBinder.Retire()` to ensure the GPU is done before the buffer is overwritten. In `ComputeUniforms.Upload()`:
   ```csharp
   // Always create a new buffer and retire the old one
   if (_gpuBuffer != null)
       GraphiteMaterialBinder.Retire(_gpuBuffer);
   _gpuBuffer = Graphics.Graphite.CreateBuffer(desc);
   ```

2. **Fix per-mip barrier in ConeTrace** — after `GenerateMipmaps` leaves the texture in `General`, the `ResourceBarrier` in `ConeTrace` must cover all mip levels, not just mip 0. Add a `TransitionAllMips` helper or use the existing `TransitionLayout(cmd, layout, baseMip, mipCount, baseLayer, layerCount)` method.

### Phase 2: Make VoxelGI Functional (P1 fixes)

3. **Rewrite voxelization** — use a compute-based approach (parallel over the voxel grid, testing triangle intersections) instead of rasterization. This avoids the render-pass requirement on Vulkan and is more correct for conservative voxelization.

4. **Fix the voxelize shader** — if keeping the rasterization approach, add `imageStore` for the 3D voxel write and create a dummy render target for the render pass.

5. **Implement SDF merge** — fill in the `MergeSDFComputeSource` shader to actually merge per-object SDFs. Implement `MeshSDFCache` SDF generation.

### Phase 3: Cleanup (P2 fixes)

6. **Dispose materials** in `VoxelGISystem.Dispose()` and `SDFGISystem.Dispose()`.
7. **Remove `_voxelNormal`** allocation until it's actually used.
8. **Remove `_linearSampler`** or wire it up to the cone trace bind group.
9. **Add frame-age safety** to `Texture3DRT` pool.

### Phase 4: Polish (P3 fixes)

10. Move inline compute shaders to asset files.
11. Implement `GITemporalFilter.Apply()`.
12. Wire up `GIDebugView` modes in the pipeline.
