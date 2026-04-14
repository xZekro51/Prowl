# Prowl Engine — Copilot Instructions

## 1. Project Overview

Prowl is an open-source, MIT-licensed **game engine** written in **pure C#** targeting **.NET 10**. It provides a Unity-like editor and scripting API built on a **GameObject / MonoBehaviour** component architecture. The engine uses **OpenGL** (via Silk.NET), **Jitter Physics 2**, **Prowl.Echo** for serialization, **Prowl.Paper** for immediate-mode UI, and **MiniAudio** for spatial audio.

### Solution Structure

| Project | Role |
|---|---|
| `Prowl.Runtime` | Core engine library (components, rendering, physics, audio, assets, math, serialization). No editor dependency. Must remain **AOT-compatible** (`IsAotCompatible=true`). |
| `Prowl.Editor` | Editor application. References `Prowl.Runtime`. Contains panels, importers, build pipeline, scripting hot-reload, docking UI, gizmos, and project management. |
| `Prowl.Runtime.Test` | Unit tests for the runtime (xUnit). |
| `BenchmarkSuite1` | Performance benchmarks. |
| `Samples/*` | Standalone sample projects demonstrating engine features. Each sample subclasses `Game` and runs headless (no editor). |

### Key Dependencies

- **Silk.NET 2.22** — OpenGL, windowing, input.
- **Jitter2 2.7** — 3D rigid-body physics.
- **Prowl.Echo** — Custom serialization library (`EchoObject`, `Serializer`, `[SerializeField]`, `[SerializeIgnore]`).
- **Prowl.Paper / Prowl.PaperUI** — Immediate-mode UI framework (shared between editor and in-game UI via `WorldCanvas`).
- **Prowl.Quill / Prowl.Scribe** — Vector graphics and text rendering (GPU-accelerated slug text + bitmap fallback).
- **Prowl.Vector** — Math library (`Float2`, `Float3`, `Float4`, `Float4x4`, `Quaternion`, `Color`, `AABB`, `Ray`, `Frustum`).
- **Vortex** — Source-generated event system (`[EventDomain]`, `[EventArgs]`, `EventKey`).
- **Magick.NET** — Image loading (PNG, JPG, HDR, EXR, DDS, etc.).

---

## 2. Architecture & Core Concepts

### 2.1 Object Hierarchy

```
EngineObject (base: IDisposable)
  ├── GameObject (entity in scene, holds Transform + components)
  ├── MonoBehaviour (component base class, lifecycle methods)
  └── Resource types: Mesh, Material, Shader, Texture2D, Texture3D,
      RenderTexture, Scene, AnimationClip, AudioClip, PrefabAsset, Model, TerrainData
```

- **`EngineObject`** — Base for all engine objects. Has `InstanceID`, `Name`, `AssetID`, `AssetPath`, `IsDisposed`. Use `obj.IsValid()` / `obj.IsNotValid()` extension methods instead of null checks.
- **`GameObject`** — Scene entity with a `Transform`, component list, parent/child hierarchy, tags, layers, and prefab instance data. Implements `ISerializable`.
- **`MonoBehaviour`** — Component base. Lifecycle: `OnAddedToScene → OnEnable → Start → Update/FixedUpdate/LateUpdate → OnDisable → OnRemovedFromScene → OnDispose`. Has `[ExecuteAlways]` for editor execution. Gameplay methods gated by `ShouldExecuteGameplay` (play mode or `[ExecuteAlways]`).

### 2.2 Scene System

- `Scene` holds a flat list of root `GameObject`s and a `PhysicsWorld`.
- `Scene.Load(scene)` / `Scene.Unload()` for the built-in scene manager.
- Scenes serialize via `ISerializationCallbackReceiver` and preserve `GameObject`/component `Identifier` GUIDs across round-trips.
- Fog, ambient lighting, and skybox settings live on `Scene`.

### 2.3 Rendering Pipeline

- **Deferred rendering** with a GBuffer → lighting passes → forward transparency → post-processing.
- `RenderPipeline` is abstract; `DefaultRenderPipeline` is the built-in implementation.
- Components return renderables via `OnRenderCollect(Camera, List<IRenderable>, List<IRenderableLight>)`.
- **`IRenderable`** interface — `GetMaterial()`, `GetRenderingData(...)`, `GetCullingData(...)`.
- **`IRenderableLight`** interface — directional, point, spot lights with shadow mapping.
- `MeshRenderable` / `InstancedMeshRenderable` / `SkinnedMeshRenderable` are concrete implementations.
- `PropertyState` holds per-object shader properties (colors, floats, vectors, matrices, textures, buffers).
- `ImageEffect` subclasses attach to `Camera.Effects` for post-processing (`RenderStage.AfterOpaques` or `RenderStage.PostProcess`).
- Custom shader language parsed by `ShaderParser` with `#include` support, multi-pass, keywords/variants, grab passes.
- Shadow mapping via `ShadowAtlas` (guillotine bin-packing).

### 2.4 Serialization (Prowl.Echo)

- `[SerializeField]` — serialize private fields.
- `[SerializeIgnore]` — exclude from serialization.
- `[HideInInspector]` — serialize but hide from inspector.
- `ISerializable` — custom serialize/deserialize via `EchoObject`.
- `ISerializationCallbackReceiver` — `OnBeforeSerialize()` / `OnAfterDeserialize()` callbacks.
- `AssetRef<T>` — lazy GUID-based asset references. Resolved via `AssetDatabase.Get()`.

### 2.5 Asset Pipeline

- **GUID-based** with `.meta` files alongside each asset.
- `IAssetDatabase` interface — `EditorAssetDatabase` (editor) / `PlayerAssetDatabase` (builds).
- Importers registered via `[ImporterFor(".ext")]` attribute on `AssetImporter` subclasses.
- Sub-assets supported with deterministic GUIDs.
- `[CreateAssetMenu("Name", Extension = ".ext")]` for scriptable assets.
- Forward & reverse dependency tracking via `DependencyGraph`.

### 2.6 Physics

- Jitter Physics 2 integration via `PhysicsWorld` (per-scene).
- Colliders: `BoxCollider`, `SphereCollider`, `CapsuleCollider`, `CylinderCollider`, `ConeCollider`, `ConvexHullCollider`, `MeshCollider`, `ModelCollider`, `TerrainCollider`.
- Constraints: `BallSocketConstraint`, `HingeJoint`, `PrismaticJoint`, `UniversalJoint`, etc.
- `Rigidbody3D`, `CharacterController`, `WheelCollider`.
- Layer-based collision filtering via `CollisionMatrix` and `LayerFilter`.

### 2.7 Input System

- `InputAction` / `InputActionMap` / `InputBinding` with composites and processors.
- `IInputHandler` interface — `SilkInputHandler` (Silk.NET), `NullInputHandler`, `GameViewInputHandler`.
- Static `Input` class provides polling API.

### 2.8 Editor Architecture

- `EditorApplication` extends `Game` — the editor is itself a game application.
- Panels extend `DockPanel` and register via `[EditorWindow("Path/Name")]`.
- Inspector draws via `PropertyGrid` (reflection-based) + `PropertyEditor` subclasses + `ComponentEditor` subclasses.
- Registries use assembly scanning pattern: `Initialize()` iterates `ScriptAssemblyManager.GetAllTypes()` or `GetAllRelevantAssemblies()`.
- Hot-reload via collectible `AssemblyLoadContext` (`ScriptAssemblyManager`).
- Project settings: subclass `ProjectSettingsBase` + `[ProjectSettings("Name")]`.
- Editor settings: `EditorSettings` (global, persists across projects).
- `EditorTheme` — static color palette with neutral/purple/blue/red/ink ramps and sizing constants.

### 2.9 Events

- Vortex source-generated events: `[EventDomain]` on a partial class, `[EventArgs(typeof(T))]` on `EventKey` fields.
- `GameEvents` — `OnBeforeBeginUpdate`, `OnAfterBeginUpdate`, `OnBeforeFixedUpdate`, etc.
- `WindowEvents` — `Load`, `Update`, `Render`, `Resize`, etc.
- Manual C# events used in specific subsystems (e.g., `ScriptAssemblyManager.OnBeforeAssemblyReload`).

---

## 3. Coding Standards & Conventions

### 3.1 General Style

- **Namespace**: `Prowl.Runtime` for runtime, `Prowl.Editor` for editor, `Prowl.Editor.Docking`, `Prowl.Editor.Panels`, `Prowl.Editor.Importers`, `Prowl.Editor.Scripting`, `Prowl.Editor.Widgets`, etc.
- **File header**: Every `.cs` file begins with:
  ```csharp
  // This file is part of the Prowl Game Engine
  // Licensed under the MIT License. See the LICENSE file in the project root for details.
  ```
- **Implicit usings disabled** — all `using` statements are explicit.
- **Nullable enabled** — use `?` annotations. Suppress specific warnings via project-level `NoWarn`.
- **Unsafe code allowed** — used for graphics interop and performance-critical paths.
- **File-scoped namespaces** — use `namespace Prowl.Runtime;` (semicolon style), not block style.
- **One type per file** (with small related types like enums/structs in the same file when tightly coupled).

### 3.2 Naming Conventions

| Element | Convention | Example |
|---|---|---|
| Private fields | `_camelCase` with underscore prefix | `_instanceID`, `_enabled`, `_shader` |
| Static private fields | `s_camelCase` with `s_` prefix | `s_nextID`, `s_defaultShader`, `s_quadMesh` |
| Public fields | `PascalCase` (no prefix) | `Name`, `CastShadows`, `Intensity` |
| Properties | `PascalCase` | `IsDisposed`, `InstanceID`, `Shader` |
| Methods | `PascalCase` | `GetMaterial()`, `OnRenderCollect()` |
| Constants/Enums | `PascalCase` | `CameraClearFlags.Skybox`, `IndexFormat.UInt32` |
| Local variables | `camelCase` | `currentScene`, `localDist` |
| Type parameters | `T` prefix | `AssetRef<T>` |
| Interfaces | `I` prefix | `IRenderable`, `IAssetDatabase`, `IInputHandler` |

### 3.3 Collection Initialization

- Use collection expressions `[]` for empty collections: `new List<T>()` → `[]`.
- Use target-typed `new()` when the type is obvious from context.

### 3.4 Class Organization

Follow this order within a class:
1. Static fields and properties
2. `#region Private Fields/Properties` — private instance fields
3. `#region Public Fields/Properties` — public fields and properties
4. Constructors
5. Public methods
6. Protected/internal methods
7. Private methods

### 3.5 Attributes (Engine-Specific)

| Attribute | Target | Purpose |
|---|---|---|
| `[AddComponentMenu("Category/Name")]` | MonoBehaviour class | Registers in Add Component menu |
| `[CreateAssetMenu("Name", Extension=".ext")]` | EngineObject class | Registers in Create Asset menu |
| `[RequireComponent(typeof(T))]` | MonoBehaviour class | Auto-adds required components |
| `[ExecutionOrder(int)]` | MonoBehaviour class | Controls update execution order |
| `[ExecuteAlways]` | MonoBehaviour class | Runs lifecycle in edit mode |
| `[SerializeField]` | Private field | Include in serialization |
| `[SerializeIgnore]` | Field | Exclude from serialization |
| `[HideInInspector]` | Field | Serialize but hide from UI |
| `[Range(min, max)]` | Float/int field | Slider in inspector |
| `[Header("text")]` | Field | Section header in inspector |
| `[Space(height)]` | Field | Vertical spacing in inspector |
| `[Tooltip("text")]` | Field | Hover tooltip in inspector |
| `[ReadOnly]` | Field | Non-editable in inspector |
| `[ShowIf("member")]` | Field | Conditional visibility |
| `[Button("label")]` | Method | Button in inspector |
| `[ImporterFor(".ext")]` | AssetImporter class | Registers file extension importer |
| `[EditorWindow("Path/Name")]` | DockPanel class | Registers editor panel |
| `[ProjectSettings("Name")]` | ProjectSettingsBase class | Registers project settings page |
| `[CustomComponentEditor(typeof(T))]` | ComponentEditor class | Custom inspector for component |
| `[InitializeOnLoad]` | Static method | Called on editor/assembly load |
| `[OnSceneSaved]` | Static method | Called when scene is saved |
| `[OnUndoRedo]` | Static method | Called on undo/redo |

### 3.6 Error Handling & Logging

- Use `Debug.Log()`, `Debug.LogWarning()`, `Debug.LogError()` — **never** `Console.WriteLine`.
- Prefix log messages with context: `"[SystemName] message"` (e.g., `"[ScriptAssemblyManager] Compilation successful."`).
- Use `try/catch` around reflection and external calls; log and continue rather than crash the editor.

### 3.7 Null Safety Patterns

- For `EngineObject` references, use `.IsValid()` / `.IsNotValid()` instead of `== null` / `!= null` (handles disposed state).
- For `AssetRef<T>`, access via `.Res` (lazy-loads) or `.ResWeak` (no load, may be null).
- Use `?.` and `??` operators freely for non-engine reference types.

---

## 4. Creating New Features — Patterns to Follow

### 4.1 New Component

```csharp
// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;

namespace Prowl.Runtime;

[AddComponentMenu("Category/MyComponent")]
public class MyComponent : MonoBehaviour
{
    // Public serialized fields
    public float Speed = 1.0f;

    [Range(0, 100)]
    public int Health = 100;

    // Private serialized field
    [SerializeField]
    private AssetRef<Material> _material;

    // Non-serialized runtime state
    [SerializeIgnore]
    private float _timer;

    public override void Start()
    {
        // Called once when gameplay begins
    }

    public override void Update()
    {
        // Called every frame during gameplay
        _timer += Time.DeltaTime * Speed;
    }

    public override void OnRenderCollect(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
    {
        // Return renderables for the rendering pipeline
    }
}
```

### 4.2 New Editor Panel

```csharp
using Prowl.Editor.Docking;
using Prowl.PaperUI;

namespace Prowl.Editor.Panels;

[EditorWindow("Tools/My Panel")]
public class MyPanel : DockPanel
{
    public override string Title => "My Panel";
    public override string Icon => EditorIcons.Wrench;

    public override void OnGUI(Paper paper, float width, float height)
    {
        // Draw UI using Paper immediate-mode API
    }
}
```

### 4.3 New Asset Importer

```csharp
using Prowl.Echo;
using Prowl.Runtime;

namespace Prowl.Editor.Importers;

[ImporterFor(".myext")]
public class MyImporter : AssetImporter
{
    public override int Version => 1; // Bump to force reimport

    public override bool Import(ImportContext ctx)
    {
        // Read file, create EngineObject, call ctx.SetMainAsset(...)
        return true;
    }
}
```

### 4.4 New Image Effect

```csharp
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;

namespace Prowl.Runtime;

public class MyEffect : ImageEffect
{
    public override RenderStage Stage => RenderStage.PostProcess;

    public override void OnRenderEffect(RenderContext context)
    {
        // Perform post-processing
    }
}
```

### 4.5 Registry Pattern (Assembly Scanning)

Many editor systems use a consistent registry pattern:

```csharp
public static class MyRegistry
{
    private static bool _initialized;

    public static void Reinitialize() { _initialized = false; Initialize(); }

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        foreach (var type in ScriptAssemblyManager.GetAllTypes())
        {
            // Discover types via attributes or interface checks
        }

        Debug.Log($"MyRegistry: {count} items registered.");
    }
}
```

All registries must:
- Support `Reinitialize()` for hot-reload.
- Be called from `ScriptAssemblyManager.OnAfterAssemblyReload` or the central registry init in `EditorApplication`.

### 4.6 New Standalone Sample

```csharp
using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace MySample;

internal class Program
{
    static void Main(string[] args)
    {
        new MyGame().Run("Sample Title", 1280, 720);
    }
}

public sealed class MyGame : Game
{
    public override void Initialize()
    {
        var scene = new Scene();
        // Create GameObjects, components, set up scene
        Scene.Load(scene);
    }

    public override void Update()
    {
        // Per-frame logic
    }
}
```

---

## 5. Critical Rules

### 5.1 Runtime ↔ Editor Boundary

- **`Prowl.Runtime` must NEVER reference `Prowl.Editor`**. The dependency is one-way: Editor → Runtime.
- Runtime code uses `IAssetDatabase` interface; the editor provides `EditorAssetDatabase`, builds provide `PlayerAssetDatabase`.
- Runtime code uses `Application.IsEditor` / `Application.IsPlaying` to branch behavior, never editor types.

### 5.2 AOT Compatibility

- `Prowl.Runtime` is marked `IsAotCompatible=true`. Avoid:
  - `Type.MakeGenericType()` at runtime with user types.
  - Unbounded reflection in hot paths.
  - Dynamic code generation (`Reflection.Emit`, `Expression.Compile`).
- The editor is exempt from AOT constraints.

### 5.3 Serialization Safety

- All public fields on `MonoBehaviour` / `EngineObject` subclasses are serialized by default.
- Mark runtime-only caches with `[SerializeIgnore]`.
- Asset references must use `AssetRef<T>`, not raw object references.
- Implement `ISerializationCallbackReceiver` when you need to rebuild caches after deserialization (e.g., lookup dictionaries, GPU resources).

### 5.4 Lifecycle Correctness

- Never call `Update()` / `Start()` / `FixedUpdate()` directly — the engine calls them.
- Override `OnDispose()` to clean up unmanaged resources (GPU objects, native handles).
- Check `ShouldExecuteGameplay` is already handled by the engine; don't re-check in component code.
- Use `[ExecuteAlways]` sparingly — only for components that must run in edit mode (e.g., camera preview, terrain).

### 5.5 Rendering

- Components do NOT call draw commands directly. They return `IRenderable` objects from `OnRenderCollect()`.
- The render pipeline owns the draw loop, sorting, culling, and state management.
- Use `PropertyState` for per-object shader data; never set global GL state from components.
- Always use the engine's `Graphics` wrapper, not raw `Silk.NET.OpenGL.GL` calls.

### 5.6 Math Types

- Use `Prowl.Vector` types: `Float2`, `Float3`, `Float4`, `Float4x4`, `Quaternion`, `Color`.
- **Not** `System.Numerics` or `Silk.NET.Maths` in engine/component code.
- `Transform` is in `Prowl.Vector` namespace but used via `GameObject.Transform`.

### 5.7 Threading

- The engine is **single-threaded** for gameplay and rendering (OpenGL constraint).
- Background work (compilation, import) uses `Task.Run()` but results are consumed on the main thread.
- Use `Interlocked` for ID generation, not locks.

### 5.8 Disposal

- All `EngineObject` subclasses are `IDisposable`. Override `OnDispose()` for cleanup.
- GPU resources (`GraphicsTexture`, `GraphicsBuffer`, `GraphicsVertexArray`, `GraphicsFrameBuffer`, `GraphicsProgram`) must be disposed.
- Use the `using` pattern for transient resources; store persistent ones as fields and dispose in `OnDispose()`.

---

## 6. Testing Guidelines

- Tests live in `Prowl.Runtime.Test` using **xUnit**.
- Test classes implement `IDisposable` for cleanup (disposing scenes, GameObjects).
- Use helper methods like `CreateScene()`, `CreateGameObject()` to track allocations.
- Test lifecycle events by checking recorded event lists on test components.
- Assert with `Assert.Equal`, `Assert.True`, `Assert.Contains`, etc.
- Name tests descriptively: `Test_AddToDisabledScene_OnlyCallsOnAddedToScene`.

---

## 7. Shader Conventions

- Shader files use `.shader` extension with a custom format parsed by `ShaderParser`.
- GLSL include files use `.glsl` extension under `Assets/Defaults/`.
- Global uniforms match `ShaderVariables.glsl` layout (std140 UBO): `prowl_MatV`, `prowl_MatP`, `_WorldSpaceCameraPos`, `_Time`, etc.
- Shader properties prefixed with underscore: `_MainColor`, `_MainTex`, `_ObjectID`.
- Per-object data uploaded via `PropertyState`; global data via `GlobalUniforms` UBO.

---

## 8. Build & Project Structure

- Projects compile with `dotnet build`. No custom MSBuild targets except native library copying.
- Embedded resources: `Prowl.Runtime/Assets/Defaults/**` (shaders, materials, meshes) and `Prowl.Editor/Resources/**` (fonts, icons).
- Build output: `Build/Runtime/{Config}/` and `Build/Editor/{Config}/`.
- Game builds use `BuildPipeline` → `DesktopBuildPipeline` → `.prowlpak` packed asset files.
- Editor supports `--project`, `--buildmode`, `--output` CLI arguments.

---

## 9. UI Framework (Paper)

- **Immediate-mode with retained state** — `paper.Column()`, `paper.Row()`, `paper.Box()` create layout nodes.
- Use `using` blocks with `.Enter()` for scoped layout: `using (paper.Column("id").Size(w, h).Enter()) { ... }`.
- IDs must be unique within their scope — use field names, indices, or instance IDs.
- Editor widgets in `Prowl.Editor.Widgets` namespace: `EditorGUI`, `PropertyGrid`, `ScrollView`, `ContextMenuBuilder`, `FileDialog`, `ColorPicker`, `CurveEditor`, etc.
- Theme colors from `EditorTheme` static class (e.g., `EditorTheme.Neutral300`, `EditorTheme.Purple400`, `EditorTheme.Ink300`).
- Sizing: `EditorTheme.RowHeight`, `EditorTheme.FontSize`, `EditorTheme.Padding`, `EditorTheme.Roundness`.

---

## 10. Common Pitfalls

1. **Don't use `== null` on EngineObject** — use `.IsValid()` / `.IsNotValid()` (checks `IsDisposed`).
2. **Don't add editor types to runtime** — will break AOT and standalone builds.
3. **Don't forget to dispose GPU resources** — leads to OpenGL leaks.
4. **Don't serialize runtime caches** — mark with `[SerializeIgnore]`, rebuild in `OnAfterDeserialize()`.
5. **Don't bypass the render pipeline** — always use `OnRenderCollect()`, never call `GL.Draw*` from components.
6. **Don't use `Console.WriteLine`** — use `Debug.Log()` family.
7. **Don't assume play mode** — check `Application.IsPlaying` or use `[ExecuteAlways]`.
8. **Don't block the main thread** — use async for long operations, poll results in `Update()`.
9. **Don't create raw `EngineObject`** — always use specific subclasses (`GameObject`, `Mesh`, `Material`, etc.).
10. **Don't modify `Transform` in `OnRenderCollect()`** — it's called during rendering traversal; mutations cause flickering.
