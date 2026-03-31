---
id: event-system-review
title: Event System Review
sidebar_position: 1
---

# Prowl Event System — Technical Review

**Reviewer perspective:** Senior engine/systems programmer, ~15 years shipping game engines and large-scale C# applications.

---

## 1. Architecture Overview

Prowl's event system is a **custom, centralized event bus** built around four main pillars:

| Component | Role |
|---|---|
| `EventManager<T>` | Per-domain event registry keyed by an `enum T`. Owns all `Event<T>` instances. Supports local and global (static) invocation. |
| `Event<T>` | Single event slot. Stores subscribers in priority-sorted buckets. Uses **copy-on-write (COW) snapshots** for lock-free iteration during `Invoke`. |
| `EventDelegateContainer<T, TArgs>` | Wraps a subscriber `Action<TArgs>`. Carries priority, enable/disable state, and optional DEBUG source-location metadata. Implements `IDisposable` for self-unsubscription. |
| `[EventDomain]` source generator | Eliminates boilerplate. From a `static partial class` with `EventKey` fields, generates a backing enum, a static `EventManager`, typed `Invoke*` / `Subscribe*` / `GlobalInvoke*` methods, and C#-style `event` accessors (`+=` / `-=`). |

Additional supporting types: `EventArgsContract<T>` (compile-time + runtime args-type validation), `ICancellable` (short-circuit propagation), `IEventManagerHolder<T>` (broadcast over collections), `Unit` (zero-size struct for parameterless events).

---

## 2. Scorecard

| Category | Score (1-10) | Notes |
|---|:---:|---|
| **Type safety** | 8 | `[EventArgs]` attribute + `EventArgsContract` validates `TArgs` at subscribe and invoke time. Mismatches throw (subscribe) or log+skip (invoke). Not fully compile-time — the mismatch is checked at runtime via reflection-cached dictionaries — but the source generator's typed `Subscribe*` / `Invoke*` methods make it nearly impossible to pass the wrong type in normal use. |
| **Performance — hot path** | 8 | `Invoke<TArgs>` reads a pre-built, strongly-typed `EventDelegateContainer<T,TArgs>[]` snapshot with **zero per-element type checks** and no locking. The `CancellableCheck<TArgs>` static-field pattern avoids boxing value-type args. `ParameterlessEventDelegateContainer<T>` avoids a closure allocation for `Action`→`Action<Unit>` wrapping. Solid. |
| **Performance — cold path** | 6 | `RebuildTypedSnapshots` uses `Dictionary<Type, object>`, `MakeGenericMethod`, and `Delegate.CreateDelegate` — all cached after first use, but the rebuild itself allocates several lists and arrays on every add/remove. Acceptable for a game engine where subscriptions change infrequently relative to invocations. |
| **Thread safety** | 7 | COW snapshots mean `Invoke` never mutates shared state and is lock-free on the read side. `Add`/`Remove` hold a per-event `_lock`. The global instances list uses a separate `s_instancesLock` with its own COW snapshot. One subtle gap: `EventManager.InvokeEvent` reads `_events` (a `Dictionary`) without locking — this is safe only because new keys are only added by `GetOrCreateEvent` under the event's own lock. It works, but a `ConcurrentDictionary` would be more defensible. |
| **Ergonomics / API surface** | 9 | The source generator is the star. Declaring an event domain is 5-6 lines of code. Consumers get `GameLoopEvents.SubscribeOnFrameBegin(handler, priority)` and `GameLoopEvents.OnFrameBegin += handler`. The `IDisposable` subscription pattern is clean and prevents leaks. |
| **Debuggability** | 8 | `#if DEBUG` captures `CallerFilePath`, `CallerLineNumber`, `CallerMemberName` on every subscription. Type-mismatch warnings print the registration site. Significantly better than chasing anonymous delegates through a standard `event` invocation list. |
| **Decoupling** | 9 | Publishers and subscribers share only a domain class and an args struct. No interface coupling, no reference from publisher to subscriber. Global invoke enables cross-assembly communication without dependency injection. |
| **Maintainability** | 7 | The generator is ~440 lines of well-structured incremental-generator code. The runtime types are cleanly separated. The main risk is `Event<T>.RebuildTypedSnapshots` which mixes reflection with generic caching. |
| **Test coverage** | 9 | Comprehensive xUnit suite covering basic invoke, priority ordering, enable/disable, add/remove, global invoke, disposal, thread safety, self-removal during invocation, cancellation, type-mismatch behavior, and source generator output. Production-grade coverage. |

**Overall: 8.0 / 10** — A well-engineered, performance-conscious event system that clearly reflects lessons learned from real engine development.

---

## 3. Strengths

### 3.1 Source-generated boilerplate elimination
The `[EventDomain]` generator is the single biggest win. Declaring five events in `GameLoopEvents` produces a fully typed API with zero hand-written plumbing.

### 3.2 Copy-on-write snapshot invocation
No locks on invoke — critical for a game loop calling events every frame. Safe self-removal during invocation.

### 3.3 Priority ordering
First-class priority support is essential for engine event ordering (e.g. physics before gameplay, gameplay before rendering).

### 3.4 Cancellation
`ICancellable` with the `CancellableCheck<TArgs>` JIT-cache pattern is elegant — zero overhead for non-cancellable events.

### 3.5 Scoped subscription lifetime
`IDisposable` container from `Subscribe*` enables `using` patterns and explicit lifecycle management.

### 3.6 Debug diagnostics
Automatic caller-info capture on subscriptions. When a handler throws or a type mismatch is detected, you get filename and line number of the registration site.

---

## 4. Weaknesses & Risks

### 4.1 Dictionary read without lock in `InvokeEvent`
`Dictionary<TKey, TValue>` is not documented as safe for concurrent read + write. A `ConcurrentDictionary` would close this gap.

### 4.2 Allocation on subscribe/unsubscribe
Every `Add`/`Remove` triggers `RebuildSnapshot` which allocates new arrays. Fine for typical game-engine patterns but problematic for high subscriber churn.

### 4.3 Reflection in `CreateArrayBuilder`
`MakeGenericMethod` is used once per unique `TArgs` type and then cached — the first subscription of a new args type pays a reflection tax.

### 4.4 No weak references
Subscriber references are strong. If a subscriber forgets to `Dispose()` or `-=`, the container will be rooted indefinitely.

### 4.5 Global static managers never dispose
The generated `s_eventManager` field is `private static readonly` — created once and never disposed.

### 4.6 No async support
All handlers are synchronous `Action<TArgs>`. For editor/tool events, async handlers would be useful.

---

## 5. Comparison: Prowl vs Standard C# `event` Delegates

| Feature | Prowl `EventManager` + `[EventDomain]` | C# `event` (delegate) |
|---|---|---|
| Declaration cost | ~5 lines per domain (generator does the rest) | 1-2 lines per event |
| Type safety | Runtime-validated via `[EventArgs]` contract | Compile-time by delegate signature |
| Priority ordering | ✅ First-class | ❌ Not supported |
| Cancellation | ✅ `ICancellable` | ❌ No mechanism |
| Enable/disable | ✅ Per-manager, per-event, per-handler | ❌ Not supported |
| Scoped unsubscription | ✅ `IDisposable` / `using` | ❌ Manual `-=` |
| Global broadcast | ✅ `GlobalInvoke*` | ❌ Requires manual static event |
| Thread safety | ✅ COW snapshots, lock-free reads | ⚠️ Pattern-dependent |
| Debug diagnostics | ✅ Caller-info on every subscription | ❌ None |
| Memory overhead | Higher | Lower |
| Complexity | ~1,200 lines runtime + ~440 lines generator | 0 — language built-in |

---

## 6. Verdict

**For a game engine — unequivocally better than standard C# events.**

Standard C# `event` falls short in engine-scale scenarios: no ordering, no cancellation, leak-prone, no global broadcast, no diagnostics. Prowl's system addresses all of these.

**Where standard C# events are still preferable:**
- Simple component-level events with 1-2 subscribers
- Library APIs consumed by external developers expecting idiomatic C#
- Cases where compile-time delegate signature enforcement is paramount

---

## 7. Recommendations

| Priority | Recommendation |
|---|---|
| 🔴 High | Replace `Dictionary<T, Event<T>>` with `ConcurrentDictionary` to close the latent race condition. |
| 🟡 Medium | Pool or reuse snapshot arrays to reduce GC pressure during subscribe/unsubscribe churn. |
| 🟡 Medium | Add an `async` invoke path for editor/tool events. |
| 🟢 Low | Consider `[CallerArgumentExpression]` capture in Release builds for production diagnostics. |
| 🟢 Low | Document lifetime semantics of generated static managers. |
| 🟢 Low | Consider offering a `WeakSubscribe*` variant for long-lived global domains. |

---

## 8. Summary

Prowl's event system is a **mature, well-tested, performance-aware design** that solves real problems standard C# events cannot. The source generator is the keystone — it turns a verbose, error-prone pattern into a clean, declarative API. The runtime implementation shows clear awareness of game-engine constraints.

**Final rating: 8.0 / 10** — Production-ready with minor improvements needed around thread safety and allocation behavior on the cold path.
