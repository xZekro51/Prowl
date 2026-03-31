---
sidebar_position: 1
title: "Event System — Technical Review"
---

# Event System — Technical Review

> **Scope:** `Prowl.Runtime.EventSystem` namespace + `Prowl.EventSystem.Generators`
> **Commit range reviewed:** current `Standalone_Editor_Graphite_migration` branch HEAD

---

## 1. Scorecard

| Criterion | Score (1–10) | Notes |
|---|---|---|
| API design & ergonomics | 9 | Generated accessors give C#-event feel with zero boilerplate; lifecycle-aware subscriptions eliminate manual cleanup |
| Type safety | 9 | `[EventArgs]` + `EventArgsContract` catch mismatches at subscribe *and* invoke |
| Thread safety | 9 | `ConcurrentDictionary.GetOrAdd` for atomic event creation; COW snapshots for invocation; dedicated global snapshot |
| Hot-path performance | 9 | Sorted array snapshot, no alloc on invoke, `IsCancellable` JIT cache; global invoke uses filtered snapshot |
| Cold-path performance | 8 | `BeginBatch` / `EndBatch` defers snapshot rebuilds during bulk subscriptions; O(n²) → O(n log n) |
| Extensibility | 7 | Adding a new domain is one enum + one attribute; cross-domain composition not yet addressed |
| Debuggability | 9 | `#if DEBUG` caller-info on every subscription; per-handler slow timing with configurable threshold |
| **Overall** | **8.7 / 10** | All major review recommendations implemented; production-quality event bus with strong guarantees |

---

## 2. Architecture Overview

```
[EventDomain] enum          Roslyn Source Generator
        │                          │
        ▼                          ▼
  EventManager<T>            generated accessors
        │                    (subscribe / invoke)
        ▼
  ConcurrentDictionary<T, Event<T>>  ← GetOrAdd (atomic)
        │
        ▼
   Event<T>  ── COW array snapshot ──►  EventDelegateContainer<T>[]
                  (deferred via                  │
                   BeginBatch/EndBatch)  ┌───────┴────────────┐
                                        ▼                    ▼
                              Typed<T,TArgs>        Parameterless<T>
                                        ▼
                              Lifecycle<T,TArgs>  (auto-unsub on dispose)

  Static snapshots:
    s_instancesSnapshot  ── all managers
    s_globalSnapshot     ── global-only managers (used by GlobalInvoke)
```

### Key design decisions

| Decision | Rationale |
|---|---|
| One `EventManager<T>` per enum type | Keeps unrelated domains in separate dictionaries; avoids a single contention point |
| Copy-on-write delegate arrays | Lock-free invocation on the hot path; mutations are rare relative to invocations |
| `ConcurrentDictionary.GetOrAdd` for event registry | Atomic creation of `Event<T>` instances without external locking or duplicate creation |
| Source-generated accessors | Eliminates magic strings; provides `+=` / `-=` syntax with full type inference |
| `[EventArgs]` attribute + runtime contract | Double-checks at both subscribe and invoke time that the declared payload type matches |
| Lifecycle-aware containers | Auto-unsubscribe when owner `EngineObject` is disposed; eliminates leaked subscriptions |
| Batch subscribe API | Defers COW rebuilds during bulk subscription operations; amortizes O(n²) to O(n log n) |
| Dedicated global snapshot | `GlobalInvokeEvent` reads only global managers; avoids scanning non-global instances |
| Per-handler timing (DEBUG) | Surfaces slow handlers before they reach profiling; zero cost in Release |

---

## 3. Strengths

### 3.1 Zero-allocation invocation path

`Event<T>.Invoke<TArgs>` iterates a pre-sorted `EventDelegateContainer<T>[]` snapshot. No `IEnumerable`, no boxing, no delegate allocation. The only branch is the `IsCancellable` check, which is cached per `TArgs` via a static generic field (`CancellableCheck<TArgs>.Value`), so the JIT can treat it as a constant after the first call.

### 3.2 Compile-time event domain generation

The Roslyn incremental generator (`EventDomainGenerator`) emits strongly-typed `On` / `Invoke` accessors for every enum member annotated with `[EventDomain]`. Adding a new event is:

```csharp
[EventDomain]
public enum GameLoopEvents
{
    [EventArgs(typeof(float))]
    Update,

    PreRender,
    // ...
}
```

No registration code, no handler interfaces, no reflection at runtime.

### 3.3 Priority ordering with stable sort

Delegates are inserted into the snapshot in priority order. Equal-priority delegates maintain insertion order. This is important for gameplay systems that need deterministic callback sequencing (e.g., physics before animation before rendering).

### 3.4 Cancellable events via `ICancellable`

Any `TArgs` implementing `ICancellable` can short-circuit the invocation chain. The check is a single boolean test per iteration — no try/catch, no allocation. The `CancellableCheck<TArgs>` cache means the type test happens exactly once per concrete `TArgs` type.

### 3.5 Debug-mode caller tracking

In `DEBUG` builds, every `EventDelegateContainer` records `[CallerFilePath]`, `[CallerLineNumber]`, and `[CallerMemberName]`. This makes it trivial to answer "who subscribed to this event?" without attaching a debugger.

### 3.6 Thread-safe event registry

The migration from `Dictionary<T, Event<T>>` to `ConcurrentDictionary<T, Event<T>>` eliminates the race condition where concurrent first-time subscriptions to different event types could corrupt the internal hash table. Combined with the COW snapshot pattern on `Event<T>`, the system is now safe for multi-threaded subscription and invocation without external synchronization.

### 3.7 Lifecycle-aware subscriptions

The `AddNewDelegate(EngineObject owner, ...)` overloads bind a subscription to an `EngineObject`'s lifetime. When the owner is disposed, the handler automatically unsubscribes on the next invocation — eliminating the most common source of leaked subscriptions from destroyed GameObjects and components.

### 3.8 Batch subscribe for scene load

`BeginBatch()` / `EndBatch()` on `Event<T>` and `EventManager<T>` defer COW snapshot rebuilds during bulk subscription operations. This turns O(n²) array copies during scene load into a single O(n log n) sort, significantly reducing GC pressure.

### 3.9 Per-handler timing diagnostics

In DEBUG builds, each handler invocation is timed via `Stopwatch`. Handlers exceeding the configurable `Event<T>.SlowHandlerThresholdMs` threshold (default 5.0ms) are logged with source location, surfacing performance regressions before they reach profiling.

### 3.10 Global manager filtering

A dedicated `s_globalSnapshot` array containing only global managers avoids iterating non-global instances during `GlobalInvokeEvent`. The snapshot is rebuilt when managers are added/removed or when the `Global` flag changes.

---

## 4. Weaknesses & Risks

### 4.1 Reflection in `CreateArrayBuilder`

`MakeGenericMethod` + `Delegate.CreateDelegate` is used once per unique `TArgs` type and then cached — acceptable, but the first subscription of a new args type pays a reflection tax. In a hot-reload or domain-reload scenario (common in editors), this cache lives in a `static` and would need explicit clearing.

### 4.2 Strong references for non-`EngineObject` subscribers

Lifecycle-aware subscriptions handle automatic cleanup for engine-managed objects. For non-`EngineObject` subscribers, references remain strong. If a subscriber forgets to `Dispose()` or `-=`, the `EventDelegateContainer` (and its closure) will be rooted by the `EventManager` indefinitely. The `IDisposable` pattern mitigates this, but doesn't prevent it.

### 4.3 Global static managers never dispose

The generated `s_eventManager` field is `private static readonly`. It's created once and never disposed. In a long-running editor with multiple project loads, the global managers accumulate. The `EventManager` finalizer logs a warning if not disposed, but the generated ones by design are never disposed.

### 4.4 No async support

All handlers are synchronous `Action<TArgs>`. There's no `Func<TArgs, Task>` or `Func<TArgs, ValueTask>` path. For a game engine's main loop this is correct (you don't want `await` in the render path), but for editor/tool events (e.g. asset import, build pipeline) async handlers would be useful.

---

## 5. Comparison with Common Alternatives

| Feature | Prowl `EventManager<T>` | C# `event` keyword | MediatR | Unity `UnityEvent` |
|---|---|---|---|---|
| Type-safe payloads | ✅ `[EventArgs]` contract | ✅ delegate signature | ✅ `IRequest<T>` | ❌ runtime `object[]` |
| Priority ordering | ✅ per-delegate | ❌ | ❌ | ❌ |
| Cancellation | ✅ `ICancellable` | ❌ (need `ref bool`) | ❌ | ❌ |
| Thread safety | ✅ `ConcurrentDictionary.GetOrAdd` + COW | ❌ (manual `volatile`) | ✅ (DI scope) | ❌ |
| Source generation | ✅ domain accessors | N/A | ❌ | ❌ |
| Lifecycle-aware | ✅ auto-unsub on owner dispose | ❌ | ❌ | ❌ |
| Batch subscribe | ✅ `BeginBatch`/`EndBatch` | N/A | ❌ | ❌ |
| Slow handler detection | ✅ DEBUG timing | ❌ | ❌ | ❌ |
| Serialisable | ❌ | ❌ | ❌ | ✅ |
| Zero-alloc invoke | ✅ | ✅ | ❌ (heap per request) | ❌ |

---

## 6. Remaining Recommendations

| Priority | Recommendation |
|---|---|
| 🟡 Medium | Add an `async` invoke path (`InvokeAsync<TArgs>`) for editor/tool events where handlers legitimately need to perform I/O. Not a priority for the game loop hot path. |
| 🟢 Low | Consider a `[CallerArgumentExpression]` capture in Release builds (not just DEBUG) to improve production diagnostics without the full `CallerFilePath` cost. |

---

## 7. Summary

The Prowl event system is a well-engineered, performance-conscious pub/sub framework that leverages Roslyn source generation to provide a type-safe, zero-boilerplate API. Key design strengths include copy-on-write lock-free invocation, priority ordering, cancellation via `ICancellable`, thread-safe registry via `ConcurrentDictionary.GetOrAdd`, lifecycle-aware subscriptions that auto-unsubscribe on `EngineObject` disposal, batch subscribe for scene-load performance, per-handler DEBUG timing, and dedicated global-only snapshots for efficient cross-assembly broadcast.

Remaining areas for improvement are async handler support for editor/tool events and Release-build diagnostics.

**Rating: 8.7 / 10** — Production-ready for a game engine with strong thread safety, lifecycle management, and diagnostics.
