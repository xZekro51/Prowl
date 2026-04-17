# StressTest CPU Profile Analysis

**Date:** Auto-generated from CPU profiler session  
**Target:** `Samples/StressTest` — 10,000 GameObjects, ~5,000 lifecycle components  
**Profile type:** CPU Usage (Instrumentation)

---

## Executive Summary

The two dominant CPU consumers are **`GameObject.PreUpdate()`** (18.24% total CPU) and **`BRDFLutGenerator.IntegrateBRDF()`** (15.51% total CPU). Together they account for roughly a third of all CPU time. The hot path splits between the **Update loop** (51.86%) and the **Render loop** (26.23%).

---

## Top CPU Hotspots

| Rank | Function | Total CPU | Self CPU | Category |
|------|----------|-----------|----------|----------|
| 1 | `GameObject.PreUpdate()` | 18.24% | 14.91% | **Lifecycle dispatch** |
| 2 | `BRDFLutGenerator.IntegrateBRDF()` | 15.51% | 4.04% | **BRDF LUT generation** |
| 3 | `Scene.Flush()` | 14.78% | 2.74% | **Scene management** |
| 4 | `BRDFLutGenerator.ImportanceSampleGGX()` | 4.53% | 1.88% | BRDF LUT generation |
| 5 | `BRDFLutGenerator.Hammersley()` | 3.24% | 3.17% | BRDF LUT generation |
| 6 | `Float3.Normalize()` | 3.17% | 3.17% | Math (called by BRDF) |
| 7 | `DefaultRenderPipeline.Internal_Render()` | 3.56% | 0.04% | Rendering |
| 8 | `List<(int, MouseButton, bool)>.MoveNext()` | 2.92% | 2.92% | Collection iteration |

---

## Detailed Analysis

### 1. `GameObject.PreUpdate()` — 18.24% total, 14.91% self

**What it does:** Called once per frame per active GameObject. With 10,000 GameObjects this method is invoked 10,000× per frame. The high *self* CPU (14.91%) indicates the cost is inside PreUpdate itself, not just in methods it calls.

**Why it's expensive:**
- The sheer volume of calls (10,000/frame) makes even lightweight per-object bookkeeping add up.
- Likely involves iterating each GameObject's component list, checking dirty flags, and preparing state for the upcoming Update/LateUpdate/FixedUpdate dispatch.
- Virtual dispatch overhead across thousands of objects with varying component counts contributes to instruction-cache and branch-prediction pressure.

**Optimization potential:** High. This is the single largest CPU consumer and is pure engine-side logic. Batching, caching active-component lists, or skipping no-op objects could yield significant gains.

---

### 2. `BRDFLutGenerator.IntegrateBRDF()` — 15.51% total, 4.04% self

**What it does:** Generates the BRDF look-up texture used by the PBR lighting pipeline. This is a CPU-side numerical integration (importance sampling of the GGX BRDF split-sum approximation).

**Why it's expensive:**
- The integration loop calls `ImportanceSampleGGX()` (4.53%) and `Hammersley()` (3.24%) thousands of times per texel.
- `Float3.Normalize()` accounts for 3.17% of total CPU, called predominantly from `ImportanceSampleGGX()`.
- This appears to run **every launch** rather than being cached to disk, meaning the full computation repeats on each startup.

**Optimization potential:** Very high.
- **Cache the LUT to disk** — generate once, save as a binary/image asset, and reload on subsequent runs. This would eliminate ~15% of total CPU on startup entirely.
- **Move to GPU** — compute the BRDF LUT in a compute shader or fragment shader (standard industry practice). This is a one-time cost but still wastes seconds of CPU time unnecessarily.
- **Reduce sample count** — if caching isn't feasible, the sample count per texel could be reduced with minimal visual impact.

---

### 3. `Scene.Flush()` — 14.78% total, 2.74% self

**What it does:** Processes pending scene operations — adding/removing GameObjects, enabling/disabling components, dispatching lifecycle callbacks.

**Why it's expensive:**
- With 10,000 objects spawned during `Initialize()`, the initial flush processes a massive batch of additions.
- The 2.92% spent in `List<(int, MouseButton, bool)>.MoveNext()` (called from Flush) suggests iteration over a collection that may be unnecessarily large or poorly structured for this access pattern.

**Optimization potential:** Moderate. Bulk-add optimizations or deferred batching could help the initialization spike. The per-frame cost depends on how many objects change state each frame.

---

### 4. `DefaultRenderPipeline.Internal_Render()` — 3.56% total

**What it does:** Orchestrates the deferred rendering passes (GBuffer, lighting, transparency, post-processing).

**Why it's expensive:** With only 1 visible mesh (the reference cube), the 3.56% is mostly fixed overhead from the multi-pass pipeline structure, shadow atlas setup, and post-processing effects (FXAA + tonemapper).

**Optimization potential:** Low for this stress test (intentionally minimal rendering). In real scenes with more geometry this would scale.

---

## Hot Path Summary

```
Main → Game.Run → Window.Start → Silk.NET.Run
  ├─ Window.OnUpdate  (51.86%)  ← GameObject.PreUpdate + Scene.Flush dominate
  └─ Window.OnRender  (26.23%)  ← BRDFLutGenerator + render pipeline
```

The Update path consumes **2× more CPU** than rendering, which is expected for a lifecycle-dispatch stress test with 10,000 objects but only 1 visible mesh.

---

## Recommendations (Priority Order)

| Priority | Area | Action | Expected Impact |
|----------|------|--------|-----------------|
| 🔴 High | BRDF LUT generation | Cache to disk or move to GPU | Eliminate ~15% CPU at startup |
| 🔴 High | `GameObject.PreUpdate()` | Profile internals; batch or skip inactive objects | Reduce ~18% per-frame CPU |
| 🟡 Medium | `Scene.Flush()` | Optimize bulk-add path; review collection types | Reduce initialization spike |
| 🟢 Low | `Float3.Normalize()` | Use SIMD or inline aggressively (library code) | Minor — mostly eliminated by fixing BRDF |
| 🟢 Low | Render pipeline | N/A for this test | Only relevant with more geometry |

---

## Notes

- The 14.98% "Self" CPU on the root process node suggests significant time in JIT compilation and runtime startup, which is normal for .NET cold-start.
- The `List<(int, MouseButton, bool)>.MoveNext()` appearing at 2.92% in multiple call sites suggests a hot iterator pattern that could benefit from `Span<T>` or array-based iteration.
