---
id: rendering
title: Rendering
sidebar_position: 1
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

The Graphite layer provides a backend-agnostic GPU interface, allowing the rendering pipeline to work identically across all backends.

## PBR (Physically Based Rendering)

Prowl uses a physically based rendering model with the following material maps:

- **Albedo Map** — Base color of the surface
- **Normal Map** — Surface detail via normal perturbation
- **Roughness Map** — Microsurface roughness (smooth ↔ rough)
- **Metallic Map** — Metalness factor (dielectric ↔ metallic)
- **Ambient Occlusion Map** — Precomputed ambient light occlusion

## Deferred Rendering Pipeline

The default render pipeline uses a **deferred rendering** approach for opaque objects with a **forward pass** for transparent objects.

### GBuffer Layout

| Buffer | Contents |
|--------|----------|
| **A** | RGB = Albedo, A = Alpha |
| **B** | RGB = Normal (view-space), A = Shading Mode |
| **C** | R = Roughness, G = Metalness, B = Specular, A = AO |
| **D** | Custom data per shading mode (e.g., Emissive) |
| **Depth** | Depth values |

### Rendering Phases

1. **Culling** — Frustum and layer culling
2. **Shadow Atlas** — Depth-only shadow rendering
3. **GBuffer Pass** — Store surface properties
4. **Lighting Pass** — Accumulate light contributions
5. **Composition** — Combine albedo × lighting + fog + ambient
6. **Transparent Pass** — Forward-rendered, back-to-front sorted
7. **Post-Processing** — Final image effects

## Lighting

| Light Type | Shadows |
|------------|---------|
| **Directional Light** | ✅ Yes |
| **Spot Light** | ✅ Yes |
| **Point Light** | ❌ Not yet implemented |

Features:
- Shadow Atlas with dynamic resolution
- Additive light accumulation

## Post-Processing

- **Tonemapping** — Melon, ACES, Reinhard, Uncharted, Filmic
- **Motion Blur** — Camera and per-object
- **Bloom** — Very fast Kawase Bloom
- Transparency support
- Procedural high-performance skybox
- Dynamic resolution per camera

## Shader System

Shaders are written in **GLSL** and automatically cross-compiled to **SPIR-V** for Vulkan. Features include:

- Multiple shader passes
- Keyword-based shader variants
- Automatic uniform buffer packing via SPIR-V reflection
- Material property resolution (per-object → material → global)

For a deep dive into the Vulkan rendering pipeline, see the [Vulkan Rendering Pipeline](/docs/architecture/vulkan-rendering-pipeline) documentation.
