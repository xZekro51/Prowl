# Prowl Rendering System — Performance Improvement Roadmap

> **Scope**: `Prowl.Runtime/Rendering`, `Prowl.Runtime/Resources/Scene.cs`, and supporting types.
> **Baseline**: Analysis performed against the current `main` branch (`.NET 10`, OpenGL via Silk.NET 2.22).

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Priority 1 — Eliminate Per-Frame Allocations](#2-priority-1--eliminate-per-frame-allocations)
   - 2.1 [Scene.CollectRenderables: Array Copies on Every Frame](#21-scenecollectrenderables-array-copies-on-every-frame)
   - 2.2 [Scene.Render: LINQ Camera Collection](#22-scenerender-linq-camera-collection)
   - 2.3 [CullRenderables: HashSet Allocation](#23-cullrenderables-hashset-allocation)
   - 2.4 [SortRenderables: Double List Allocation](#24-sortrenderables-double-list-allocation)
   - 2.5 [DrawRenderables: Batch List & Dictionary Per Call](#25-drawrenderables-batch-list--dictionary-per-call)
   - 2.6 [PropertyState.ComputeHash: LINQ OrderBy in Hot Path](#26-propertystatecomputehash-linq-orderby-in-hot-path)
3. [Priority 2 — Light Uniform Upload Overhaul](#3-priority-2--light-uniform-upload-overhaul)
   - 3.1 [String Interpolation Allocations](#31-string-interpolation-allocations)
   - 3.2 [Individual Uniform Calls → Light UBO](#32-individual-uniform-calls--light-ubo)
4. [Priority 3 — Spatial Acceleration for Culling](#4-priority-3--spatial-acceleration-for-culling)
   - 4.1 [Current State: Flat Iteration](#41-current-state-flat-iteration)
   - 4.2 [Proposed: BVH or Grid-Based Broad Phase](#42-proposed-bvh-or-grid-based-broad-phase)
   - 4.3 [Dirty Tracking with Vortex Events](#43-dirty-tracking-with-vortex-events)
5. [Priority 4 — Shadow Atlas Caching](#5-priority-4--shadow-atlas-caching)
   - 5.1 [Current: Full Rebuild Every Frame](#51-current-full-rebuild-every-frame)
   - 5.2 [Proposed: Incremental Allocation with Dirty Tracking](#52-proposed-incremental-allocation-with-dirty-tracking)
6. [Priority 5 — Rendering Pipeline Events](#6-priority-5--rendering-pipeline-events)
   - 6.1 [Current: Hardcoded Pipeline Stages](#61-current-hardcoded-pipeline-stages)
   - 6.2 [Proposed: Source-Generated Pipeline Events](#62-proposed-source-generated-pipeline-events)
7. [Priority 6 — Occlusion Culling](#7-priority-6--occlusion-culling)
8. [Priority 7 — Uniform System Refinements](#8-priority-7--uniform-system-refinements)
   - 8.1 [String Key Lookups → Integer IDs](#81-string-key-lookups--integer-ids)
   - 8.2 [Per-Object UBO](#82-per-object-ubo)
9. [Appendix A — Allocation Map](#appendix-a--allocation-map)
10. [Appendix B — Benchmark Suggestions](#appendix-b--benchmark-suggestions)

---

## 1. Executive Summary

The Prowl rendering pipeline has a solid architectural foundation: material-based batching, GPU instancing, a uniform cache that skips redundant GL calls, a pooled `RenderTexture` system, and a single UBO for global camera/time data. These are non-trivial systems and they work.

However, the pipeline's primary bottleneck is **per-frame managed allocations** — not GPU work. Nearly every stage of the frame (collection, culling, sorting, batching, light upload) creates temporary objects that pressure the GC. In a 60 FPS loop, these allocations compound into measurable frame hitches during Gen 0/1 collections.

The recommendations below are ordered by **impact-to-effort ratio**. Priorities 1–3 are low-risk, high-reward changes that can be tackled incrementally. Priorities 4–7 are larger architectural investments.

| Priority | Area | Impact | Effort | Allocations Eliminated |
|----------|------|--------|--------|----------------------|
| **P1** | Per-frame allocations | 🔴 Critical | 🟢 Low–Medium | Hundreds per frame |
| **P2** | Light uniform upload | 🟠 High | 🟢 Low | ~160+ strings/frame |
| **P3** | Spatial culling | 🟠 High | 🟡 Medium | N/A (CPU time) |
| **P4** | Shadow atlas caching | 🟡 Medium | 🟡 Medium | Reduced GPU work |
| **P5** | Pipeline events | 🟡 Medium | 🟢 Low | N/A (extensibility) |
| **P6** | Occlusion culling | 🟡 Medium | 🔴 High | N/A (GPU/CPU time) |
| **P7** | Uniform system | 🟢 Low–Med | 🟡 Medium | String lookups/frame |

---

## 2. Priority 1 — Eliminate Per-Frame Allocations

This is the single most impactful category. Every allocation below happens **every frame, for every camera**. In a scene with 2 cameras (game + editor preview), the cost doubles.

### 2.1 Scene.CollectRenderables: Array Copies on Every Frame

**File**: `Scene.cs` — `CollectRenderables()` / `ForeachComponent()`

**Current behavior**:
```csharp
// Scene.cs — CollectRenderables copies the entire ActiveObjects list
foreach (var go in [.. ActiveObjects])  // ← spread into new list

// Scene.cs — ForeachComponent copies the component array per GO
foreach (MonoBehaviour comp in [.. go.GetComponents<MonoBehaviour>()])  // ← new array per GO
```

For a scene with 500 active GameObjects averaging 3 components each, this produces:
- **1 list allocation** (500 elements) for the ActiveObjects copy.
- **500 array allocations** (one per GameObject) for the component snapshots.
- **~1,500 MonoBehaviour references** copied into those arrays.

**Every single frame.**

**Recommended fix**:

Replace collection-expression copies with direct iteration using index-based loops and a version/stamp guard to detect structural modification:

```csharp
// Option A: Direct indexed iteration with modification guard
public void CollectRenderables(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
{
    int count = ActiveObjects.Count;
    for (int i = 0; i < count; i++)
    {
        var go = ActiveObjects[i];
        if (!go.Enabled) continue;

        var components = go.GetComponentsSpan<MonoBehaviour>(); // New: return ReadOnlySpan
        for (int j = 0; j < components.Length; j++)
        {
            var comp = components[j];
            if (comp.Enabled)
                comp.OnRenderCollect(camera, renderables, lights);
        }
    }
}
```

If `GetComponents` cannot safely return a span (because the internal list may mutate during iteration due to `OnRenderCollect` side-effects), the fallback is a **reusable thread-static list**:

```csharp
// Option B: Reusable scratch buffer
[ThreadStatic] private static List<MonoBehaviour>? t_scratchComponents;

private static List<MonoBehaviour> GetScratchComponents()
{
    t_scratchComponents ??= new List<MonoBehaviour>(32);
    t_scratchComponents.Clear();
    return t_scratchComponents;
}
```

**Impact**: Eliminates **501+ allocations per frame** in a 500-object scene.

---

### 2.2 Scene.Render: LINQ Camera Collection

**File**: `Scene.cs` — `Render()`

**Current behavior**:
```csharp
var cameras = ActiveObjects.SelectMany(x => x.GetComponentsInChildren<Camera>()).ToList();
cameras.Sort((a, b) => a.Depth.CompareTo(b.Depth));
```

This creates:
- LINQ iterator state machines (SelectMany, enumerator allocations).
- Intermediate `IEnumerable<Camera>` per GameObject.
- Final `List<Camera>` via `ToList()`.

**Recommended fix**:

Maintain a **static sorted camera list** that is rebuilt only when cameras are added/removed/re-parented, not every frame:

```csharp
// In Scene or a dedicated CameraRegistry
private readonly List<Camera> _sortedCameras = [];
private bool _cameraOrderDirty = true;

// Called from Camera.OnEnable / OnDisable / OnDepthChanged
internal void MarkCameraOrderDirty() => _cameraOrderDirty = true;

public ReadOnlySpan<Camera> GetSortedCameras()
{
    if (_cameraOrderDirty)
    {
        _sortedCameras.Clear();
        // Direct iteration — no LINQ
        for (int i = 0; i < ActiveObjects.Count; i++)
            CollectCamerasRecursive(ActiveObjects[i], _sortedCameras);
        _sortedCameras.Sort((a, b) => a.Depth.CompareTo(b.Depth));
        _cameraOrderDirty = false;
    }
    return CollectionsMarshal.AsSpan(_sortedCameras);
}
```

Camera registration/unregistration can be driven by the existing `OnEnable`/`OnDisable` lifecycle — or, for a cleaner decoupled approach, by a Vortex event (see [§6](#6-priority-5--rendering-pipeline-events) for how pipeline events naturally fit here).

**Impact**: Eliminates **LINQ allocations + list allocation every frame**. Camera list rebuild becomes O(1) amortized.

---

### 2.3 CullRenderables: HashSet Allocation

**File**: `RenderPipeline.cs` — `CullRenderables()`

**Current behavior**:
```csharp
public HashSet<int> CullRenderables(Camera camera, List<IRenderable> renderables)
{
    HashSet<int> culled = [];  // ← new HashSet every frame, every camera
    // ... frustum test, add culled indices ...
    return culled;
}
```

**Recommended fix**:

Use a **reusable `BitArray`** (or a simple `bool[]` / stackalloc for small counts) stored on the pipeline instance:

```csharp
private BitArray _cullMask = new(256);

public BitArray CullRenderables(Camera camera, List<IRenderable> renderables)
{
    int count = renderables.Count;
    if (_cullMask.Length < count)
        _cullMask.Length = count; // BitArray.Length setter resizes
    else
        _cullMask.SetAll(false); // Reset

    Frustum frustum = camera.GetFrustum();
    for (int i = 0; i < count; i++)
    {
        var (bounds, layer) = renderables[i].GetCullingData();
        bool outsideFrustum = !frustum.Intersects(bounds);
        bool layerMasked = (camera.CullingMask & (1 << layer)) == 0;
        _cullMask[i] = outsideFrustum || layerMasked;
    }
    return _cullMask;
}
```

Downstream consumers (`DrawRenderables`, `SortRenderables`) check `_cullMask[i]` instead of `culled.Contains(i)` — which is also faster (O(1) bit test vs. hash lookup).

**Impact**: Eliminates **1 HashSet allocation + N hash insertions per camera per frame**. Bit tests are cache-friendly.

---

### 2.4 SortRenderables: Double List Allocation

**File**: `RenderPipeline.cs` — `SortRenderables()`

**Current behavior**:
```csharp
public List<IRenderable> SortRenderables(Camera camera, List<IRenderable> renderables, ...)
{
    List<(IRenderable renderable, float distance)> sortableList = []; // alloc 1
    // ... populate with distance pairs ...
    sortableList.Sort(...);
    List<IRenderable> sorted = [];  // alloc 2
    foreach (var item in sortableList)
        sorted.Add(item.renderable);
    return sorted;
}
```

Two list allocations plus tuple boxing for every sort call.

**Recommended fix**:

Sort **in-place** using a reusable key buffer:

```csharp
private readonly List<float> _sortKeys = [];

public void SortRenderables(Camera camera, List<IRenderable> renderables, BitArray cullMask)
{
    _sortKeys.Clear();
    if (_sortKeys.Capacity < renderables.Count)
        _sortKeys.Capacity = renderables.Count;

    Float3 camPos = camera.Transform.Position;
    for (int i = 0; i < renderables.Count; i++)
    {
        if (cullMask[i]) { _sortKeys.Add(float.MaxValue); continue; }
        var (bounds, _) = renderables[i].GetCullingData();
        float dist = Float3.DistanceSquared(camPos, bounds.Center);
        _sortKeys.Add(dist);
    }

    // In-place co-sort: sort renderables list by corresponding key
    // Use Array.Sort with Span overloads (.NET 10)
    var keysSpan = CollectionsMarshal.AsSpan(_sortKeys);
    var renderablesSpan = CollectionsMarshal.AsSpan(renderables);
    MemoryExtensions.Sort(keysSpan, renderablesSpan); // keys ascending = front-to-back
}
```

For back-to-front (transparency), negate the keys or reverse after sort.

**Impact**: Eliminates **2 list allocations per sort call**. `MemoryExtensions.Sort` is allocation-free on .NET 10.

---

### 2.5 DrawRenderables: Batch List & Dictionary Per Call

**File**: `RenderPipeline.cs` — `DrawRenderables()`

**Current behavior**:
```csharp
public void DrawRenderables(List<IRenderable> renderables, ...)
{
    List<RenderBatch> batches = []; // ← new list
    // Phase 1: Build batches with dictionary lookup (implicit allocations)
    // Phase 2: Sort & draw
}
```

**Recommended fix**:

Promote the batch list and lookup dictionary to **pipeline-level reusable fields**:

```csharp
private readonly List<RenderBatch> _batchList = [];
private readonly Dictionary<(long matHash, int passIdx, int meshId), int> _batchLookup = [];

public void DrawRenderables(List<IRenderable> renderables, BitArray cullMask, ...)
{
    _batchList.Clear();
    _batchLookup.Clear();

    // Phase 1: Build batches (same logic, no allocations)
    for (int i = 0; i < renderables.Count; i++)
    {
        if (cullMask[i]) continue;
        // ... existing batch-building logic using _batchLookup / _batchList ...
    }

    // Phase 2: Sort & draw
    _batchList.Sort((a, b) => a.SortKey.CompareTo(b.SortKey));
    // ... draw ...
}
```

The tuple key `(long, int, int)` is a value type — no allocation for dictionary keys. The dictionary itself is cleared but retains its internal bucket array across frames.

**Impact**: Eliminates **1 list + 1 dictionary allocation per `DrawRenderables` call**. In a typical frame with opaque + transparent passes, that's 2 of each.

---

### 2.6 PropertyState.ComputeHash: LINQ OrderBy in Hot Path

**File**: `PropertyState.cs` — `ComputeHash()`

**Current behavior**:
```csharp
private static long HashDictionary<T>(Dictionary<string, T> dict, long hash)
{
    foreach (var kvp in dict.OrderBy(x => x.Key)) // ← LINQ allocations
    {
        hash = FnvCombine(hash, kvp.Key.GetHashCode());
        hash = FnvCombine(hash, kvp.Value.GetHashCode());
    }
    return hash;
}
```

`OrderBy` allocates an `OrderedEnumerable`, a `Buffer<T>`, and a sorted array — **for every dictionary, for every material, for every renderable, for every frame**.

**Why OrderBy exists**: Dictionary iteration order is not deterministic, so hashing requires a stable traversal order to produce consistent hashes.

**Recommended fix — Option A (sorted insertion)**:

Replace `Dictionary<string, T>` with `SortedList<string, T>` in `PropertyState`. `SortedList` iteration is inherently ordered and backed by contiguous arrays (cache-friendly). Lookups remain O(log n) but uniform property counts per material are typically <20, so the binary search is negligible.

**Recommended fix — Option B (hash without ordering)**:

Use an **order-independent hash combine** such as XOR-of-individual-hashes:

```csharp
private static long HashDictionaryUnordered<T>(Dictionary<string, T> dict, long hash)
{
    long combined = 0;
    foreach (var kvp in dict)
    {
        long entry = FnvCombine((long)kvp.Key.GetHashCode(), kvp.Value.GetHashCode());
        combined ^= entry; // XOR is commutative — order doesn't matter
    }
    return FnvCombine(hash, combined);
}
```

XOR has worse collision properties than ordered FNV, but for batching (where false equality triggers a full compare anyway), it is sufficient and **completely allocation-free**.

**Impact**: Eliminates **12× LINQ allocations per `ComputeHash` call** (one per dictionary in `PropertyState`). `ComputeHash` is called for every renderable in `DrawRenderables`.

---

## 3. Priority 2 — Light Uniform Upload Overhaul

### 3.1 String Interpolation Allocations

**File**: `ForwardLightManager.cs` — `SelectAndUploadLights()`

**Current behavior**:
```csharp
for (int i = 0; i < count; i++)
{
    PropertyState.SetGlobalInt($"_LightType[{i}]", (int)type);
    PropertyState.SetGlobalVector($"_LightPosition[{i}]", pos);
    PropertyState.SetGlobalVector($"_LightDirection[{i}]", dir);
    PropertyState.SetGlobalFloat($"_LightIntensity[{i}]", intensity);
    PropertyState.SetGlobalColor($"_LightColor[{i}]", color);
    PropertyState.SetGlobalFloat($"_LightRange[{i}]", range);
    // ... ~15 more per light ...
    // Plus shadow cascade matrices: 4 per directional, 6 per point
}
```

With 8 lights × ~20 properties = **~160 interpolated strings per frame**, each allocating a new `string` on the managed heap. Shadow cascade matrices can push this to 200+.

**Recommended fix — immediate (low effort)**:

Pre-compute and cache the uniform name strings in a static array:

```csharp
private static class LightUniformNames
{
    public const int MaxLights = 8;

    public static readonly string[] Type = new string[MaxLights];
    public static readonly string[] Position = new string[MaxLights];
    public static readonly string[] Direction = new string[MaxLights];
    public static readonly string[] Intensity = new string[MaxLights];
    public static readonly string[] Color = new string[MaxLights];
    public static readonly string[] Range = new string[MaxLights];
    // ... all other per-light uniform names ...

    static LightUniformNames()
    {
        for (int i = 0; i < MaxLights; i++)
        {
            Type[i] = $"_LightType[{i}]";
            Position[i] = $"_LightPosition[{i}]";
            Direction[i] = $"_LightDirection[{i}]";
            Intensity[i] = $"_LightIntensity[{i}]";
            Color[i] = $"_LightColor[{i}]";
            Range[i] = $"_LightRange[{i}]";
            // ...
        }
    }
}

// Usage:
PropertyState.SetGlobalInt(LightUniformNames.Type[i], (int)type);
PropertyState.SetGlobalVector(LightUniformNames.Position[i], pos);
```

**Impact**: Eliminates **160–200+ string allocations per frame**. Zero-effort, zero-risk change.

---

### 3.2 Individual Uniform Calls → Light UBO

**Current state**: Each light property is uploaded individually via `PropertyState.SetGlobal*()`, which resolves the uniform location by name (string → dictionary lookup → GL call).

`GlobalUniforms` already demonstrates the UBO pattern for camera/time data. Lights are the natural next candidate.

**Recommended fix**:

Define a `LightUniforms` struct matching an `std140` UBO layout and upload the entire light array in a single buffer update:

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct PackedLight
{
    public Float4 PositionAndType;     // xyz = position, w = type
    public Float4 DirectionAndRange;   // xyz = direction, w = range
    public Float4 ColorAndIntensity;   // xyz = color,     w = intensity
    public Float4 SpotParams;          // x = innerAngle,  y = outerAngle, z = shadowIndex, w = shadowBias
    // Shadow matrices stored separately or in a SSBO
}

public class LightUniforms
{
    private const int MaxLights = 8;
    private readonly PackedLight[] _lights = new PackedLight[MaxLights];
    private GraphicsBuffer? _ubo;
    private bool _dirty;
    private int _activeLightCount;

    public void SetLight(int index, in PackedLight light)
    {
        _lights[index] = light;
        _dirty = true;
    }

    public void Upload()
    {
        if (!_dirty) return;
        _dirty = false;
        // Single GL buffer upload for all 8 lights
        _ubo ??= new GraphicsBuffer(BufferTarget.UniformBuffer, MaxLights * Unsafe.SizeOf<PackedLight>(), true);
        _ubo.SetData(MemoryMarshal.AsBytes(_lights.AsSpan()));
        _ubo.BindRange(BufferTarget.UniformBuffer, bindingPoint: 1); // binding 0 = GlobalUniforms
    }
}
```

This replaces **~160 individual `glUniform*` calls with 1 `glBufferSubData`** call. The GPU reads a contiguous block, which is also more cache-friendly on the shader side.

Shader-side:
```glsl
layout(std140, binding = 1) uniform LightBlock
{
    PackedLight prowl_Lights[8];
    int prowl_LightCount;
};
```

**Impact**: ~160 GL calls → 1. Significant driver overhead reduction. Enables future expansion beyond 8 lights (transition to SSBO) without API changes.

---

## 4. Priority 3 — Spatial Acceleration for Culling

### 4.1 Current State: Flat Iteration

`CullRenderables` performs a linear scan of all renderables, testing each AABB against the camera frustum. For N renderables, this is O(N) frustum tests per camera per frame.

`CollectRenderables` also performs a linear scan of all GameObjects — even those that are nowhere near the camera.

With 5,000 objects and 2 cameras, that's 10,000 frustum tests and 10,000 component iterations per frame, regardless of how many are actually visible.

### 4.2 Proposed: BVH or Grid-Based Broad Phase

Introduce a **loose bounding volume hierarchy (BVH)** or **uniform grid** for the scene's renderable set:

**Option A — Loose BVH (recommended for general scenes)**:
- Binary tree of AABBs with "loose" expansion factors to reduce refit frequency.
- Refit on transform change (incremental, not full rebuild).
- Frustum traversal early-outs on subtree rejection — typical scenes cull 60–80% of the tree without testing individual objects.
- O(log N) average case for frustum queries.

**Option B — Uniform Grid (recommended for large outdoor scenes)**:
- Spatial hash grid with configurable cell size.
- Frustum query iterates only cells that intersect the frustum.
- Simpler implementation, better for uniformly distributed objects (terrain vegetation, debris).

Either approach should be maintained **per-scene** and exposed via the existing `Scene` class:

```csharp
public class Scene
{
    private readonly SceneBVH _bvh = new();

    // Called when a renderable's bounds change
    internal void NotifyBoundsChanged(MonoBehaviour component) => _bvh.MarkDirty(component);

    // Replaces flat iteration in CollectRenderables
    public void CollectVisibleRenderables(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
    {
        Frustum frustum = camera.GetFrustum();
        _bvh.QueryFrustum(frustum, camera.CullingMask, renderables, lights);
    }
}
```

### 4.3 Dirty Tracking with Vortex Events

A BVH needs to know when objects move. Currently, `Transform` mutations are silent — there's no notification system.

This is a natural fit for Vortex's source-generated events. Rather than polling every transform each frame, define a lightweight event that fires when a transform's world matrix actually changes:

```csharp
// New file: Prowl.Runtime/Events/SpatialEvents.cs

using Vortex;

namespace Prowl.Runtime;

[EventDomain]
public static partial class SpatialEvents
{
    /// <summary>
    /// Fired when a Transform's world-space position, rotation, or scale changes.
    /// Listeners: SceneBVH, physics broad phase, audio spatial index.
    /// </summary>
    [EventArgs(typeof(TransformChangedArgs))]
    public static readonly EventKey TransformChanged;

    /// <summary>
    /// Fired when a renderable's bounding volume changes without a transform change
    /// (e.g., mesh swap, animation bounds update).
    /// </summary>
    [EventArgs(typeof(BoundsChangedArgs))]
    public static readonly EventKey BoundsChanged;
}

public readonly record struct TransformChangedArgs(Transform Transform, Float3 OldPosition, Float3 NewPosition);
public readonly record struct BoundsChangedArgs(MonoBehaviour Component, AABB OldBounds, AABB NewBounds);
```

**Why Vortex here (and not a C# `event`)**:

1. **Zero-allocation dispatch**: Vortex generates static dispatch tables at compile time. A C# `event` creates a delegate allocation per subscriber and a multicast invocation list that's cloned on add/remove. For something that fires **thousands of times per frame** (transforms move constantly), this matters.

2. **Domain isolation**: `[EventDomain]` scopes the event so only relevant systems subscribe. The BVH subscribes to `SpatialEvents.TransformChanged`; it doesn't need to know about `GameEvents` or `WindowEvents`. This prevents accidental coupling.

3. **Discoverability**: Source-generated event keys are visible at compile time, making it trivial for future systems (audio spatial index, physics broad phase, LOD manager) to hook into transform changes without modifying `Transform` itself.

The `Transform` class fires the event in its property setters (position, rotation, scale) after recomputing the world matrix, but only if the value actually changed. This keeps the fast path (no change) cost-free.

**Impact**: Enables incremental BVH updates instead of per-frame full rebuilds. Amortizes culling cost from O(N) to O(log N). Eliminates the need for `CollectRenderables` to touch every GameObject.

---

## 5. Priority 4 — Shadow Atlas Caching

### 5.1 Current: Full Rebuild Every Frame

**File**: `ShadowAtlas.cs`, `Game.cs`

```csharp
// Game.cs — every frame:
ShadowAtlas.Clear();  // Resets to single free rectangle
// ... later, ForwardLightManager re-reserves tiles for shadow-casting lights ...
```

The atlas packing (guillotine bin-packing with BSSF heuristic) is not expensive per se, but:
- All shadow maps are re-rendered every frame regardless of whether the light or scene changed.
- The atlas layout may differ frame-to-frame due to non-deterministic light ordering, causing unnecessary GPU texture copies.
- The existing `TODO: Merge adjacent free rectangles` comment indicates known fragmentation issues.

### 5.2 Proposed: Incremental Allocation with Dirty Tracking

**Step 1 — Stable atlas assignments**: Keep shadow tile assignments across frames. Only re-pack when a light is added/removed or its resolution requirements change.

**Step 2 — Dirty shadow maps**: Only re-render a shadow map when:
- The light's transform changed.
- An object within the light's shadow frustum moved.
- The shadow cascade parameters changed.

This requires knowing "did anything move within this light's influence?" — which the spatial BVH from §4 naturally provides. Query the BVH with the light's shadow frustum; if no dirty transforms intersect it, skip the shadow render pass.

A Vortex event is again well-suited to propagate light dirty state:

```csharp
// In SpatialEvents or a dedicated ShadowEvents domain
[EventArgs(typeof(ShadowInvalidatedArgs))]
public static readonly EventKey ShadowInvalidated;

public readonly record struct ShadowInvalidatedArgs(Light Light, ShadowInvalidationReason Reason);

public enum ShadowInvalidationReason
{
    LightMoved,
    LightParametersChanged,
    SceneObjectMovedInFrustum,
    ForcedRefresh,
}
```

Subscribers (the shadow atlas) can then decide whether to re-render that specific light's shadow map or reuse the cached result.

**Step 3 — Implement free-rectangle merging**: Address the existing TODO to merge adjacent free rectangles in the guillotine allocator. This reduces fragmentation and allows better utilization of atlas space for variable-resolution shadows.

**Impact**: Static scenes (common in architectural viz, cutscenes, and many game scenarios) see **near-zero shadow rendering cost** after the first frame. Dynamic scenes only re-render shadow maps for lights affected by moving objects.

---

## 6. Priority 5 — Rendering Pipeline Events

### 6.1 Current: Hardcoded Pipeline Stages

`DefaultRenderPipeline.Internal_Render()` is a monolithic ~250-line method with 13+ sequential stages. Injecting custom behavior requires either:
- Subclassing `DefaultRenderPipeline` and overriding `Render()` (fragile, must replicate the entire flow).
- Using `ImageEffect` (limited to `AfterOpaques` or `PostProcess` stages).

There's no way for external systems to hook into, say, "after shadow atlas is built but before opaques" or "after culling but before sorting."

### 6.2 Proposed: Source-Generated Pipeline Events

Define a `RenderEvents` domain that fires at each pipeline stage:

```csharp
using Vortex;

namespace Prowl.Runtime.Rendering;

[EventDomain]
public static partial class RenderEvents
{
    [EventArgs(typeof(RenderStageArgs))] public static readonly EventKey PreCull;
    [EventArgs(typeof(RenderStageArgs))] public static readonly EventKey PostCull;
    [EventArgs(typeof(RenderStageArgs))] public static readonly EventKey PreShadows;
    [EventArgs(typeof(RenderStageArgs))] public static readonly EventKey PostShadows;
    [EventArgs(typeof(RenderStageArgs))] public static readonly EventKey PreOpaques;
    [EventArgs(typeof(RenderStageArgs))] public static readonly EventKey PostOpaques;
    [EventArgs(typeof(RenderStageArgs))] public static readonly EventKey PreTransparents;
    [EventArgs(typeof(RenderStageArgs))] public static readonly EventKey PostTransparents;
    [EventArgs(typeof(RenderStageArgs))] public static readonly EventKey PrePostProcess;
    [EventArgs(typeof(RenderStageArgs))] public static readonly EventKey PostPostProcess;
}

public readonly record struct RenderStageArgs(
    Camera Camera,
    List<IRenderable> Renderables,
    List<IRenderableLight> Lights,
    RenderContext Context
);
```

Then in `Internal_Render`:

```csharp
// Before culling
RenderEvents.PreCull.Raise(new RenderStageArgs(camera, renderables, lights, context));
var cullMask = CullRenderables(camera, renderables);
RenderEvents.PostCull.Raise(new RenderStageArgs(camera, renderables, lights, context));

// Before shadows
RenderEvents.PreShadows.Raise(new RenderStageArgs(camera, renderables, lights, context));
RenderShadowAtlas(camera, lights);
RenderEvents.PostShadows.Raise(new RenderStageArgs(camera, renderables, lights, context));
// ... etc.
```

**Benefits**:
- **Extensibility without subclassing**: Custom rendering features (decal systems, volumetric fog, GPU particle injection) subscribe to the appropriate event.
- **Profiling hooks**: A diagnostics subscriber can measure time between `Pre*` and `Post*` events without modifying the pipeline.
- **Editor integration**: The editor's overlay, gizmo, and selection systems can subscribe to `PostOpaques` or `PostTransparents` without the runtime pipeline knowing about the editor.
- **Plugin architecture**: Third-party rendering plugins can inject passes by subscribing to events, enabling a modular render pipeline.

This directly follows the `GameEvents` / `WindowEvents` pattern already established in the engine, so it's a consistent architectural choice.

**Impact**: Transforms the rendering pipeline from a closed monolith to an open event-driven architecture. Low implementation cost, high long-term value.

---

## 7. Priority 6 — Occlusion Culling

Frustum culling rejects objects outside the camera's view. But in scenes with significant depth complexity (indoor environments, dense cities), many objects pass frustum tests but are completely hidden behind closer geometry.

**Options (in order of increasing complexity)**:

### Option A — Software Rasterized Occlusion (Recommended first step)

Render a simplified depth buffer on the CPU (or compute shader) from the camera's perspective using coarse bounding geometry. Test candidate renderables against this buffer.

Libraries like Intel's Masked Software Occlusion Culling provide proven algorithms. A C# port targeting `Span<T>` and SIMD intrinsics (`System.Runtime.Intrinsics`) would fit the engine's pure-C# philosophy.

Integrate after frustum culling in the pipeline:

```
Frustum Cull → Occlusion Cull → Sort → Batch → Draw
```

### Option B — Hierarchical Z-Buffer (HZB)

Use the previous frame's depth buffer to build a mipmap hierarchy. Test object bounding boxes against the HZB using a compute shader. This is entirely GPU-side and avoids CPU readback latency.

Requires:
- A depth-buffer mipmap generation pass (series of downsample dispatches).
- A visibility test compute shader (one workgroup per N renderables).
- A CPU readback buffer (with 1-frame latency) or indirect draw commands.

**Recommendation**: Start with frustum culling improvements (§4) and spatial acceleration first. Occlusion culling provides diminishing returns without a good frustum culler, and its implementation complexity is significantly higher.

---

## 8. Priority 7 — Uniform System Refinements

### 8.1 String Key Lookups → Integer IDs

**Current**: `PropertyState` stores uniforms in `Dictionary<string, T>` and `UniformCache` resolves locations by string name. Every `ApplyGlobals` / `ApplyMaterialUniforms` / `ApplyInstanceUniforms` call iterates dictionaries with string key operations.

**Proposed**: Introduce a `ShaderPropertyID` (similar to Unity's `Shader.PropertyToID()`) that maps string names to stable integer IDs at initialization time:

```csharp
public readonly struct ShaderPropertyID : IEquatable<ShaderPropertyID>
{
    public readonly int ID;

    private static readonly Dictionary<string, int> s_nameToId = [];
    private static readonly List<string> s_idToName = [];
    private static int s_nextId;

    public static ShaderPropertyID Get(string name)
    {
        if (!s_nameToId.TryGetValue(name, out int id))
        {
            id = s_nextId++;
            s_nameToId[name] = id;
            s_idToName.Add(name);
        }
        return new ShaderPropertyID(id);
    }

    // For GL calls that still need the string:
    public string Name => s_idToName[ID];

    public bool Equals(ShaderPropertyID other) => ID == other.ID;
    public override int GetHashCode() => ID;
}
```

Then `PropertyState` uses `Dictionary<int, T>` (or flat arrays indexed by ID) instead of `Dictionary<string, T>`. Integer hashing and comparison are substantially faster than string operations.

**Impact**: Eliminates string hashing/comparison in the uniform upload hot path. Enables future optimization to flat arrays when the ID space is dense.

---

### 8.2 Per-Object UBO

Currently, per-object data (model matrix, object ID, custom properties) is uploaded via individual `glUniform*` calls. For scenes with many objects sharing the same material, this means the material's shader is bound once but per-object uniforms are set individually between draw calls.

A **per-object UBO** (or SSBO for larger data) would allow batching per-object data into a single buffer upload, with each draw call receiving an offset or base instance index:

```glsl
layout(std140, binding = 2) uniform ObjectBlock
{
    mat4 prowl_MatM;
    mat4 prowl_MatMVP;
    vec4 prowl_ObjectID;
    // ... custom per-object data ...
};
```

This pairs well with the existing batching system — a batch of objects sharing the same material could have their per-object data uploaded as contiguous blocks, with `glDrawElementsInstanced` + `gl_InstanceID` indexing into the buffer.

**This is a larger refactor** and should be tackled after the simpler wins in §2–§3 are realized.

---

## Appendix A — Allocation Map

A visual summary of per-frame allocations in the current rendering pipeline for a single camera pass:

```
Frame Start
│
├─ Scene.Render()
│  └─ SelectMany + ToList (cameras)                    → 1 List<Camera> + LINQ iterators
│
├─ Camera.Render()
│  └─ CollectRenderables()
│     ├─ [.. ActiveObjects]                            → 1 List<GameObject> copy
│     └─ per GO: [.. GetComponents<MonoBehaviour>()]   → N arrays (N = active GO count)
│
├─ CullRenderables()
│  └─ new HashSet<int>                                 → 1 HashSet + M inserts
│
├─ SortRenderables() (opaques)
│  ├─ new List<(IRenderable, float)>                   → 1 List
│  └─ new List<IRenderable>                            → 1 List
│
├─ DrawRenderables() (opaques)
│  ├─ new List<RenderBatch>                            → 1 List
│  ├─ batch lookup dictionary                          → 1 Dictionary
│  └─ per renderable: ComputeHash()
│     └─ 12× OrderBy LINQ                             → 12 LINQ allocs per renderable
│
├─ ForwardLightManager.SelectAndUploadLights()
│  └─ ~20 string interpolations × 8 lights            → ~160 strings
│
├─ SortRenderables() (transparents)
│  ├─ new List<(IRenderable, float)>                   → 1 List
│  └─ new List<IRenderable>                            → 1 List
│
├─ DrawRenderables() (transparents)
│  ├─ new List<RenderBatch>                            → 1 List
│  ├─ batch lookup dictionary                          → 1 Dictionary
│  └─ per renderable: ComputeHash()
│     └─ 12× OrderBy LINQ                             → 12 LINQ allocs per renderable
│
└─ End Frame

Total per camera per frame (500 objects, 100 visible, 8 lights):
  ~500 array copies (components)
  + 1 list copy (ActiveObjects)
  + 1 HashSet
  + 4 Lists (sort)
  + 2 Lists + 2 Dicts (batch)
  + ~2,400 LINQ allocations (ComputeHash: 200 renderables × 12)
  + ~160 strings (lights)
  ≈ 3,000+ managed allocations per camera per frame
```

For 2 cameras at 60 FPS: **~360,000 allocations per second** from the rendering pipeline alone.

---

## Appendix B — Benchmark Suggestions

Add targeted benchmarks in `BenchmarkSuite1` to measure the impact of each optimization:

| Benchmark | What It Measures |
|-----------|-----------------|
| `CollectRenderables_500Objects` | Scene traversal + component iteration allocation rate |
| `CullRenderables_Frustum_1000` | Frustum culling throughput (flat vs. BVH) |
| `SortRenderables_500_Opaque` | Sort throughput and allocation comparison |
| `DrawRenderables_BatchBuilding_200` | Batch construction time with/without allocation reuse |
| `ComputeHash_PropertyState_20Props` | Hash computation with OrderBy vs. XOR vs. SortedList |
| `ForwardLightManager_Upload_8Lights` | Light uniform upload: individual calls vs. UBO |
| `ShadowAtlas_Pack_16Lights` | Atlas packing time: full rebuild vs. incremental |
| `FrustumCull_BVH_vs_Flat_5000` | Culling time comparison at scale |

Use `[MemoryDiagnoser]` on all benchmarks to capture allocation counts — the primary metric for P1 and P2 changes.

---

## Summary of Recommended Execution Order

```
Phase 1 (1–2 weeks): P1 allocation elimination + P2 light string caching
   └─ Immediate, low-risk, measurable improvement.
   └─ Benchmark before/after with MemoryDiagnoser.

Phase 2 (1–2 weeks): P2 light UBO + P5 render pipeline events
   └─ Light UBO follows GlobalUniforms pattern (proven).
   └─ Pipeline events (Vortex) unlock future extensibility.

Phase 3 (2–4 weeks): P3 spatial BVH + Vortex transform dirty events
   └─ Largest architectural change but highest long-term payoff.
   └─ Transform events benefit physics, audio, and LOD too.

Phase 4 (2–4 weeks): P4 shadow caching + P7 uniform system
   └─ Build on BVH infrastructure for shadow dirty detection.
   └─ ShaderPropertyID is a foundational change for uniform system.

Phase 5 (4+ weeks): P6 occlusion culling
   └─ Only after spatial acceleration is in place.
   └─ Consider HZB as a stretch goal.
```

Each phase is independently shippable and benchmarkable. No phase requires the next to be valuable.
