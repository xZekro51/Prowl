# Scene Update Iteration Benchmark Report

**Date:** June 2025
**Configuration:** 10,000 GameObjects × 3 Components = 30,000 MonoBehaviours
**Runtime:** .NET 10, BenchmarkDotNet, Release mode

---

## Approaches Compared

| # | Approach | Description |
|---|---|---|
| 1 | **Original** | `foreach` over `ActiveObjects` (LINQ Where) → copy to array via `[.. GetComponents()]` → iterate components |
| 2 | **Span** | `CollectionsMarshal.AsSpan(ActiveObjectsList)` → `GetComponentsAsSpan<T>()` → `for` loop with index |
| 3 | **Vortex Events** | Components subscribe to a Vortex `EventKey` on enable; Scene calls `InvokeUpdateEvent()` once per frame |

---

## Results

### Timing

| Method | Mean | Error | StdDev |
|---|---:|---:|---:|
| Original: foreach + GetComponents copy | 99,498 µs | 18,723 µs | 4,862 µs |
| Original: Update pattern (PreUpdate + Update + LateUpdate) | 184,221 µs | 8,771 µs | 2,278 µs |
| Span: ReadOnlySpan + GetComponentsAsSpan | 79,467 µs | 2,503 µs | 387 µs |
| Span: Update pattern | 152,649 µs | 2,474 µs | 643 µs |
| Vortex: single event Invoke | 108 µs | 4 µs | 1 µs |
| Vortex: Update pattern (3× Invoke) | 316 µs | 3 µs | 0.5 µs |

### Memory (per invocation)

| Method | Allocated |
|---|---:|
| Original: foreach + GetComponents copy | 1,440,128 B |
| Original: Update pattern | 2,800,128 B |
| Span: ReadOnlySpan + GetComponentsAsSpan | 1,142,456 B |
| Span: Update pattern | 2,547,368 B |
| Vortex: single event Invoke | **0 B** |
| Vortex: Update pattern (3× Invoke) | **0 B** |

### Bonus: ActiveObjects gathering

| Method | Mean | Allocated |
|---|---:|---:|
| ActiveObjects via LINQ Where | 116 µs | 78 KB |
| ActiveObjectsList via manual loop | 332 µs | 256 KB |

> Note: `ActiveObjectsList` is slower because it builds a full `List<GameObject>` (allocating a 10K-element backing array), while the LINQ version lazily enumerates.

---

## Summary Comparison

| Approach | Update Pattern (Mean) | Allocated | vs Original (Speed) | vs Original (Memory) |
|---|---:|---:|---|---|
| **Original** | 184,221 µs | 2,800 KB | baseline | baseline |
| **Span** | 152,649 µs | 2,547 KB | **~17% faster** | ~9% less |
| **Vortex Events** | 316 µs | 0 B | **583× faster** | **100% less** |

---

## Profiling Insights

### CPU Hotspots (from VS Profiler)

| Function | Total CPU | Self CPU |
|---|---:|---:|
| `Scene.get_ActiveObjectsList()` | 55.13% | 5.86% |
| `List<T>.AddWithResize(T)` | 48.65% | 48.65% |
| `RuntimeUtils.GetExecutionOrder()` | 1.75% | 0.05% |

The dominant bottleneck is **rebuilding the active objects list every call** — `ActiveObjectsList` allocates a new `List<GameObject>`, iterates all 10K objects, and copies matching ones. At scale this lands on the Large Object Heap, triggering expensive Gen2 GCs.

### Allocation Breakdown (from .NET Object Allocation Profiler)

| Object Type | Total Allocations | Total Size |
|---|---:|---:|
| `MonoBehaviour[]` (component copies) | 12,799,120 | **727 MB** |
| `List<>` (GetComponentsList + ActiveObjectsList) | 6,401,316 | **247 MB** |
| `GetComponents` enumerator state machines | 3,199,760 | **172 MB** |

### GC Issues Detected

- **Excessive LOH-triggered Gen2 GCs** — the 10K-element `GameObject[]` backing array (~80 KB) exceeds the 85 KB LOH threshold, forcing full Gen2 collections on every `ActiveObjectsList` call.
- **High LOH fragmentation** — repeated temporary LOH allocations waste memory between collections.

---

## Conclusions

1. **Span optimization (current work):** Provides a modest ~17% speedup and ~9% memory reduction. Worth doing but not transformative.

2. **Vortex event-driven updates:** Eliminates the iteration problem entirely — **583× faster, zero allocations**. Components subscribe on `OnEnable` and unsubscribe on `OnDisable`; the Scene simply fires one event per lifecycle phase.

3. **The real bottleneck is `ActiveObjectsList`:** Both the original and span approaches spend >55% of CPU just rebuilding the active objects list. Even if component iteration were free, you'd still pay ~332 µs per frame just to gather which objects are active.

4. **Recommended path forward:**
   - **Short term:** If staying with the iteration model, cache the active objects list and invalidate on add/remove/enable/disable instead of rebuilding every frame.
   - **Long term:** Move to a Vortex event-based dispatch model. Components that override `Update()` subscribe to the event; components that don't simply never subscribe (free skip of empty virtual calls). This also naturally solves execution ordering via Vortex's priority parameter.

---

## Trade-offs of Vortex Event Approach

| Pro | Con |
|---|---|
| Zero per-frame allocation | Requires subscribe/unsubscribe lifecycle management |
| O(subscribers) not O(all components) | Changes the MonoBehaviour API contract |
| Built-in priority ordering | Subscription cost on Enable/Disable |
| No empty virtual call overhead | Event args struct passed by value (trivial for empty args) |

---

## How to Reproduce

```bash
cd BenchmarkSuite3
dotnet run -c Release
```

Benchmark source: [`BenchmarkSuite3/SceneIterationBenchmarks.cs`](SceneIterationBenchmarks.cs)
