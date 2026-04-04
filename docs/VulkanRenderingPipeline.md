# Prowl Engine — Vulkan Rendering Pipeline

A beginner-friendly explanation of how the Prowl game engine draws things on screen using its Vulkan rendering backend.

---

## Table of Contents

1. [What Is a Rendering Pipeline?](#1-what-is-a-rendering-pipeline)
2. [High-Level Architecture](#2-high-level-architecture)
3. [The Graphite Abstraction Layer](#3-the-graphite-abstraction-layer)
4. [Vulkan Device Initialization](#4-vulkan-device-initialization)
5. [Core GPU Resources](#5-core-gpu-resources)
6. [Shaders and Cross-Compilation](#6-shaders-and-cross-compilation)
7. [Command Lists — Talking to the GPU](#7-command-lists--talking-to-the-gpu)
8. [The Frame Lifecycle](#8-the-frame-lifecycle)
9. [The Default Render Pipeline — Step by Step](#9-the-default-render-pipeline--step-by-step)
10. [The Material Binding System](#10-the-material-binding-system)
11. [Pipeline State Caching](#11-pipeline-state-caching)
12. [Synchronization — Keeping CPU and GPU in Harmony](#12-synchronization--keeping-cpu-and-gpu-in-harmony)
13. [Swapchain and Presenting to Screen](#13-swapchain-and-presenting-to-screen)
14. [Glossary](#14-glossary)

---

## 1. What Is a Rendering Pipeline?

Imagine you're painting a scene. You'd probably follow a process:

1. **Sketch the outlines** of every object.
2. **Fill in the base colors**.
3. **Add lighting and shadows**.
4. **Apply final touches** — blur, glow, color correction.
5. **Frame it and hang it on the wall** (display it on screen).

A **rendering pipeline** is exactly this, but performed by your GPU (graphics card) millions of times per second. It's a series of ordered steps that transform 3D objects (meshes, materials, lights) into the 2D image you see on your monitor.

**Vulkan** is one of the "languages" you can use to communicate with the GPU. It's very explicit and low-level — you tell the GPU *exactly* what to do, step by step. This gives you maximum control and performance, but requires more setup than simpler APIs like OpenGL.

---

## 2. High-Level Architecture

Prowl's rendering stack is organized in layers, from highest level (closest to game code) to lowest level (closest to the GPU hardware):

```
┌─────────────────────────────────────────────┐
│          Game Code / Components              │  ← Your scripts, Camera, Lights, MeshRenderers
├─────────────────────────────────────────────┤
│      DefaultRenderPipeline                   │  ← Orchestrates the full rendering process
│      (Deferred + Forward hybrid)             │
├─────────────────────────────────────────────┤
│      RenderCommandBuffer                     │  ← High-level command recorder
├─────────────────────────────────────────────┤
│      Graphite Abstraction Layer              │  ← Backend-agnostic GPU interface
│  ┌───────────────┬──────────────────────┐   │
│  │  GL Backend   │  Vulkan Backend      │   │
│  │  (OpenGL)     │  (VK* classes)       │   │
│  └───────────────┴──────────────────────┘   │
├─────────────────────────────────────────────┤
│          GPU Hardware                        │  ← Your graphics card
└─────────────────────────────────────────────┘
```

**Key insight:** Prowl supports both OpenGL and Vulkan through a shared abstraction called **Graphite**. The rendering pipeline code doesn't talk to Vulkan directly — it talks to Graphite, and Graphite translates those calls into whichever backend is active. This document focuses on what happens inside the Vulkan backend.

---

## 3. The Graphite Abstraction Layer

**Graphite** is Prowl's cross-backend graphics API. Think of it as a universal remote that can control both an OpenGL TV and a Vulkan TV.

### The Device (`GraphiteDevice`)

The central hub is `GraphiteDevice` — an abstract class with methods like:

| Method | What It Does |
|--------|-------------|
| `CreateBuffer(...)` | Allocates memory on the GPU for vertex data, uniforms, etc. |
| `CreateTexture(...)` | Creates an image the GPU can read/write (textures, render targets). |
| `CreateShaderModule(...)` | Loads a compiled shader program onto the GPU. |
| `CreatePipelineState(...)` | Bundles together shaders + rendering settings into a pipeline. |
| `CreateCommandList()` | Creates a recorder for GPU commands. |
| `SubmitCommands(...)` | Sends recorded commands to the GPU for execution. |
| `BeginFrame()` / `EndFrame()` | Manages per-frame synchronization and swapchain image acquisition. |

The Vulkan implementation of this is `VKGraphiteDevice`, which translates every Graphite call into the corresponding Vulkan API calls using the **Silk.NET** library (a .NET wrapper around native Vulkan).

---

## 4. Vulkan Device Initialization

Before any rendering can happen, the Vulkan backend must be set up. This is a one-time process that happens at engine startup inside `VKGraphiteDevice.Initialize()`:

### Step-by-Step Initialization

```
1. Load Vulkan API    → Get access to the Vulkan library on the system
        ↓
2. Create Instance    → Register the application with the Vulkan driver
        ↓                (optionally enable validation layers for debugging)
        ↓
3. Create Surface     → Connect Vulkan to the application window
        ↓                (so it knows WHERE to display images)
        ↓
4. Pick Physical      → Choose which GPU to use
   Device               (prefers discrete/dedicated GPUs over integrated ones)
        ↓
5. Create Logical     → Establish a communication channel with the chosen GPU
   Device               and request Queue Families (graphics + presentation)
        ↓
6. Create Command     → Allocate a pool from which command buffers can be created
   Pool
        ↓
7. Create Swapchain   → Set up a chain of images that cycle between
                         "being drawn to" and "being displayed on screen"
        ↓
8. Create Sync        → Create semaphores and fences for CPU-GPU synchronization
   Objects
```

### What Is a Swapchain?

Imagine you have two canvases (this is called **double buffering**). While canvas A is being shown to the viewer, you're painting on canvas B. When you're done painting, you swap them — now B is on display and you start painting on A again.

Prowl uses **2 frames in flight** (`MaxFramesInFlight = 2`), meaning the GPU can be working on the next frame while the previous one is being displayed.

The swapchain prefers the **BGRA8 Unorm** format (a common display format) and **Mailbox** present mode (low-latency — always shows the newest completed frame).

---

## 5. Core GPU Resources

The Vulkan backend creates several types of GPU resources. Here's what each one is:

### Buffers (`VKBuffer`)

A buffer is a block of GPU memory that holds raw data. Different uses include:

| Buffer Type | What It Stores |
|-------------|---------------|
| **Vertex Buffer** | Position, color, UV coordinates of mesh vertices |
| **Index Buffer** | Which vertices form which triangles |
| **Uniform Buffer** | Shader constants (camera matrices, time, light data) |
| **Storage Buffer** | Large read/write data for compute shaders |

When creating a buffer, you specify **memory access**:
- **GPU-Only**: Fastest for the GPU, but the CPU can't write directly. Data must be uploaded via a temporary "staging buffer".
- **CPU-to-GPU**: The CPU can write directly (good for data that changes every frame, like uniforms).
- **GPU-to-CPU**: For reading data back from the GPU (e.g., screenshots).

### Textures (`VKTexture`)

A texture is a 2D (or 3D, or cube) image stored on the GPU. Textures can be:
- **Sampled**: Read by shaders to apply color/detail to surfaces.
- **Render Targets**: Written to by the GPU as the destination of a rendering pass.
- **Depth/Stencil**: Store depth information for 3D sorting.

An important Vulkan concept is **image layouts**. A texture can be in different states:
- `Undefined` — Just created, no valid data
- `ShaderReadOnlyOptimal` — Ready to be read by shaders
- `ColorAttachmentOptimal` — Ready to be drawn to as a color target
- `DepthStencilAttachmentOptimal` — Ready to be used as a depth buffer
- `TransferDstOptimal` — Ready to receive data from a copy operation
- `PresentSrcKhr` — Ready to be presented to the screen

Transitioning between these states requires **pipeline barriers** — explicit commands that tell the GPU "wait until X is done before starting Y". The `VKTexture.TransitionLayout()` method handles this.

### Samplers (`VKSampler`)

A sampler tells the GPU *how* to read a texture:
- **Filtering**: Nearest (pixelated) vs. Linear (smooth)
- **Wrapping**: Repeat, Clamp to Edge, Mirror
- **Anisotropy**: Improves quality when viewing textures at steep angles

### Fences (`VKFence`)

A fence is a synchronization primitive. Think of it as a flag that the GPU raises when it finishes a batch of work. The CPU can wait on this flag to know when it's safe to reuse resources.

---

## 6. Shaders and Cross-Compilation

### What Are Shaders?

Shaders are small programs that run on the GPU. The two most important types are:

- **Vertex Shader**: Runs once per vertex. Transforms 3D positions into screen positions.
- **Fragment Shader**: Runs once per pixel. Determines the color of each pixel.

### GLSL → SPIR-V Cross-Compilation

Prowl's shaders are written in **GLSL** (the OpenGL shading language), but Vulkan requires **SPIR-V** (a compiled binary format). The `ShaderCrossCompiler` class handles this translation automatically at runtime:

```
GLSL Source Code  →  ShaderCrossCompiler  →  SPIR-V Binary  →  VKShaderModule
  (human-readable)     (uses shaderc)        (GPU-ready)       (Vulkan object)
```

The compiler enables "relaxed Vulkan rules" so that OpenGL-style GLSL (which doesn't require explicit binding qualifiers) can be compiled for Vulkan.

### SPIR-V Reflection

After compilation, `SpirvReflection` parses the SPIR-V binary to discover:
- What uniform buffers the shader expects (and their member layouts)
- What textures/samplers the shader needs
- Which binding slots each resource should use

This information is used to automatically create the correct **bind groups** (Vulkan descriptor sets) at render time.

---

## 7. Command Lists — Talking to the GPU

In Vulkan, you don't call the GPU directly. Instead, you **record** a list of commands into a **command buffer**, then **submit** the whole list at once. This is like writing a shopping list and giving it to someone, rather than asking them to fetch items one by one.

### The Recording Flow

```
CommandList.Begin()          ← Start recording
    │
    ├── BeginRenderPass()    ← "I'm going to draw to these textures"
    │   ├── SetPipeline()    ← "Use this shader + these render settings"
    │   ├── SetBindGroup()   ← "Here are the uniforms and textures"
    │   ├── SetVertexBuffer()← "Here's the mesh data"
    │   ├── SetIndexBuffer() ← "Here's how vertices connect"
    │   ├── SetViewport()    ← "Draw in this rectangle"
    │   ├── DrawIndexed()    ← "GO! Draw the triangles!"
    │   └── ... more draws
    ├── EndRenderPass()      ← "Done drawing to those textures"
    │
    ├── ResourceBarrier()    ← "Wait for textures to finish writing before reading"
    │
    ├── BeginRenderPass()    ← Start another pass (e.g., lighting)
    │   └── ...
    ├── EndRenderPass()
    │
CommandList.End()            ← Stop recording
    │
Device.SubmitCommands()      ← Send the whole list to the GPU
```

### Render Passes

A **render pass** defines which textures the GPU will draw to (color attachments, depth attachment) and what to do with existing contents:
- **Clear**: Wipe the texture to a solid color before drawing
- **Load**: Keep whatever was there (useful for additive passes)
- **Don't Care**: Contents don't matter (optimization hint)

The Vulkan backend creates render passes on-the-fly and caches them by their configuration (format, load/store ops, sample count) to avoid recreating identical passes.

### Viewport Y-Flip

Vulkan and OpenGL have opposite Y-axis conventions. Prowl handles this by flipping the viewport height to negative in `VKCommandList.SetViewportCore()`, which makes 3D rendering consistent. For 2D fullscreen passes (like post-processing), `SetViewportRaw()` is used instead (no flip).

---

## 8. The Frame Lifecycle

Every frame follows this pattern:

```
┌──────────────────────────────────────────────────────┐
│ BeginFrame()                                          │
│  ├── Wait for the GPU to finish frame N-2 (fence)    │
│  ├── Acquire next swapchain image                     │
│  └── Clean up retired resources from 2 frames ago     │
├──────────────────────────────────────────────────────┤
│ Render (per camera)                                   │
│  ├── Create RenderCommandBuffer                       │
│  ├── Record all draw commands                         │
│  ├── Submit command buffer to GPU                     │
│  └── (multiple cameras = multiple submissions)        │
├──────────────────────────────────────────────────────┤
│ EndFrame()                                            │
│  ├── Present the swapchain image to the screen        │
│  └── Advance frame index (0 → 1 → 0 → 1 → ...)      │
└──────────────────────────────────────────────────────┘
```

### Resource Retirement

When a draw call creates temporary GPU resources (uniform buffers, bind groups), those resources can't be deleted immediately — the GPU might still be using them. Prowl uses a **deferred disposal** system:

1. Temporary resources are "retired" into the current frame's slot.
2. Two frames later, when the fence guarantees the GPU is done, those resources are destroyed.

This happens in both `VKGraphiteDevice` (for framebuffers and command buffers) and `GraphiteMaterialBinder` (for per-draw uniform buffers and bind groups).

---

## 9. The Default Render Pipeline — Step by Step

The `DefaultRenderPipeline` orchestrates the entire rendering process. It uses a **deferred rendering** approach with a **forward pass** for transparent objects. Here's what happens each frame for each camera:

### Phase 0: Setup

- Validate default resources (quad mesh, materials, skybox dome).
- Create a Graphite command buffer.
- Take a snapshot of camera data (position, matrices, frustum).
- Upload global uniforms (camera matrices, time, screen size) to a GPU buffer.

### Phase 1: Pre-Cull & Pre-Render

- Notify all image effects that rendering is about to begin.
- They can prepare resources or modify camera settings.

### Phase 2: Culling

**Culling** means deciding which objects are NOT visible and skipping them.

- **Frustum Culling**: Each object's bounding box is tested against the camera's view frustum (the pyramid-shaped region the camera can see). Objects outside are skipped.
- **Layer Culling**: Objects on layers the camera doesn't render are skipped.

### Phase 3: Shadow Atlas Rendering

Before the main scene, shadow-casting lights render their depth maps into a shared **shadow atlas** — a single large texture divided into tiles, one per shadow-casting light. This uses a depth-only render pass.

### Phase 4: GBuffer Pass (Deferred Rendering)

This is where **deferred rendering** happens. Instead of calculating lighting for every pixel of every object immediately, we first store all the surface information into multiple textures called the **GBuffer**:

| GBuffer | Contents | Why |
|---------|----------|-----|
| **Buffer A** | RGB = Albedo (base color), A = Alpha | The raw color of the surface |
| **Buffer B** | RGB = Normal (view-space), A = Shading Mode | Which direction the surface faces |
| **Buffer C** | R = Roughness, G = Metalness, B = Specular, A = AO | Physical material properties |
| **Buffer D** | Custom data per shading mode (e.g., Emissive) | Extra data for special materials |
| **Depth** | Depth values | How far each pixel is from the camera |

**Why deferred?** In a scene with 100 lights and 1000 objects, forward rendering would need to evaluate every light for every object's pixels. Deferred rendering evaluates each light just once for every screen pixel, regardless of how many objects contributed to that pixel.

All opaque objects are drawn in this pass. The Vulkan command buffer records:
1. `BeginRenderPass` with 4 color attachments + 1 depth attachment (with `LoadOp.Clear`)
2. For each visible object: set pipeline, bind materials, draw mesh
3. `EndRenderPass`

### Phase 5: Lighting Pass

Now we have a GBuffer full of surface data. Time to calculate lighting:

1. A **light accumulation** render texture is created.
2. A render pass is begun with `LoadOp.Clear` (start from black).
3. For each light in the scene, its contribution is rendered as a fullscreen quad (for directional lights) or a volume mesh (for point/spot lights). Each light reads the GBuffer to know what surface it's illuminating.
4. Lights use **additive blending** — each light's contribution adds to the previous ones.

The GBuffer textures are set as **global shader properties** so all light shaders can access them:
```
_GBufferA          → Albedo
_GBufferB          → Normals
_GBufferC          → PBR properties
_GBufferD          → Custom data
_CameraDepthTexture → Depth
```

### Phase 6: Deferred Composition

The composition pass multiplies the accumulated light with the albedo and applies:
- **Fog** (linear, exponential, or exponential-squared)
- **Ambient lighting** (uniform or hemisphere)
- **Emissive** contribution from GBuffer D

This produces the final composed scene color (still without transparent objects).

### Phase 7: Depth Copy

The depth buffer from the GBuffer is copied to the composed output's depth buffer. On Vulkan, this is done via explicit `CopyTextureToTexture` commands with resource barriers to manage image layout transitions.

### Phase 8: Image Effects (After Lighting)

Image effects registered at the `AfterLighting` stage run here. Examples include:
- Screen-Space Reflections (SSR)
- Effects that need the final opaque scene color

### Phase 9: Transparent Pass (Forward Rendering)

Transparent objects can't use deferred rendering (you can't store multiple overlapping transparent surfaces in a single GBuffer pixel). So they're rendered using **forward rendering** — each object evaluates its lighting directly.

Objects are sorted **back-to-front** (farthest first) so alpha blending works correctly. The render pass uses `LoadOp.Load` to preserve the existing composed image.

### Phase 10: Post-Processing

Final image effects run here:
- **Tonemapping** (HDR → LDR conversion)
- **Bloom** (glow around bright areas)
- **Depth of Field** (blur based on distance)
- **FXAA** (anti-aliasing)
- **Color Grading**

### Phase 11: Gizmos

In the editor, debug visualization (wireframes, icons, transform handles) is drawn on top.

### Phase 12: Final Blit to Screen

The composed result is copied to the swapchain image for display:

- On Vulkan, `BlitToSwapchainGraphite()` uses a dedicated fullscreen quad draw with the blit shader to copy the result into the swapchain image.
- Resource barriers transition the swapchain image to `PresentSrcKhr` so it can be displayed.

### Visual Summary

```
Scene Objects ──→ Culling ──→ Shadow Atlas
                                   │
                                   ▼
                    ┌─── GBuffer Pass (Deferred) ───┐
                    │  Albedo │ Normals │ PBR │ Depth │
                    └──────────────┬─────────────────┘
                                   │
                                   ▼
                    ┌──── Lighting Pass ─────┐
                    │  Light A + Light B + … │
                    └──────────┬─────────────┘
                               │
                               ▼
                    ┌── Composition Pass ──┐
                    │ Albedo × Lighting    │
                    │ + Fog + Ambient      │
                    └──────────┬───────────┘
                               │
                    ┌── After-Lighting FX ──┐
                    │ SSR, SSAO, etc.       │
                    └──────────┬────────────┘
                               │
                               ▼
                    ┌── Transparent Pass ──┐
                    │  Forward-rendered    │
                    │  (back-to-front)     │
                    └──────────┬───────────┘
                               │
                               ▼
                    ┌── Post-Processing ───┐
                    │ Bloom, Tonemap, DOF  │
                    │ FXAA, Color Grading  │
                    └──────────┬───────────┘
                               │
                               ▼
                        ┌── Gizmos ──┐
                        └──────┬─────┘
                               │
                               ▼
                    ┌── Blit to Screen ────┐
                    │  Swapchain Present   │
                    └──────────────────────┘
```

---

## 10. The Material Binding System

### The Problem

Vulkan uses a **descriptor set** model: before drawing, you must explicitly declare every resource (buffers, textures) a shader needs, grouped into sets. This is very different from OpenGL, where you set uniforms one by one.

### The Solution: `GraphiteMaterialBinder`

This class bridges Prowl's OpenGL-style property system (`PropertyState`) with Vulkan's descriptor model:

1. **Read shader reflection** to discover what the shader expects.
2. **Pack uniform data** into a temporary GPU buffer:
   - Global uniforms (camera, time) → shared `GlobalUniforms` buffer
   - Material properties (colors, floats, matrices) → per-draw UBO
   - Per-object data (model matrix) → packed into the same UBO
3. **Resolve textures** from material properties or fall back to a 1×1 white texture.
4. **Create a Bind Group** (Vulkan descriptor set) tying everything together.
5. **Retire** the temporary resources for deferred cleanup.

### Property Resolution Order

When looking for a shader property value, the system checks in this order:
1. **Per-object instance properties** (e.g., a specific tint on one object)
2. **Material properties** (shared across all objects using this material)
3. **Global properties** (set by the pipeline — GBuffer textures, shadow maps)

---

## 11. Pipeline State Caching

A **Pipeline State Object (PSO)** in Vulkan bundles together:
- Vertex and fragment shaders
- Vertex layout (how vertex data is structured)
- Rasterizer settings (culling, depth test, blending)
- Render pass compatibility (what texture formats are being rendered to)

Creating a PSO is expensive, so `PipelineStateCache` stores them in a dictionary keyed by a hash of all their configuration. The same shader + settings combo will reuse the cached pipeline.

---

## 12. Synchronization — Keeping CPU and GPU in Harmony

The CPU and GPU work at different speeds and in parallel. Vulkan requires you to manage this explicitly:

### Semaphores (GPU ↔ GPU)

- **Image Available Semaphore**: Signaled when the swapchain image is ready to be drawn to.
- **Render Finished Semaphore**: Signaled when rendering is complete and the image can be presented.

### Fences (CPU ↔ GPU)

- **In-Flight Fences**: One per frame-in-flight. The CPU waits on these at the start of each frame to ensure the GPU finished the frame from 2 frames ago before reusing its resources.

### Pipeline Barriers (within a command buffer)

- **Image Memory Barriers**: Ensure one operation on a texture completes before another begins. For example, you must transition a texture from `ColorAttachmentOptimal` to `ShaderReadOnlyOptimal` before a shader can sample it.

### Flow Diagram

```
Frame N                         Frame N+1
  │                               │
  ├─ Wait fence[0]                ├─ Wait fence[1]
  ├─ Acquire swapchain image      ├─ Acquire swapchain image
  │   (waits on imageAvailable)   │   (waits on imageAvailable)
  ├─ Record & submit commands     ├─ Record & submit commands
  │   (signals renderFinished     │   (signals renderFinished
  │    + fence[0])                │    + fence[1])
  ├─ Present                      ├─ Present
  │   (waits on renderFinished)   │   (waits on renderFinished)
  ▼                               ▼
```

---

## 13. Swapchain and Presenting to Screen

### Swapchain Configuration

The swapchain is configured during initialization:

| Setting | Choice | Why |
|---------|--------|-----|
| **Format** | B8G8R8A8 Unorm | Standard display format, wide hardware support |
| **Present Mode** | Mailbox (preferred) or FIFO (fallback) | Mailbox = low latency; FIFO = vsync |
| **Image Count** | min + 1 (usually 3) | Triple buffering for smooth frames |
| **Image Usage** | Color Attachment + Transfer Destination | Can render to and blit into |

### The Present Flow

1. `BeginFrame()` acquires the next available swapchain image using `vkAcquireNextImageKHR`.
2. The engine renders the scene to offscreen render textures.
3. `BlitToSwapchainGraphite()` draws a fullscreen quad to copy the final image into the swapchain image.
4. `EndFrame()` calls `vkQueuePresentKHR` to display the image.

### Swapchain Recreation

When the window is resized, the swapchain becomes invalid. `ResizeSwapchain()` waits for the GPU to finish, then recreates the swapchain with the new dimensions.

---

## 14. Glossary

| Term | Definition |
|------|-----------|
| **Bind Group** | A set of resources (buffers, textures, samplers) bound together for shader access. Vulkan calls these "descriptor sets". |
| **Command Buffer/List** | A recorded sequence of GPU commands submitted as a batch. |
| **Deferred Rendering** | A technique that first stores surface data (GBuffer), then calculates lighting in a separate pass. Efficient for many lights. |
| **Descriptor Set** | Vulkan's mechanism for passing resources to shaders. |
| **Fence** | A CPU-GPU synchronization object. The CPU can wait until the GPU signals it. |
| **Forward Rendering** | A technique where each object evaluates all its lighting during its own draw call. Used for transparent objects. |
| **Framebuffer** | A collection of textures (color + depth) that a render pass draws into. |
| **Frustum** | The 3D pyramid-shaped volume visible to the camera. |
| **GBuffer** | A set of screen-sized textures storing surface properties (albedo, normals, roughness, etc.) for deferred rendering. |
| **GLSL** | OpenGL Shading Language — the text-based language shaders are written in. |
| **Image Layout** | The internal arrangement of a texture's memory, optimized for different operations (rendering, sampling, transfer). |
| **Pipeline Barrier** | A command that ensures one GPU operation finishes before another begins. |
| **Pipeline State Object (PSO)** | A compiled bundle of shader programs + fixed-function state (blending, depth test, rasterization). |
| **Render Pass** | A defined scope of rendering: which textures to draw to, how to clear them, and how to store results. |
| **Semaphore** | A GPU-GPU synchronization object used to order queue operations. |
| **SPIR-V** | Standard Portable Intermediate Representation — the compiled binary shader format Vulkan requires. |
| **Staging Buffer** | A temporary CPU-visible buffer used to upload data to GPU-only memory. |
| **Swapchain** | A series of images that rotate between being rendered to and displayed on screen. |
| **UBO (Uniform Buffer Object)** | A buffer containing constant data read by shaders (matrices, colors, parameters). |
| **Vertex** | A point in 3D space with associated data (position, normal, UV coordinates). |

---

## Source File Reference

| File | Role |
|------|------|
| `Prowl.Runtime/Graphite/GraphiteDevice.cs` | Abstract GPU device interface |
| `Prowl.Runtime/Graphite/Vulkan/VKGraphiteDevice.cs` | Vulkan device implementation (init, resources, frame mgmt) |
| `Prowl.Runtime/Graphite/Vulkan/VKCommandList.cs` | Vulkan command buffer recording |
| `Prowl.Runtime/Graphite/Vulkan/VKPipelineState.cs` | Vulkan graphics pipeline creation |
| `Prowl.Runtime/Graphite/Vulkan/VKShaderModule.cs` | SPIR-V shader module wrapper |
| `Prowl.Runtime/Graphite/Vulkan/VKTexture.cs` | Vulkan texture + image layout transitions |
| `Prowl.Runtime/Graphite/Vulkan/VKBuffer.cs` | Vulkan buffer (vertex, index, uniform) |
| `Prowl.Runtime/Graphite/Vulkan/VKBindGroup.cs` | Vulkan descriptor set/pool management |
| `Prowl.Runtime/Graphite/Vulkan/VKSampler.cs` | Vulkan texture sampler |
| `Prowl.Runtime/Graphite/Vulkan/VKFence.cs` | Vulkan fence synchronization |
| `Prowl.Runtime/Graphite/Vulkan/VKFormatHelper.cs` | Graphite↔Vulkan enum conversion |
| `Prowl.Runtime/Graphite/ShaderCrossCompiler.cs` | GLSL → SPIR-V compilation |
| `Prowl.Runtime/Graphite/SpirvReflection.cs` | SPIR-V binary reflection (binding discovery) |
| `Prowl.Runtime/Graphite/Commands/CommandList.cs` | Abstract command list base class |
| `Prowl.Runtime/Graphite/Enums/GraphiteEnums.cs` | Shared GPU enumerations |
| `Prowl.Runtime/Rendering/RenderPipeline.cs` | Base render pipeline (culling, sorting, uniforms) |
| `Prowl.Runtime/Rendering/DefaultRenderPipeline.cs` | Full deferred+forward pipeline implementation |
| `Prowl.Runtime/Rendering/DefaultRenderPipelineAsset.cs` | Pipeline configuration asset |
| `Prowl.Runtime/Rendering/RenderCommandBuffer.cs` | High-level Graphite command wrapper |
| `Prowl.Runtime/Rendering/GraphiteMaterialBinder.cs` | Property→BindGroup bridge |
| `Prowl.Runtime/Rendering/PipelineStateCache.cs` | PSO caching for performance |
| `Prowl.Runtime/Rendering/GlobalUniforms.cs` | Per-frame camera/time uniform buffer |
| `Prowl.Runtime/Rendering/RenderContext.cs` | Context passed to image effects |
| `Prowl.Runtime/Rendering/ShadowAtlas.cs` | Shadow map atlas allocation |
| `Prowl.Runtime/Rendering/RenderStage.cs` | Image effect injection points |
