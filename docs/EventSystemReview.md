# Prowl Event System — Technical Review

**Reviewer perspective:** Senior engine/systems programmer, ~15 years shipping game engines and large-scale C# applications.

---

## 1. Architecture Overview

Prowl's event system is a **custom, centralized event bus** built around four main pillars:

| Component | Role |
|---|---|
| `EventManager<T>` | Per-domain event registry using `ConcurrentDictionary<T, Event<T>>` for thread-safe, atomic event creation via `GetOrAdd`. Owns all `Event<T>` instances. Supports local and global invocation via a dedicated global-only snapshot. |
| `Event<T>` | Single event slot. Stores subscribers in priority-sorted buckets. Uses **copy-on-write (COW) snapshots** for lock-free iteration during `Invoke`. Supports `BeginBatch()`/`EndBatch()` to defer snapshot rebuilds during bulk subscriptions. |
| `EventDelegateContainer<T, TArgs>` | Wraps a subscriber `Action<TArgs>`. Carries priority, enable/disable state, and optional DEBUG source-location metadata. Implements `IDisposable` for self-unsubscription. `LifecycleEventDelegateContainer<T, TArgs>` extends this to auto-unsubscribe when the owning `EngineObject` is disposed. |
| `[EventDomain]` source generator | Eliminates boilerplate. From a `static partial class` with `EventKey` fields, generates a backing enum, a static `EventManager`, typed `Invoke*` / `Subscribe*` / `GlobalInvoke*` methods, and C#-style `event` accessors (`+=` / `-=`). |

Additional supporting types: `EventArgsContract<T>` (compile-time + runtime args-type validation), `ICancellable` (short-circuit propagation), `IEventManagerHolder<T>` (broadcast over collections), `Unit` (zero-size struct for parameterless events).

---

## 2. Scorecard

| Category | Score (1-10) | Notes |
|---|:---:|---|
| **Type safety** | 8 | `[EventArgs]` attribute + `EventArgsContract` validates `TArgs` at subscribe and invoke time. Mismatches throw (subscribe) or log+skip (invoke). Not fully compile-time — the mismatch is checked at runtime via reflection-cached dictionaries — but the source generator's typed `Subscribe*` / `Invoke*` methods make it nearly impossible to pass the wrong type in normal use. |
| **Performance — hot path** | 9 | `Invoke<TArgs>` reads a pre-built, strongly-typed `EventDelegateContainer<T,TArgs>[]` snapshot with **zero per-element type checks** and no locking. The `CancellableCheck<TArgs>` static-field pattern avoids boxing value-type args. `ParameterlessEventDelegateContainer<T>` avoids a closure allocation for `Action`→`Action<Unit>` wrapping. `GlobalInvokeEvent` reads a dedicated global-only snapshot, avoiding iteration over non-global instances. |
| **Performance — cold path** | 8 | `RebuildTypedSnapshots` uses `Dictionary<Type, object>`, `MakeGenericMethod`, and `Delegate.CreateDelegate` — all cached after first use. `BeginBatch()`/`EndBatch()` defers snapshot rebuilds during bulk subscriptions, turning O(n²) array copies into a single O(n log n) sort. Individual add/remove still allocates new snapshot arrays, but this is acceptable for a game engine where subscriptions change infrequently relative to invocations. |
| **Thread safety** | 9 | COW snapshots mean `Invoke` never mutates shared state and is lock-free on the read side. `Add`/`Remove` hold a per-event `_lock`. The event registry uses `ConcurrentDictionary<T, Event<T>>` with atomic `GetOrAdd` for event creation. The global instances list uses a separate `s_instancesLock` with its own COW snapshot, and a dedicated `s_globalSnapshot` for global-only managers. The test suite explicitly covers concurrent invoke + add. |
| **Ergonomics / API surface** | 9 | The source generator is the star here. Declaring an event domain is 5-6 lines of code. Consumers get `GameLoopEvents.SubscribeOnFrameBegin(handler, priority)` and `GameLoopEvents.OnFrameBegin += handler`. The `IDisposable` subscription pattern is clean and prevents leaks when used with `using`. Lifecycle-aware overloads (`AddNewDelegate(EngineObject owner, ...)`) auto-unsubscribe when the owner is disposed. Priority ordering is first-class. |
| **Debuggability** | 9 | `#if DEBUG` captures `CallerFilePath`, `CallerLineNumber`, `CallerMemberName` on every subscription. Type-mismatch warnings in DEBUG print the registration site. Per-handler timing via `Stopwatch` logs slow handlers exceeding a configurable threshold (`Event<T>.SlowHandlerThresholdMs`, default 5.0ms). Significantly better than chasing anonymous delegates through a standard `event` invocation list. |
| **Decoupling** | 9 | Publishers and subscribers share only a domain class (e.g. `GameLoopEvents`) and an args struct. No interface coupling, no reference from publisher to subscriber. Global invoke enables cross-assembly communication without dependency injection wiring. |
| **Maintainability** | 7 | The generator is ~440 lines of well-structured incremental-generator code with proper value-equatable data models. The runtime types are cleanly separated. The main risk is the `Event<T>.RebuildTypedSnapshots` method, which is the most complex piece and mixes reflection with generic caching — any future args-type changes require careful testing. |
| **Test coverage** | 9 | Comprehensive xUnit suite covering basic invoke, priority ordering, enable/disable at manager/event/delegate levels, add/remove/re-add, global invoke, disposal, thread safety, self-removal during invocation, cancellation, type-mismatch behavior, `+=`/`-=` syntax, lifecycle subscriptions, batch subscribe, global filtering, and the source generator's output. Production-grade coverage. |

**Overall: 8.5 / 10** — A well-engineered, performance-conscious event system with strong thread safety, lifecycle management, and diagnostics.

---

## 3. Strengths

### 3.1 Source-generated boilerplate elimination
The `[EventDomain]` generator is the single biggest win. Declaring five events in `GameLoopEvents` produces a fully typed API with zero hand-written plumbing. This is something standard C# `event` delegates simply cannot offer without manual repetition.

### 3.2 Copy-on-write snapshot invocation
The COW pattern means:
- **No locks on invoke** — critical for a game loop calling events every frame.
- **Safe self-removal during invocation** — a handler can unsubscribe itself or others without corrupting the iteration. Standard C# `event` delegates copy the delegate on every invoke via `Delegate.GetInvocationList()` or the idiomatic null-conditional pattern, but they don't offer priority ordering or per-handler enable/disable.

### 3.3 Priority ordering
First-class priority support is essential for engine event ordering (e.g. physics before gameplay, gameplay before rendering). C# events provide no ordering guarantees.

### 3.4 Cancellation
`ICancellable` with the `CancellableCheck<TArgs>` JIT-cache pattern is elegant — zero overhead for non-cancellable events, short-circuit for cancellable ones. C# events have no built-in cancellation mechanism.

### 3.5 Scoped subscription lifetime
Returning an `IDisposable` container from `Subscribe*` enables `using` patterns and explicit lifecycle management. This is a significant improvement over C# events, where forgetting to unsubscribe is one of the most common sources of memory leaks.

### 3.6 Debug diagnostics
Automatic caller-info capture on subscriptions is genuinely useful. When a handler throws or a type mismatch is detected, you get a filename and line number pointing to the registration site — not a stack trace pointing to `Invoke`.

### 3.7 Lifecycle-aware subscriptions
The `AddNewDelegate(EngineObject owner, ...)` overloads bind a subscription to an `EngineObject`'s lifetime. When the owner is disposed, the handler automatically unsubscribes on the next invocation — eliminating the most common source of leaked subscriptions from destroyed GameObjects and components.

### 3.8 Batch subscribe for bulk operations
`BeginBatch()` / `EndBatch()` on `Event<T>` and `EventManager<T>` defer COW snapshot rebuilds during bulk subscription operations (e.g. scene load). This turns O(n²) array copies into a single O(n log n) sort, significantly reducing GC pressure. Calls may be nested; only the outermost `EndBatch` triggers the rebuild.

### 3.9 Per-handler timing diagnostics
In DEBUG builds, each handler invocation is timed via `Stopwatch`. Handlers exceeding the configurable `Event<T>.SlowHandlerThresholdMs` threshold (default 5.0ms) are logged with their source location, surfacing performance regressions before they reach profiling. Zero cost in Release builds.

### 3.10 Global manager filtering
A dedicated `s_globalSnapshot` array containing only global managers avoids iterating non-global instances during `GlobalInvokeEvent`. The snapshot is rebuilt when managers are added/removed or when the `Global` flag changes.

---

## 4. Weaknesses & Risks

### 4.1 Reflection in `CreateArrayBuilder`
`MakeGenericMethod` + `Delegate.CreateDelegate` is used once per unique `TArgs` type and then cached — acceptable, but it means the first subscription of a new args type pays a reflection tax. In a hot-reload or domain-reload scenario (common in editors), this cache lives in a `static` and would need explicit clearing.

### 4.2 Strong references for non-`EngineObject` subscribers
Lifecycle-aware subscriptions (`AddNewDelegate(EngineObject owner, ...)`) handle automatic cleanup for engine-managed objects. However, for non-`EngineObject` subscribers, references remain strong. If a subscriber forgets to `Dispose()` or `-=`, the `EventDelegateContainer` (and its closure) will be rooted by the `EventManager` indefinitely. The `IDisposable` pattern mitigates this, but doesn't prevent it.

### 4.3 Global static managers never dispose
The generated `s_eventManager` field is `private static readonly`. It's created once and never disposed. In a long-running editor with multiple project loads, this means the global managers accumulate. The `EventManager` finalizer logs a warning if not disposed, but the generated ones by design are never disposed.

### 4.4 No async support
All handlers are synchronous `Action<TArgs>`. There's no `Func<TArgs, Task>` or `Func<TArgs, ValueTask>` path. For a game engine's main loop this is correct (you don't want `await` in the render path), but for editor/tool events (e.g. asset import, build pipeline) async handlers would be useful.

---

## 5. Comparison: Prowl Event System vs. Standard C# `event` Delegates

| Feature | Prowl `EventManager` + `[EventDomain]` | C# `event` (delegate) |
|---|---|---|
| **Declaration cost** | ~5 lines per domain (generator does the rest) | 1-2 lines per event, but repeated per event |
| **Type safety** | Runtime-validated via `[EventArgs]` contract + typed generated methods | Compile-time enforced by delegate signature |
| **Priority ordering** | ✅ First-class, integer priority | ❌ Invocation order = subscription order (not guaranteed by spec) |
| **Cancellation** | ✅ `ICancellable` interface with short-circuit | ❌ No built-in mechanism |
| **Enable/disable** | ✅ Per-manager, per-event, and per-handler | ❌ Not supported |
| **Scoped unsubscription** | ✅ `IDisposable` / `using` pattern + lifecycle-aware auto-unsubscribe | ❌ Manual `-=` required |
| **Global broadcast** | ✅ `GlobalInvoke*` via dedicated global-only snapshot | ❌ Requires manual static event or mediator |
| **Thread safety** | ✅ `ConcurrentDictionary` + COW snapshots, lock-free reads | ⚠️ Standard pattern (`var h = Event; h?.Invoke()`) is safe but doesn't prevent concurrent modification of the backing delegate |
| **Debug diagnostics** | ✅ Caller-info + per-handler slow timing | ❌ None — you get a multicast delegate with no metadata |
| **Batch subscribe** | ✅ `BeginBatch()`/`EndBatch()` defers snapshot rebuilds | ❌ N/A |
| **Lifecycle management** | ✅ Auto-unsubscribe on `EngineObject` disposal | ❌ Not supported |
| **Invoke performance** | ~Same (array iteration vs. delegate invocation list). Prowl has a dictionary lookup + snapshot array read; C# event has delegate null-check + internal array iteration. Both are O(n) in handlers. | ~Same |
| **Subscribe performance** | Slower (allocates container, rebuilds snapshots; mitigated by batch mode) | Faster (`Delegate.Combine` — one allocation) |
| **Memory overhead** | Higher (per-handler container object, snapshot arrays, dictionaries) | Lower (single `MulticastDelegate` internal array) |
| **Complexity** | ~1,400 lines runtime + ~440 lines generator | 0 lines — language built-in |
| **Learning curve** | Moderate — need to understand `EventKey`, `[EventDomain]`, `[EventArgs]`, `Unit`, priority semantics | Near-zero for C# developers |

---

## 6. Verdict: Is It Better Than C# Event Delegates?

**Yes, for a game engine — unequivocally.**

The standard C# `event` keyword was designed for component-level pub/sub (e.g. a button's `Click` event). It works well in that context. But it falls short in engine-scale scenarios:

1. **No ordering guarantees.** In a game loop, execution order of systems matters. Prowl's priority system solves this cleanly.
2. **No cancellation.** Engine events frequently need short-circuiting (e.g. input consumed by UI before reaching gameplay). Prowl handles this with `ICancellable`.
3. **Leak-prone.** The #1 bug with C# events in long-running applications is forgotten `-=` calls. Prowl's `IDisposable` subscription pattern and DEBUG source tracking directly address this.
4. **No global broadcast.** Engine subsystems often need to fire events across assembly boundaries without tight coupling. Prowl's `GlobalInvoke` pattern eliminates the need for a separate service locator or DI framework.
5. **No diagnostics.** When debugging "why did this handler fire?" or "who is still subscribed?", C# events give you nothing. Prowl gives you file:line of every registration.

The trade-offs (higher memory overhead, more complex internals, runtime rather than compile-time type checking) are acceptable given the benefits. The source generator eliminates most of the ergonomic cost, and the COW snapshot pattern ensures the hot path (invoke) is competitive with raw delegate invocation.

**Where standard C# events are still preferable:**
- Simple component-level events with 1-2 subscribers (e.g. `PropertyChanged`).
- Library APIs consumed by external developers who expect idiomatic C# patterns.
- Cases where compile-time delegate signature enforcement is more valuable than runtime contract checking.

---

## 7. Remaining Recommendations

| Priority | Recommendation |
|---|---|
| 🟡 Medium | Add an `async` invoke path (`InvokeAsync<TArgs>`) for editor/tool events where handlers legitimately need to perform I/O. Not a priority for the game loop hot path. |
| 🟢 Low | Consider a `[CallerArgumentExpression]` capture in Release builds (not just DEBUG) to improve production diagnostics without the full `CallerFilePath` cost. |

---

## 8. Summary

Prowl's event system is a **mature, well-tested, performance-aware design** that solves real problems that standard C# events cannot. The source generator is the keystone — it turns what would otherwise be a verbose, error-prone pattern into a clean, declarative API. The runtime implementation shows clear awareness of game-engine constraints: lock-free hot path via COW snapshots, priority ordering, cancellation, thread-safe registry via `ConcurrentDictionary`, lifecycle-aware subscriptions for automatic cleanup, batch subscribe for scene-load performance, per-handler DEBUG timing, and dedicated global-only snapshots for efficient cross-assembly broadcast.

It is not a replacement for C# events everywhere — but for an engine's cross-cutting event infrastructure, it is categorically better.

**Final rating: 8.5 / 10** — Production-ready with strong thread safety, lifecycle management, and diagnostics. Remaining areas for improvement are async handler support and Release-build diagnostics.
