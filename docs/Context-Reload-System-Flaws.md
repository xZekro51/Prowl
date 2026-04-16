# Context Reload System — Flaw Analysis & Fix Proposals

> **Scope**: `Prowl.Editor/Scripting/*`, `Prowl.Runtime/Hotload/*`, and their integration
> points in `EditorApplication`, `Scene`, and `GameObject`.
>
> **Goal**: Catalogue every structural flaw in the current hot-reload pipeline, explain
> *why* each is a problem, and propose concrete, incremental fixes — culminating in a
> dual-ALC overlay architecture that eliminates the most painful class of bugs.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Current Flow (As-Is)](#2-current-flow-as-is)
3. [The Core Problem: Reference Pinning & ALC Collection](#3-the-core-problem-reference-pinning--alc-collection)
4. [Dual-ALC Overlay Strategy (Proposed Architecture)](#4-dual-alc-overlay-strategy-proposed-architecture)
5. [Flaw Catalogue](#5-flaw-catalogue)
6. [Priority Matrix](#6-priority-matrix)
7. [Implementation Roadmap](#7-implementation-roadmap)

---

## 1. Executive Summary

Prowl's hot-reload pipeline works — scripts recompile and changes appear — but it does so
through a *serialize-the-entire-world* strategy that is fragile, slow, and architecturally
far more complex than necessary. The root cause of nearly every flaw traces back to a
single design constraint:

> **The engine holds direct `List<MonoBehaviour>` references from `GameObject` (default ALC)
> to user-script instances (script ALC). A single ALC must be fully unreferenced before it
> can be collected, so the pipeline serializes everything, destroys all components, unloads
> the ALC, loads a new one, and deserializes everything back.**

This document proposes a **dual-ALC overlay strategy** as the primary architectural fix:
load the new assembly into a *second* ALC while the old one is still live, copy state
directly between old and new component instances, swap references in-place, then release
the old ALC for eventual collection. This eliminates the serialization round-trip, decouples
ALC collection from correctness, and dramatically simplifies the pipeline.

The remaining flaws (heuristic classifiers, registry stampedes, event leaks, watcher races)
are addressed as independent, incremental improvements that compose cleanly with the
dual-ALC core.

---

## 2. Current Flow (As-Is)

`HotloadPipeline.PerformFullReload()` executes roughly 14 synchronous steps:

```
1.  Raise OnBeforeAssemblyReload
2.  Invoke [OnCodeCleanup] methods
3.  Serialize entire scene -> EchoObject tree (Prowl.Echo)
4.  Destroy all GameObjects (releases MonoBehaviour references)
5.  Unload script ALC
6.  Force GC.Collect x 3, wait for pending finalizers
7.  Compile user project (dotnet build)
8.  Load new ALC + assembly
9.  Reinitialize 13+ type registries (full assembly scan each)
10. Deserialize EchoObject tree -> rebuild scene
11. Invoke [OnCodeInitializing] methods
12. Invoke [OnHotloaded] methods
13. Raise OnAfterAssemblyReload
14. Rebuild all editor state
```

**Wall-clock cost**: Even a trivial one-line change serializes every component on every
GameObject, triggers full GC, recompiles, deserializes back, and reinitializes every
registry. On a scene with a few hundred objects this is perceptible; on a real game scene
it's seconds of frozen editor.

---

## 3. The Core Problem: Reference Pinning & ALC Collection

### Why references "stick"

.NET's collectible `AssemblyLoadContext` can only be garbage-collected when **zero**
references to its types exist from outside the ALC. In Prowl:

```
Scene (default ALC)
  +-- GameObject (default ALC)
        +-- _components: List<MonoBehaviour>   <-- engine type
              +-- [0]: PlayerController         <-- user-script ALC type!
              +-- [1]: HealthSystem             <-- user-script ALC type!
```

`GameObject._components` is `internal List<MonoBehaviour>`. `MonoBehaviour` itself lives in
`Prowl.Runtime` (default ALC), but the *concrete instances* (`PlayerController`,
`HealthSystem`) are types from the script ALC. As long as any `GameObject` anywhere holds
a reference to any user-script instance, the old ALC **cannot be collected**.

The current pipeline "solves" this by nuking everything: serialize the world, destroy all
GameObjects, null all references, force GC, pray the ALC collects, then rebuild. This is
the root cause of:

- **Flaw 1**: The serialize-everything round-trip.
- **Flaw 3**: ALC collection fragility (one leaked delegate = permanent leak).
- **Flaw 8**: Undo history destruction (serialized snapshots hold old-ALC types).
- **Performance**: Multiple full GC pauses on the main thread.

### Why a single ALC forces this

With one ALC, you must fully sever *all* references before loading the replacement. There's
no way to incrementally swap components — the old ALC and new ALC are the same slot. You
can't have both loaded simultaneously, which means you can't do a live hand-off.

**The solution: use two ALCs.**

---

## 4. Dual-ALC Overlay Strategy (Proposed Architecture)

### 4.1 Core Idea

Instead of one ALC that must be fully released before replacement, maintain **two ALC
slots** (A and B). On reload:

```
State before reload:
  ALC-A: loaded (current scripts)
  ALC-B: empty

Reload sequence:
  1. Compile new assembly
  2. Load into ALC-B (ALC-A still alive)
  3. For each GameObject with user-script components:
     a. Create new component instance from ALC-B type
     b. Copy fields directly from old (ALC-A) instance -> new (ALC-B) instance
     c. Swap in GameObject._components list
     d. Run lifecycle (OnEnable, Start, etc.)
  4. Clear all remaining ALC-A references
  5. Request ALC-A unload (non-blocking)
  6. Swap labels: ALC-B becomes the "current" slot, ALC-A slot becomes "pending unload"

State after reload:
  ALC-A: pending collection (no references if swap was clean)
  ALC-B: loaded (current scripts)

Next reload uses ALC-A slot again -> cycle continues
```

### 4.2 Key Benefits

| Benefit | Detail |
|---|---|
| **No serialization round-trip** | Fields are copied directly between live objects. No `EchoObject` tree, no `Serializer.Serialize`, no `Serializer.Deserialize`. |
| **Scene stays loaded** | GameObjects are never destroyed. Transform hierarchies, parent/child relationships, physics bodies — all preserved in-place. |
| **ALC collection is non-blocking** | If ALC-A fails to collect (leaked delegate, static cache), it's a memory leak — not a correctness failure. The editor keeps working. |
| **Incremental migration** | Only components whose types actually changed need new instances. Unchanged components can keep their existing ALC-A instance (or still be swapped for uniformity). |
| **Undo history survives** | No scene teardown means undo snapshots remain valid for the current session. |
| **Simpler pipeline** | Eliminates steps 3, 4, 5, 6, 10 from the current 14-step flow. |

### 4.3 Field Copy Mechanics

Direct field-to-field copy works for:

| Field Type | Copy Strategy |
|---|---|
| Primitives (`int`, `float`, `bool`, `string`) | Direct assignment |
| Engine types (`Float3`, `Color`, `Quaternion`, `Mesh`, `Material`) | Direct assignment (same types across ALCs) |
| `AssetRef<T>` | Direct assignment (T is always an engine type) |
| `List<T>`, `T[]` where T is engine/primitive | Direct copy |
| `enum` from user scripts | Convert by name or underlying value |

Requires serialization fallback for:

| Field Type | Why |
|---|---|
| User-defined classes/structs (from script ALC) | Different `Type` identity across ALCs — can't assign directly |
| `List<UserType>`, `Dictionary<K, UserType>` | Generic closed over script-ALC type |
| Delegates referencing script-ALC methods | Different `MethodInfo` identity |

For fallback cases, use Prowl.Echo as a bridge: serialize the field value from old ALC
type, then deserialize into new ALC type. This is *per-field*, not per-scene — orders of
magnitude smaller than the current whole-world serialization.

### 4.4 Type Resolution Across ALCs

Both ALCs load the same assembly (different versions). Type matching is by **full name**:

```csharp
// Given old instance from ALC-A:
Type oldType = oldComponent.GetType(); // "MyGame.PlayerController" in ALC-A

// Find equivalent in ALC-B:
Type newType = newAssembly.GetType(oldType.FullName);
```

If a type was renamed or removed, the engine falls back to `MissingMonoBehaviour` — same
as today, but without losing the entire scene's state in the process.

### 4.5 Lifecycle Integration

```csharp
// Pseudocode for component swap:
foreach (var go in scene.AllGameObjects())
{
    for (int i = 0; i < go._components.Count; i++)
    {
        var old = go._components[i];
        if (!IsFromScriptALC(old)) continue; // skip engine components

        var newType = ResolveInNewALC(old.GetType());
        if (newType == null)
        {
            go._components[i] = CreateMissing(old); // MissingMonoBehaviour
            continue;
        }

        var fresh = (MonoBehaviour)Activator.CreateInstance(newType);
        CopyFields(source: old, target: fresh); // direct + fallback

        old.OnDisable();
        old.OnRemovedFromScene();

        go._components[i] = fresh;
        fresh.GameObject = go;
        fresh.OnAddedToScene();
        fresh.OnEnable();
    }
}
```

### 4.6 Handling ALC Collection Failure

With dual ALCs, a stale ALC that won't collect is **not fatal**:

```
Reload 1: ALC-A loaded -> ALC-B loaded, ALC-A pending unload
Reload 2: ALC-A should be collected by now
  |-- If collected: reuse ALC-A slot, load into it
  +-- If NOT collected: log warning, allocate ALC-C, continue
       (ALC-A will eventually collect when the leaked reference dies,
        or it's a small memory leak — not a frozen editor)
```

The engine can track "generations" — if more than N stale ALCs accumulate, surface a
diagnostic warning pointing at the likely leak source (using the existing
`ScriptAssemblyLoadContext.GetDiagnostics()` / `TrackObject` infrastructure that is
currently unused).

### 4.7 Integration With Existing Infrastructure

The dual-ALC strategy **reuses** existing code:

- **`InstanceMigrationEngine`** already has `CaptureSceneState()` / `RestoreSceneState()`
  with field-level snapshot logic. Adapt its field-walking code for direct cross-ALC copy.
- **`ChangeClassifier`** still useful for determining *which* types changed (skip
  unchanged components for faster reload).
- **`HotloadAttributes`** (`[OnCodeCleanup]`, `[OnHotloaded]`, `[PreserveOnHotload]`) all
  apply naturally — invoke on old instances before swap, on new instances after.
- **`IHotloadUpgrader`** — custom migration logic runs during `CopyFields` phase, handling
  renamed/restructured fields.
- **Type registries** still reinitialize (they scan the new assembly), but the scene
  doesn't need to be torn down and rebuilt around them.

---

## 5. Flaw Catalogue

### Flaw 1 — Serialize-Everything Round-Trip

**Location**: `HotloadPipeline.PerformFullReload()` steps 3 + 10

**Problem**: Every reload serializes the *entire scene* into an `EchoObject` tree via
`Prowl.Echo`, destroys all GameObjects, then deserializes everything back. This is:

- **Slow**: Serialization visits every field on every component on every GameObject.
  Complex scenes with large meshes, materials, and nested prefab instances create massive
  `EchoObject` trees.
- **Lossy**: Runtime-only state (coroutine progress, accumulated timers, physics warm-up,
  particle positions) is destroyed even if `[PreserveOnHotload]` fields survive.
- **Fragile**: Any serialization bug (circular reference, missing type converter, version
  mismatch) corrupts the *entire scene*, not just the affected component.

**Root Cause**: The single-ALC design forces total reference severance before loading the
replacement. Serialization is the only way to preserve state across the gap.

**Fix (Dual-ALC)**: With the overlay strategy (section 4), fields are copied directly between live
objects. Prowl.Echo is only invoked as a *fallback* for fields whose types exist in the
script ALC (user-defined classes/structs). This reduces serialization volume by 90%+ in
typical projects where most fields are primitives, engine types, or `AssetRef<T>`.

**Fix (Incremental, without dual-ALC)**: Even within the current single-ALC design,
`InstanceMigrationEngine.CaptureSceneState()` already exists and captures per-component
snapshots instead of whole-scene serialization. Wire it into the main reload path:
serialize only components with script-ALC types, leave engine-only GameObjects untouched.

---

### Flaw 2 — Dual Update Pipeline in ScriptAssemblyManager

**Location**: `ScriptAssemblyManager.Update()` + `ScriptAssemblyManager.InternalUpdate()`

**Problem**: `ScriptAssemblyManager` runs its own polling loop that watches for compilation
results, manages state transitions, and triggers reloads — duplicating orchestration that
`HotloadPipeline` also performs. Both classes have `Update()` methods called from
`EditorApplication.Update()`. Control flow ping-pongs between them:

```
EditorApplication.Update()
  -> HotloadPipeline.Update()       // state machine
  -> ScriptAssemblyManager.Update() // also a state machine
    -> ScriptAssemblyManager.InternalUpdate() // yet another layer
```

This makes the reload sequence hard to reason about and creates subtle ordering bugs (e.g.,
`HotloadPipeline` assumes assembly is loaded, but `ScriptAssemblyManager` hasn't finished
its internal state transition yet).

**Fix**: Collapse `ScriptAssemblyManager` into a pure **loader service** with synchronous
methods:

```csharp
public static class ScriptAssemblyManager
{
    public static Assembly LoadAssembly(string path);
    public static void UnloadCurrentALC();
    public static IReadOnlyList<Type> GetAllTypes();
    // No Update(), no state machine, no polling
}
```

All orchestration lives in `HotloadPipeline` alone. Compilation is delegated to
`ScriptCompiler` (which already exists). The pipeline calls the loader at the right point
in its state machine — no dual-loop confusion.

---

### Flaw 3 — ALC Collection Fragility

**Location**: `ScriptAssemblyManager.UnloadAssemblies()`, `ScriptAssemblyLoadContext`

**Problem**: After unloading the ALC, the pipeline calls `GC.Collect()` three times and
`GC.WaitForPendingFinalizers()`, hoping the ALC's reference count drops to zero. If *any*
reference survives — a static cache, an event handler delegate, a closure captured in a
`Task`, a LINQ lambda — the ALC is **permanently leaked**. The current code has no
detection or recovery mechanism.

Known leak vectors:

| Vector | How It Pins |
|---|---|
| Static fields in user scripts | Direct reference from ALC root |
| Event subscribers (`+=`) not cleaned up | Delegate holds `Target` reference to ALC instance |
| Closures / lambdas captured by engine callbacks | Captured variables hold ALC type references |
| `Task.ContinueWith` / `async void` | Continuation chain holds ALC method references |
| Reflection caches (`Type` objects) | `Type` keeps its ALC alive |

**Root Cause**: The single-ALC model means collection failure = total failure. There's no
fallback.

**Fix (Dual-ALC)**: This is the flagship benefit of the overlay strategy. With dual ALCs:

1. Collection failure is **non-blocking** — the new ALC is already loaded and working.
2. Leaked ALCs are tracked as "stale generations" with diagnostic reporting.
3. The engine can continue functioning indefinitely even if old ALCs never collect.
4. `[AutoStaticsCleanup]` and `[OnCodeCleanup]` still help reduce leaks, but they're
   no longer critical-path requirements.

**Fix (Incremental)**: Add a `WeakReference`-based probe after unload:

```csharp
var probe = new WeakReference(currentALC);
currentALC.Unload();
currentALC = null;

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

if (probe.IsAlive)
{
    Debug.LogWarning("[HotloadPipeline] Old ALC was not collected. " +
        "A reference leak is preventing assembly unload. " +
        "Check static fields, event subscribers, and async continuations.");
    // Use ScriptAssemblyLoadContext.GetDiagnostics() to report tracked objects
}
```

---

### Flaw 4 — Heuristic Structural Hash Is Unreliable

**Location**: `ChangeClassifier.ComputeStructuralHash()`

**Problem**: The structural hash uses a brace-depth parser to extract "type signatures"
from raw C# source text. This is not a parser — it's a regex-grade heuristic that fails
on:

- String literals containing braces: `var s = "{ }";`
- Verbatim strings, raw strings, interpolated strings with braces
- Comments containing type declarations
- `#if` / `#endif` conditional compilation
- Partial classes across multiple files
- Nested types, generic constraints, records, primary constructors
- Attributes on type declarations

A false negative (hash unchanged when structure changed) causes the pipeline to skip
migration for a type that needs it, leading to **silent data corruption**. A false positive
(hash changed when only a comment changed) causes unnecessary migration overhead.

**Fix**: Replace the brace-depth heuristic with Roslyn `SyntaxTree` analysis:

```csharp
var tree = CSharpSyntaxTree.ParseText(source);
var root = tree.GetRoot();
foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
{
    var signature = ExtractStructuralSignature(typeDecl);
    // signature = type name + base types + field names/types + method signatures
    hash.Append(signature);
}
```

Roslyn is already a transitive dependency (the compiler is invoked via `dotnet build`), so
adding the syntax API has near-zero cost. The structural signature becomes:
- Type name + kind (class/struct/record)
- Base type + interfaces
- Field names + types + modifiers
- Property names + types
- Method names + parameter types + return type

This is robust against comments, strings, whitespace, and preprocessor directives.

---

### Flaw 5 — ChangeClassifier File-Scoped Namespace Bug

**Location**: `ChangeClassifier.ExtractNamespace()`

**Problem**: The namespace extraction regex expects block-scoped namespaces:

```csharp
namespace Foo.Bar
{
    // ...
}
```

But Prowl's coding standard (per `.github/copilot-instructions.md` section 3.1) uses
**file-scoped namespaces**:

```csharp
namespace Foo.Bar;
```

The regex fails to match the semicolon-terminated form, returning an empty namespace. This
means:
- Content hash keys are wrong (missing namespace prefix)
- Structural hash comparisons across files may collide
- The classifier may report "unchanged" when only the namespace form differs

**Fix**: Update the regex to handle both forms:

```csharp
// Block-scoped: namespace Foo.Bar { ... }
// File-scoped:  namespace Foo.Bar;
private static readonly Regex s_namespaceRegex = new(
    @"namespace\s+([\w.]+)\s*[{;]",
    RegexOptions.Compiled);
```

Or better — if adopting the Roslyn fix from Flaw 4, namespace extraction becomes trivial:

```csharp
var ns = typeDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
string namespaceName = ns?.Name.ToString() ?? "";
```

---

### Flaw 6 — Registry Stampede on Every Reload

**Location**: `EditorApplication.ReinitializeRegistries()`

**Problem**: Every reload calls `Reinitialize()` on **13+ registries**, each performing a
full assembly scan via `ScriptAssemblyManager.GetAllTypes()`:

```
PropertyEditor.Reinitialize()
ComponentEditor.Reinitialize()
ImporterRegistry.Reinitialize()
CreateAssetMenuRegistry.Reinitialize()
AddComponentMenuRegistry.Reinitialize()
InitializeOnLoadRegistry.Reinitialize()
EditorCallbacks.Reinitialize()
ProjectSettingsRegistry.Reinitialize()
EditorWindowRegistry.Reinitialize()
CustomEditorRegistry.Reinitialize()
AssetDatabase type caches
Gizmo registry
... and more
```

Each registry independently iterates all types, checks attributes, and builds lookup
tables. That's 13x full type iteration, 13x attribute reflection, 13x dictionary
rebuilds. On a project with hundreds of user types, this is measurable.

**Fix**: Centralize type scanning into a single pass:

```csharp
public static class TypeScanCache
{
    // Single pass over all types, partitioned by attribute/interface
    public static IReadOnlyList<(Type Type, Attribute Attr)> ComponentEditors { get; }
    public static IReadOnlyList<(Type Type, Attribute Attr)> PropertyEditors { get; }
    public static IReadOnlyList<(Type Type, Attribute Attr)> Importers { get; }
    // ... etc.

    public static void Rebuild()
    {
        // ONE iteration over GetAllTypes()
        // Partition results by attribute type
        // Notify registries via event (source-generated domain events
        // are ideal here — type-safe, zero-alloc, and auto-cleanup)
    }
}
```

Each registry consumes its partition. Total type iteration: 1x instead of 13x.

Additionally, with the dual-ALC strategy, registries that only care about engine types
(unchanged across reloads) can skip reinitialization entirely. Only registries that index
user-script types need updating.

---

### Flaw 7 — Event Subscriber Leaks

**Location**: Throughout — any `+=` on engine events from user-script code

**Problem**: When user scripts subscribe to engine events:

```csharp
// In user MonoBehaviour:
public override void OnEnable()
{
    SomeEngineEvent += MyHandler; // delegate captures 'this' (script ALC type)
}
```

If the script doesn't unsubscribe in `OnDisable()` / `OnRemovedFromScene()`, the delegate
holds a reference to the old-ALC instance, which keeps the entire ALC alive (Flaw 3).

The current mitigation is `[OnCodeCleanup]` methods where users manually unsub, but this is
opt-in and error-prone — forgetting one handler leaks the ALC.

**Fix (Structural)**: Use weak-reference delegates or an event broker pattern:

```csharp
// Engine-side: weak event pattern
public static class WeakEvent
{
    // Wraps subscribers in WeakReference<Action<T>>
    // Automatically prunes dead references on raise
    // No strong reference from engine -> user script
}
```

A source-generated event domain (like the existing Vortex pattern used in `GameEvents` and
`WindowEvents`) naturally supports this — event infrastructure can be made aware of ALC
boundaries and automatically deregister subscribers from stale ALCs during the reload
handshake.

**Fix (Pragmatic)**: During the dual-ALC swap phase, enumerate all engine-side event
delegate invocation lists, find any targets whose type is from the old ALC, and remove
them. This is a safety net, not a replacement for proper lifecycle management:

```csharp
// After component swap, before releasing old ALC references:
EventLeakScanner.CleanDelegatesFromALC(oldALC);
```

---

### Flaw 8 — Undo History Destruction

**Location**: `HotloadPipeline.PerformFullReload()` (implicit — scene is destroyed/rebuilt)

**Problem**: The undo system stores snapshots of scene state. When the reload pipeline
destroys the entire scene and rebuilds it, all undo history becomes invalid — the objects
it references no longer exist (different instances, different identity).

Users lose the ability to undo/redo across a hot reload, which breaks the editor workflow
every time they save a script file.

**Fix (Dual-ALC)**: Since the dual-ALC strategy keeps GameObjects alive and only swaps
component instances, undo history tied to GameObject-level operations (transform changes,
hierarchy modifications, component add/remove of engine types) survives automatically.

For component field changes, the undo system can be made ALC-aware: store field-level
diffs keyed by `(GameObject.Identifier, componentTypeName, fieldName)` rather than object
references. This way undo entries survive across component instance swaps.

---

### Flaw 9 — ScriptFileWatcher Race Conditions

**Location**: `ScriptFileWatcher`

**Problem**: `FileSystemWatcher` raises events on I/O thread pool threads.
`ScriptFileWatcher` coalesces changes with a debounce timer, but:

1. **No thread synchronization**: The `ChangeSet` (presumably a collection) is mutated
   from watcher callbacks (thread pool) and read from the main thread (`Update()`)
   without visible locking or concurrent collection usage.

2. **IDE save patterns**: Many editors (VS, VSCode, Rider) save via
   write-to-temp, rename, delete-original, producing multiple events for a single
   logical save. The debounce timer mitigates this but doesn't guarantee it — a slow
   disk or antivirus pause can spread events beyond the debounce window.

3. **Build artifact feedback**: When `dotnet build` produces outputs in the project
   directory, the watcher may pick up `.dll` / `.pdb` changes and trigger a second
   reload while the first is still in progress.

**Fix**:

```csharp
// 1. Use ConcurrentDictionary for thread safety:
private readonly ConcurrentDictionary<string, DateTime> _pendingChanges = new();

// 2. Filter by extension early (in watcher callback):
private static readonly HashSet<string> s_watchedExtensions = [".cs", ".shader", ".glsl"];

if (!s_watchedExtensions.Contains(Path.GetExtension(e.FullPath)))
    return; // Ignore non-source files immediately

// 3. Exclude build output directories:
if (path.Contains("/bin/") || path.Contains("/obj/"))
    return;

// 4. Increase debounce window and use a "stable" check:
//    Only trigger when no new changes have arrived for N ms
```

---

### Flaw 10 — Unused Infrastructure

**Location**: Multiple files

**Problem**: Several systems were built but never wired into the main reload path:

| Infrastructure | Status | Issue |
|---|---|---|
| `InstanceMigrationEngine.CaptureSceneState()` | Exists | Not called from `HotloadPipeline` |
| `InstanceMigrationEngine.RestoreSceneState()` | Exists | Not called from `HotloadPipeline` |
| `ScriptAssemblyLoadContext.TrackObject()` | Exists | Never called by anything |
| `ScriptAssemblyLoadContext.GetDiagnostics()` | Exists | Never called by anything |
| `ChangeClassifier.Classify()` results | Computed | `isILSafe` parameter in `PerformFullReload` is accepted but ignored |
| `IHotloadUpgrader` interface | Defined | No discovery/invocation in the pipeline |

This represents significant implemented-but-unused work. More importantly, it means the
infrastructure for a better reload already exists in partial form.

**Fix**: Wire it all in:

1. `TrackObject()` / `GetDiagnostics()` -> call during ALC creation, report on collection
   failure (see Flaw 3).
2. `CaptureSceneState()` / `RestoreSceneState()` -> adapt for the dual-ALC field copy
   phase. The field-walking logic is already there.
3. `ChangeClassifier` results -> use to determine which components need instance swaps
   vs. which can be left untouched (optimization for dual-ALC).
4. `IHotloadUpgrader` -> discover implementations in the new assembly, invoke during
   field copy for custom migration logic.

---

## 6. Priority Matrix

| Flaw | Severity | Effort | Fix Approach |
|---|---|---|---|
| **Flaw 1** — Serialize-everything | CRITICAL | Large | Dual-ALC (eliminates it) |
| **Flaw 3** — ALC collection fragility | CRITICAL | Large | Dual-ALC (decouples it) |
| **Flaw 2** — Dual update pipeline | HIGH | Medium | Refactor ScriptAssemblyManager |
| **Flaw 5** — File-scoped namespace bug | HIGH | Tiny | One regex fix |
| **Flaw 7** — Event subscriber leaks | HIGH | Medium | Delegate cleanup + weak events |
| **Flaw 6** — Registry stampede | MEDIUM | Medium | Centralized type scan |
| **Flaw 9** — Watcher race conditions | MEDIUM | Small | Thread-safe collection + filters |
| **Flaw 4** — Heuristic structural hash | MEDIUM | Medium | Roslyn syntax analysis |
| **Flaw 10** — Unused infrastructure | MEDIUM | Small | Wire existing code into pipeline |
| **Flaw 8** — Undo destruction | LOW | Medium | ALC-aware undo (after dual-ALC) |

---

## 7. Implementation Roadmap

### Phase 0 — Quick Wins (less than 1 day each)

- [ ] **Fix file-scoped namespace regex** (Flaw 5) — one line change, immediate
  correctness improvement.
- [ ] **Add ALC collection probe** (Flaw 3 incremental) — `WeakReference` check +
  diagnostic logging using existing `GetDiagnostics()`.
- [ ] **Filter watcher events by extension** (Flaw 9) — prevent build artifact feedback
  loops.
- [ ] **Wire `TrackObject()` into ALC creation** (Flaw 10) — start gathering diagnostic
  data for future leak detection.

### Phase 1 — Pipeline Simplification (1-2 weeks)

- [ ] **Collapse ScriptAssemblyManager** (Flaw 2) — extract loader service, remove
  internal state machine and Update loop. All orchestration in `HotloadPipeline`.
- [ ] **Centralize type scanning** (Flaw 6) — single-pass `TypeScanCache` consumed by
  all registries. Leverage source-generated event notifications for registry refresh.
- [ ] **Wire ChangeClassifier results** (Flaw 10) — use `isILSafe` to skip migration
  for IL-compatible changes (method body edits, comment changes).

### Phase 2 — Dual-ALC Overlay (2-4 weeks)

This is the core architectural change that resolves Flaws 1, 3, and 8 simultaneously.

- [ ] **Implement ALC slot manager** — maintains two ALC slots (A/B) with generation
  tracking and stale-ALC diagnostics.
- [ ] **Implement cross-ALC field copier** — walks fields, direct-copies engine types,
  falls back to Echo serialization for script-ALC types only.
- [ ] **Implement in-place component swap** — iterate `GameObject._components`, create
  new instances from new ALC, copy fields, swap references, run lifecycle hooks.
- [ ] **Wire `IHotloadUpgrader`** (Flaw 10) — discover and invoke during field copy for
  custom migration.
- [ ] **Adapt `InstanceMigrationEngine`** (Flaw 10) — reuse its field-walking code for
  the cross-ALC copier.
- [ ] **Update `HotloadPipeline` state machine** — new states: `LoadingOverlay`,
  `SwappingComponents`, `ReleasingOldALC` replace `Serializing`, `Destroying`,
  `Deserializing`.
- [ ] **Add stale-ALC tracking** — log warnings when old ALCs don't collect, surface
  `GetDiagnostics()` results in editor console.

### Phase 3 — Hardening & Polish (1-2 weeks)

- [ ] **Replace heuristic hash with Roslyn** (Flaw 4) — robust structural signatures.
- [ ] **Implement delegate leak scanner** (Flaw 7) — enumerate engine event invocation
  lists post-swap, remove stale ALC targets.
- [ ] **ALC-aware undo system** (Flaw 8) — field-level diffs keyed by stable identifiers,
  survive across component instance swaps.
- [ ] **Thread-safe watcher** (Flaw 9) — `ConcurrentDictionary`, stable-state debounce.
- [ ] **Comprehensive logging** — `HotloadLogger` with per-phase timing, field copy
  stats, ALC collection status, registry rebuild time.

---

*Document generated from analysis of the Prowl engine codebase. All file references are
relative to the solution root.*
