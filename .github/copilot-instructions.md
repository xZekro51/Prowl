# Copilot Instructions — Prowl Engine

## Project Overview

Prowl is a **Unity-like game engine** written in C# targeting **.NET 9**. The solution is organized into these key projects:

| Project | Role |
|---------|------|
| `Prowl.Runtime` | Core engine library — ECS-like GameObject/MonoBehaviour, rendering, physics, audio, input, serialization, event system |
| `Prowl.Editor` | Editor application (Dear ImGui-based) — scene/game panels, inspector, project browser, build pipeline |
| `Prowl.EventSystem.Generators` | Roslyn source generator for the `[EventDomain]` event system (targets .NET Standard 2.0) |
| `Prowl.Runtime.Test` | xUnit tests for runtime code |
| `Prowl.Editor.Tests` | xUnit tests for editor code |
| `Prowl.Benchmarks` | BenchmarkDotNet performance benchmarks |
| `Samples/*` | Standalone sample games demonstrating engine features |

The runtime has **implicit usings disabled** and **nullable reference types enabled**. Editor has implicit usings enabled.

---

## Code Style & Naming Conventions

The project enforces conventions via `.editorconfig`. Follow them:

- **File-scoped namespaces** (`namespace Foo;`) — enforced as warning.
- **Braces** on new lines (Allman style) for all constructs.
- **4-space indentation**, no tabs.
- **`var`** usage is discouraged — prefer explicit types.
- **Private fields**: `_camelCase` (underscore prefix).
- **Static private fields**: `s_camelCase` (s-underscore prefix).
- **Public fields & properties**: `PascalCase`.
- **Constants**: `PascalCase`.
- Avoid `this.` qualification unless necessary.
- Use keyword types (`int`, `string`) over BCL names (`Int32`, `String`).
- Modifier order: `public, private, protected, internal, static, extern, new, virtual, abstract, sealed, override, readonly, unsafe, volatile, async`.
- `using` directives go outside the namespace, system directives first.

---

## Architecture & Key Types

### EngineObject

`EngineObject` is the base class for all engine-managed objects. It provides:
- `InstanceID` — unique per-instance integer identifier.
- `AssetID` / `AssetPath` — for asset-database-backed objects.
- `IsDisposed` — disposal tracking; check with extension methods `obj.IsValid()` / `obj.IsNotValid()`.
- `Dispose()` → calls virtual `OnDispose()`.

**Always** null-check engine objects with `.IsValid()` / `.IsNotValid()` instead of `== null`, since disposed objects are logically dead but may not be null.

### GameObject & MonoBehaviour

Prowl follows a **Unity-like component model**:

- `GameObject` holds a `Transform`, a parent/child hierarchy, and a list of `MonoBehaviour` components.
- `MonoBehaviour` provides virtual lifecycle methods: `OnAddedToScene`, `OnRemovedFromScene`, `OnEnable`, `OnDisable`, `Start`, `Update`, `FixedUpdate`, `LateUpdate`, `DrawGizmos`, `OnGui`.
- Components are queried via `GetComponent<T>()`, `GetComponentInChildren<T>()`, `GetComponentInParent<T>()`, etc.
- Use `[RequireComponent(typeof(...))]` to declare component dependencies.
- Use `[ExecutionOrder(N)]` to control the order of lifecycle calls.
- Use `[ExecuteInEditMode]` if a component must run in the editor's edit mode.

### Scene & SceneManager

- `Scene` holds a flat list of root `GameObjects`. Add/remove via `scene.Add(go)` / `scene.Remove(go)`.
- `Scene.Load(scene)` replaces the current scene. `Scene.Current` is the active scene.
- `SceneManager` supports **additive scene loading** for multi-scene workflows: `SceneManager.LoadSceneAdditive(scene)`, `SceneManager.UnloadScene(scene)`.
- `SceneManagerEvents` fires `OnSceneLoaded` / `OnSceneUnloaded`.

### EngineContext

`EngineContext` centralizes mutable runtime state that used to live in scattered statics (`Scene.Current`, `Time.TimeStack`, `AssetDatabase.Current`). The existing static accessors delegate to `EngineContext.Current`. For advanced scenarios (headless servers, multi-scene tests), create and swap additional `EngineContext` instances.

Key properties: `ActiveScene`, `IsPlayMode`, `SimulatePhysics`, `TimeStack`, `AssetDatabase`.

### Time

`Time` exposes frame-timing data via a stack of `TimeData` objects:
- `Time.DeltaTime`, `Time.UnscaledDeltaTime`, `Time.FixedDeltaTime`.
- `Time.FrameCount`, `Time.TimeSinceStartup`, `Time.TimeScale`.
- `Time.SmoothDeltaTime` for display-driven code.

### Serialization (Prowl.Echo)

Prowl uses the **Prowl.Echo** library (`Prowl.Echo` NuGet) for serialization. Key points:
- `EchoObject` is the in-memory representation (like a JSON DOM).
- Mark fields for serialization with `[SerializeField]` (include in serialization even if private).
- Exclude fields with `[SerializeIgnore]`.
- Public fields are serialized by default unless marked `[NonSerialized]`.
- Hide from inspector without affecting serialization: `[HideInInspector]`.
- Types that need custom serialization implement `ISerializable` (with `Serialize`/`Deserialize` methods taking `EchoObject`).
- Types that need pre/post callbacks implement `ISerializationCallbackReceiver` (`OnBeforeSerialize`, `OnAfterDeserialize`).
- Asset references use `AssetDatabase.ConfigureContext(ctx)` which stores/resolves `$assetId` tags.

### ScriptableObject

`ScriptableObject` is a data container saved as a `.asset` file. Create via `ScriptableObject.CreateInstance<T>()`. Use `[CreateAssetMenu(...)]` to expose it in the editor's Create menu.

---

## Rendering

### Graphics Backend (Graphite)

Prowl abstracts GPU rendering through the **Graphite** layer (`Prowl.Runtime/Graphite/`):

- `GraphiteDevice` — abstract base for GPU device operations. Concrete backends: `GLGraphiteDevice` (OpenGL), `VKGraphiteDevice` (Vulkan).
- `CommandList` — records GPU commands (`Begin` → draw calls → `End`). Must `BeginRenderPass`/`EndRenderPass` around draw calls.
- GPU resources (`Buffer`, `Texture`, `PipelineState`, `ShaderModule`, `BindGroup`, `Sampler`, `Fence`) all extend `GraphiteResource` and **must be explicitly disposed**. A finalizer warns if disposal is missed.
- The active device is accessed via `Graphics.Graphite`.
- `Graphics.IsOpenGL` checks the active backend. Guard legacy GL code behind this check.

### Render Pipeline

- `RenderPipeline` is the abstract base. `DefaultRenderPipeline` implements a deferred pipeline (GBuffer → lighting → transparents → post-process).
- `Camera` components drive rendering. Each camera can have a per-camera `Pipeline` or `PipelineAsset` override, otherwise the global `RenderPipeline.ActivePipelineAsset` is used.
- Image effects extend `ImageEffect` and declare their `Stage` (`BeforeGBuffer`, `AfterGBuffer`, `DuringLighting`, `AfterLighting`, `PostProcess`).
- Renderable objects implement `IRenderable`; lights implement `IRenderableLight`.
- Shader properties are managed through `PropertyState` (dictionaries of typed uniforms).

### Editor vs Game Rendering

In the editor, `GraphicsContext.Game` drives scene/game rendering through the project's chosen backend, while `GraphicsContext.Editor` uses OpenGL for the ImGui chrome. Standalone games stay in `GraphicsContext.Game`.

---

## Physics

Physics is powered by **Jitter2** (`Jitter2` NuGet):

- `PhysicsWorld` wraps `Jitter2.World` and provides raycasting, shape-cast, and collision queries.
- `Rigidbody3D` is the MonoBehaviour for dynamic/static physics bodies.
- `Collider` subclasses (`BoxCollider`, `SphereCollider`, `CapsuleCollider`, etc.) define collision shapes.
- Physics events (`PhysicsEvents.OnPrePhysicsStep`, `OnPostPhysicsStep`) fire around each physics step.
- Fixed timestep is `Time.FixedDeltaTime` (default 1/60 s). `MonoBehaviour.FixedUpdate()` runs in this loop.

---

## Input

`Input` provides both low-level queries and an action-based system:

- Low-level: `Input.GetKey(KeyCode)`, `Input.GetMouseButton(int)`, `Input.MouseDelta`, etc.
- Action-based: `InputActionMap` → `InputAction` → `InputBinding`. Supports keyboard, mouse, and gamepad composites.
- Input handlers are stacked (`Input.PushHandler` / `Input.PopHandler`). `DefaultInputHandler` bridges Silk.NET window input.

---

## Audio

Audio uses **MiniAudio** via native interop (`Prowl.Runtime/Audio/`):

- `AudioContext.Initialize(...)` sets up the audio device.
- `AudioSource` (MonoBehaviour) plays clips; `AudioListener` captures spatial audio.
- Audio effects (`IAudioEffect`) can be chained on sources.

---

## Profiling

The hierarchical CPU profiler (`Prowl.Runtime/Profiling/Profiler.cs`) is compiled away in Release builds (guarded by `PROWL_PROFILING` define):

```csharp
using (Profiler.Section("MySection"))
{
    // timed code
}
```

Register sections with `Profiler.RegisterSection(name, category, description)` for rich display in the profiler panel.

---

## Logging

Use `Debug.Log`, `Debug.LogWarning`, `Debug.LogError` for all logging. These fire `DebugEvents.OnLog` so the editor console and other listeners receive messages automatically. Do not use `Console.WriteLine`.

---

## Editor

### Project Structure

- Editor panels live in `Prowl.Editor/Panels/` (ImGui windows).
- Editor services are abstracted via interfaces in `Prowl.Editor/Services/Interfaces/` with concrete implementations alongside.
- `EditorApplication` extends `Game` and is the main entry point.
- `EditorPlayMode` manages play/pause/stop lifecycle.
- `EditorEvents` fires `OnPlayModeStateChanged`, `OnAssemblyChanged`, `OnErrorLogged`.

### Script Compilation

The editor compiles user scripts at runtime via `ProjectAssemblyManager` and `ProjectScriptCompiler`. The event-system source generator is shipped as an analyzer DLL alongside the editor output.

---

## Testing

- Use **xUnit** for all tests.
- Runtime tests go in `Prowl.Runtime.Test`; editor tests in `Prowl.Editor.Tests`.
- Test classes should implement `IDisposable` and clean up any `EngineObject`, `Scene`, or `EventManager` instances in `Dispose()`.
- Prefer focused unit tests. Create helper factories (`CreateScene()`, `CreateManager()`) to keep tests concise.

---

## Event System

Prowl uses a **source-generated, strongly-typed event system** located in `Prowl.Runtime/EventSystem/`. All cross-system communication should go through this event system rather than direct C# events, static callbacks, or ad-hoc delegate fields.

### Declaring new events

1. **Add events to an existing domain** when the event logically belongs there (e.g. rendering → `RenderingEvents`, physics → `PhysicsEvents`, game loop → `GameLoopEvents`). Only create a new `[EventDomain]` class when no existing domain fits.

2. Every event domain class must be `static partial` (for global/engine events) or `partial` (for per-instance events) and annotated with `[EventDomain]`.

3. Each event is a `private static readonly EventKey` field whose name starts with `_` (the generator strips the underscore to produce the public name):
   ```csharp
   [EventDomain(Global = true)]
   public static partial class MyEvents
   {
       [EventArgs(typeof(MyPayload))]
       private static readonly EventKey _OnSomething = new();
   }
   ```

4. **Always** annotate every `EventKey` field with `[EventArgs(typeof(...))]`. Use `typeof(Unit)` for parameterless events. Omitting the attribute disables the compile-time type-safety contract and will produce a runtime warning in DEBUG builds.

5. Argument types should be **`readonly record struct`** with descriptive parameter names. Keep them immutable and small. Define them in the same file as their event domain, directly below the domain class.

6. Set `Global = true` on `[EventDomain]` only when the events must be broadcastable across all managers via `GlobalInvokeOnXxx`. Engine-wide lifecycle events (game loop, rendering, physics) are global; component-level or editor-only events typically are not.

### Static vs instance event domains

Choose between **static** and **instance** event domains based on who owns and fires the events:

| Criteria | Static domain | Instance domain |
|----------|--------------|-----------------|
| Owner is a singleton / global system | ✅ `static partial class` | ❌ |
| Owner is a non-static class / service | ❌ | ✅ `partial class` |
| Multiple instances may coexist | ❌ | ✅ |
| Events scoped to object lifetime | ❌ | ✅ |

**Static domains** — use `static partial class` with `[EventDomain(Global = true)]` for engine-wide singletons (window lifecycle, game loop, physics, rendering). All methods are static:
```csharp
// Static domain: one shared manager, static subscribe/invoke
WindowEvents.SubscribeOnLoad(handler);
WindowEvents.InvokeOnLoad();
```

**Instance domains** — use `partial class` (not static) with `[EventDomain]` for per-object or per-service events. The generator produces a per-instance `EventManager` and instance methods. Expose the domain instance as a property on the owning class or interface so subscribers can reach it:
```csharp
// Instance domain class
[EventDomain]
public partial class SceneServiceEvents
{
    [EventArgs(typeof(DirtyStateChangedArgs))]
    private static readonly EventKey _OnDirtyStateChanged = new();
}

// Owning interface exposes it
public interface ISceneService
{
    SceneServiceEvents Events { get; }
}

// Subscribers access via the instance
var svc = EditorServices.Get<ISceneService>();
svc.Events.SubscribeOnDirtyStateChanged(args => { /* ... */ });

// Implementation invokes via the instance
Events.InvokeOnDirtyStateChanged(new DirtyStateChangedArgs(true));
```

**Dispose** instance event managers when the owner is disposed or no longer needed (`Events.Manager.Dispose()`) to unregister from the global instance list and prevent leaks.

### Subscribing to events

- **Preferred (`+=` syntax)** — concise, fine for fire-and-forget scenarios:
  ```csharp
  GameLoopEvents.OnFrameBegin += args => { /* ... */ };
  ```

- **`SubscribeOnXxx` methods** — use these when you need:
  - A **priority** value (lower runs first, default 0).
  - A **disposable handle** for deterministic unsubscription (`using` or manual `.Dispose()`).
  ```csharp
  _sub = GameLoopEvents.SubscribeOnFrameBegin(OnFrame, priority: -10);
  // later: _sub.Dispose();
  ```

- **Lifecycle-aware subscriptions** — when the subscriber is an `EngineObject` (e.g. `MonoBehaviour`), prefer the `AddNewDelegate` overload that takes an owner. This auto-unsubscribes when the object is disposed, preventing leaked handlers from destroyed objects:
  ```csharp
  Manager.AddNewDelegate(this, eventType, handler, priority);
  ```
  The engine's `LifecycleEventDelegateContainer` checks `_owner.IsDisposed` on every invoke and removes itself automatically.

- **Async handlers** — use `SubscribeOnXxxAsync` or `+= Func<Task>` when the handler must `await`. Fire via `InvokeOnXxxAsync` to properly await all async handlers.

### Invoking events

- Use the generated convenience methods — **never** call `EventManager.InvokeEvent` directly unless you are writing framework-level code inside `EventManager` itself:
  ```csharp
  GameLoopEvents.InvokeOnFrameBegin(new FrameBeginArgs(frameCount, dt));
  PhysicsEvents.InvokeOnPrePhysicsStep(new PhysicsStepArgs(deltaTime));
  ```

- For global broadcast across all managers of a domain:
  ```csharp
  GameLoopEvents.GlobalInvokeOnFrameBegin(args);
  ```

### Unsubscribing

- **`-=` operator** removes the first matching delegate:
  ```csharp
  GameLoopEvents.OnFrameBegin -= myHandler;
  ```

- **`IDisposable` pattern** — `SubscribeOnXxx` returns a container that implements `IDisposable`. Store it and call `.Dispose()` when done. This is the safest approach and prevents accidental leaks.

- **Lifecycle containers** auto-unsubscribe when the owning `EngineObject` is disposed. Prefer these in `MonoBehaviour` code.

### Event cancellation

If an event should support stopping propagation, make the args type implement `ICancellable`:
```csharp
public struct MyArgs : ICancellable
{
    public bool Cancelled { get; set; }
    public int Value;
}
```
Handlers are invoked in priority order; once `Cancelled` is set to `true`, remaining handlers are skipped.

### Priority ordering

Handlers execute in **ascending priority** (lower values first). Use negative priorities for handlers that must run early (e.g. input capture before gameplay). Within the same priority, order is insertion-based.

### Batching

When adding or removing many handlers at once, wrap mutations in `BeginBatch()` / `EndBatch()` to defer the internal copy-on-write snapshot rebuild to a single pass:
```csharp
Manager.BeginBatch();
try { /* add/remove many handlers */ }
finally { Manager.EndBatch(); }
```
### Existing event domains (reference)

| Domain | File | Static | Global | Purpose |
|--------|------|--------|--------|---------|
| `GameLoopEvents` | `EventSystem/GameLoopEvents.cs` | ✅ | ✅ | Frame lifecycle (init, begin, end, render, close) |
| `WindowEvents` | `EventSystem/WindowEvents.cs` | ✅ | ✅ | Platform window lifecycle (load, render, resize, close, file drop) |
| `RenderingEvents` | `EventSystem/RenderingEvents.cs` | ✅ | ✅ | Render pipeline phases, per-camera stage events (GBuffer, lighting, composition, transparent, stats) |
| `PhysicsEvents` | `EventSystem/PhysicsEvents.cs` | ✅ | ✅ | Physics step begin/end |
| `AssetEvents` | `EventSystem/AssetEvents.cs` | ✅ | ✅ | Asset refresh/import/delete (editor) |
| `GraphiteDeviceEvents` | `EventSystem/GraphiteDeviceEvents.cs` | ✅ | ✅ | GPU device lifecycle, swapchain, frame boundaries, upload windows, validation |
| `BaseEvents` | `EventSystem/BaseEvents.cs` | ✅ | ❌ | Update/LateUpdate before/after |
| `SceneManagerEvents` | `EventSystem/SceneManagerEvents.cs` | ✅ | ❌ | Scene load/unload |
| `DpiEvents` | `EventSystem/DpiEvents.cs` | ✅ | ❌ | DPI scale changes |
| `DebugEvents` | `EventSystem/DebugEvents.cs` | ✅ | ❌ | Log messages |
| `EditorEvents` | `Editor/Core/EditorEvents.cs` | ✅ | ❌ | Play mode, assembly reload |
| `SceneServiceEvents` | `Editor/Services/Events/SceneServiceEvents.cs` | ❌ | ❌ | Per-service: scene dirty state, scene loaded |
| `SelectionServiceEvents` | `Editor/Services/Events/SelectionServiceEvents.cs` | ❌ | ❌ | Per-service: selection changed |
| `PrefabEditModeEvents` | `Editor/Prefabs/PrefabEditModeEvents.cs` | ❌ | ❌ | Per-instance: prefab edit mode entered/exited |

### Anti-patterns to avoid

- ❌ **Do not** use raw C# `event` keyword or `Action`/`Func` delegate fields for cross-system communication. Use the event system instead.
- ❌ **Do not** invoke events by calling `EventManager.InvokeEvent<TArgs>` directly — use the generated `InvokeOnXxx` methods.
- ❌ **Do not** forget `[EventArgs(...)]` on `EventKey` fields — this disables the type-safety contract.
- ❌ **Do not** subscribe without a plan for unsubscription. Either use a lifecycle-aware container, store the `IDisposable` handle, or use `-=`.
- ❌ **Do not** create new event domains for a single event that fits an existing domain. Group related events together.
- ❌ **Do not** make event args mutable classes — use `readonly record struct` or `readonly struct`.

---

## General Anti-Patterns

- ❌ **Do not** use `Console.WriteLine` — use `Debug.Log` / `Debug.LogWarning` / `Debug.LogError`.
- ❌ **Do not** check `== null` on `EngineObject` — use `.IsValid()` / `.IsNotValid()`.
- ❌ **Do not** forget to dispose GPU resources (`GraphiteResource` subclasses). Leaked resources trigger finalizer warnings.
- ❌ **Do not** access `Graphics.GL` without checking `Graphics.IsOpenGL` first — the Vulkan backend will throw.
- ❌ **Do not** put editor-only code in `Prowl.Runtime`. Editor code belongs in `Prowl.Editor`.
- ❌ **Do not** add implicit usings in `Prowl.Runtime` — they are disabled. Always write explicit `using` directives.
- ❌ **Do not** mark members with `[Obsolete]` pointing to old APIs — the migration is toward the event system's generated methods and `EngineContext`-based accessors.
