# Graphite Migration Plan

> **Goal:** Switch the entire Prowl engine rendering from the legacy `GraphicsDevice`
> (immediate-mode, OpenGL-only) to the Graphite abstraction layer, and ship working
> OpenGL **and** Vulkan backends behind the same API.

---

## Architecture Overview

```
┌──────────────────────────────────────────────────────────────┐
│  Layer 4: Engine Systems (Camera, DefaultRenderPipeline,     │
│           ImageEffects, Shadows, UI Rendering)               │
├──────────────────────────────────────────────────────────────┤
│  Layer 3: High-Level Resources (Material, Mesh, Shader,      │
│           RenderTexture, Texture2D) — engine resource types   │
├──────────────────────────────────────────────────────────────┤
│  Layer 2: Rendering Bridge / Command Layer                    │
│           (CommandBuffer wrapping Graphite.CommandList,        │
│            RenderContext, PropertyState → BindGroup mapping)   │
├──────────────────────────────────────────────────────────────┤
│  Layer 1: Graphite Core (GraphiteDevice, CommandList,         │
│           Buffer, Texture, PipelineState, BindGroup, Fence)   │
├──────────────────────────────────────────────────────────────┤
│  Layer 0: Backend (GLGraphiteDevice / VKGraphiteDevice)       │
│           via Silk.NET OpenGL / Vulkan                        │
└──────────────────────────────────────────────────────────────┘
```

---

## Current State

### Local Graphite (`Prowl.Runtime\Graphite\`)
- WebGPU/Vulkan-inspired low-level abstraction embedded in `Prowl.Runtime`.
- Resources: `Buffer`, `Texture`, `Sampler`, `ShaderModule`, `PipelineState`, `BindGroup`, `Fence`.
- `CommandList` with render pass scoping, compute dispatch, copy commands, debug markers.
- **OpenGL backend** — fully implemented (`GL*` classes).
- **Vulkan backend** — structurally present (`VK*` classes), uses Silk.NET Vulkan.
- Descriptors, enums, and unit tests in place.

### Upstream Graphite (`ProwlEngine/Prowl.Graphite` repo)
- Higher-level Unity-style abstraction (separate project).
- `GraphicsDevice` (singleton), `CommandBuffer`, `Renderer`, `Material`, `Mesh`, `Shader`.
- **Only OpenGL** — Vulkan enum exists but no implementation.
- Threaded GL dispatcher, queued command objects.
- Many stubs — significantly less complete.

### Legacy Rendering (`Prowl.Runtime\Graphics\`)
- `GraphicsDevice` → `OpenGLGraphicsDevice` (working) / `VulkanGraphicsDevice` (stub).
- OpenGL immediate-mode: `BindBuffer`, `SetUniform*`, `Draw*`.
- `GraphicsBuffer`, `GraphicsTexture`, `GraphicsFrameBuffer`, `GraphicsProgram`, `GraphicsVertexArray` — all Silk.NET OpenGL types.
- `Graphics` static class: delegates to `GraphicsDevice`, exposes `Graphics.GL`.
- `DefaultRenderPipeline`: full deferred + forward pipeline.

### Decision
Use the **local Graphite** as the low-level GPU API and build upstream-style higher-level
abstractions on top. The upstream repo is too incomplete to adopt directly.

---

## Phase 0 — Preparation & Audit *(no runtime changes)*

- [x] Grep for every `Graphics.GL.` call site — each is a direct OpenGL leak to abstract.
  - **77 call sites** across 6 files, all inside `Graphics\` wrapper types or
    `StubEditorRendering`: `GraphicsProgram.cs` (35), `GraphicsTexture.cs` (14),
    `GraphicsVertexArray.cs` (11), `GraphicsFrameBuffer.cs` (10), `GraphicsBuffer.cs` (5),
    `StubEditorRendering.cs` (2). These are expected — they will be removed when the
    wrapper types are deleted (Phase 4, deferred).
- [x] Grep for every `using Silk.NET.OpenGL;` outside `Graphite\OpenGL\` and `Graphics\` — flag for migration.
  - **3 files**: `StubEditorRendering.cs`, `GraphiteTestFixture.cs`, `IntegrationTests.cs`.
    All expected — `StubEditorRendering` will be cleaned when wrappers are removed;
    test files need GL for integration testing.
- [x] Catalog all `Graphics.Device.*` call sites.
  - **Zero** — all `Graphics.Device.*` references were removed in Phase 4.
- [x] Ensure all Vulkan `VK*` files compile (structural stubs are fine).
  - All 10 `VK*.cs` files compile: `VKBindGroup`, `VKBuffer`, `VKCommandList`, `VKFence`,
    `VKFormatHelper`, `VKGraphiteDevice`, `VKPipelineState`, `VKSampler`, `VKShaderModule`,
    `VKTexture`.

---

## Phase 1 — Unify Device Initialisation

> Wire `GraphiteDevice` into the engine startup alongside the legacy `GraphicsDevice`.
> Both devices are available; new code uses Graphite, old code keeps working.

- [x] Add `Graphics.Graphite` property returning `GraphiteDevice`.
- [x] Add `Graphics.SetGraphiteDevice()` for test injection.
- [x] Map `RenderingBackend` → `GraphicsBackendType` in a helper.
- [x] In `Graphics.Initialize()`: create **and** initialise `GraphiteDevice` after the legacy device.
- [x] In `Graphics.Dispose()`: dispose `GraphiteDevice`.
- [x] Update `GraphiteTestFixture` to use the new `Graphics.SetGraphiteDevice()`.

---

## Phase 2 — Bridge Layer: Shadow Graphite Resources

Add Graphite shadow resources alongside every legacy GL resource wrapper so that
both APIs have live GPU objects for the same data. This doubles GPU memory during
the transition but lets Phase 3 consume Graphite resources directly.

- [x] **`GraphiteFormatMapper`** (NEW) — maps legacy enums → Graphite enums:
      `TextureImageFormat → TextureFormat`, `BufferType → BufferUsage`,
      `TextureMin/Mag → TextureFilter`, `TextureWrap → TextureAddressMode`,
      `Topology → PrimitiveTopology`, `VertexFormat → VertexBufferLayout`.
- [x] **`GraphicsBuffer`** — shadow `Graphite.Buffer` created in `Set()`,
      data-synced in `Update()`, disposed alongside GL buffer.
- [x] **`GraphicsTexture`** — shadow `Graphite.Texture` created on first
      `TexImage2D`/`TexImage3D` (mip 0), disposed alongside GL texture.
      Stores `ImageFormat` for deferred Graphite mapping.
- [x] **`GraphicsProgram`** — shadow `Graphite.ShaderModule` for each stage
      (vertex/fragment/geometry) compiled from same GLSL source strings.
- [x] **`GraphicsFrameBuffer`** — stores references to Graphite textures
      from the attachments' `GraphicsTexture.GraphiteTexture`.
- [x] **`GraphicsVertexArray`** — stores `Graphite.VertexLayoutDescriptor`
      mapped from the legacy `VertexFormat`(s).

---

## Phase 3 — Command-Based Rendering

Replace immediate-mode draw calls with `CommandList`-based rendering.

- [x] Create `RenderCommandBuffer` wrapper (high-level over `CommandList`).
  - Wraps `Graphite.CommandList` with engine-friendly API.
  - `BeginRenderPass(RenderTexture)` builds `RenderPassDescriptor` from shadow textures.
  - `BeginDepthOnlyRenderPass(RenderTexture)` for shadow mapping.
  - `SetMaterialPipeline(program, vertexLayout, state, topology)` via `PipelineStateCache`.
  - `SetMeshBuffers(Mesh)` / `DrawMeshIndexed(Mesh)` for draw calls.
  - `Submit()` ends recording and submits to `GraphiteDevice`.
- [x] Create `PipelineStateCache` — FNV-1a-hashed static cache of `PipelineState` objects
      keyed by (program, rasterizerState, topology, renderPassLayout).
- [x] Extend `GraphiteFormatMapper` with state mappings:
      `RasterizerState → RasterizerStateDescriptor`, `DepthStencilStateDescriptor`,
      `BlendStateDescriptor`; `GraphicsFrameBuffer → RenderPassLayout`.
- [x] Add `VertexBuffer` / `IndexBuffer` properties to `GraphicsVertexArray`
      so `RenderCommandBuffer.SetMeshBuffers()` can access Graphite shadow buffers.
- [x] Add `Graphics.CreateCommandBuffer()` factory method.
- [x] Migrate `DefaultRenderPipeline` (GBuffer, Lighting, Forward, PostProcess → render passes).
  - `Graphics.ActiveGraphiteCmdBuffer` static property enables parallel Graphite recording.
  - `Internal_Render` creates per-frame `RenderCommandBuffer`, begins/ends render passes
    at each pipeline stage (shadow atlas, GBuffer, lighting, compose, forward transparent).
  - `DrawMeshNow` and `DrawRenderables` record parallel Graphite draw commands
    (pipeline state + mesh buffers per batch, indexed draw per object).
  - `DrawInstancedRenderablePass` records parallel instanced Graphite draw commands.
  - `Graphics.Viewport` mirrors viewport changes to active Graphite command buffer.
  - Commands are disposed without submitting during bridge phase (no BindGroup/uniform support yet).
- [x] Migrate `ShadowAtlas` to depth-only render passes.
  - `RenderShadowAtlas` begins a depth-only Graphite render pass on the shadow atlas,
    per-cascade viewports mirrored via `Graphics.Viewport`, ended after all lights.
- [ ] Migrate `PaperRenderer` / UI rendering (deferred to Phase 4 — requires BindGroup
      support for per-drawcall uniforms: projection, texture, scissor, brush params).

---

## Phase 4 — Remove Legacy Graphics Layer

- [x] Delete `Graphics\GraphicsDevice.cs`, `OpenGLGraphicsDevice.cs`, `VulkanGraphicsDevice.cs`.
  - Merged `OpenGLGraphicsDevice`'s immediate-mode API into `GLGraphiteDevice`.
  - `GLGraphiteDevice` now owns the GL context directly (`GLContext` property), acquires it
    in `Initialize()` via `GL.GetApi(Window.InternalWindow)`.
  - All legacy state tracking (depth, blend, cull, winding), framebuffer tracking,
    uniform/attribute caches, and draw methods live in `GLGraphiteDevice`.
- [ ] Delete `Graphics\GraphicsBuffer.cs`, `GraphicsTexture.cs`, `GraphicsFrameBuffer.cs`,
      `GraphicsProgram.cs`, `GraphicsVertexArray.cs`.
  - These wrapper types still exist (they use `Graphics.GL` which now routes through
    `GLGraphiteDevice.GLContext`). Full removal requires migrating all consumers to
    Graphite resources directly (Phase 5+).
- [x] Delete `RenderingBackend.cs` (replaced by `GraphicsBackendType`).
  - All references across runtime, editor, and build pipeline updated to use
    `Prowl.Runtime.Graphite.GraphicsBackendType`.
- [x] Update `Graphics` static class to expose only `GraphiteDevice`.
  - Removed `_device` field and `Device` property.
  - `Graphics.GL` now routes through `GLGraphiteDevice.GLContext`.
  - All pass-through methods (Viewport, Clear, SetState, bind/draw) route through
    `RequireGLDevice` — a helper that throws a clear `InvalidOperationException`
    (instead of cryptic NullReferenceException) when the Vulkan backend is active.
  - `Graphics.Initialize()` takes `GraphicsBackendType` instead of `RenderingBackend`.
- [ ] Remove all `Silk.NET.OpenGL` imports from non-backend code.
  - `Graphics.GL` is still used by `Graphics*` wrapper types, `ImGuiManager`,
    `StubEditorRendering`, etc. Full cleanup deferred until wrapper types are removed.

---

## Phase 5 — Complete Vulkan Backend

- [x] `VKGraphiteDevice` — swapchain creation, proper init.
  - `KhrSurface` + `KhrSwapchain` extensions loaded at instance/device level.
  - `VkSurfaceKHR` created from `Window.InternalWindow` via `IVkSurface`.
  - Present queue family discovered alongside graphics queue family.
  - `CreateSwapchain()` selects format (prefer BGRA8), present mode (prefer Mailbox),
    extent, image count; creates `VkSwapchainKHR`, retrieves images, creates image views.
  - `ResizeSwapchain()` recreates the swapchain (was previously a stub).
- [x] `VKBuffer` — buffer + memory allocation.
  - Already complete from prior phases (staging uploads, memory type selection).
- [x] `VKTexture` — image, views, format translation.
  - Already complete from prior phases (layout transitions, aspect flags).
- [x] `VKPipelineState` — `VkGraphicsPipelineCreateInfo`.
  - Already complete from prior phases (dynamic viewport/scissor/blend/stencil).
- [x] `VKCommandList` — `vkCmdBeginRenderPass`, `vkCmdDraw*`, etc.
  - Already complete; updated to use `VKSwapchainImageTexture` (real image views)
    instead of the old placeholder singleton.
- [x] `VKBindGroup` — descriptor sets + pools.
  - Already complete from prior phases.
- [x] `VKShaderModule` — SPIR-V loading.
  - Already complete from prior phases.
- [x] `VKSampler`, `VKFence`.
  - Already complete from prior phases.
- [x] Frame-in-flight tracking, image acquisition, presentation.
  - `MaxFramesInFlight = 2` constant, per-frame semaphores + fences.
  - `BeginFrame()` waits on in-flight fence, acquires next image via
    `vkAcquireNextImageKHR`, handles `VK_ERROR_OUT_OF_DATE_KHR`.
  - `Present()` via `vkQueuePresentKHR`, handles out-of-date / suboptimal.
  - `SubmitFrameCommands()` submits with wait/signal semaphores + fence.
  - `GraphiteDevice` base class gained `BeginFrame()` / `Present()` virtual methods
    (default no-op for GL backend).
  - `Window.OnRender()` calls `BeginFrame()` / `Present()` around the render loop.
  - `Window.OnFramebufferResize()` calls `ResizeSwapchain()`.
  - `VKSwapchainImageTexture` replaces the old singleton `VKSwapchainTexture`,
    wrapping real `VkImage` + `VkImageView` per swapchain image.
  - `VKFormatHelper.FromVkFormat()` added for reverse Vulkan → Graphite format mapping.

---

## Phase 6 — Shader Cross-Compilation

- [x] Integrate upstream `ShaderCompiler` / `ShaderParser`.
  - `ShaderParser` (in Runtime) already handles the custom `.shader` DSL, extracting
    per-pass GLSL (Shared/Vertex/Fragment sections, includes, properties).
  - The old Editor-side `ShaderCompiler`/`VariantCompiler`/`ShaderCrossCompiler` files
    do not exist in this branch; shader compilation was already handled at the
    `ShaderPass.TryGetVariantProgram()` → `GraphicsProgram` level.
- [x] Per-backend `ShaderData` in `ShaderPass`.
  - `ShaderPass.TryGetVariantProgram()` now selects `#version 450` for Vulkan
    (enables explicit `binding` qualifiers required by SPIR-V) and keeps
    `#version 410` for the OpenGL path.
  - `GraphicsProgram` is now fully backend-aware:
    - OpenGL path: legacy GL shader compile + link as before, plus shadow GLSL
      Graphite modules.
    - Vulkan path (`_isGraphiteOnly = true`): skips all GL calls; cross-compiles
      GLSL → SPIR-V and creates Graphite SPIR-V shader modules directly.
    - `Use()` and `Dispose()` guard against the GL-less Vulkan path.
- [x] GLSL → SPIR-V compilation for Vulkan path.
  - New `ShaderCrossCompiler` static class in `Prowl.Runtime.Graphite`:
    - Wraps `Silk.NET.Shaderc` (same family as existing Silk.NET 2.22.0 deps).
    - `CompileGLSLToSPIRV(string glsl, ShaderStage stage)` — targets Vulkan 1.0 /
      SPIR-V 1.0, returns `byte[]` of SPIR-V bytecode.
    - `TryCompileGLSLToSPIRV(...)` — non-throwing variant with error message out param.
    - Thread-safe singleton shaderc compiler; `Shutdown()` called from `Graphics.Dispose()`.
  - Added `Silk.NET.Shaderc` 2.22.0 + `Silk.NET.Shaderc.Native` 2.22.0 NuGet packages.
  - New `ShaderModuleDescriptor` factory methods: `VertexSPIRV()`, `FragmentSPIRV()`,
    `GeometrySPIRV()`, `ComputeSPIRV()` (mirror the existing GLSL variants).

### Slang Evaluation

A brief evaluation of **Slang** (NVIDIA's shader language) as a potential replacement
for authoring shaders in Prowl:

| Aspect | Assessment |
|--------|------------|
| **Multi-target output** | Excellent — Slang compiles to SPIR-V, GLSL, HLSL, Metal, and WGSL from a single source. This would eliminate the cross-compilation step entirely. |
| **Language features** | Superior to GLSL — generics, interfaces, modules, namespaces, automatic differentiation. Enables more reusable shader libraries. |
| **.NET integration** | `SlangNet` NuGet package exists; Slang also ships a C API suitable for P/Invoke. However, it's a large native dependency (~30+ MB). |
| **Ecosystem maturity** | Actively developed by NVIDIA; used in production by Falcor, RTXDI, and other NVIDIA projects. Growing community adoption. |
| **Migration cost** | High — all 20+ `.shader` files and 6+ `.glsl` includes would need rewriting. The custom Prowl shader DSL (`Shader`, `Pass`, `GLSLPROGRAM`) would also need adaptation. |
| **AOT / platform** | Native library; supports Win/Linux/Mac but would need validation for all Prowl target platforms. AOT should be fine (P/Invoke). |
| **Risk** | Medium — Slang's API is still evolving; committing to it now couples the engine to NVIDIA's roadmap. |

**Recommendation:** Defer Slang adoption to a future phase. The current approach
(GLSL authoring + shaderc cross-compilation to SPIR-V) is pragmatic, proven, and
requires zero shader rewrites. Slang could be revisited once the multi-backend pipeline
is fully stable and if the shader library grows complex enough to benefit from Slang's
module/interface system. A gradual migration path would be:
1. Current: GLSL + shaderc cross-compilation (Phase 6, done).
2. Future: Optional Slang compiler backend alongside GLSL.
3. Eventually: Full Slang adoption if the benefits outweigh migration cost.

---

## Migration Priority (by file)

| Priority | Files | Reason |
|----------|-------|--------|
| **P0** | `Graphics.cs`, `Game.cs`, `Window.cs` | Device init & frame lifecycle |
| **P1** | `Mesh.cs`, `RenderTexture.cs`, `Texture2D.cs` | Core resources |
| **P2** | `ShaderPass.cs`, `Material.cs`, `PropertyState.cs` | Shader & uniform binding |
| **P3** | `DefaultRenderPipeline.cs`, `ShadowAtlas.cs` | Main render pipeline |
| **P4** | `UIDrawListRenderer.cs`, `PaperRenderer` | UI rendering |
| **P5** | All image effects | Post-processing |
| **P6** | `VK*.cs` (Graphite/Vulkan) | Vulkan backend completion |

---

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Shader compatibility (GLSL → SPIR-V) | ✅ Resolved in Phase 6 — shaderc cross-compilation from GLSL to SPIR-V |
| Performance regression during migration | Keep old path behind feature flag until validated |
| Breaking samples / editor | Migrate `SimpleCube` first as proof-of-concept |
| Vulkan complexity (memory, sync) | Use existing VK scaffolding; consider VMA |
| `Graphics.GL` scattered everywhere | ✅ Phase 0 audit complete — 77 sites in 6 files, all in `Graphics\` wrappers or `StubEditorRendering`. `RequireGLDevice` guard provides clear errors on non-GL backends. |

---

## Current Status Summary

### Completed Phases
| Phase | Status | Notes |
|-------|--------|-------|
| **0 — Audit** | ✅ Complete | All 4 audit items resolved. 77 `Graphics.GL.` sites cataloged, zero `Graphics.Device.*` remaining. |
| **1 — Device Init** | ✅ Complete | `Graphics.Graphite` property, test injection, unified initialization. |
| **2 — Shadow Resources** | ✅ Complete | All 5 legacy wrapper types have Graphite shadow resources. |
| **3 — Command Rendering** | ✅ Complete | `RenderCommandBuffer`, `PipelineStateCache`, `DefaultRenderPipeline`, `ShadowAtlas` migrated. |
| **4 — Remove Legacy** | 🟡 Partial | Legacy device deleted; 5 wrapper types + `Silk.NET.OpenGL` imports remain (deferred). |
| **5 — Vulkan Backend** | ✅ Complete | Full swapchain, frame-in-flight, all VK resource types implemented. |
| **6 — Shader Cross-Compilation** | ✅ Complete | GLSL→SPIR-V via shaderc, backend-aware `GraphicsProgram`, per-backend GLSL versions. |

### Remaining Work (future phases)
1. **Delete legacy wrapper types** (Phase 4 deferred) — `GraphicsBuffer`, `GraphicsTexture`,
   `GraphicsFrameBuffer`, `GraphicsProgram`, `GraphicsVertexArray`. Requires migrating
   all consumers to Graphite resources directly.
2. ~~**Migrate PaperRenderer / UI rendering**~~ ✅ Complete — PaperRenderer now uses Graphite
   `Buffer`, `BindGroup`, `PipelineState`, and `RenderCommandBuffer`. UI uniforms are packed
   into a std140-compatible UBO ring buffer with dynamic offsets. Per-drawcall texture bind
   groups are cached. Callers set `PaperRenderer.RenderTarget` before `Paper.EndFrame()`.
   `BindGroupLayoutEntry.Name` field added for GL UBO/sampler linkage;
   `GLPipelineState.SetupBindGroupLinkage()` links blocks/samplers at pipeline creation.
3. **Remove `Silk.NET.OpenGL` imports** from non-backend code (Phase 4 deferred) — blocked
   on wrapper type removal.
4. **End-to-end Vulkan rendering** — all Graphite/VK resources and shader cross-compilation
   are in place, but the render pipeline still uses legacy GL pass-throughs. The bridge
   phase needs to transition from "record & discard" to "record & submit" for the
   Graphite command path.
5. **Image effects** (P5) — Post-processing effects need Graphite render pass migration.
6. **PropertyState → BindGroup bridge** — General material uniform binding through Graphite
   BindGroups. Required before legacy wrapper types can be fully removed.
7. **Slang evaluation** — Deferred; current GLSL + shaderc approach is sufficient.
