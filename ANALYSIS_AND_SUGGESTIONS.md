# Prowl Engine — Codebase Analysis & Next Steps

> **Date:** Auto-generated analysis of the `Standalone_Editor` branch  
> **Target:** .NET 9 | OpenGL 4.1 (Silk.NET) | Jitter2 Physics | Echo Serialization

---

## Table of Contents

1. [Project Overview](#1-project-overview)  
2. [Architecture Summary](#2-architecture-summary)  
3. [Strengths](#3-strengths)  
4. [Areas for Improvement](#4-areas-for-improvement)  
   - 4.1 [Architecture & Design](#41-architecture--design)  
   - 4.2 [Runtime Performance](#42-runtime-performance)  
   - 4.3 [Editor UX & Stability](#43-editor-ux--stability)  
   - 4.4 [Rendering Pipeline](#44-rendering-pipeline)  
   - 4.5 [Serialization & Asset Pipeline](#45-serialization--asset-pipeline)  
   - 4.6 [Testing & Quality](#46-testing--quality)  
   - 4.7 [Code Hygiene](#47-code-hygiene)  
5. [Feature Roadmap Suggestions](#5-feature-roadmap-suggestions)  
6. [Quick Wins](#6-quick-wins)  
7. [Long-Term Strategic Items](#7-long-term-strategic-items)  

---

## 1. Project Overview

| Project | Role | Key Dependencies |
|---------|------|-----------------|
| `Prowl.Runtime` | Core engine (ECS-like GameObject/Component, rendering, physics, audio, input, scene management) | Silk.NET 2.22, Jitter2 2.7.3, Prowl.Echo 2.0, Prowl.Paper 0.7, Magick.NET |
| `Prowl.Editor` | Unity-like editor built with Dear ImGui + Paper UI | Microsoft.CodeAnalysis.CSharp 4.12 (Roslyn) |
| `Prowl.Launcher` | Project hub / launcher | Runtime reference |
| `Prowl.Runtime.Test` | Unit tests for runtime | xUnit 2.9, NSubstitute 5.1 |
| `Prowl.Editor.Tests` | Unit tests for editor | xUnit |
| `Samples/*` | 10 sample projects | Runtime reference |

The solution contains **~190+ source files** across Runtime and Editor, targeting **.NET 9** with `AllowUnsafeBlocks` and nullable reference types enabled.

---

## 2. Architecture Summary

```
┌─────────────────────────────────────────────────┐
│                  Prowl.Editor                    │
│  (ImGui + Paper UI, Panels, Docking, Services)  │
│  EditorApplication : Game                        │
└────────────────────┬────────────────────────────┘
                     │ references
┌────────────────────▼────────────────────────────┐
│                 Prowl.Runtime                    │
│  ┌──────────┐ ┌──────────┐ ┌──────────────────┐ │
│  │ Scene /  │ │ Rendering│ │ Physics (Jitter2)│ │
│  │ GameObject│ │ Pipeline │ │                  │ │
│  │ MonoBehav│ │ Graphite │ │ Audio (MiniAudio)│ │
│  └──────────┘ └──────────┘ └──────────────────┘ │
│  ┌──────────┐ ┌──────────┐ ┌──────────────────┐ │
│  │  Input   │ │EventSystem│ │  Serialization   │ │
│  │ (Silk.NET)│ │          │ │  (Prowl.Echo)    │ │
│  └──────────┘ └──────────┘ └──────────────────┘ │
└─────────────────────────────────────────────────┘
```

**Key patterns observed:**
- **GameObject + MonoBehaviour** hierarchy (Unity-like)
- **Service Locator** (`EditorServices`) for editor-side dependency injection
- **Event System** with priority-sorted delegates and thread-safe invoke
- **Render Pipeline** abstraction (`RenderPipeline` → `DefaultRenderPipeline`)
- **Graphite** abstraction layer over OpenGL (with device capabilities)
- **Echo** serializer for scene/asset serialization
- **Collectible AssemblyLoadContext** for hot-reloading user scripts

---

## 3. Strengths

| Area | Details |
|------|---------|
| **Clean separation** | Runtime has zero editor dependencies; editor extends `Game` cleanly |
| **Modern .NET** | Targets .NET 9, uses `Span<T>`, collection expressions, nullable annotations |
| **AOT-compatible runtime** | `<IsAotCompatible>true</IsAotCompatible>` on the runtime project |
| **GPU abstraction** | Graphite layer provides a good foundation for multi-backend rendering |
| **Hot-reload infrastructure** | `ProjectAssemblyManager` uses collectible ALCs with file-system watching |
| **Play mode isolation** | `EditorPlayMode` properly snapshots/restores scene state |
| **Undo/Redo** | Command-based undo system with history trimming |
| **DPI awareness** | Full DPI scaling pipeline with per-monitor detection |
| **Thread-safe events** | `Event<T>.Invoke` takes a snapshot under lock before dispatching |
| **Layout persistence** | ImGui layout saved/loaded from project settings |

---

## 4. Areas for Improvement

### 4.1 Architecture & Design

#### 4.1.1 — Static Mutable State Overuse
Several core systems rely heavily on static state:
- `Scene.Current`, `Scene.IsPlayMode`, `Scene.SimulatePhysics`
- `Window.InternalWindow`, `Window.InternalInput`
- `Time.TimeStack` (static stack)
- `AssetDatabase.Current`
- `Debug.OnLog`

**Risk:** Makes unit testing extremely difficult, prevents multi-scene or headless scenarios, and introduces hidden coupling.

**Suggestion:**
- Introduce an `EngineContext` / `RuntimeContext` object that holds references to the active scene, window, time, input, and asset database.
- Pass it through constructors or a scoped service provider rather than global statics.
- Keep static accessors as thin convenience wrappers over the context for backward compatibility.

#### 4.1.2 — `Game` Base Class Does Too Much
`Game` handles window creation, ImGui controller, Paper UI, audio init, DPI management, input, scene lifecycle, and rendering frame orchestration — all in one class (~300+ lines).

**Suggestion:**
- Extract a `WindowManager` that handles Silk.NET window + DPI.
- Extract a `FrameOrchestrator` that handles the Update/Render loop ordering.
- Extract `ImGuiManager` for Dear ImGui lifecycle.
- `Game` becomes a thin composition root.

#### 4.1.3 — Event System Complexity
The `EventManager<T>` / `Event<T>` / `EventDelegateContainer<T>` / `EventParam` system is generic and flexible but:
- `EventParam` is an empty abstract class — it provides no type safety beyond `is` checks.
- Priority-sorted dispatch with dictionaries and sorted key lists adds overhead.
- The static `s_instances` list with `LastGlobalInstance` is fragile.

**Suggestion:**
- Consider replacing `EventParam[]` with strongly-typed event args (e.g., `record struct UpdateEvent(float DeltaTime)`).
- Evaluate whether a simpler `event Action<T>` pattern on the relevant manager classes would suffice for most use cases.
- If priority ordering is truly needed, a `SortedList` or a single sorted array would be simpler.

#### 4.1.4 — Duplicate Update Loop Code
`Game.WindowUpdate()` and `EditorApplication.WindowUpdate()` contain nearly identical code (audio, time, input, fixed update, scene update, gizmos, end update, frame counter). The editor overrides the virtual method but duplicates rather than extending.

**Suggestion:**
- Make `Game.WindowUpdate()` call well-defined virtual hooks (`OnPreUpdate`, `OnPostUpdate`, etc.) so `EditorApplication` only overrides what differs.

### 4.2 Runtime Performance

#### 4.2.1 — Excessive LINQ and List Allocations in Hot Paths
`Scene.Update()`, `Scene.FixedUpdate()`, `Scene.Render()`, and related methods allocate lists every frame:
```csharp
List<GameObject> activeGOs = [.. ActiveObjects]; // allocates every frame
```
`ActiveObjects` itself is a LINQ `Where` query re-evaluated each call. `Render()` uses `SelectMany` + `ToList()` + `Sort` on every frame.

**Suggestion:**
- Maintain a dirty-flagged cached list of active objects, updated only when objects are added/removed/enabled/disabled.
- Pre-allocate and reuse lists (object pooling or `List<T>.Clear()` pattern).
- Replace LINQ in hot paths with explicit loops.

#### 4.2.2 — Component Iteration via Copies
Every `ForeachComponent` and lifecycle call creates `[.. go.GetComponents<MonoBehaviour>()]` copies to avoid concurrent modification. This allocates arrays per-object per-frame.

**Suggestion:**
- Use a deferred add/remove queue (similar to Unity's internal approach) so components are only added/removed between lifecycle phases, eliminating the need for defensive copies.

#### 4.2.3 — `FindObjectsOfType<T>` / `FindObjectByID` Linear Scans
These scan all objects and all components every call — O(N×M).

**Suggestion:**
- Maintain type-indexed caches (`Dictionary<Type, List<MonoBehaviour>>`) updated on add/remove.
- Maintain an ID-indexed lookup (`Dictionary<int, EngineObject>`).

#### 4.2.4 — String Allocations in Frame Diagnostics
```csharp
Console.Title = $"... FPS: {1.0 / Time.DeltaTime}";
```
This allocates a string every 60 frames. Minor, but symptomatic of allocations in the main loop.

**Suggestion:** Use `StringBuilder` or `string.Create` for hot-path string building.

### 4.3 Editor UX & Stability

#### 4.3.1 — Error Handling in Render Loop
Both `Game` and `EditorApplication` catch exceptions and **re-throw** them, which will crash the application:
```csharp
catch (Exception e) { Debug.LogError(...); throw; }
```

**Suggestion:**
- In the editor, swallow exceptions (with logging) to keep the editor responsive.
- Add a "safe mode" that disables the offending component/script.
- In standalone builds, consider a crash reporter before terminating.

#### 4.3.2 — Launcher References a Local DLL
`Prowl.Launcher.csproj` has a hardcoded hint path:
```xml
<HintPath>..\..\Prowl.Echo\Echo\bin\Debug\net9.0\Echo.dll</HintPath>
```
This will break on other machines or in Release builds.

**Suggestion:** Use a NuGet `PackageReference` (like the Runtime project does) or a `ProjectReference` if the Echo source is part of the repo.

#### 4.3.3 — No Editor Auto-Save
There's layout persistence but no auto-save for scene changes.

**Suggestion:**
- Implement periodic auto-save of the scene to a temp file.
- Show a "unsaved changes" indicator in the title bar.
- Prompt on close if there are unsaved changes.

#### 4.3.4 — Script Recompilation Robustness
`ProjectAssemblyManager` uses a 1-second debounce and `FileSystemWatcher`. FSW is notoriously unreliable across platforms.

**Suggestion:**
- Add a manual "Recompile" button/shortcut as fallback.
- Consider polling as a backup detection mechanism on platforms where FSW is unreliable (Linux with some filesystems).
- Handle compilation errors gracefully in the UI (show error list panel).

### 4.4 Rendering Pipeline

#### 4.4.1 — No Deferred Rendering Path
The README mentions both "Forward Renderer" and a `_deferredCompose` material exists, but the pipeline is primarily forward.

**Suggestion:**
- If deferred is planned, formalize the G-Buffer layout and add a `DeferredRenderPipeline`.
- Consider a hybrid approach (deferred for opaque, forward for transparent) which is standard in modern engines.

#### 4.4.2 — Missing Shadow Types
Point light shadows are explicitly listed as not implemented in the README.

**Suggestion:**
- Implement cube-map shadow mapping for point lights.
- Add cascaded shadow maps (CSM) for directional lights (currently listed on roadmap as ❌).

#### 4.4.3 — Graphite Backend Expansion
The `GraphiteDevice` abstraction exists but only OpenGL is implemented (`GLGraphiteDevice`, `GLCommandList`, etc.).

**Suggestion:**
- Prioritize Vulkan backend for performance on Linux/Windows.
- Metal backend for macOS (required for Apple Silicon performance).
- Consider WebGPU for future web export support.

#### 4.4.4 — Render Pipeline Extensibility
`DefaultRenderPipeline` uses static resources (`s_quadMesh`, `s_skyDome`, etc.) making it hard to have multiple pipeline instances.

**Suggestion:**
- Move static resources to instance fields or a shared resource cache.
- Make the pipeline fully configurable via a `RenderPipelineAsset` (similar to Unity's SRP).

### 4.5 Serialization & Asset Pipeline

#### 4.5.1 — Dual Serialization Paths
`PrefabManager` uses `System.Text.Json` while `JsonSceneSerializer` uses `Prowl.Echo` → `System.Text.Json.Nodes` bridge. This creates inconsistency.

**Suggestion:**
- Standardize on a single serialization path. Since Echo already handles the complex graph serialization, use it for prefabs too.
- This ensures prefab data goes through the same `ISerializationCallbackReceiver` pipeline as scenes.

#### 4.5.2 — Prefab Serialization Is Shallow
`PrefabManager.SerializeFields` does basic reflection on `RuntimeUtils.GetSerializableFields` but handles complex types (nested objects, asset references, collections) with just `value?.ToString()` fallback.

**Suggestion:**
- Route prefab serialization through Echo to get proper graph handling.
- Support prefab overrides (property-level diff from the prefab source) like Unity does.

#### 4.5.3 — No Binary Scene Format
Scenes are saved as JSON via the Echo→JSON bridge. For large scenes in builds, this is slow to parse.

**Suggestion:**
- Echo already supports binary serialization. Add a `BinarySceneSerializer : ISceneSerializer` for use in standalone builds.
- Keep JSON for the editor (human-readable, diff-friendly).

### 4.6 Testing & Quality

#### 4.6.1 — Very Low Test Coverage
- **Runtime tests:** `LifecycleTests`, `Boolean32MatrixTests`, `ColorTests`, and Graphite GPU tests.
- **Editor tests:** `ProjectScriptCompilerTests`, `ProjectAssemblyManagerTests`.
- **Missing:** No tests for Scene lifecycle, GameObject hierarchy, component add/remove, serialization round-trips, event system, physics integration, input processing, render pipeline, or asset database.

**Suggestion (priority order):**
1. **Scene & GameObject tests** — Add/remove objects, parent/child, enable/disable, component lifecycle (OnEnable, Start, Update, OnDisable, OnDispose order).
2. **Serialization round-trip tests** — Serialize a scene with various component types, deserialize, verify equality.
3. **Event system tests** — Priority ordering, enable/disable, thread safety.
4. **Undo/Redo tests** — Execute/undo/redo commands, verify state.
5. **Asset database tests** — GUID resolution, reference serialization.

#### 4.6.2 — No CI/CD Pipeline Visible
No GitHub Actions, Azure Pipelines, or similar config detected.

**Suggestion:**
- Add a CI workflow that builds all projects, runs tests, and reports coverage.
- Add a matrix build for Windows/Linux/macOS.

#### 4.6.3 — Suppressed Nullable Warnings
Both Runtime and Editor suppress a wide range of nullable warnings:
```xml
<NoWarn>8600;8601;8618;8602;8603;8604;8625;1591</NoWarn>
```

**Suggestion:**
- Gradually fix nullable warnings and remove suppressions.
- This will prevent NullReferenceExceptions at runtime, especially in user-facing APIs.

### 4.7 Code Hygiene

#### 4.7.1 — Unused `using` Statements
Several files import namespaces that don't appear to be used (e.g., `System.Collections` in `EventManager.cs`, `System.Threading.Tasks` in `BaseEvents.cs`).

**Suggestion:** Run a solution-wide "Remove Unused Usings" pass.

#### 4.7.2 — Inconsistent Implicit Usings
`Prowl.Runtime` uses `<ImplicitUsings>disable</ImplicitUsings>` while `Prowl.Editor` uses `enable`. Both approaches are valid but the inconsistency means different files need different explicit imports.

**Suggestion:** Pick one approach and apply it consistently. For a game engine with many specific types, `disable` with explicit usings is usually clearer.

#### 4.7.3 — `EngineObject` Equality Operators
The current `==` operator uses `ReferenceEquals` which is correct, but `Equals` delegates to `==` which means it won't work correctly with `Dictionary` or `HashSet` that call `Equals(object)` on boxed values.

**Suggestion:** Verify this is intentional. If `InstanceID` is the identity, consider using it in both `Equals` and `GetHashCode` consistently.

#### 4.7.4 — Commented-Out Code
Several `.csproj` files and source files contain commented-out code (e.g., old Jitter2 version, old OpenAL reference, `Serializer_OnResolveCustomType`).

**Suggestion:** Remove commented-out code; use source control history to recover it if needed.

---

## 5. Feature Roadmap Suggestions

Based on the existing roadmap in the README crossed with codebase analysis:

### High Priority (Stability & Core)

| # | Feature | Effort | Impact |
|---|---------|--------|--------|
| 1 | **Cascaded Shadow Maps** for directional lights | Medium | High — required for any outdoor scene |
| 2 | **Point light shadows** (cubemap) | Medium | High — listed as missing |
| 3 | **Scene dirty tracking & auto-save** | Low | High — prevents data loss |
| 4 | **Error-tolerant editor loop** (don't crash on script exceptions) | Low | High — usability |
| 5 | **Reduce per-frame allocations** in Scene.Update / Render | Medium | High — framerate stability |

### Medium Priority (Editor & Workflow)

| # | Feature | Effort | Impact |
|---|---------|--------|--------|
| 6 | **Animation editor / timeline** | High | High — listed on roadmap |
| 7 | **Material node editor** | High | Medium — listed on roadmap |
| 8 | **Asset import progress UI** | Low | Medium — better UX |
| 9 | **Prefab overrides** (property-level) | Medium | Medium — proper prefab workflow |
| 10 | **Console panel search & filter** | Low | Medium — debugging workflow |

### Lower Priority (Platform & Expansion)

| # | Feature | Effort | Impact |
|---|---------|--------|--------|
| 11 | **Vulkan backend** for Graphite | High | High — perf on modern GPUs |
| 12 | **2D rendering mode** | Medium | Medium — listed on roadmap |
| 13 | **Android / iOS export** | High | Medium — mobile market |
| 14 | **Web export** (via WebGPU + WASM) | High | Medium — distribution |
| 15 | **VR support** | High | Low–Medium — niche but growing |

---

## 6. Quick Wins

These can be done in 1–2 sessions each and provide immediate value:

1. **Remove nullable warning suppressions and fix warnings** — Improves null safety across the board.
2. **Add a `[MethodImpl(AggressiveInlining)]` pass** on hot-path property getters (e.g., `Transform`, `Enabled`, `Scene`).
3. **Replace `[.. collection]` allocations in `Scene.Update`** with reusable `List<T>` fields cleared each frame.
4. **Fix Launcher project reference** — Replace the hardcoded Echo DLL path with a PackageReference.
5. **Add a CI GitHub Action** — `dotnet build` + `dotnet test` on push/PR.
6. **Remove dead code** — Commented-out lines in .csproj and source files.
7. **Standardize implicit usings** across all projects.
8. **Swallow exceptions in editor update/render loops** instead of re-throwing.
9. **Add scene dirty flag** — set on any undo command execution, clear on save.
10. **Add manual recompile button** in the editor toolbar as FSW fallback.

---

## 7. Long-Term Strategic Items

### 7.1 — ECS Hybrid Architecture
The current `GameObject` + `MonoBehaviour` pattern is familiar but has known scalability limits (cache misses, GC pressure from component arrays). Consider:
- Adding an optional ECS layer (e.g., Arch, Friflo, or custom) for performance-critical systems (particles, AI, large worlds).
- Keeping `MonoBehaviour` as the user-facing scripting API that bridges to ECS internally.

### 7.2 — Render Graph
Modern engines (Unity URP/HDRP, Unreal, Godot 4) use a render graph to:
- Automatically manage render target lifetimes.
- Enable pass culling and resource aliasing.
- Make the pipeline data-driven and extensible.
Consider evolving the `RenderPipeline` abstraction toward a graph-based approach.

### 7.3 — Multi-Scene Support
The current `Scene.Current` singleton limits the engine to one active scene. For:
- Additive scene loading (streaming open worlds).
- Server-side simulation (multiple game instances).
- Editor previews (scene + prefab preview simultaneously).
A scene manager that supports multiple active scenes would be valuable.

### 7.4 — Asset Bundle / Addressable System
The current asset pipeline uses GUIDs and meta files (good). The next step is:
- Asset bundles for downloadable content.
- An addressable system for lazy loading and memory management.
- Async asset loading with progress callbacks.

### 7.5 — Scripting Sandbox
Currently, user scripts run in the same process with full trust. For editor safety:
- Consider running user scripts in a separate process or with restricted permissions.
- At minimum, add try/catch wrappers around all user-script lifecycle calls in the editor.

---

## Summary

Prowl is a well-structured, modern C# game engine with a solid foundation. The Unity-like API, clean Runtime/Editor separation, hot-reload support, and DPI-aware editor are strong starting points. The most impactful next steps are:

1. **Harden the editor** (exception handling, auto-save, dirty tracking)
2. **Reduce frame allocations** (reusable lists, deferred add/remove queues)
3. **Expand test coverage** (scene lifecycle, serialization, events)
4. **Complete shadow rendering** (CSM + point shadows)
5. **Standardize serialization** (use Echo for everything, add binary format for builds)

These improvements will move the engine from "early development" toward the "stable and ready" goal mentioned in the README.
