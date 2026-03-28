# Vulkan ImGui Migration Plan

> **Goal:** Replace the OpenGL-only Dear ImGui integration (`Silk.NET.OpenGL.Extensions.ImGui`)
> with a Vulkan-compatible ImGui backend so the entire editor can run on the Vulkan
> graphics backend — eliminating the forced `GraphicsBackendType.OpenGL` constraint
> in `Prowl.Editor\Program.cs`.

---

## 1. Current Architecture & Constraints

### 1.1 Why the Editor Is Locked to OpenGL Today

```
Prowl.Editor\Program.cs (line 27):
    GraphicsBackendType backend = GraphicsBackendType.OpenGL;
```

The comment reads:
> *"The editor window always uses the OpenGL backend because the Dear ImGui
> integration (Silk.NET.OpenGL.Extensions.ImGui) requires a GL context."*

All editor chrome (menu bar, dockspace, hierarchy, inspector, project browser,
game/scene viewports, console, build panel) is drawn via `ImGuiNET` calls.
The `Silk.NET.OpenGL.Extensions.ImGui.ImGuiController` class handles:
- Font atlas texture upload (GL texture)
- Vertex/index buffer streaming per frame (GL buffers)
- Shader program creation (GL shaders)
- Draw list rendering with `glDrawElementsBaseVertex`
- Input forwarding from `Silk.NET.Input` to ImGui

**Silk.NET does not provide a Vulkan ImGui backend.** The `Silk.NET.OpenGL.Extensions.ImGui`
package only targets OpenGL. There is no `Silk.NET.Vulkan.Extensions.ImGui`.

### 1.2 Current ImGui Integration Points

| File | Role |
|------|------|
| `Prowl.Runtime\ImGuiManager.cs` | Creates `ImGuiController`, calls `Update()` / `Render()` per frame. **Guards with `if (!Graphics.IsOpenGL) return;`** |
| `Prowl.Runtime\Game.cs` (lines 303-313) | Calls `_imguiManager.Update()` → `BeginFrame()` → `BeginImGui()` / `EndImGui()` → `Render()` |
| `Prowl.Editor\EditorApplication.cs` | Overrides `BeginImGui()` / `EndImGui()` — draws the entire editor UI (dockspace, panels) |
| `Prowl.Launcher\LauncherApplication.cs` | Overrides `BeginImGui()` — draws the project launcher UI |
| `Prowl.Editor\Panels\ScenePanel.cs` (line 79) | `nint texId = (nint)rt.MainTexture.Handle.Handle;` — passes raw GL texture handle to `ImGui.Image()` |
| `Prowl.Editor\Panels\GamePanel.cs` (line 125) | Same pattern — raw GL handle to `ImGui.Image()` |
| `Prowl.Editor\Panels\InspectorPanel.cs` | Material preview thumbnails via GL handles |
| `Prowl.Editor\Icons\IconManager.cs` | Icon textures as GL handles |
| `Prowl.Runtime\Prowl.Runtime.csproj` | `Silk.NET.OpenGL.Extensions.ImGui` 2.23.0 package reference |

### 1.3 Other UI Layers

The engine also uses **Paper UI** (`Prowl.Paper` / `PaperRenderer`) for in-game
canvas UI. `PaperRenderer` has already been migrated to the Graphite abstraction
layer (uses Graphite `Buffer`, `BindGroup`, `PipelineState`, `CommandList`). It
works on both OpenGL and Vulkan backends.

### 1.4 Graphite Vulkan Backend Status

Per `GRAPHITE_MIGRATION.md`, the Vulkan backend is **complete**:
- Swapchain, frame-in-flight sync, all resource types (`VKBuffer`, `VKTexture`, `VKPipelineState`, `VKCommandList`, `VKBindGroup`, `VKShaderModule`, `VKSampler`, `VKFence`)
- GLSL → SPIR-V cross-compilation via `Silk.NET.Shaderc`
- `DefaultRenderPipeline` already records parallel Graphite commands
- `BlitToSwapchainGraphite()` already presents to the Vulkan swapchain

---

## 2. Options for Vulkan ImGui Rendering

### Option A: Use `ImGui.NET` + Write a Custom Vulkan Backend (Recommended)

**`ImGui.NET`** (NuGet: `ImGui.NET`) provides the C# bindings to `cimgui` / Dear ImGui.
This package is **already transitively referenced** via `Silk.NET.OpenGL.Extensions.ImGui`.

Dear ImGui itself is rendering-API-agnostic — it produces vertex/index buffers and
draw commands. The "backend" is responsible for uploading those buffers and issuing
draw calls. Writing a Vulkan backend means implementing:

1. **Font atlas texture** — upload to a `Graphite.Texture` (or raw `VkImage`)
2. **Vertex/index buffers** — stream per-frame data into `Graphite.Buffer`
3. **Pipeline state** — a single graphics pipeline (vertex shader + fragment shader,
   alpha blending, scissor, dynamic viewport)
4. **Descriptor set / bind group** — font texture + sampler binding
5. **Render pass** — target the swapchain (or an offscreen `RenderTexture`)
6. **Draw loop** — iterate `ImDrawList` commands, set scissor, bind texture, draw indexed

This is the same pattern already implemented by `PaperRenderer` (UBO ring buffer,
per-drawcall texture bind groups, cached pipelines).

**Advantages:**
- Full control; uses existing Graphite abstractions (`CommandList`, `PipelineStateCache`, `BindGroup`)
- No new native dependencies beyond what's already linked (`cimgui` via ImGui.NET)
- Matches the engine's architecture (everything goes through Graphite)
- Can target both OpenGL and Vulkan through the same Graphite API
- Reference implementations available: `imgui_impl_vulkan.cpp` (Dear ImGui repo),
  Licht's `ImGuiController` (Silk.NET + Vulkan)

**Effort:** Medium-High (~800-1200 lines for the renderer class)

### Option B: Use `Licht` (JensKrumsieck/Licht) as a Reference/Wrapper

[Licht](https://github.com/JensKrumsieck/Licht) is an archived (read-only as of
April 2025) thin Vulkan abstraction for Silk.NET with an ImGui example. It:
- Uses `ImGui.NET` for bindings
- Implements a Vulkan ImGui renderer on top of Silk.NET.Vulkan
- Provides `VkGraphicsDevice`, `VkGraphicsPipeline`, `CommandBuffer` wrappers

**However**, Licht:
- Is **archived** (no maintenance, no updates)
- Uses its own Vulkan device/swapchain — conflicts with `VKGraphiteDevice`
- Would introduce a **second** Vulkan abstraction alongside Graphite
- Targets .NET 7 (Prowl targets .NET 9)

**Recommendation:** Use Licht's ImGui renderer source code as a **reference** for
understanding the Vulkan ImGui rendering pattern, but do **not** take a NuGet
dependency on it. Instead, implement the backend natively using Prowl's Graphite API.

### Option C: Use `veldrid-imgui` or `ImGuiBackend.Vulkan`

Other community options exist but have similar drawbacks (separate device management,
stale maintenance). The Graphite-native approach (Option A) is the cleanest fit.

---

## 3. Migration Plan

### Phase 0 — Preparation (No Runtime Changes)

- [ ] **Audit all `ImGui.Image()` / `ImGui.ImageButton()` call sites** in the editor.
      Each passes a raw GL texture handle (`nint`). Under Vulkan, these must be replaced
      with Vulkan descriptor set handles (or an engine-level texture ID mapping).
  - `ScenePanel.cs` line 79: `(nint)rt.MainTexture.Handle.Handle`
  - `GamePanel.cs` line 125: same pattern
  - `InspectorPanel.cs`: material preview thumbnails
  - `IconManager.cs`: icon textures
  - Any other `ImGui.Image*` call sites
- [ ] **Audit font loading** — `ImGuiController` currently loads fonts via
      `ImGuiFontConfig`. The Vulkan backend must upload the font atlas texture
      after `ImGui.GetIO().Fonts.Build()`.
- [ ] **Catalog ImGui input handling** — currently `ImGuiController.Update(delta)`
      forwards Silk.NET input to ImGui. This is backend-agnostic and can be reused.

### Phase 1 — `ImGuiRendererGraphite`: Graphite-Native ImGui Backend

Create a new class that replaces `Silk.NET.OpenGL.Extensions.ImGui.ImGuiController`
with a Graphite-based implementation.

#### 1.1 New File: `Prowl.Runtime\GUI\ImGuiRendererGraphite.cs`

```
public sealed class ImGuiRendererGraphite : IDisposable
{
    // Graphite resources
    private Graphite.Buffer _vertexBuffer;
    private Graphite.Buffer _indexBuffer;
    private Graphite.Texture _fontTexture;
    private Graphite.Sampler _fontSampler;
    private Graphite.BindGroupLayout _textureBGL;
    private Graphite.BindGroup _fontBindGroup;
    private Graphite.ShaderModule _vertexShader;
    private Graphite.ShaderModule _fragmentShader;
    
    // Per-frame texture bind group cache (for ImGui.Image() textures)
    private Dictionary<nint, Graphite.BindGroup> _textureBindGroups;
    
    // UBO for projection matrix
    private Graphite.Buffer _projectionUBO;
    private Graphite.BindGroupLayout _uboBGL;
    private Graphite.BindGroup _uboBindGroup;
    
    // Methods
    void Initialize(int width, int height);
    void UpdateFontAtlas();
    void NewFrame(float deltaTime, int fbWidth, int fbHeight);
    void RenderDrawData(ImDrawDataPtr drawData, CommandList cmd);
    
    // Texture registration for ImGui.Image()
    nint RegisterTexture(Graphite.Texture texture);
    void UnregisterTexture(nint id);
    
    void Dispose();
}
```

#### 1.2 Shaders

ImGui requires a minimal vertex/fragment shader:

**Vertex shader** (GLSL 450, cross-compiled to SPIR-V for Vulkan):
```glsl
#version 450
layout(set=0, binding=0) uniform Uniforms { mat4 projection; };
layout(location=0) in vec2 aPos;
layout(location=1) in vec2 aUV;
layout(location=2) in vec4 aColor;
layout(location=0) out vec2 vUV;
layout(location=1) out vec4 vColor;
void main() {
    vUV = aUV;
    vColor = aColor;
    gl_Position = projection * vec4(aPos, 0.0, 1.0);
}
```

**Fragment shader:**
```glsl
#version 450
layout(set=1, binding=0) uniform sampler2D sTexture;
layout(location=0) in vec2 vUV;
layout(location=1) in vec4 vColor;
layout(location=0) out vec4 fragColor;
void main() {
    fragColor = vColor * texture(sTexture, vUV);
}
```

These can be embedded as GLSL strings and cross-compiled at runtime via
`ShaderCrossCompiler.CompileGLSLToSPIRV()` (already available in the engine).
For OpenGL, the same GLSL source works directly.

#### 1.3 Vertex/Index Buffer Streaming

Each frame, `ImGui.GetDrawData()` provides vertex and index arrays. The backend
must upload them to GPU buffers. Strategy:

- Maintain a ring of vertex/index buffers (or resize-on-demand like `PaperRenderer`)
- Use `Graphite.Buffer` with `MemoryAccess.CpuToGpu` (mapped / staging)
- Upload via `Buffer.SetData()` or mapped pointer

#### 1.4 Draw Loop

```csharp
for each ImDrawList in drawData.CmdLists:
    upload vertices + indices
    for each ImDrawCmd in drawList:
        if cmd.UserCallback != null: invoke callback
        else:
            set scissor rect (cmd.ClipRect, adjusted for framebuffer scale)
            bind texture bind group (cmd.TextureId → registered Graphite.Texture)
            draw indexed (cmd.ElemCount, 1, cmd.IdxOffset, cmd.VtxOffset, 0)
```

#### 1.5 Texture Registration System

`ImGui.Image(texId, ...)` requires a `nint` texture ID. Under OpenGL, this is
the raw GL texture handle. Under Vulkan, ImGui backends typically use a descriptor
set handle or a custom ID mapped to a descriptor set.

**Design:**
- `ImGuiRendererGraphite` maintains a `Dictionary<nint, BindGroup>` mapping
  engine texture IDs to Graphite bind groups.
- `RegisterTexture(Graphite.Texture tex) → nint` creates a `BindGroup` with the
  texture + sampler and returns an opaque ID.
- The `RenderDrawData` loop looks up `cmd.TextureId` in this dictionary.
- For the font atlas, the bind group is pre-registered.

**Editor call sites must change from:**
```csharp
nint texId = (nint)rt.MainTexture.Handle.Handle;  // raw GL handle
ImGui.Image(texId, ...);
```
**To:**
```csharp
nint texId = ImGuiRendererGraphite.RegisterTexture(rt.GraphiteTexture);
ImGui.Image(texId, ...);
```

### Phase 2 — Input Handling (Backend-Agnostic)

The `Silk.NET.OpenGL.Extensions.ImGui.ImGuiController` bundles input handling with
GL rendering. We need to separate input forwarding:

#### 2.1 New File: `Prowl.Runtime\GUI\ImGuiInputHandler.cs`

Extract/rewrite the input forwarding from `ImGuiController`:
- Keyboard mapping (`Silk.NET.Input.Key` → `ImGuiKey`)
- Mouse position, buttons, scroll wheel
- Text input (char events)
- Gamepad (optional)
- Cursor shape feedback

This is largely mechanical — the mapping tables from `Silk.NET.OpenGL.Extensions.ImGui`
can be adapted (they're MIT-licensed). Alternatively, write from scratch using
`Window.InternalInput` (already available as `IInputContext`).

#### 2.2 DPI / Display Size

```csharp
var io = ImGui.GetIO();
io.DisplaySize = new Vector2(fbWidth, fbHeight);
io.DisplayFramebufferScale = new Vector2(1, 1); // Silk handles scaling
io.DeltaTime = deltaTime;
```

### Phase 3 — Integrate into `ImGuiManager`

Modify `Prowl.Runtime\ImGuiManager.cs`:

```csharp
public sealed class ImGuiManager : IDisposable
{
    // OLD (OpenGL-only):
    // private SilkImGui.ImGuiController? _controller;
    
    // NEW (backend-agnostic):
    private ImGuiRendererGraphite? _renderer;
    private ImGuiInputHandler? _inputHandler;
    
    public void Initialize()
    {
        // Remove the IsOpenGL guard
        // Works on both backends via Graphite
        
        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        
        // Load fonts
        string? systemFont = FindSystemFont();
        if (systemFont != null)
            ImGuiUIRenderer.LoadFonts(systemFont, DpiManager.Scale);
        
        _renderer = new ImGuiRendererGraphite();
        _renderer.Initialize(
            Window.InternalWindow.FramebufferSize.X,
            Window.InternalWindow.FramebufferSize.Y);
        
        _inputHandler = new ImGuiInputHandler(Window.InternalInput);
        
        IsReady = true;
    }
    
    public void Update(float delta)
    {
        _inputHandler?.Update(delta);
        ImGui.NewFrame();
    }
    
    public void Render()
    {
        ImGui.Render();
        var drawData = ImGui.GetDrawData();
        
        using var cmd = Graphics.CreateCommandBuffer("ImGui");
        // Begin render pass targeting swapchain (or current RT)
        _renderer.RenderDrawData(drawData, cmd);
        cmd.Submit();
    }
}
```

### Phase 4 — Update Editor Texture Handles

All `ImGui.Image()` call sites must use the new texture registration system.

#### 4.1 Helper Method on `ImGuiManager` or a Static Registry

```csharp
public static class ImGuiTextureRegistry
{
    private static readonly Dictionary<Graphite.Texture, nint> _registered = new();
    private static nint _nextId = 1;
    
    public static nint GetOrRegister(Graphite.Texture tex)
    {
        if (tex == null) return 0;
        if (!_registered.TryGetValue(tex, out var id))
        {
            id = _nextId++;
            _registered[tex] = id;
            ImGuiRendererGraphite.Instance.RegisterTexture(id, tex);
        }
        return id;
    }
}
```

#### 4.2 Update Call Sites

| File | Change |
|------|--------|
| `ScenePanel.cs` line 79 | `nint texId = ImGuiTextureRegistry.GetOrRegister(rt.GraphiteColorAttachments[0]);` |
| `GamePanel.cs` line 125 | Same pattern |
| `InspectorPanel.cs` | Same pattern for material preview textures |
| `IconManager.cs` | Register icon textures at load time |

### Phase 5 — Update Editor & Launcher Entry Points

#### 5.1 `Prowl.Editor\Program.cs`

```csharp
// BEFORE:
GraphicsBackendType backend = GraphicsBackendType.OpenGL;

// AFTER:
GraphicsBackendType backend = GraphicsBackendType.Vulkan;
// (with fallback to OpenGL already handled by Game.Run())
```

#### 5.2 `Prowl.Launcher\Program.cs`

The launcher also uses ImGui — same changes apply. `Game.Run()` already has
Vulkan → OpenGL fallback logic.

### Phase 6 — Remove `Silk.NET.OpenGL.Extensions.ImGui` Dependency

Once `ImGuiRendererGraphite` is proven:

- [ ] Remove `Silk.NET.OpenGL.Extensions.ImGui` from `Prowl.Runtime.csproj`
- [ ] Remove `using SilkImGui = Silk.NET.OpenGL.Extensions.ImGui;` from `ImGuiManager.cs`
- [ ] Ensure `ImGui.NET` is referenced directly (it's currently a transitive dep)
- [ ] Clean up any remaining `Graphics.IsOpenGL` guards related to ImGui

### Phase 7 — Handle Swapchain Integration

ImGui rendering must compose with the existing render pipeline:

1. Scene/game views render into `RenderTexture` objects (offscreen)
2. ImGui renders to the **swapchain** as the last pass of the frame
3. ImGui's draw data includes the scene/game view textures via `ImGui.Image()`

In the Vulkan backend, the render pass that draws ImGui must:
- Use `LoadOp.Load` if the swapchain was already rendered to (e.g., by Paper UI)
- Use `LoadOp.Clear` if ImGui is the first pass to touch the swapchain
- The `Graphics.SwapchainClearedThisFrame` flag (already tracked) handles this

#### Render Order in `Game.cs` (lines 240-313):

```
1. BeginRender()      — scene/game views render to RenderTextures
2. RenderScenes()     — DefaultRenderPipeline renders + blits to swapchain (standalone)
3. EndRender()
4. Paper UI           — renders to swapchain via Graphite
5. ImGui              — renders to swapchain via Graphite ← NEW
```

For the editor, `RenderScenes()` is overridden to no-op (line 385), so scene
rendering only targets offscreen `RenderTexture` objects. ImGui composites them
as images in the dockspace.

---

## 4. Detailed Implementation Notes

### 4.1 Pipeline State for ImGui

```csharp
var pipelineDesc = new GraphicsPipelineDescriptor
{
    VertexLayout = ImGuiVertexLayout,  // pos(float2), uv(float2), color(u8x4 normalized)
    Topology = PrimitiveTopology.TriangleList,
    Rasterizer = new RasterizerStateDescriptor
    {
        CullMode = CullMode.None,
        FrontFace = FrontFace.CounterClockwise,
    },
    DepthStencil = new DepthStencilStateDescriptor
    {
        DepthTestEnabled = false,
        DepthWriteEnabled = false,
    },
    Blend = new BlendStateDescriptor
    {
        Enabled = true,
        SrcColor = BlendFactor.SrcAlpha,
        DstColor = BlendFactor.OneMinusSrcAlpha,
        ColorOp = BlendOp.Add,
        SrcAlpha = BlendFactor.One,
        DstAlpha = BlendFactor.OneMinusSrcAlpha,
        AlphaOp = BlendOp.Add,
    },
};
```

### 4.2 ImGui Vertex Format

```csharp
var ImGuiVertexLayout = new VertexLayoutDescriptor(new[]
{
    new VertexAttribute(VertexFormat.Float2, 0),   // Position
    new VertexAttribute(VertexFormat.Float2, 8),   // UV
    new VertexAttribute(VertexFormat.UByte4Norm, 16), // Color (packed RGBA)
}, stride: 20);
```

ImGui's `ImDrawVert` is 20 bytes: `float2 pos, float2 uv, uint32 color`.

### 4.3 Scissor Handling

ImGui provides clip rectangles per draw command. These must be translated to
Vulkan scissor rects:

```csharp
cmd.SetScissor(
    (int)(clipRect.X - clipOff.X),
    (int)(clipRect.Y - clipOff.Y),
    (uint)(clipRect.Z - clipRect.X),
    (uint)(clipRect.W - clipRect.Y));
```

### 4.4 Font Atlas Upload

```csharp
ImGui.GetIO().Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height);

var fontTexDesc = new TextureDescriptor
{
    Width = (uint)width,
    Height = (uint)height,
    Format = TextureFormat.RGBA8Unorm,
    Usage = TextureUsage.Sampled | TextureUsage.CopyDestination,
};
_fontTexture = device.CreateTexture(fontTexDesc);
_fontTexture.SetData(new ReadOnlySpan<byte>(pixels.ToPointer(), width * height * 4));

ImGui.GetIO().Fonts.SetTexID(_fontBindGroupId);
```

---

## 5. Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| ImGui vertex format mismatch | Garbled UI rendering | Use `sizeof(ImDrawVert)` assertions; validate layout matches |
| Texture registration leaks | Memory growth | Track frame-lifetime; auto-unregister stale textures |
| Scissor/clip rect off-by-one | Visual glitches | Test with complex docked layouts; compare with GL reference |
| Swapchain format mismatch | Incorrect blending | Ensure ImGui pipeline's render pass format matches `_swapchainFormat` |
| Multi-viewport (future) | Secondary windows | Defer — single-viewport first; multi-viewport needs per-window swapchain |
| `Silk.NET.OpenGL.Extensions.ImGui` removal breaks transitive `ImGui.NET` | Build failure | Add explicit `ImGui.NET` package reference before removing Silk ext |
| Performance regression (buffer uploads per frame) | Frame drops | Use persistent mapped buffers; ring buffer strategy (like PaperRenderer) |
| Gamepad input | Missing bindings | Low priority — keyboard/mouse first; gamepad is rarely used in editor |

---

## 6. File Change Summary

| File | Action |
|------|--------|
| **NEW** `Prowl.Runtime\GUI\ImGuiRendererGraphite.cs` | Graphite-based ImGui renderer (~800-1200 lines) |
| **NEW** `Prowl.Runtime\GUI\ImGuiInputHandler.cs` | Backend-agnostic input forwarding (~200-300 lines) |
| **NEW** `Prowl.Runtime\GUI\ImGuiTextureRegistry.cs` | Texture ID ↔ BindGroup mapping (~100 lines) |
| **NEW** `Prowl.Runtime\Assets\Defaults\ImGui.vert.glsl` | ImGui vertex shader (or inline string) |
| **NEW** `Prowl.Runtime\Assets\Defaults\ImGui.frag.glsl` | ImGui fragment shader (or inline string) |
| **MODIFY** `Prowl.Runtime\ImGuiManager.cs` | Replace `ImGuiController` with `ImGuiRendererGraphite` + `ImGuiInputHandler` |
| **MODIFY** `Prowl.Runtime\Game.cs` | Update ImGui render pass to use Graphite command list |
| **MODIFY** `Prowl.Runtime\Prowl.Runtime.csproj` | Add explicit `ImGui.NET` reference; eventually remove `Silk.NET.OpenGL.Extensions.ImGui` |
| **MODIFY** `Prowl.Editor\Program.cs` | Change backend from `OpenGL` to `Vulkan` |
| **MODIFY** `Prowl.Editor\Panels\ScenePanel.cs` | Use `ImGuiTextureRegistry` for render texture display |
| **MODIFY** `Prowl.Editor\Panels\GamePanel.cs` | Same |
| **MODIFY** `Prowl.Editor\Panels\InspectorPanel.cs` | Same for preview textures |
| **MODIFY** `Prowl.Editor\Icons\IconManager.cs` | Register icon textures via `ImGuiTextureRegistry` |
| **MODIFY** `Prowl.Launcher\LauncherApplication.cs` | No code changes needed (uses ImGui via `Game` base class) |

---

## 7. Execution Order & Dependencies

```
Phase 0  (Audit)
   │
   ▼
Phase 1  (ImGuiRendererGraphite)  ← core implementation
   │
   ├──► Phase 2  (ImGuiInputHandler)  ← can be parallel
   │
   ▼
Phase 3  (Integrate into ImGuiManager)
   │
   ▼
Phase 4  (Update editor texture handles)
   │
   ▼
Phase 5  (Update entry points → Vulkan default)
   │
   ▼
Phase 6  (Remove Silk.NET.OpenGL.Extensions.ImGui)
   │
   ▼
Phase 7  (Swapchain integration polish)
```

**Phases 1 and 2 can be developed in parallel.** Phase 3 depends on both.
Phase 4 is the largest editor-wide refactor (many files touched).
Phase 5 is a one-line change but requires all prior phases to be stable.

---

## 8. References

- [Dear ImGui Vulkan backend](https://github.com/ocornut/imgui/blob/master/backends/imgui_impl_vulkan.cpp) — canonical C++ reference implementation
- [Licht ImGui example](https://github.com/JensKrumsieck/Licht/tree/master/Examples) — C# / Silk.NET / Vulkan ImGui renderer (archived, MIT license)
- [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET) — .NET bindings for Dear ImGui
- [Prowl PaperRenderer](Prowl.Runtime/GUI/PaperRenderer.cs) — existing Graphite-native UI renderer (same pattern to follow)
- [Prowl GRAPHITE_MIGRATION.md](GRAPHITE_MIGRATION.md) — overall Graphite migration status
