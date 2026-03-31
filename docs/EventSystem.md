# Prowl Engine — Event System

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Core Types](#core-types)
  - [EventManager\<T\>](#eventmanagert)
  - [Event\<T\>](#eventt)
  - [EventDelegateContainer\<T\> / EventDelegateContainer\<T, TArgs\>](#eventdelegatecontainert--eventdelegatecontainert-targs)
  - [EventAccessor\<TEnum\> / EventAccessor\<TEnum, TArgs\>](#eventaccessortenum--eventaccessortenum-targs)
  - [Unit](#unit)
  - [ICancellable](#icancellable)
  - [IInvocable\<TArgs\>](#iinvocabletargs)
  - [IEventManagerHolder\<T\>](#ieventmanagerholdert)
- [Source Generator (EventDomain)](#source-generator-eventdomain)
  - [EventDomainAttribute](#eventdomainattribute)
  - [EventKey](#eventkey)
  - [EventArgsAttribute](#eventargsattribute)
  - [What Gets Generated](#what-gets-generated)
  - [Static vs Instance Domains](#static-vs-instance-domains)
- [Type Safety via EventArgsContract](#type-safety-via-eventargscontract)
- [Usage Guide](#usage-guide)
  - [Defining an Event Domain](#defining-an-event-domain)
  - [Subscribing to Events](#subscribing-to-events)
  - [Invoking Events](#invoking-events)
  - [Unsubscribing from Events](#unsubscribing-from-events)
  - [Priority Ordering](#priority-ordering)
  - [Event Cancellation](#event-cancellation)
  - [Global Events](#global-events)
  - [Instance Domains (Per-Object Events)](#instance-domains-per-object-events)
  - [Enabling / Disabling Events](#enabling--disabling-events)
- [Built-in Event Domains](#built-in-event-domains)
  - [GameLoopEvents](#gameloopevents)
  - [RenderingEvents](#renderingevents)
  - [PhysicsEvents](#physicsevents)
  - [SceneManagerEvents](#scenemanagerevents)
  - [AssetEvents](#assetevents)
  - [DpiEvents](#dpievents)
  - [DebugEvents](#debugevents)
  - [BaseEvents](#baseevents)
  - [EditorEvents](#editorevents)
- [Thread Safety](#thread-safety)
- [DEBUG Diagnostics](#debug-diagnostics)
- [Best Practices](#best-practices)
- [File Reference](#file-reference)

---

## Overview

The Prowl Event System is a strongly-typed, source-generated publish/subscribe system designed for decoupled communication between engine subsystems, editor components, and gameplay code. It replaces traditional C# events and manual delegate lists with a structured approach that provides:

- **Compile-time safety** — a Roslyn source generator produces typed `Invoke`, `Subscribe`, and `GlobalInvoke` methods for each event, preventing type mismatches at compile time.
- **Priority-ordered dispatch** — handlers execute in ascending priority order (lower values first), with deterministic ordering within the same priority bucket.
- **Event cancellation** — argument types that implement `ICancellable` can stop propagation mid-chain.
- **Copy-on-write snapshots** — invocation iterates over immutable array snapshots, making it safe to add or remove handlers during dispatch.
- **Global broadcast** — managers marked as `Global` participate in static `GlobalInvoke` calls, enabling engine-wide event broadcast without requiring direct references.
- **Automatic lifetime management** — delegate containers implement `IDisposable` for deterministic unsubscription via `using` blocks.
- **DEBUG diagnostics** — in debug builds, every subscription captures its source file, line number, and member name, and type-mismatch warnings are logged with full provenance.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                    User Code / Engine Subsystem                     │
│                                                                     │
│   GameLoopEvents.OnFrameBegin += MyHandler;                        │
│   GameLoopEvents.InvokeOnFrameBegin(args);                         │
└──────────────┬──────────────────────────────┬───────────────────────┘
               │ Subscribe                    │ Invoke
               ▼                              ▼
┌──────────────────────────────────────────────────────────────────────┐
│               [EventDomain] Generated Partial Class                  │
│                                                                      │
│  ┌──────────────┐  ┌──────────────────────┐  ┌───────────────────┐  │
│  │  EventTypes   │  │  EventAccessor<T>    │  │  InvokeOnXxx()    │  │
│  │  (enum)       │  │  (+= / -= / Invoke)  │  │  SubscribeOnXxx() │  │
│  └──────┬───────┘  └──────────┬───────────┘  │  GlobalInvokeXxx()│  │
│         │                     │               └────────┬──────────┘  │
│         │                     │                        │             │
└─────────┼─────────────────────┼────────────────────────┼─────────────┘
          │                     │                        │
          ▼                     ▼                        ▼
┌──────────────────────────────────────────────────────────────────────┐
│                     EventManager<T>                                   │
│                                                                      │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │ Dictionary<T, Event<T>>  _events                               │  │
│  │                                                                │  │
│  │  Event<T>  (per enum value)                                    │  │
│  │  ├─ Dictionary<int, List<EventDelegateContainer<T>>>           │  │
│  │  │    (priority buckets)                                       │  │
│  │  ├─ EventDelegateContainer<T>[]  _cachedSnapshot               │  │
│  │  │    (flat, priority-sorted COW array — all types)            │  │
│  │  └─ Dictionary<Type, object>  _typedSnapshots                  │  │
│  │       (per-TArgs COW arrays for zero-cast invocation)          │  │
│  └────────────────────────────────────────────────────────────────┘  │
│                                                                      │
│  Static: List<EventManager<T>> s_instances  (global broadcast pool)  │
└──────────────────────────────────────────────────────────────────────┘
```

---

## Core Types

### EventManager\<T\>

**File:** `Prowl.Runtime/EventSystem/EventManager.cs`  
**Namespace:** `Prowl.Runtime.EventSystem`

The central hub for a set of events defined by an enum `T`. Each enum value maps to a lazily-created `Event<T>` instance. The manager supports:

| Member | Description |
|--------|-------------|
| `EventManager(bool global = false)` | Constructor. Pass `true` to include this manager in `GlobalInvokeEvent` broadcasts. |
| `Global` | Gets/sets whether this manager participates in global broadcasts. |
| `Enabled` | Gets/sets the enabled state. Disabled managers silently skip all invocations. |
| `AddNewDelegate<TArgs>(T, Action<TArgs>, int priority)` | Registers a typed handler. Returns a disposable `EventDelegateContainer<T, TArgs>`. |
| `AddNewDelegate(T, Action, int priority)` | Registers a parameterless handler (uses `Unit` internally). |
| `RemoveDelegate(EventDelegateContainer<T>)` | Removes a specific delegate container. |
| `RemoveDelegate(T, Delegate)` | Removes the first delegate matching the given handler. Used by `EventAccessor.-=`. |
| `InvokeEvent<TArgs>(T, TArgs)` | Invokes all handlers for the given event with typed arguments. |
| `InvokeEvent(T)` | Invokes all parameterless handlers for the given event. |
| `EnableEvent(T)` / `DisableEvent(T)` | Enables/disables a specific event type. |
| `GetEvent(T)` | Returns the `Event<T>` if it exists, or `null`. |
| `static GlobalInvokeEvent<TArgs>(T, TArgs)` | Broadcasts to all enabled global managers. |
| `static GlobalInvokeEvent(T)` | Parameterless global broadcast. |
| `static LastGlobalInstance` | Returns the most recently created enabled global manager. |
| `Dispose()` | Removes the manager from the global pool and disables all events. |

### Event\<T\>

**File:** `Prowl.Runtime/EventSystem/Event.cs`  
**Namespace:** `Prowl.Runtime.EventSystem`

Represents a single event type within a manager. Maintains priority-bucketed delegate lists and copy-on-write snapshot arrays for lock-free invocation.

| Member | Description |
|--------|-------------|
| `EventType` | The enum value this event represents. |
| `EventManager` | The parent manager. |
| `Enabled` | Volatile flag; disabled events skip invocation. |
| `Invoke<TArgs>(TArgs)` | Iterates the typed COW snapshot. Supports `ICancellable` short-circuiting. |
| `GetHandlers<TArgs>()` | Returns a `ReadOnlySpan` of currently registered typed handlers. |
| `Add(EventDelegateContainer<T>)` | Adds a handler and rebuilds snapshots. |
| `Remove(EventDelegateContainer<T>)` | Removes a handler and rebuilds snapshots. |
| `RemoveByDelegate(Delegate)` | Finds and removes the first container wrapping the given delegate. |

**Snapshot architecture:** When handlers are added or removed, `RebuildSnapshot()` constructs:
1. A flat `EventDelegateContainer<T>[]` sorted by priority (used for DEBUG type-mismatch warnings).
2. Per-`TArgs` typed arrays (`EventDelegateContainer<T, TArgs>[]`) stored in `_typedSnapshots`. `Invoke<TArgs>` retrieves the matching array with a single dictionary lookup and iterates it with direct virtual calls — zero per-element type checks at runtime.

### EventDelegateContainer\<T\> / EventDelegateContainer\<T, TArgs\>

**File:** `Prowl.Runtime/EventSystem/EventDelegateContainer.cs`  
**Namespace:** `Prowl.Runtime.EventSystem`

Wraps a handler delegate with metadata (priority, event type, enabled state) and provides `IDisposable` for self-unsubscription.

**Base class** (`EventDelegateContainer<T>`):

| Member | Description |
|--------|-------------|
| `EventType` | The enum value this handler is registered for. |
| `Priority` | Integer priority (ascending order; negative values run first). |
| `Enabled` | Per-handler enable/disable toggle. |
| `ArgsType` | The `TArgs` type (abstract, overridden by the generic subclass). |
| `MatchesDelegate(Delegate)` | Returns `true` if the container wraps the given delegate. |
| `Enable()` / `Disable()` | Toggle the handler's enabled state. |
| `Dispose()` | Removes this handler from its parent event. |
| `SourceFile`, `SourceLine`, `SourceMember` | DEBUG-only: source location where the handler was registered. |
| `SourceDescription` | DEBUG-only: compact `"File:Line (Member)"` string. |

**Generic class** (`EventDelegateContainer<T, TArgs>`):

| Member | Description |
|--------|-------------|
| `Invoke(TArgs)` | Calls the wrapped `Action<TArgs>` if enabled. |

**Specialized class** (`ParameterlessEventDelegateContainer<T>`):

Extends `EventDelegateContainer<T, Unit>` but stores a raw `Action` instead of `Action<Unit>`, avoiding a closure allocation for parameterless events.

### EventAccessor\<TEnum\> / EventAccessor\<TEnum, TArgs\>

**File:** `Prowl.Runtime/EventSystem/EventAccessor.cs`  
**Namespace:** `Prowl.Runtime.EventSystem`

Lightweight `readonly struct` returned by generated event properties. Enables familiar C#-style `+=` / `-=` subscription syntax and `.Invoke()` invocation.

```csharp
// Parameterless EventAccessor<TEnum>
GameLoopEvents.OnFrameBegin += MyHandler;       // subscribe
GameLoopEvents.OnFrameBegin -= MyHandler;       // unsubscribe
GameLoopEvents.OnFrameBegin.Invoke();           // fire

// Typed EventAccessor<TEnum, TArgs>
GameLoopEvents.OnFrameEnd += (args) => { ... }; // subscribe with args
GameLoopEvents.OnFrameEnd.Invoke(args);         // fire with args
```

The generated property has a no-op setter, which is required because `+=` on a property expands to `set(get() + handler)`.

### Unit

**File:** `Prowl.Runtime/EventSystem/EventParam.cs`  
**Namespace:** `Prowl.Runtime.EventSystem`

```csharp
public readonly struct Unit;
```

A zero-size struct used as `TArgs` for parameterless events. This avoids special-casing `void` throughout the generic event pipeline.

### ICancellable

**File:** `Prowl.Runtime/EventSystem/ICancellable.cs`  
**Namespace:** `Prowl.Runtime.EventSystem`

```csharp
public interface ICancellable
{
    bool Cancelled { get; set; }
}
```

When an event argument type implements `ICancellable`, the `Event<T>.Invoke<TArgs>` method checks `Cancelled` after each handler. If `true`, remaining handlers in the priority chain are skipped. The check uses a JIT-time static boolean (`CancellableCheck<TArgs>.IsCancellable`) so non-cancellable events pay zero cost.

### IInvocable\<TArgs\>

**File:** `Prowl.Runtime/EventSystem/IInvocable.cs`  
**Namespace:** `Prowl.Runtime.EventSystem`

```csharp
public interface IInvocable<in TArgs>
{
    void Invoke(TArgs args);
}
```

Implemented by `EventDelegateContainer<T, TArgs>` for zero-cast typed invocation.

### IEventManagerHolder\<T\>

**File:** `Prowl.Runtime/EventSystem/IEventManagerHolder.cs`  
**Namespace:** `Prowl.Runtime.EventSystem`

An interface for objects that own an `EventManager<T>`. Includes extension methods for batch-invoking events across arrays or lists of holders:

```csharp
holders.InvokeEvents(eventType, args);  // typed
holders.InvokeEvents(eventType);        // parameterless
```

---

## Source Generator (EventDomain)

**File:** `Prowl.EventSystem.Generators/EventDomainGenerator.cs`

The `EventDomainGenerator` is a Roslyn incremental source generator that eliminates boilerplate for defining event groups. It produces:

1. A backing `enum EventTypes` with one value per event.
2. An `EventManager<EventTypes>` field (static or instance).
3. `EventAccessor` properties for `+=` / `-=` syntax.
4. `InvokeOnXxx()`, `SubscribeOnXxx()`, and `GlobalInvokeOnXxx()` convenience methods for each event.

### EventDomainAttribute

**File:** `Prowl.Runtime/EventSystem/EventDomainAttribute.cs`

```csharp
[EventDomain]                   // non-global
[EventDomain(Global = true)]    // global — participates in GlobalInvoke
```

Place on a `partial class` to mark it as an event domain. The class can be `static` (shared singleton manager) or non-static (per-instance manager).

### EventKey

**File:** `Prowl.Runtime/EventSystem/EventKey.cs`

```csharp
public readonly struct EventKey;
```

A marker type. Declare `private static readonly EventKey _OnXxx = new();` fields to define events. The leading underscore is stripped by the generator to produce the public event name `OnXxx`.

### EventArgsAttribute

**File:** `Prowl.Runtime/EventSystem/EventArgsAttribute.cs`

```csharp
[EventArgs(typeof(MyPayload))]
private static readonly EventKey _OnSomething = new();
```

Declares the canonical argument type for an event. When omitted, the event defaults to `Unit` (parameterless). This attribute is also propagated to the generated enum values, enabling runtime type-safety checks via `EventArgsContract<T>`.

### What Gets Generated

Given this input:

```csharp
[EventDomain(Global = true)]
public static partial class GameLoopEvents
{
    [EventArgs(typeof(FrameBeginArgs))]
    private static readonly EventKey _OnFrameBegin = new();

    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnClosing = new();
}
```

The generator produces a partial class containing:

```csharp
public static partial class GameLoopEvents
{
    // Backing enum
    public enum EventTypes
    {
        [EventArgs(typeof(FrameBeginArgs))]
        OnFrameBegin,
        [EventArgs(typeof(Unit))]
        OnClosing,
    }

    // Manager
    private static readonly EventManager<EventTypes> s_eventManager = new(global: true);
    public static EventManager<EventTypes> Manager => s_eventManager;

    // EventAccessor properties (support += / -= / .Invoke())
    public static EventAccessor<EventTypes, FrameBeginArgs> OnFrameBegin
    {
        get => new(s_eventManager, EventTypes.OnFrameBegin);
        set { } // no-op setter for compound assignment
    }

    public static EventAccessor<EventTypes> OnClosing
    {
        get => new(s_eventManager, EventTypes.OnClosing);
        set { }
    }

    // Typed convenience methods
    public static void InvokeOnFrameBegin(FrameBeginArgs args) => ...;
    public static void GlobalInvokeOnFrameBegin(FrameBeginArgs args) => ...;
    public static EventDelegateContainer<EventTypes, FrameBeginArgs>
        SubscribeOnFrameBegin(Action<FrameBeginArgs> handler, int priority = 0) => ...;

    // Parameterless convenience methods
    public static void InvokeOnClosing() => ...;
    public static void GlobalInvokeOnClosing() => ...;
    public static EventDelegateContainer<EventTypes, Unit>
        SubscribeOnClosing(Action handler, int priority = 0) => ...;
}
```

### Static vs Instance Domains

| Feature | Static Domain | Instance Domain |
|---------|---------------|-----------------|
| Class declaration | `public static partial class` | `public partial class` |
| Manager storage | `private static readonly` field | `private readonly` field |
| Convenience methods | `static` | Instance methods |
| `GlobalInvoke` | Always `static` | Always `static` |
| Isolation | Single shared manager | Each instance has its own `EventManager` |
| Disposal | Not typically needed | Call `Manager.Dispose()` when done |

**Instance domain example:**

```csharp
[EventDomain]
public partial class ActorEvents : IDisposable
{
    [EventArgs(typeof(DamageArgs))]
    private static readonly EventKey _OnDamaged = new();

    public readonly record struct DamageArgs(float Amount);

    public void Dispose() => Manager.Dispose();
}

// Usage:
var actor = new ActorEvents();
actor.OnDamaged += (args) => Console.WriteLine($"Hit for {args.Amount}");
actor.InvokeOnDamaged(new ActorEvents.DamageArgs(25));
actor.Dispose(); // cleans up the manager
```

---

## Type Safety via EventArgsContract

**File:** `Prowl.Runtime/EventSystem/EventArgsContract.cs`

`EventArgsContract<T>` builds a compile-time-cached mapping from each enum value to its declared `TArgs` type (read from `[EventArgs]` on the enum field). It provides runtime validation:

- **On subscribe:** `AddNewDelegate<TArgs>` throws `InvalidOperationException` if `TArgs` does not match the declared type.
- **On invoke:** `InvokeEvent<TArgs>` logs an error and returns without firing if `TArgs` does not match.
- **Opt-in:** If no `[EventArgs]` attribute is present on a value, any `TArgs` is accepted.

This catches bugs such as subscribing to a `FrameBeginArgs` event with a `string` handler.

---

## Usage Guide

### Defining an Event Domain

1. Create a `partial class` with the `[EventDomain]` attribute.
2. Declare events as `private static readonly EventKey _OnXxx = new();` fields.
3. Optionally annotate each field with `[EventArgs(typeof(YourArgs))]`.
4. Define argument types as `readonly record struct` (recommended) or classes.

```csharp
using Prowl.Runtime.EventSystem;

[EventDomain(Global = true)]
public static partial class MyEvents
{
    [EventArgs(typeof(ScoreChangedArgs))]
    private static readonly EventKey _OnScoreChanged = new();

    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnGameOver = new();
}

public readonly record struct ScoreChangedArgs(int OldScore, int NewScore);
```

### Subscribing to Events

There are two subscription patterns:

**Pattern 1: `+=` / `-=` (quick & familiar)**

```csharp
// Parameterless
MyEvents.OnGameOver += OnGameOver;

// Typed
MyEvents.OnScoreChanged += (args) =>
    Console.WriteLine($"Score: {args.OldScore} -> {args.NewScore}");
```

**Pattern 2: `SubscribeOnXxx()` (priority, IDisposable)**

```csharp
// Returns IDisposable — call Dispose() or use 'using' to unsubscribe
var sub = MyEvents.SubscribeOnScoreChanged(
    handler: (args) => UpdateUI(args),
    priority: 10
);

// Later:
sub.Dispose(); // unsubscribes
```

```csharp
// Using pattern for scoped subscriptions
using (MyEvents.SubscribeOnGameOver(() => ShowGameOverScreen()))
{
    // Handler is active within this scope
}
// Handler is automatically removed here
```

### Invoking Events

**Pattern 1: `.Invoke()` via EventAccessor**

```csharp
MyEvents.OnGameOver.Invoke();
MyEvents.OnScoreChanged.Invoke(new ScoreChangedArgs(oldScore, newScore));
```

**Pattern 2: `InvokeOnXxx()` convenience method**

```csharp
MyEvents.InvokeOnGameOver();
MyEvents.InvokeOnScoreChanged(new ScoreChangedArgs(oldScore, newScore));
```

**Pattern 3: `GlobalInvokeOnXxx()` — broadcast across all global managers**

```csharp
MyEvents.GlobalInvokeOnScoreChanged(new ScoreChangedArgs(0, 100));
```

### Unsubscribing from Events

```csharp
// Option 1: -= with the same delegate reference
Action handler = () => { ... };
MyEvents.OnGameOver += handler;
MyEvents.OnGameOver -= handler;   // removes first match

// Option 2: Dispose the subscription container
var sub = MyEvents.SubscribeOnGameOver(() => { ... });
sub.Dispose();

// Option 3: 'using' block for automatic cleanup
using var sub = MyEvents.SubscribeOnGameOver(() => { ... }, priority: 5);

// Option 4: Remove via manager directly
manager.RemoveDelegate(container);
```

### Priority Ordering

Handlers execute in **ascending priority order** (lower values first). Negative priorities run before zero.

```csharp
MyEvents.SubscribeOnGameOver(() => Console.Write("C"), priority: 2);
MyEvents.SubscribeOnGameOver(() => Console.Write("A"), priority: 0);
MyEvents.SubscribeOnGameOver(() => Console.Write("B"), priority: 1);

MyEvents.InvokeOnGameOver(); // Output: "ABC"
```

Handlers with the same priority execute in registration order.

### Event Cancellation

Implement `ICancellable` on your argument type to allow handlers to stop propagation:

```csharp
public class ValidateArgs : ICancellable
{
    public bool Cancelled { get; set; }
    public string Input { get; set; }
}

[EventDomain]
public static partial class ValidationEvents
{
    [EventArgs(typeof(ValidateArgs))]
    private static readonly EventKey _OnValidate = new();
}

// Handler with priority 0 runs first
ValidationEvents.SubscribeOnValidate(args =>
{
    if (string.IsNullOrEmpty(args.Input))
        args.Cancelled = true; // stops further handlers
}, priority: 0);

// Handler with priority 1 is skipped if cancelled
ValidationEvents.SubscribeOnValidate(args =>
{
    ProcessInput(args.Input); // only runs if not cancelled
}, priority: 1);
```

> **Note:** The cancellation check uses a JIT-time constant (`CancellableCheck<TArgs>.IsCancellable`), so events with non-cancellable argument types incur zero overhead for the check.

### Global Events

Managers created with `global: true` (or domains with `[EventDomain(Global = true)]`) participate in `GlobalInvoke` broadcasts:

```csharp
// All global managers for this domain type receive the event
GameLoopEvents.GlobalInvokeOnFrameBegin(new FrameBeginArgs(frameCount, dt));
```

Multiple global managers can coexist. `GlobalInvokeEvent` iterates a copy-on-write snapshot of all registered instances, invoking only those that are both `Enabled` and `Global`.

`EventManager<T>.LastGlobalInstance` returns the most recently registered enabled global manager — useful for systems that need a "current" manager reference.

### Instance Domains (Per-Object Events)

Non-static event domains create a per-instance `EventManager<T>`:

```csharp
[EventDomain]
public partial class ActorEvents : IDisposable
{
    [EventArgs(typeof(HitArgs))]
    private static readonly EventKey _OnHit = new();

    public readonly record struct HitArgs(int Damage);
    public void Dispose() => Manager.Dispose();
}

var actor1 = new ActorEvents();
var actor2 = new ActorEvents();

actor1.OnHit += (args) => Console.WriteLine($"Actor1 hit for {args.Damage}");
actor2.OnHit += (args) => Console.WriteLine($"Actor2 hit for {args.Damage}");

actor1.InvokeOnHit(new ActorEvents.HitArgs(10)); // Only actor1's handler fires
actor1.Dispose();
```

Instance domains still generate `static GlobalInvokeOnXxx()` methods for cross-instance broadcast (if the domain is marked `Global = true`).

### Enabling / Disabling Events

Events can be toggled at three levels:

| Level | API | Effect |
|-------|-----|--------|
| **Manager** | `manager.Enabled = false` | All events on this manager are silenced. |
| **Event** | `manager.DisableEvent(eventType)` | A specific event type is silenced. |
| **Handler** | `container.Disable()` | A specific handler is skipped during invocation. |

```csharp
// Disable all events on a manager
GameLoopEvents.Manager.Enabled = false;

// Disable a specific event
GameLoopEvents.Manager.DisableEvent(GameLoopEvents.EventTypes.OnFrameBegin);

// Disable a specific handler
var sub = GameLoopEvents.SubscribeOnFrameEnd(args => { ... });
sub.Disable();
sub.Enable(); // re-enable later
```

---

## Built-in Event Domains

### GameLoopEvents

**File:** `Prowl.Runtime/EventSystem/GameLoopEvents.cs`  
**Global:** Yes

| Event | Args Type | Description |
|-------|-----------|-------------|
| `OnInitialized` | `InitializedArgs` | Engine and window fully initialized. Carries backend type, name, and initial window dimensions. |
| `OnFrameBegin` | `FrameBeginArgs` | Start of each frame, before input processing. Carries frame count and unscaled delta time. |
| `OnFrameEnd` | `FrameEndArgs` | After all update logic. Carries frame count, scaled/unscaled delta time, and total time. |
| `OnRenderComplete` | `RenderCompleteArgs` | After rendering is complete. Carries frame count and render delta time. |
| `OnClosing` | `ClosingArgs` | Application window is closing. Carries total runtime and total frames. |

### RenderingEvents

**File:** `Prowl.Runtime/EventSystem/RenderingEvents.cs`  
**Global:** Yes

| Event | Args Type | Description |
|-------|-----------|-------------|
| `OnBeginRender` | `Unit` | Before any scene rendering begins for the frame. |
| `OnEndRender` | `Unit` | After all rendering and post-processing is complete. |
| `OnShadowsReady` | `Unit` | After the shadow atlas has been initialized and cleared. |

### PhysicsEvents

**File:** `Prowl.Runtime/EventSystem/PhysicsEvents.cs`  
**Global:** Yes

| Event | Args Type | Description |
|-------|-----------|-------------|
| `OnPrePhysicsStep` | `PhysicsStepArgs` | Before the physics world steps. Carries the fixed timestep duration. |
| `OnPostPhysicsStep` | `PhysicsStepArgs` | After the physics world completes a step. |

### SceneManagerEvents

**File:** `Prowl.Runtime/EventSystem/SceneManagerEvents.cs`  
**Global:** No

| Event | Args Type | Description |
|-------|-----------|-------------|
| `OnSceneLoaded` | `SceneEventArgs` | After a scene is loaded additively. Carries the `Scene` reference. |
| `OnSceneUnloaded` | `SceneEventArgs` | After a scene is unloaded. |

### AssetEvents

**File:** `Prowl.Runtime/EventSystem/AssetEvents.cs`  
**Global:** Yes

| Event | Args Type | Description |
|-------|-----------|-------------|
| `OnAssetsRefreshed` | `Unit` | After the asset database is refreshed. |
| `OnAssetsImported` | `AssetImportedArgs` | Files imported into the project. Carries imported paths. |
| `OnAssetDeleted` | `AssetDeletedArgs` | Asset deleted from the project. Carries the relative path. |

### DpiEvents

**File:** `Prowl.Runtime/EventSystem/DpiEvents.cs`  
**Global:** No

| Event | Args Type | Description |
|-------|-----------|-------------|
| `OnDpiChanged` | `DpiChangedArgs` | DPI scale changed (e.g., window moved to a different monitor). Carries old and new scale. |

### DebugEvents

**File:** `Prowl.Runtime/EventSystem/DebugEvents.cs`  
**Global:** No

| Event | Args Type | Description |
|-------|-----------|-------------|
| `OnLog` | `LogEventArgs` | A message was logged via `Debug.Log` or similar. Carries message, optional stack trace, and severity. |

### BaseEvents

**File:** `Prowl.Runtime/EventSystem/BaseEvents.cs`  
**Global:** No

| Event | Args Type | Description |
|-------|-----------|-------------|
| `OnBeforeUpdate` | `Unit` | Before the update tick. |
| `OnAfterUpdate` | `Unit` | After the update tick. |
| `OnBeforeLateUpdate` | `Unit` | Before the late update tick. |
| `OnAfterLateUpdate` | `Unit` | After the late update tick. |

### EditorEvents

**File:** `Prowl.Editor/Core/EditorEvents.cs`  
**Global:** No

| Event | Args Type | Description |
|-------|-----------|-------------|
| `OnPlayModeStateChanged` | `PlayModeChangedArgs` | Play mode state changes (play, pause, stop). |
| `OnAssemblyChanged` | `Unit` | User-script assembly recompiled and reloaded. |
| `OnErrorLogged` | `Unit` | Error or exception logged; used to pause play mode. |

---

## Thread Safety

The event system is designed for concurrent use:

- **Copy-on-write snapshots:** `Event<T>` rebuilds immutable snapshot arrays whenever handlers are added or removed (under a lock). `Invoke<TArgs>` reads the snapshot reference without locking, then iterates the frozen array. This means:
  - Adding or removing handlers during dispatch is safe — the current invocation continues over the old snapshot.
  - Self-removal during invocation does not corrupt iteration.
- **Manager instance list:** `EventManager<T>` maintains a `s_instancesSnapshot` array rebuilt on add/remove (under `s_instancesLock`). `GlobalInvokeEvent` reads the snapshot without locking.
- **`Enabled` flags:** `Event<T>.Enabled` is `volatile`, ensuring visibility across threads without full locking.
- **Lock scope:** Locks are held only during structural mutations (add/remove/rebuild), never during invocation. This keeps the hot path lock-free.

---

## DEBUG Diagnostics

In `DEBUG` builds, the event system captures additional diagnostic information:

### Handler Source Tracking

Every `AddNewDelegate` overload accepts `[CallerFilePath]`, `[CallerLineNumber]`, and `[CallerMemberName]` parameters. These are stored on the `EventDelegateContainer<T>` and accessible via `SourceDescription`:

```
"GamePanel.cs:42 (Initialize)"
```

### Type Mismatch Warnings

When `Invoke<TArgs>` is called, if the full snapshot has more handlers than the typed snapshot, it means some handlers were registered with a different `TArgs`. In DEBUG builds, a warning is logged for each mismatched handler, including its source description:

```
[EventSystem] Type mismatch on GameLoopEvents.OnFrameBegin: handler registered for 'string'
but invoked with 'FrameBeginArgs'. Handler was skipped. (registered at MySystem.cs:15 (Setup))
```

### Contract Violation Errors

If `InvokeEvent<TArgs>` is called with a type that doesn't match the `[EventArgs]` declaration:

```
[EventSystem] Type mismatch on GameLoopEvents.OnFrameBegin: invoked with 'string' but the
event declares 'FrameBeginArgs' via [EventArgs].
```

---

## Best Practices

1. **Use `readonly record struct` for argument types.** Value types avoid heap allocations; records provide built-in equality and deconstruction.

2. **Prefer `SubscribeOnXxx()` over `+=` when you need priority control or deterministic cleanup.** The returned container is `IDisposable` and can be used with `using` blocks.

3. **Always dispose instance domain managers.** Non-static domains create `EventManager` instances that register themselves in a static list. Failing to `Dispose()` will leak the manager and produce a finalizer warning.

4. **Use `readonly record struct` with `ICancellable`** only when the args is a **class** — value types are passed by value, so `Cancelled = true` on a struct copy won't propagate back to the `Event<T>` invocation loop. Use a `class` for cancellable arguments.

5. **Keep priorities simple.** Use 0 for normal handlers, negative values for "before" hooks, and positive values for "after" hooks. Avoid large priority ranges.

6. **Prefer domain-specific events over generic ones.** Instead of one catch-all event with a string discriminator, define separate `EventKey` fields for each logical event. This provides compile-time safety and better discoverability.

7. **Don't hold references to snapshot arrays.** The `ReadOnlySpan` from `GetHandlers<TArgs>()` references a COW snapshot that may become stale. Use it immediately or copy if needed.

8. **Leverage `GlobalInvoke` for engine-wide events.** Systems like the game loop, physics, and rendering use global events so that any part of the engine can respond without tight coupling.

---

## File Reference

| File | Description |
|------|-------------|
| `Prowl.Runtime/EventSystem/EventManager.cs` | Central event hub; manages `Event<T>` instances and global broadcast. |
| `Prowl.Runtime/EventSystem/Event.cs` | Single event type; priority buckets, COW snapshots, and invocation. |
| `Prowl.Runtime/EventSystem/EventDelegateContainer.cs` | Handler wrappers with priority, enable/disable, and `IDisposable`. |
| `Prowl.Runtime/EventSystem/EventAccessor.cs` | Lightweight structs enabling `+=` / `-=` / `.Invoke()` syntax. |
| `Prowl.Runtime/EventSystem/EventKey.cs` | Marker struct for declaring events in `[EventDomain]` classes. |
| `Prowl.Runtime/EventSystem/EventDomainAttribute.cs` | Attribute marking a class as an event domain for the source generator. |
| `Prowl.Runtime/EventSystem/EventArgsAttribute.cs` | Attribute declaring the canonical `TArgs` type for an event. |
| `Prowl.Runtime/EventSystem/EventArgsContract.cs` | Runtime type-safety validation against `[EventArgs]` declarations. |
| `Prowl.Runtime/EventSystem/EventParam.cs` | `Unit` struct for parameterless events. |
| `Prowl.Runtime/EventSystem/ICancellable.cs` | Interface for cancellable event arguments. |
| `Prowl.Runtime/EventSystem/IInvocable.cs` | Interface for typed, zero-cast invocation. |
| `Prowl.Runtime/EventSystem/IEventManagerHolder.cs` | Interface + extensions for batch event invocation across holders. |
| `Prowl.Runtime/EventSystem/BaseEvents.cs` | Update lifecycle events. |
| `Prowl.Runtime/EventSystem/GameLoopEvents.cs` | Game loop lifecycle events. |
| `Prowl.Runtime/EventSystem/RenderingEvents.cs` | Rendering pipeline events. |
| `Prowl.Runtime/EventSystem/PhysicsEvents.cs` | Physics simulation events. |
| `Prowl.Runtime/EventSystem/SceneManagerEvents.cs` | Scene load/unload events. |
| `Prowl.Runtime/EventSystem/AssetEvents.cs` | Asset database events. |
| `Prowl.Runtime/EventSystem/DpiEvents.cs` | DPI scaling events. |
| `Prowl.Runtime/EventSystem/DebugEvents.cs` | Debug logging events. |
| `Prowl.Editor/Core/EditorEvents.cs` | Editor-specific events (play mode, assembly reload). |
| `Prowl.EventSystem.Generators/EventDomainGenerator.cs` | Roslyn incremental source generator. |
| `Prowl.Runtime.Test/EventSystemTests.cs` | Unit tests for core EventManager/Event/Container behavior. |
| `Prowl.Runtime.Test/EventDomainGeneratorTests.cs` | Unit tests for source-generated event domains. |
