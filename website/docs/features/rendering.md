---
id: rendering
title: Rendering
sidebar_position: 1
description: Prowl's modular rendering system with multiple backends, PBR materials, and a deferred rendering pipeline.
keywords: [prowl, rendering, pbr, deferred, vulkan, opengl, graphite]
---

# Rendering

Prowl features a modular rendering system with multiple backend support and a full PBR deferred rendering pipeline.

## Rendering Backends

Prowl supports multiple graphics backends through the **Graphite** abstraction layer:

| Backend | Status | Platforms |
|---------|--------|-----------|
| **OpenGL** | ✅ Stable | Windows, Linux, macOS |
| **OpenGL ES** | ✅ Stable | Cross-platform |
| **Vulkan** | 🛠️ In Progress | Windows, Linux |
| **Metal** | 🛠️ In Progress | macOS |
| **DirectX 11** | 🛠️ In Progress | Windows |

:::tip

The Graphite layer provides a **backend-agnostic GPU interface**, allowing the rendering pipeline to work identically across all backends. Your game code never touches backend-specific APIs.

:::

## PBR (Physically Based Rendering)

Prowl uses a physically based rendering model with the following material maps:

| Map | Purpose |
|-----|---------|
| **Albedo** | Base color of the surface |
| **Normal** | Surface detail via normal perturbation |
| **Roughness** | Microsurface roughness (smooth ↔ rough) |
| **Metallic** | Metalness factor (dielectric ↔ metallic) |
| **Ambient Occlusion** | Precomputed ambient light occlusion |

## Deferred Rendering Pipeline

The default render pipeline uses a **deferred rendering** approach for opaque objects with a **forward pass** for transparent objects.

<details>
<summary><strong>📋 GBuffer Layout</strong></summary>

| Buffer | Contents | Purpose |
|--------|----------|---------|
| **A** | RGB = Albedo, A = Alpha | Base color |
| **B** | RGB = Normal (view-space), A = Shading Mode | Surface direction |
| **C** | R = Roughness, G = Metalness, B = Specular, A = AO | Material properties |
| **D** | Custom data per shading mode (e.g., Emissive) | Extra data |
| **Depth** | Depth values | Distance from camera |

</details>

### Rendering Phases

```
Scene Objects ──→ Culling ──→ Shadow Atlas
                                   │
                    ┌─── GBuffer Pass (Deferred) ───┐
                    │  Albedo │ Normals │ PBR │ Depth │
                    └──────────────┬─────────────────┘
                                   │
                    ┌──── Lighting Pass ─────┐
                    │  Accumulate per-light   │
                    └──────────┬─────────────┘
                               │
                    ┌── Composition Pass ──┐
                    │ Albedo × Lighting    │
                    │ + Fog + Ambient      │
                    └──────────┬───────────┘
                               │
                    ┌── Transparent Pass ──┐
                    │  Forward-rendered    │
                    └──────────┬───────────┘
                               │
                    ┌── Post-Processing ───┐
                    │ Bloom, Tonemap, etc. │
                    └──────────┬───────────┘
                               │
                    ┌── Blit to Screen ────┐
                    │  Swapchain Present   │
                    └──────────────────────┘
```

| Phase | Description |
|-------|-------------|
| **Culling** | Frustum and layer culling to skip invisible objects |
| **Shadow Atlas** | Depth-only shadow rendering into a shared atlas |
| **GBuffer Pass** | Store surface properties per-pixel |
| **Lighting Pass** | Accumulate light contributions with additive blending |
| **Composition** | Combine albedo × lighting + fog + ambient + emissive |
| **Transparent Pass** | Forward-rendered, back-to-front sorted |
| **Post-Processing** | Final image effects |

## Lighting

| Light Type | Shadows | Notes |
|------------|---------|-------|
| **Directional Light** | ✅ Yes | — |
| **Spot Light** | ✅ Yes | — |
| **Point Light** | ❌ Not yet | Planned |

:::info Features

- Shadow Atlas with dynamic resolution
- Additive light accumulation
- Per-light shadow mapping

:::

## Post-Processing

| Effect | Notes |
|--------|-------|
| **Tonemapping** | Melon, ACES, Reinhard, Uncharted, Filmic |
| **Motion Blur** | Camera and per-object |
| **Bloom** | Very fast Kawase Bloom |
| **Transparency** | Supported |
| **Procedural Skybox** | High-performance |
| **Dynamic Resolution** | Per camera |

## Shader System

Shaders are written in **GLSL** and automatically cross-compiled to **SPIR-V** for Vulkan:

```
GLSL Source → ShaderCrossCompiler → SPIR-V Binary → GPU
```

:::note Shader Features

- Multiple shader passes
- Keyword-based shader variants
- Automatic uniform buffer packing via SPIR-V reflection
- Material property resolution: **per-object → material → global**

:::

---

:::tip Deep Dive

For a comprehensive walkthrough of the Vulkan rendering pipeline, see the [Vulkan Rendering Pipeline](../architecture/vulkan-rendering-pipeline) architecture doc.

:::
