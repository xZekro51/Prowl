# Vulkan Robustness & OpenGL Restoration Plan

## Executive Summary

The Prowl engine's Graphite rendering abstraction has two backends: **Vulkan** (`VKGraphiteDevice`) and **OpenGL** (`GLGraphiteDevice`). Currently Vulkan works but feels fragile ("flimsy"), while OpenGL has become non-functional. This document identifies the root causes of both problems and provides a phased plan to make Vulkan robust and restore OpenGL functionality.

**Root cause (shared):** The codebase is mid-migration from a legacy immediate-mode OpenGL API to a modern command-list-based Graphite abstraction. The migration is incomplete — rendering code uses a **dual-path architecture** where Vulkan takes the new Graphite path while OpenGL still relies on legacy API calls (`Graphics.BindFramebuffer`, `Graphics.SetState`, `Graphics.Clear`, etc.). As the Graphite path matured and the render pipeline evolved, the legacy OpenGL paths rotted. At the same time, the Vulkan backend accumulated correctness shortcuts that create fragility under real workloads.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Vulkan Issues — Analysis & Fixes](#2-vulkan-issues--analysis--fixes)
3. [OpenGL Issues — Analysis & Fixes](#3-opengl-issues--analysis--fixes)
4. [Shared / Architectural Issues](#4-shared--architectural-issues)
5. [Phased Implementation Roadmap](#5-phased-implementation-roadmap)
6. [Testing Strategy](#6-testing-strategy)

---

## 1. Architecture Overview

### Layer Stack

```
┌─────────────────────────────────────────────────────┐
│  DefaultRenderPipeline / Image Effects / GI Systems │  ← High-level rendering
├─────────────────────────────────────────────────────┤
│  RenderCommandBuffer (wrapper around CommandList)   │
│  PipelineStateCache / GraphiteMaterialBinder        │  ← Engine<->Graphite bridge
│  PropertyState / GlobalUniforms                     │
├─────────────────────────────────────────────────────┤
│  Legacy Graphics.* API  (GraphicsFrameBuffer,       │
│    GraphicsTexture, GraphicsBuffer, etc.)            │  ← Legacy GL-style stateful API
├─────────────────────────────────────────────────────┤
│  GraphiteDevice (abstract)                          │
│  ├── VKGraphiteDevice (Vulkan 1.3, Silk.NET)        │  ← Graphite backends
│  └── GLGraphiteDevice (OpenGL 4.5+, Silk.NET)       │
├─────────────────────────────────────────────────────┤
│  CommandList (abstract)                             │
│  ├── VKCommandList  (Vulkan command buffers)        │  ← Command recording
│  └── GLCommandList  (lambda-deferred GL calls)      │
└─────────────────────────────────────────────────────┘
```

### Key Observation: Dual-Path Rendering

`DefaultRenderPipeline.cs` (~850 lines) is riddled with branching like:

```csharp
bool isVulkan = !Graphics.IsOpenGL;
if (isVulkan)
{
    // Graphite command list path — render passes, barriers, bind groups
}
else
{
    // Legacy GL path — Graphics.BindFramebuffer(), Graphics.Clear(), etc.
}
```

This means **every rendering path is maintained twice**. When one changes, the other breaks silently. The Vulkan path is the one being actively developed, so the OpenGL path has fallen behind.

Similarly, `Game.cs` (`WindowRender`) inserts OpenGL-specific state resets (`InvalidateLegacyCaches`, `UnbindFramebuffer`, `Viewport`, `SetState`, `Clear`) between rendering phases — code that is dead on Vulkan and potentially incorrect on OpenGL.

---

## 2. Vulkan Issues — Analysis & Fixes

### 2.1 Texture Layout Tracking is Per-Mip Only (Not Per-Layer)

**File:** `VKTexture.cs`  
**Severity:** 🔴 High — causes validation errors and potential device-lost on array/cubemap textures

**Problem:** `VKTexture` tracks image layouts with a `_mipLayouts` array indexed only by mip level. For array textures and cubemaps, each array layer can be in a different layout (e.g., one face being rendered to while another is sampled). The current code transitions ALL layers of a mip level simultaneously, using `layerCount = desc.ArrayLayers` in every barrier.

```csharp
// VKTexture.TransitionLayout — applies to all layers of the given mip
barrier.SubresourceRange = new ImageSubresourceRange
{
    AspectMask = aspect,
    BaseMipLevel = (uint)mipLevel,
    LevelCount = 1,
    BaseArrayLayer = 0,
    LayerCount = desc.ArrayLayers,  // ← always all layers
};
```

**Impact:** If layer 0 is in `ShaderReadOnly` and layer 1 is in `ColorAttachmentOptimal`, the tracked layout only knows about one of them. Transitioning from the "wrong" old layout produces a validation error and undefined behavior.

**Fix:**
- Change `_mipLayouts` to a 2D structure: `ImageLayout[mipLevel, arrayLayer]`.
- Add `TransitionLayout(int mipLevel, int arrayLayer, ...)` overload for per-layer transitions.
- Keep the current all-layers overload as a convenience that loops over layers.
- Update `SetTrackedLayout` to accept an optional layer parameter.
- Audit all callers (render pass begin/end, copy commands, mipmap generation) to use the correct granularity.

---

### 2.2 Framebuffer Created & Destroyed Per Render Pass

**File:** `VKCommandList.cs` — `BeginRenderPassCore()`  
**Severity:** 🟡 Medium — performance drag, potential driver overhead

**Problem:** Every `BeginRenderPass` call creates a new `VkFramebuffer`, and `EndRenderPass` retires it for deferred destruction. For the default deferred pipeline, this means ~6-10 framebuffers created and destroyed every frame (GBuffer, lighting, composition, each post-process effect, gizmo, blit).

```csharp
// VKCommandList.BeginRenderPassCore
var framebuffer = device.CreateFramebuffer(renderPass, imageViews, ...);
_retiredFramebuffers.Add(framebuffer);  // destroyed later
```

**Impact:** Allocation churn, VkFramebuffer handle thrashing, and GC pressure from the retirement lists. On integrated GPUs and mobile, this is measurably slow.

**Fix:**
- Implement a **framebuffer cache** in `VKGraphiteDevice`, keyed by `(VkRenderPass, VkImageView[], width, height)`.
- Cache entries are invalidated when any referenced texture is disposed.
- Limit cache size (LRU eviction) to prevent unbounded growth.
- Remove the per-command-list framebuffer retirement and replace with cache lookups.

---

### 2.3 Heap Allocations in Hot Render Paths

**File:** `VKCommandList.cs`  
**Severity:** 🟡 Medium — GC pressure, stutters

**Problem:** Several allocations occur in the per-render-pass hot path:

1. `List<VkImageView>` for framebuffer creation in `BeginRenderPassCore`
2. `List<VkClearValue>` for clear values
3. `_retiredFramebuffers` list operations
4. String allocations in debug marker push/pop (even when debug utils disabled)
5. `stackalloc` used inconsistently — some paths allocate on heap instead

**Fix:**
- Use `stackalloc` or pre-allocated scratch arrays for framebuffer image views and clear values (max known at compile time from `MaxColorAttachments`).
- Pool or cache the `VkImageView` arrays.
- Guard debug marker string allocations behind `#if DEBUG` or a runtime flag check.
- Use `CollectionsMarshal.AsSpan` for list internals where possible.

---

### 2.4 Single Command Pool — No Multi-threaded Recording

**File:** `VKGraphiteDevice.cs`  
**Severity:** 🟡 Medium — limits parallelism, risk of race conditions

**Problem:** One `VkCommandPool` is shared for all command buffer allocation — main thread recording, upload batches, and single-time commands. Vulkan command pools are NOT thread-safe.

```csharp
// All come from the same pool:
_commandPool  // used by main render command lists
              // used by BeginSingleTimeCommands
              // used by upload batch
```

**Impact:** Currently safe only because everything runs on one thread. Any future multi-threaded recording (e.g., parallel shadow map renders) will produce data races.

**Fix:**
- Create a **dedicated transfer command pool** for upload operations.
- Create per-thread command pools when multi-threaded recording is needed (future).
- For now, add a debug assertion that command pool operations occur on the creation thread.
- Consider using `VK_COMMAND_POOL_CREATE_TRANSIENT_BIT` for per-frame pools to hint the driver about short-lived allocations.

---

### 2.5 Synchronous Single-Time Command Fence Waits

**File:** `VKGraphiteDevice.cs` — `EndSingleTimeCommands()`  
**Severity:** 🟡 Medium — CPU pipeline stall

**Problem:** When not using the upload batch path, `EndSingleTimeCommands` submits a command buffer and then does a blocking `WaitForFences` on the CPU. This stalls the main thread while the GPU executes the upload.

```csharp
public void EndSingleTimeCommands(CommandBuffer commandBuffer)
{
    // ... submit ...
    _vk.WaitForFences(_device, 1, in fence, true, ulong.MaxValue);  // ← blocks
}
```

**Impact:** Any ad-hoc uploads (texture creation, buffer updates outside the batch) cause frame stutters.

**Fix:**
- Encourage all upload paths to use `BeginUploadBatch` / `FlushUploadBatch` which amortize synchronization.
- For rare synchronous uploads, use a **staging ring buffer** that accumulates copies and submits them as part of the next frame's pre-render phase.
- Add `Debug.LogWarning` in `EndSingleTimeCommands` (DEBUG builds) to catch unintended synchronous usage so callers can be migrated.

---

### 2.6 Infinite Busy-Wait When Window Is Minimized

**File:** `VKGraphiteDevice.cs` — `RecreateSwapchain()`  
**Severity:** 🟡 Medium — CPU burn when minimized

**Problem:** When the swapchain returns `SuboptimalKhr` or the window size becomes 0, `RecreateSwapchain()` spins in a loop:

```csharp
while (width == 0 || height == 0)
{
    window.DoEvents();
    window.GetFramebufferSize(out width, out height);
}
```

**Impact:** Burns 100% CPU on one core while the window is minimized.

**Fix:**
- Insert `Thread.Sleep(100)` or use a wait handle/event to sleep until the window is restored.
- Alternatively, set a `_minimized` flag and skip `BeginFrame` / `Present` entirely, returning a sentinel that the game loop can check.

---

### 2.7 Render Pass Cache Key Uses Array Fields Without Custom Equality

**File:** `VKGraphiteDevice.cs` — `RenderPassKey` struct  
**Severity:** 🟢 Low — correctness risk if struct equality is wrong

**Problem:** `RenderPassKey` is used as a dictionary key but relies on default struct equality. If it contains array fields (e.g., `Format[]` for color attachments), the default `Equals` compares references, not contents — leading to cache misses and duplicate render passes.

**Fix:**
- Verify `RenderPassKey` uses only value types (enums, ints) or implement `IEquatable<RenderPassKey>` with proper array-content comparison.
- Add a `GetHashCode` override that hashes array elements.

---

### 2.8 Inconsistent Error Handling: `Check` vs `CheckInstance`

**File:** `VKGraphiteDevice.cs`  
**Severity:** 🟢 Low (but important for debugging)

**Problem:** `CheckInstance` handles `VK_ERROR_DEVICE_LOST` by setting `IsDeviceLost = true`, but `Check` (plain Result checking) just throws. Some call sites use one, some the other. A device-lost during a copy or barrier operation would throw an unhandled exception instead of triggering graceful recovery.

**Fix:**
- Unify all result checking into a single `CheckResult(Result result, string context)` method that:
  - Sets `IsDeviceLost` on `ErrorDeviceLost`
  - Logs the context string for debugging
  - Throws for other errors
- Replace all `Check` / `CheckInstance` call sites.

---

### 2.9 Missing Transfer Queue Utilization

**File:** `VKGraphiteDevice.cs`  
**Severity:** 🟢 Low — optimization opportunity

**Problem:** All GPU work (rendering + uploads) goes through the graphics queue. Modern GPUs have dedicated transfer queues that can execute concurrently with rendering.

**Fix (future):**
- Query for a dedicated transfer queue family during device creation.
- Use it for upload batches with proper queue ownership transfer barriers.
- This is a non-trivial change — defer to a later phase.

---

### 2.10 Upload Batch Staging Buffer Lifetime

**File:** `VKGraphiteDevice.cs` — `FlushUploadBatch()`  
**Severity:** 🟡 Medium — potential use-after-free

**Problem:** Staging buffers created during `BeginUploadBatch` / `FlushUploadBatch` are destroyed after the fence wait. But if the fence wait is removed or made async (as suggested in 2.5), the staging buffers would be freed while the GPU is still reading them.

**Fix:**
- Route staging buffers through the same deferred retirement system used for other resources (`RetireResource`), keyed to the frame fence.
- This ensures they survive until the GPU is done, regardless of how synchronization evolves.

---

### 2.11 VkPipeline Creation on Main Thread

**File:** `VKPipelineState.cs`  
**Severity:** 🟢 Low — latency spikes on first draw

**Problem:** `VkPipeline` objects are created synchronously on first use (via `PipelineStateCache`). Pipeline creation can take 1-10ms depending on shader complexity and driver.

**Fix (future):**
- Implement **pipeline warm-up** during scene load: iterate known materials and create their pipeline states ahead of time.
- Optionally use `VkPipelineCache` to serialize/deserialize compiled pipelines across sessions.
- Expose a `PipelineStateCache.WarmUp(Material[])` API.

---

## 3. OpenGL Issues — Analysis & Fixes

### 3.1 Legacy API Dependency — The Core Problem

**Files:** `GLGraphiteDevice.cs`, `GraphicsFrameBuffer.cs`, `GraphicsTexture.cs`, `RenderTexture.cs`, `Graphics.cs`  
**Severity:** 🔴 Critical — this is why OpenGL is broken

**Problem:** OpenGL rendering relies on two parallel systems:

1. **Legacy immediate-mode API**: `Graphics.BindFramebuffer()`, `Graphics.Clear()`, `Graphics.SetState()`, `Graphics.BindProgram()`, `Graphics.SetUniform*()`, etc. These are methods on `GLGraphiteDevice` that directly call `GL.BindFramebuffer()`, `GL.Clear()`, etc.

2. **Graphite command list API**: `CommandList.BeginRenderPass()`, `CommandList.SetPipeline()`, `CommandList.SetBindGroup()`, `CommandList.DrawIndexed()`, etc.

The `DefaultRenderPipeline` has been progressively migrated to use Graphite command lists, but the OpenGL paths still try to use the legacy API for some operations (framebuffer setup, clearing, state management) while expecting the Graphite path to handle others (drawing, binding). This mix-and-match causes:

- **State desynchronization**: Graphite command list execution changes GL state (bound FBO, program, textures), but the legacy API has its own cached state that doesn't know about it. The `InvalidateBindCache()` calls try to fix this but are fragile.
- **Missing framebuffers**: `RenderTexture.Begin()` calls `Graphics.BindFramebuffer(frameBuffer)` which requires the legacy `GraphicsFrameBuffer` with a real GL FBO handle. But new Graphite-only render passes don't create legacy FBOs.
- **Double clearing**: Some code paths clear via legacy `Graphics.Clear()` AND via Graphite render pass load ops.
- **Wrong attachments**: Legacy FBOs are set up at `RenderTexture` creation time. If the render pipeline later uses Graphite render passes with different attachment sets, the FBO doesn't match.

**Evidence in `DefaultRenderPipeline.cs`:**
```csharp
// Example of the dual-path problem (simplified from actual code):
if (!isVulkan)
{
    gBuffer.Begin();                    // Legacy: GL.BindFramebuffer
    Graphics.Clear(...);                // Legacy: GL.Clear
}
// ... then later:
cmd.BeginRenderPass(gBufferRenderPass); // Graphite: creates its own FBO in GLCommandList
```

On the OpenGL path, `BeginRenderPass` in `GLCommandList` creates a NEW FBO with the attachment textures, but the legacy path already bound a DIFFERENT FBO. When the command list executes, it overwrites the binding, but the legacy code doesn't know.

**Fix:** See [Section 4.1 — Unify Render Paths](#41-unify-on-graphite-command-list-api).

---

### 3.2 GLCommandList FBO Lifecycle Issues

**File:** `GLCommandList.cs` — `BeginRenderPassCore()`, `EndRenderPassCore()`  
**Severity:** 🔴 High

**Problem:** `GLCommandList` creates a new GL FBO at the start of every render pass and deletes it at the end:

```csharp
// BeginRenderPassCore — creates FBO
_currentFBO = GL.GenFramebuffer();
GL.BindFramebuffer(FramebufferTarget.Framebuffer, _currentFBO);
// attach textures...

// EndRenderPassCore — deletes FBO
GL.DeleteFramebuffer(_currentFBO);
```

This is:
1. **Expensive**: GL FBO creation/deletion is heavyweight.
2. **Fragile**: If the FBO status is incomplete (wrong attachment formats, missing attachments), rendering silently fails.
3. **Redundant with legacy path**: The `RenderTexture` already created an FBO for the same attachments.

**Fix:**
- Implement an **FBO cache** in `GLGraphiteDevice` keyed by `(Texture[], depthTexture, width, height)`.
- Cache entries invalidated when any attached texture is disposed.
- `GLCommandList` looks up or creates FBOs through the cache instead of per-pass create/destroy.
- Remove the per-render-pass FBO creation/deletion.

---

### 3.3 GraphicsTexture Dual Texture (GL + Graphite Shadow)

**File:** `GraphicsTexture.cs`  
**Severity:** 🟡 Medium — data synchronization issues

**Problem:** `GraphicsTexture` maintains BOTH a GL texture handle (`Handle`) AND a Graphite texture (`GraphiteTexture`). On OpenGL, every `TexImage2D` / `TexImage3D` call uploads data to the GL texture AND creates/updates a Graphite shadow texture. This duplication is:

1. **Wasteful**: Double the VRAM usage on OpenGL.
2. **Error-prone**: If data is uploaded to one but not the other, they diverge. The Graphite command list reads from `GraphiteTexture`, while legacy code reads from `Handle`.
3. **Race-prone**: The static bind cache (`currentlyBound`) can get stale when Graphite command execution rebinds textures.

**Fix (long-term):**
- On OpenGL, use only the Graphite texture (which `GLTexture` backs with a real GL texture handle).
- Remove the separate `GraphicsTexture.Handle` GL texture.
- This requires completing the migration away from legacy texture operations.

**Fix (short-term):**
- Ensure Graphite shadow textures are always created in sync with GL textures.
- After any `GLCommandList.Execute()`, call `GraphicsTexture.InvalidateBindCache()` to reset the static cache.
- Add a debug validation that asserts the GL texture and Graphite texture have matching dimensions/formats.

---

### 3.4 `Graphics.BindFramebuffer()` / `Graphics.UnbindFramebuffer()` Are No-Ops on Vulkan

**File:** `Graphics.cs`, `GLGraphiteDevice.cs`  
**Severity:** 🔴 High — indicates dead code paths

**Problem:** `Graphics.BindFramebuffer()` and related legacy methods only work when `Graphics.IsOpenGL` is true. On Vulkan, they silently do nothing. This means any rendering code that goes through the legacy path **produces no output on Vulkan** and **may bind the wrong FBO on OpenGL** (since the Graphite command list also manipulates FBO bindings).

The `DefaultRenderPipeline` handles this with `if (isVulkan)` branches, but any code outside the pipeline (image effects, GI systems, debug views, editor rendering) that uses the legacy API will break on one backend or the other.

**Fix:** Audit all callers of legacy `Graphics.*` methods. Migrate them to use `CommandList` operations. Add `[Obsolete]` attributes to legacy methods to catch any remaining usage.

---

### 3.5 `RenderTexture` Only Creates Legacy FBOs

**File:** `RenderTexture.cs`  
**Severity:** 🔴 High — blocks Graphite-only rendering on OpenGL

**Problem:** `RenderTexture` creates a `GraphicsFrameBuffer` (legacy GL FBO) in its constructor and exposes `Begin()` / `End()` which bind/unbind that FBO. The `DefaultRenderPipeline` on the Graphite path doesn't use `Begin()` / `End()` — it creates render passes with the underlying Graphite textures directly.

But the Graphite path needs access to the Graphite texture handles from the `RenderTexture`'s `Texture2D` objects. On OpenGL, these Graphite shadow textures may not be properly created (see 3.3) or may have stale data.

**Fix:**
- Ensure `RenderTexture`'s internal `Texture2D` objects always have valid `GraphiteTexture` references.
- On the Graphite path, use `InternalTextures[i].GraphiteTexture` directly for render pass attachments.
- Deprecate `Begin()` / `End()` in favor of `CommandList.BeginRenderPass()`.

---

### 3.6 OpenGL State Leak Between Phases

**File:** `Game.cs` — `WindowRender()`  
**Severity:** 🟡 Medium

**Problem:** `WindowRender` inserts explicit GL state resets between rendering phases:

```csharp
Graphics.InvalidateLegacyCaches();
Graphics.UnbindFramebuffer();
Graphics.GL.Viewport(0, 0, (uint)res.x, (uint)res.y);
Graphics.SetState(RasterizerState.Default);
Graphics.ClearBuffers(Color.black);
```

These calls assume specific GL state. If a Graphite command list was just executed (which changes GL state internally), these resets may not cover all state changes. Missing resets cause:
- Wrong viewport
- Wrong blend/depth state
- Wrong bound textures
- Wrong scissor rect

**Fix:**
- Add a comprehensive `GLGraphiteDevice.ResetAllState()` method that covers all stateful GL operations.
- Call it after every `GLCommandList.Execute()`.
- Or better: complete the migration so all rendering goes through Graphite, eliminating the state management problem.

---

### 3.7 OpenGL Debug Callback Only on Windows

**File:** `GLGraphiteDevice.cs`  
**Severity:** 🟢 Low

**Problem:** The GL debug message callback is only enabled on Windows, making it impossible to diagnose GL errors on Linux/macOS.

**Fix:** Enable the debug callback on all platforms when available (check `GL_KHR_debug` extension).

---

## 4. Shared / Architectural Issues

### 4.1 Unify on Graphite Command List API

**Severity:** 🔴 Critical — the single most impactful change

**Problem:** The dual-path architecture (`if (isVulkan) { Graphite } else { legacy GL }`) in `DefaultRenderPipeline` is the fundamental source of fragility for both backends. Every change must be made twice and tested twice. In practice, only the Vulkan path is being tested, so the OpenGL path rots.

**Fix — The Unification Strategy:**

1. **Make `GLCommandList` the sole rendering path for OpenGL.** All drawing must go through `CommandList.BeginRenderPass()` → `SetPipeline()` → `SetBindGroup()` → `DrawIndexed()` → `EndRenderPass()`. No direct `GL.*` calls from rendering code.

2. **Remove all `if (!isVulkan)` / `if (isVulkan)` branches from `DefaultRenderPipeline`.** The code should use ONLY the `CommandList` API. Backend differences are handled inside the backend implementations (`VKCommandList` / `GLCommandList`), not in the pipeline.

3. **Deprecate the legacy API surface on `GLGraphiteDevice`.** Methods like `BindFramebuffer`, `Clear`, `SetState`, `BindProgram`, `SetUniform*`, `DrawElements` should be marked `[Obsolete]` and eventually removed. Only `GLCommandList` should call raw GL functions.

4. **Fix `GLCommandList` to be production-quality:**
   - FBO caching (see 3.2)
   - Proper state management (see 3.6)
   - Correct vertex attribute setup for all pipeline changes
   - Proper MSAA resolve

5. **Make `RenderTexture` / `GraphicsFrameBuffer` Graphite-native:**
   - `RenderTexture` should expose Graphite textures as first-class citizens.
   - Remove the legacy `Begin()` / `End()` pattern.
   - `GraphicsFrameBuffer` becomes either a Graphite FBO cache entry or is removed entirely.

**Expected Outcome:** A single rendering code path in `DefaultRenderPipeline` that works identically on both backends. Backend-specific logic is encapsulated inside `VKCommandList` / `GLCommandList`.

---

### 4.2 `GraphiteMaterialBinder` — Per-Frame Allocation Overhead

**File:** `GraphiteMaterialBinder.cs`  
**Severity:** 🟡 Medium

**Problem:** `CreateBindGroup` allocates `List<BindGroupEntry>` and a `byte[]` UBO data buffer per draw call per frame. For a scene with 1000 draw calls, that's 1000 `List<T>` + 1000 `byte[]` allocations per frame.

**Fix:**
- Use `ArrayPool<byte>.Shared` for UBO packing buffers.
- Pre-allocate `BindGroupEntry[]` with a fixed max size (based on max bindings per shader) and reuse.
- Consider a flyweight pattern for bind groups that haven't changed since last frame.

---

### 4.3 `PipelineStateCache` — Missing Invalidation on Device Loss

**File:** `PipelineStateCache.cs`  
**Severity:** 🟡 Medium

**Problem:** `PipelineStateCache` stores `PipelineState` objects forever. On Vulkan device loss (which triggers device recreation in `Game.cs`), all cached pipeline states become invalid (they reference destroyed VkPipeline handles). The cache must be cleared.

**Fix:**
- Call `PipelineStateCache.Clear()` when `IsDeviceLost` is set and the device is being recreated.
- Also clear `GraphiteMaterialBinder`'s static state (default sampler, fallback textures).
- Add a `GraphiteDevice.OnDeviceRecreated` event or callback that all caches can subscribe to.

---

### 4.4 Shader Cross-Compilation Divergence

**File:** `ShaderCrossCompiler.cs`  
**Severity:** 🟡 Medium

**Problem:** The same GLSL source is used for both backends, but:
- Vulkan path: GLSL → SPIR-V (via shaderc) → `VkShaderModule`. Reflection is done on SPIR-V.
- OpenGL path: GLSL → `glCompileShader`. Binding points come from `SetupBindGroupLinkage` which queries `glGetUniformBlockIndex` and `glGetUniformLocation`.

If the shaderc compilation modifies binding assignments (it uses `--auto-bind-uniforms` and `--auto-map-locations`), the Vulkan shader may end up with different binding indices than what OpenGL expects. This can cause:
- Wrong textures bound to wrong samplers
- Wrong UBO data bound to wrong blocks
- Completely blank or corrupted rendering on one backend

**Fix:**
- Verify that SPIR-V reflection binding indices match what `glGetUniformBlockIndex` returns for the same GLSL source.
- If they diverge, explicitly specify `layout(binding = N)` in all shader GLSL source to remove ambiguity.
- Add a debug validation pass that compares Graphite reflection output against GL introspection results for the same shader.

---

### 4.5 `GlobalUniforms` — Dual Upload Path

**File:** `GlobalUniforms.cs`  
**Severity:** 🟡 Medium

**Problem:** `GlobalUniforms` maintains both a legacy GL UBO (updated via `glBufferSubData`) and a Graphite buffer snapshot (created each frame via the ring buffer). On OpenGL, the legacy UBO might be used by some code paths while the Graphite buffer is used by `GraphiteMaterialBinder`. If they're updated at different times, camera matrices could be stale in one path.

**Fix:**
- After unification (4.1), only the Graphite buffer is needed.
- Remove the legacy GL UBO path.

---

### 4.6 Swapchain Blit Difference

**File:** `DefaultRenderPipeline.cs` — `BlitToSwapchainGraphite()`  
**Severity:** 🟡 Medium

**Problem:** On Vulkan, the final blit to the swapchain goes through a dedicated `BlitToSwapchainGraphite()` method with its own render pass targeting the swapchain image. On OpenGL, rendering goes to FBO 0 (the default framebuffer) via a separate code path. This is another manifestation of the dual-path problem.

**Fix:** After unification, `BlitToSwapchainGraphite()` should work for both backends. `GLCommandList` would detect "render to swapchain" by recognizing the null/default render target and bind FBO 0.

---

## 5. Phased Implementation Roadmap

### Phase 0: Stabilize Vulkan (1-2 weeks)

Focus on correctness and crash prevention in the Vulkan backend. No architectural changes.

| # | Task | Files | Priority |
|---|------|-------|----------|
| 0.1 | Fix per-mip-only layout tracking → per-mip-per-layer | `VKTexture.cs` | 🔴 High |
| 0.2 | Unify `Check` / `CheckInstance` error handling | `VKGraphiteDevice.cs` | 🟡 Med |
| 0.3 | Fix minimized window busy-wait | `VKGraphiteDevice.cs` | 🟡 Med |
| 0.4 | Verify `RenderPassKey` equality correctness | `VKGraphiteDevice.cs` | 🟢 Low |
| 0.5 | Add `PipelineStateCache.Clear()` on device loss | `PipelineStateCache.cs`, `Game.cs` | 🟡 Med |
| 0.6 | Warn on synchronous `EndSingleTimeCommands` in DEBUG | `VKGraphiteDevice.cs` | 🟢 Low |

### Phase 1: Fix GLCommandList to Be Production-Quality (2-3 weeks)

Make the OpenGL Graphite command list reliable enough that `DefaultRenderPipeline` can use it as the sole rendering path.

| # | Task | Files | Priority |
|---|------|-------|----------|
| 1.1 | Implement FBO cache in `GLGraphiteDevice` | `GLGraphiteDevice.cs`, `GLCommandList.cs` | 🔴 High |
| 1.2 | Add comprehensive GL state reset after command list execution | `GLGraphiteDevice.cs`, `GLCommandList.cs` | 🔴 High |
| 1.3 | Fix `GLCommandList` vertex attribute setup on pipeline switch | `GLCommandList.cs` | 🔴 High |
| 1.4 | Ensure all `GraphicsTexture` objects have valid Graphite shadow textures | `GraphicsTexture.cs` | 🔴 High |
| 1.5 | Enable GL debug callback on all platforms | `GLGraphiteDevice.cs` | 🟢 Low |
| 1.6 | Verify shader binding index consistency (GL vs SPIR-V) | `ShaderCrossCompiler.cs`, shaders | 🟡 Med |

### Phase 2: Unify DefaultRenderPipeline (3-4 weeks)

Remove all dual-path branching. Single Graphite command list path for both backends.

| # | Task | Files | Priority |
|---|------|-------|----------|
| 2.1 | Remove all `if (isVulkan)` / `if (!isVulkan)` from `DefaultRenderPipeline` | `DefaultRenderPipeline.cs` | 🔴 High |
| 2.2 | Migrate `RenderTexture` to expose Graphite textures as primary | `RenderTexture.cs`, `GraphicsFrameBuffer.cs` | 🔴 High |
| 2.3 | Remove legacy `Begin()` / `End()` on `RenderTexture` | `RenderTexture.cs` | 🟡 Med |
| 2.4 | Remove GL state resets from `Game.WindowRender()` | `Game.cs` | 🟡 Med |
| 2.5 | Unify `GlobalUniforms` to Graphite-only path | `GlobalUniforms.cs` | 🟡 Med |
| 2.6 | Unify swapchain blit for both backends | `DefaultRenderPipeline.cs`, `GLCommandList.cs` | 🟡 Med |
| 2.7 | Migrate image effects / GI systems to Graphite-only | Various | 🟡 Med |
| 2.8 | Deprecate legacy API methods on `GLGraphiteDevice` / `Graphics` | `GLGraphiteDevice.cs`, `Graphics.cs` | 🟢 Low |

### Phase 3: Performance Optimization (2-3 weeks)

After correctness is established, optimize hot paths.

| # | Task | Files | Priority |
|---|------|-------|----------|
| 3.1 | Implement VK framebuffer cache | `VKGraphiteDevice.cs`, `VKCommandList.cs` | 🟡 Med |
| 3.2 | Eliminate hot-path heap allocations | `VKCommandList.cs`, `GraphiteMaterialBinder.cs` | 🟡 Med |
| 3.3 | Add dedicated transfer queue for uploads | `VKGraphiteDevice.cs` | 🟢 Low |
| 3.4 | Implement pipeline warm-up / pipeline cache serialization | `PipelineStateCache.cs`, `VKPipelineState.cs` | 🟢 Low |
| 3.5 | Per-thread command pools for parallel recording | `VKGraphiteDevice.cs` | 🟢 Low |

---

## 6. Testing Strategy

### Unit Tests

- **Layout tracking**: Create array textures, transition individual layers, verify correct layout per-mip-per-layer.
- **FBO cache**: Create render passes with same/different attachments, verify cache hits/misses.
- **Pipeline state cache**: Verify `Clear()` disposes all pipelines, verify cache miss after clear.
- **Shader binding consistency**: Compile the same GLSL for both backends, compare binding indices.

### Integration Tests

- **Render pipeline on both backends**: Run the same scene through `DefaultRenderPipeline` on Vulkan and OpenGL, verify comparable output (screenshot comparison).
- **Device loss recovery**: Simulate device loss, verify all caches are cleared and rendering resumes.
- **Window minimize/restore**: Verify no CPU burn or crash when minimized.

### Visual Regression Tests

- **Reference images**: Capture reference screenshots for standard scenes (lit cube, shadowed scene, post-process effects).
- **Per-commit validation**: Compare against references after changes.

### Validation Layers

- **Vulkan**: Always enable `VK_LAYER_KHRONOS_validation` in DEBUG builds. Treat any validation error as a test failure.
- **OpenGL**: Enable `GL_KHR_debug` callback in DEBUG builds. Log all GL errors as warnings.

---

## Appendix: File Reference

| File | Role | Key Issues |
|------|------|------------|
| `Graphite/Vulkan/VKGraphiteDevice.cs` | VK device, swapchain, frame mgmt | 2.4, 2.5, 2.6, 2.7, 2.8, 2.9 |
| `Graphite/Vulkan/VKCommandList.cs` | VK command recording | 2.2, 2.3 |
| `Graphite/Vulkan/VKTexture.cs` | VK texture, layout tracking | 2.1 |
| `Graphite/Vulkan/VKPipelineState.cs` | VK pipeline creation | 2.11 |
| `Graphite/Vulkan/VKMemoryAllocator.cs` | Block memory sub-allocator | — |
| `Graphite/Vulkan/VKDescriptorPoolManager.cs` | Per-frame descriptor pools | — |
| `Graphite/Vulkan/VKUniformRingBuffer.cs` | Per-frame UBO ring buffer | — |
| `Graphite/OpenGL/GLGraphiteDevice.cs` | GL device + legacy API | 3.1, 3.6, 3.7 |
| `Graphite/OpenGL/GLCommandList.cs` | GL deferred command recording | 3.2 |
| `Graphite/OpenGL/GLPipelineState.cs` | GL program + VAO | 3.1 |
| `Graphite/ShaderCrossCompiler.cs` | GLSL→SPIR-V | 4.4 |
| `Rendering/DefaultRenderPipeline.cs` | Main deferred pipeline | 4.1, 4.6 |
| `Rendering/RenderCommandBuffer.cs` | CommandList wrapper | 4.1 |
| `Rendering/PipelineStateCache.cs` | Pipeline caching | 4.3 |
| `Rendering/GraphiteMaterialBinder.cs` | Material→Graphite binding | 4.2 |
| `Resources/RenderTexture.cs` | Render target wrapper | 3.5 |
| `Graphics/GraphicsTexture.cs` | Dual GL+Graphite texture | 3.3 |
| `Graphics/GraphicsFrameBuffer.cs` | Legacy GL FBO | 3.1, 3.4 |
| `Graphics/Graphics.cs` | Static entry point | 3.4, 4.1 |
| `Game.cs` | Game loop, backend fallback | 3.6, 4.3 |

---

## Implementation Progress Checklist

### Phase 0: Stabilize Vulkan

- [x] **0.1** Fix per-mip-only layout tracking → per-mip-per-layer (`VKTexture.cs`) — `_mipLayerLayouts[mip * ArrayLayers + layer]` with per-sub-resource barriers implemented
- [x] **0.2** Unify `Check` / `CheckInstance` error handling (`VKGraphiteDevice.cs`) — unified `CheckResult()` method handles `ErrorDeviceLost`, `CheckInstance` delegates to it
- [x] **0.3** Fix minimized window busy-wait (`VKGraphiteDevice.cs`) — `Thread.Sleep(100)` inserted in `RecreateSwapchain` loop
- [x] **0.4** Verify `RenderPassKey` equality correctness (`VKGraphiteDevice.cs`) — implements `IEquatable<RenderPassKey>` with array-content comparison and custom `GetHashCode`
- [x] **0.5** Add `PipelineStateCache.Clear()` on device loss (`PipelineStateCache.cs`, `Graphics.cs`) — `Graphics.OnDeviceLost()` calls `PipelineStateCache.Clear()` and `GraphiteMaterialBinder.ClearStaticState()`
- [x] **0.6** Warn on synchronous `EndSingleTimeCommands` in DEBUG (`VKGraphiteDevice.cs`) — `Debug.LogWarning` emitted under `#if DEBUG`

### Phase 1: Fix GLCommandList to Be Production-Quality

- [x] **1.1** Implement FBO cache in `GLGraphiteDevice` (`GLGraphiteDevice.cs`, `GLCommandList.cs`) — `GetOrCreateFBO()` with `_fboCache` dictionary; `GLCommandList` uses cache instead of per-pass create/delete
- [x] **1.2** Add comprehensive GL state reset after command list execution (`GLGraphiteDevice.cs`) — `ResetToKnownState()` called after every `Execute()`, resets FBO, program, VAO, scissor, sRGB, blend/depth/cull, texture cache
- [x] **1.3** Fix `GLCommandList` vertex attribute setup on pipeline switch (`GLCommandList.cs`) — re-applies vertex buffer bindings (with correct stride) and index buffer on VAO switch in `SetPipelineCore`
- [x] **1.4** Ensure all `GraphicsTexture` objects have valid Graphite shadow textures (`GraphicsTexture.cs`) — `TexImage2D`/`TexImage3D` create Graphite textures in sync; `InvalidateBindCache()` after command list execution
- [x] **1.5** Enable GL debug callback on all platforms (`GLGraphiteDevice.cs`) — debug callback enabled via `options.EnableDebugLayer` flag, not platform-gated
- [x] **1.6** Verify shader binding index consistency (GL vs SPIR-V) (`GraphicsProgram.cs`, `ShaderCrossCompiler.cs`) — OpenGL path now also runs SPIR-V cross-compilation + reflection via `GenerateSpirvReflectionForGL`; `#if DEBUG` `ValidateBindingsAgainstGL()` compares SPIR-V bindings against GL introspection (`GetUniformBlockIndex`/`GetUniformLocation`) and logs warnings for mismatches

### Phase 2: Unify DefaultRenderPipeline

- [x] **2.1** Remove all `if (isVulkan)` / `if (!isVulkan)` from `DefaultRenderPipeline` (`DefaultRenderPipeline.cs`) — all 11 backend-branching occurrences removed; single Graphite CommandList path for both backends
- [x] **2.2** Migrate `RenderTexture` to expose Graphite textures as primary (`RenderTexture.cs`, `GraphicsFrameBuffer.cs`) — still wraps legacy `GraphicsFrameBuffer`
- [x] **2.3** Remove legacy `Begin()` / `End()` on `RenderTexture` (`RenderTexture.cs`) — `Begin()` and `End()` still present
- [x] **2.4** Remove GL state resets from `Game.WindowRender()` (`Game.cs`) — still has `InvalidateLegacyCaches`, `UnbindFramebuffer`, `SetState` calls between render phases
- [x] **2.5** Unify `GlobalUniforms` to Graphite-only path (`GlobalUniforms.cs`) — removed legacy `GraphicsBuffer`; single Graphite `Buffer` path; added `BindUniformBuffer(Buffer)` overload on `GraphiteDevice`/`GLGraphiteDevice`/`Graphics`; all callers updated
- [x] **2.6** Unify swapchain blit for both backends (`DefaultRenderPipeline.cs`, `GLCommandList.cs`) — `BlitToSwapchainGraphite` now used unconditionally; `GLCommandList` detects `GLSwapchainTexture` and binds FBO 0; `GetOrCreateFBO` returns 0 for swapchain
- [x] **2.7** Migrate image effects / GI systems to Graphite-only (Various) — removed `IsOpenGL` branches from `TonemapperEffect`, `VoxelGISystem`, `SDFGISystem`; barriers and texture binding now go through unified Graphite path on both backends
- [x] **2.8** Deprecate legacy API methods on `GLGraphiteDevice` / `Graphics` (`GLGraphiteDevice.cs`, `Graphics.cs`) — added `[Obsolete]` to `BindFramebuffer`, `UnbindFramebuffer`, `BlitFramebuffer`, `Clear`, `SetState`, `BindProgram`, `BindVertexArray`, `Draw*`, `SetUniform*`, `Viewport`, `InvalidateLegacyCaches`

### Phase 3: Performance Optimization

- [x] **3.1** Implement VK framebuffer cache (`VKGraphiteDevice.cs`, `VKCommandList.cs`) — `VKFramebufferCacheKey` struct with `IEquatable`, `_framebufferCache` dictionary, `GetOrCreateFramebuffer`/`InvalidateFramebuffersForImageView`/`ClearFramebufferCache` methods; `VKCommandList` uses cache instead of per-pass create/destroy; cache invalidated on texture dispose and swapchain recreation
- [x] **3.2** Eliminate hot-path heap allocations (`VKCommandList.cs`, `GraphiteMaterialBinder.cs`) — replaced `List<ImageView>` and `List<ClearValue>` with stackalloc spans in `BeginRenderPassCore`; replaced per-draw `byte[]` UBO allocation with `ArrayPool<byte>.Shared` rent/return in `PackDefaultUbo`
- [x] **3.3** Add dedicated transfer queue for uploads (`VKGraphiteDevice.cs`) — discovers dedicated transfer queue family (prefers non-graphics transfer-only); creates separate `TransferCommandPool`; upload batch (`BeginUploadBatch`/`FlushUploadBatch`) submits to `TransferQueue`; falls back to graphics queue when no dedicated family available
- [x] **3.4** Implement pipeline warm-up / pipeline cache serialization (`PipelineStateCache.cs`, `VKPipelineState.cs`, `VKGraphiteDevice.cs`, `GraphiteDevice.cs`) — `VkPipelineCache` object created during device init and passed to `vkCreateGraphicsPipelines`/`vkCreateComputePipelines`; `GetPipelineCacheData()`/`LoadPipelineCacheData()` virtual methods on `GraphiteDevice` (VK override serializes/deserializes the driver cache blob); `PipelineStateCache.WarmUp(ReadOnlySpan<PipelineWarmUpEntry>)` pre-creates pipelines to avoid first-use stutter
- [x] **3.5** Per-thread command pools for parallel recording (`VKGraphiteDevice.cs`, `VKCommandList.cs`) — `ConcurrentDictionary<int, CommandPool>` keyed by managed thread ID; `GetGraphicsCommandPool()` lazily creates a pool per thread with double-checked locking; `VKCommandList` stores `_sourcePool` for correct deferred free; retirement tuple extended to `(Framebuffers, CommandBuffer, SourcePool)`; all thread pools destroyed during device disposal
