# TextMesh Pro–Style Text Rendering for Prowl Engine

## Implementation Plan

> **Goal:** Deliver a feature-complete, TMPro-equivalent rich-text component for the Prowl engine, covering the full pipeline from font asset generation through GPU-accelerated SDF rendering to an ergonomic `MonoBehaviour` API usable in both world-space and screen-space contexts.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Phase 0 — Prerequisites & Groundwork](#2-phase-0--prerequisites--groundwork)
3. [Phase 1 — Font Asset Pipeline](#3-phase-1--font-asset-pipeline)
4. [Phase 2 — SDF Font Atlas Generation](#4-phase-2--sdf-font-atlas-generation)
5. [Phase 3 — Text Shaping & Layout Engine](#5-phase-3--text-shaping--layout-engine)
6. [Phase 4 — Mesh Generation](#6-phase-4--mesh-generation)
7. [Phase 5 — SDF Shader](#7-phase-5--sdf-shader)
8. [Phase 6 — The `TextRenderer` MonoBehaviour](#8-phase-6--the-textrenderer-monobehaviour)
9. [Phase 7 — Rich Text Markup Parser](#9-phase-7--rich-text-markup-parser)
10. [Phase 8 — Text Effects & Animations](#10-phase-8--text-effects--animations)
11. [Phase 9 — Font Fallbacks & Dynamic Atlas Expansion](#11-phase-9--font-fallbacks--dynamic-atlas-expansion)
12. [Phase 10 — UI / Screen-Space Integration](#12-phase-10--ui--screen-space-integration)
13. [Phase 11 — Editor Tooling](#13-phase-11--editor-tooling)
14. [Phase 12 — Testing & Benchmarking](#14-phase-12--testing--benchmarking)
15. [Phase 13 — Polish & Optimization](#15-phase-13--polish--optimization)
16. [Event System Integration](#16-event-system-integration)
17. [Appendix A — File & Namespace Layout](#appendix-a--file--namespace-layout)
18. [Appendix B — Risk Register](#appendix-b--risk-register)
19. [Appendix C — Progress Tracker](#appendix-c--progress-tracker)

---

## 1. Architecture Overview

```
┌──────────────────────────────────────────────────────────────────┐
│  Editor (Prowl.Editor)                                           │
│  ┌─────────────────────────┐  ┌──────────────────────────────┐   │
│  │  FontAssetImporter       │  │  TextRenderer Inspector      │   │
│  │  (imports .ttf/.otf)     │  │  (rich text preview, style   │   │
│  │                          │  │   editing, glyph debugger)   │   │
│  └─────────┬───────────────┘  └──────────────────────────────┘   │
│            │ produces                                             │
├────────────┼─────────────────────────────────────────────────────┤
│  Runtime (Prowl.Runtime)     │                                   │
│            ▼                                                     │
│  ┌─────────────────────┐     ┌──────────────────────────────┐    │
│  │  FontAsset           │────▶│  FontAtlas (Texture2D)       │    │
│  │  (ScriptableObject)  │     │  + glyph metrics table       │    │
│  │  - GlyphTable         │     │  + kerning pairs             │    │
│  │  - KerningTable       │     └──────────────────────────────┘    │
│  │  - FallbackFonts[]    │                                        │
│  └────────┬────────────┘                                         │
│           │ consumed by                                           │
│           ▼                                                       │
│  ┌─────────────────────────────────────────────────────────┐      │
│  │  TextShaper                                              │     │
│  │  - Unicode bidi / line-break                             │     │
│  │  - Glyph lookup + kerning                                │     │
│  │  - Rich-text tag parsing → style runs                    │     │
│  └───────────┬─────────────────────────────────────────────┘     │
│              │ produces TextLayout                                │
│              ▼                                                    │
│  ┌───────────────────────────────────────────────────────────┐   │
│  │  TextMeshBuilder                                          │   │
│  │  - Generates quad-per-glyph Mesh                          │   │
│  │  - UV mapping into atlas                                  │   │
│  │  - Per-vertex color, underline/strikethrough geometry     │   │
│  └───────────┬───────────────────────────────────────────────┘   │
│              │                                                    │
│              ▼                                                    │
│  ┌───────────────────────────────────────────────────────────┐   │
│  │  TextRenderer : MonoBehaviour, IRenderable                │   │
│  │  - World-space 3D text                                    │   │
│  │  - Manages Mesh + Material (SDF shader)                   │   │
│  │  - PropertyState for per-instance uniforms                │   │
│  └───────────────────────────────────────────────────────────┘   │
│                                                                   │
│  ┌───────────────────────────────────────────────────────────┐   │
│  │  UITextRenderer  (future: integrates with Prowl.Quill UI) │   │
│  └───────────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────────┘
```

### Guiding Principles

- **SDF-first rendering** — Multi-channel signed distance field (MSDF) for sharp text at any scale, with single-channel SDF fallback for simpler use cases.
- **Asset pipeline integration** — Fonts are imported through the editor as `.fontasset` ScriptableObjects, just like materials or meshes.
- **Minimal allocations at render time** — The mesh is rebuilt only when text or layout parameters change, not every frame.
- **Pluggable shaping** — Start with a basic built-in shaper; design the interface so HarfBuzz can be swapped in later for complex scripts (Arabic, Devanagari, CJK vertical).
- **Rich text via inline tags** — `<b>`, `<i>`, `<color=#FF0000>`, `<size=24>`, `<font="AltFont">`, `<sprite=0>`, etc.

---

## 2. Phase 0 — Prerequisites & Groundwork

### 2.1 Add NuGet Dependencies

| Package | Purpose | Target |
|---------|---------|--------|
| `FreeTypeSharp` or `SharpFont` | TrueType/OpenType font file parsing and glyph outline extraction | `Prowl.Runtime` (or a new `Prowl.Text` library) |
| `msdfgen` (native) or `Msdf.NET` | MSDF atlas generation from glyph outlines | Editor-only (atlas is baked offline) |

> **Decision point:** If we want zero native dependencies in the runtime, atlas generation stays editor-only and the runtime only consumes the baked `Texture2D` + metadata. This is the recommended approach matching TMPro's model.

### 2.2 Define the `DefaultShader.SDF` Enum Entry

Add `SDF` to `DefaultShader` in `Prowl.Runtime/Resources/DefaultAssets.cs`. This shader will be the built-in SDF text shader shipped with the engine.

### 2.3 Vertex Format Extension

The text mesh needs per-vertex data beyond the standard `MeshRenderer` path:

| Attribute | Type | Purpose |
|-----------|------|---------|
| `Position` | `Float3` | Quad corner in local space |
| `UV0` | `Float2` | Atlas UV for the glyph |
| `UV1` | `Float2` | SDF parameters (outline width, softness) or sprite-sheet index |
| `Color` | `Color32` | Per-character vertex color |
| `Normal` | `Float3` | Face normal (for world-space lighting on 3D text) |
| `Tangent` | `Float4` | For normal-mapped text effects |

Verify the existing `VertexFormat` / `Mesh` API supports setting custom UV1 and per-vertex Color32. If not, extend `Mesh` to support a second UV channel and per-vertex color.

---

## 3. Phase 1 — Font Asset Pipeline

### 3.1 `FontAsset` (Runtime — ScriptableObject)

**File:** `Prowl.Runtime/Resources/FontAsset.cs`

```
[CreateAssetMenu(MenuName = "Text/Font Asset", FileName = "NewFontAsset")]
public class FontAsset : ScriptableObject
```

**Serialized data:**

| Field | Type | Description |
|-------|------|-------------|
| `_atlasTexture` | `Texture2D` | The baked MSDF atlas |
| `_atlasWidth`, `_atlasHeight` | `int` | Atlas dimensions |
| `_atlasType` | `AtlasType` enum | `SDF`, `MSDF`, `Bitmap` |
| `_pointSize` | `float` | Font size used during atlas generation |
| `_padding` | `int` | Pixel padding around each glyph in the atlas |
| `_atlasPxRange` | `float` | SDF distance range in pixels (for shader) |
| `_lineHeight` | `float` | Default line height in font units |
| `_ascender`, `_descender`, `_baseline` | `float` | Vertical metrics |
| `_glyphTable` | `GlyphData[]` | Indexed glyph metrics |
| `_characterTable` | `Dictionary<uint, int>` | Unicode codepoint → glyph index |
| `_kerningPairs` | `Dictionary<ulong, float>` | Packed pair key → kerning advance |
| `_fallbackFonts` | `FontAsset[]` | Fallback chain for missing glyphs |

**`GlyphData` struct:**

```
public readonly record struct GlyphData(
    uint   GlyphIndex,
    float  Width,
    float  Height,
    float  BearingX,
    float  BearingY,
    float  Advance,
    float  AtlasX,      // UV rect in atlas
    float  AtlasY,
    float  AtlasWidth,
    float  AtlasHeight,
    float  Scale         // em-to-pixel scale at bake time
);
```

### 3.2 `FontAssetImporter` (Editor)

**File:** `Prowl.Editor/AssetImporters/FontAssetImporter.cs`

Responsible for:

1. Reading `.ttf` / `.otf` files via FreeType.
2. Extracting glyph outlines for a configurable character set (ASCII, Latin Extended, CJK ranges, or custom).
3. Packing glyphs into an optimally-sized atlas using a rect-packing algorithm (Skyline Bottom-Left or MaxRects).
4. Generating the SDF/MSDF distance field for each glyph (via msdfgen or a managed port).
5. Serializing the result into a `FontAsset` ScriptableObject + the `Texture2D` atlas.

**Inspector for the importer should expose:**

- Character set selection (preset ranges + custom string).
- Atlas resolution (auto-fit, 512, 1024, 2048, 4096).
- Atlas type (SDF / MSDF / Bitmap).
- SDF pixel range.
- Padding.
- Sampling point size.
- Render mode (hinted / smooth / mono).
- Preview of the generated atlas.

---

## 4. Phase 2 — SDF Font Atlas Generation

### 4.1 Rect Packing

Implement or vendor a rect-packer. Skyline BL is simple and efficient. Interface:

```csharp
public class RectPacker
{
    public RectPacker(int width, int height);
    public bool TryPack(int w, int h, out int x, out int y);
}
```

### 4.2 SDF Generation

For each glyph:

1. Rasterize at high resolution or extract Bézier contours from FreeType.
2. For **SDF**: compute the signed distance from each texel center to the nearest contour edge.
3. For **MSDF**: use the multi-channel approach (R/G/B encode distance to different edge segments) for sharper corners.
4. Normalize distances into the `[0, 1]` range based on `_atlasPxRange`.
5. Write into the atlas `Texture2D` at the rect position.

**Output format:**

| Atlas Type | Texture Format | Channels |
|-----------|---------------|----------|
| SDF | `R8` (single channel) | R = distance |
| MSDF | `Color3b` (RGB8) | R/G/B = per-edge distance |
| Bitmap | `Color4b` (RGBA8) | Standard rasterized glyph |

### 4.3 Dynamic Atlas (Runtime — Phase 9)

Deferred to Phase 9. The initial implementation uses fully pre-baked atlases.

---

## 5. Phase 3 — Text Shaping & Layout Engine

### 5.1 `TextShaper` (Runtime)

**File:** `Prowl.Runtime/Text/TextShaper.cs`

Takes input text + `FontAsset` + layout constraints → produces a `TextLayout`.

**Responsibilities:**

1. **Character → Glyph mapping** — look up each codepoint in `FontAsset._characterTable`, fall back through `_fallbackFonts`.
2. **Kerning** — apply pair-based kerning from `FontAsset._kerningPairs`.
3. **Word wrapping** — respect `maxWidth` and wrapping mode (word / character / none / truncate + ellipsis).
4. **Line breaking** — split into lines, compute per-line width for alignment.
5. **Alignment** — left / center / right / justified.
6. **Vertical layout** — top / middle / bottom baseline anchoring.
7. **Rich text style runs** — apply bold/italic/color/size overrides from the parsed rich text stream (Phase 7 feeds into here).

**`TextLayout` output struct:**

```csharp
public struct TextLayout
{
    public GlyphPlacement[] Glyphs;       // positioned glyphs
    public LineInfo[] Lines;              // per-line metadata
    public Float2 TextBounds;             // total bounding box
    public Float2 PreferredSize;          // size if unconstrained
}

public struct GlyphPlacement
{
    public int   GlyphIndex;             // into FontAsset glyph table
    public int   FontAssetIndex;         // 0 = primary, 1+ = fallback
    public Float2 Position;              // baseline-relative position
    public Float2 Scale;                 // for size overrides
    public Color  Color;                 // per-character color
    public int   StyleFlags;             // bold | italic | underline | strikethrough
    public int   LineIndex;              // which line this glyph belongs to
    public int   CharacterIndex;         // index into source string
}

public struct LineInfo
{
    public int   StartGlyphIndex;
    public int   GlyphCount;
    public float Width;
    public float Ascender;
    public float Descender;
    public float Baseline;
}
```

### 5.2 Overflow Modes

| Mode | Behavior |
|------|----------|
| `Overflow` | Text extends beyond the rect (default) |
| `WordWrap` | Break at word boundaries |
| `CharacterWrap` | Break at character boundaries |
| `Truncate` | Cut off at boundary |
| `Ellipsis` | Cut off + append `…` |
| `Page` | Paginated (for dialogue systems) |
| `ScrollRect` | Scrollable (for console/chat) |

---

## 6. Phase 4 — Mesh Generation

### 6.1 `TextMeshBuilder` (Runtime)

**File:** `Prowl.Runtime/Text/TextMeshBuilder.cs`

Converts a `TextLayout` into a `Mesh` object compatible with Prowl's rendering pipeline.

**Algorithm per glyph:**

1. Read `GlyphData` from the `FontAsset`.
2. Compute 4 corner positions from placement position + bearing + size.
3. Compute UV0 from atlas rect.
4. Optionally compute UV1 for SDF parameters per-vertex.
5. Set per-vertex color from `GlyphPlacement.Color`.
6. Emit 4 vertices + 6 indices (two triangles).

**Additional geometry:**

- **Underline:** A thin quad strip along the underline position of each style run.
- **Strikethrough:** Same, at the midline.
- **Background highlight:** A filled quad behind text runs (for selection or `<mark>` tags).

**Optimizations:**

- Reuse a scratch `List<Float3>` / `List<Float2>` / `List<Color>` / `List<uint>` and call `Mesh.SetVertices()` / `Mesh.SetUVs()` etc. to avoid per-frame allocations.
- Set `Mesh.IndexFormat = IndexFormat.UInt16` when glyph count × 4 < 65535, else fall back to `UInt32`.
- Assign a single `SubMeshDescriptor` covering all text geometry (or multiple if mixing atlas textures from fallback fonts).

### 6.2 Dirty Tracking

The `TextRenderer` component should only rebuild the mesh when:

- `Text` string changes.
- `FontAsset` changes.
- Layout parameters (size, alignment, wrapping mode) change.
- `Transform` scale changes (for auto-sizing).
- Rich text tags cause a different style run layout.

Use a hash or version counter on each mutable property. On `LateUpdate`, if dirty, rebuild.

---

## 7. Phase 5 — SDF Shader

### 7.1 Shader File

**File:** `Prowl.Runtime/Rendering/DefaultShaders/SDF.shader` (or wherever the engine's default shaders live)

The shader needs to work with both the OpenGL and Vulkan backends via the Prowl shader format parsed by `ShaderParser`.

**Passes:**

| Pass | Tag | Purpose |
|------|-----|---------|
| `SDFText` | `RenderType=Transparent` | Main text rendering |
| `SDFTextOutline` | `RenderType=Transparent` | Outline-only pass (optional) |

**Vertex shader inputs:**

```glsl
layout(location = 0) in vec3 _position;
layout(location = 1) in vec2 _texCoord0;   // atlas UV
layout(location = 2) in vec2 _texCoord1;   // SDF params (outline, softness)
layout(location = 3) in vec4 _color;        // per-vertex color
```

**Fragment shader — SDF core:**

```glsl
uniform sampler2D _FontAtlas;
uniform float _PxRange;         // from FontAsset.AtlasPxRange
uniform vec4 _FaceColor;        // base face tint
uniform vec4 _OutlineColor;     // outline tint
uniform float _OutlineWidth;    // 0..0.5 in SDF space
uniform float _Softness;        // anti-aliasing softness

float median(float r, float g, float b) {
    return max(min(r, g), min(max(r, g), b));
}

void main() {
    vec3 sdf = texture(_FontAtlas, uv).rgb;
    float sd = median(sdf.r, sdf.g, sdf.b);

    // Convert distance to screen-space pixels for resolution-independent AA
    float screenPxDistance = _PxRange * (sd - 0.5);
    float opacity = clamp(screenPxDistance / fwidth(screenPxDistance) + 0.5, 0.0, 1.0);

    // Outline
    float outlineOpacity = clamp(
        (_PxRange * (sd - 0.5 + _OutlineWidth)) / fwidth(screenPxDistance) + 0.5,
        0.0, 1.0
    );

    vec4 faceColor = _FaceColor * vertexColor;
    vec4 finalColor = mix(_OutlineColor, faceColor, opacity);
    finalColor.a *= outlineOpacity;

    if (finalColor.a < 0.001) discard;
    fragColor = finalColor;
}
```

**Shader properties (exposed via `PropertyState`):**

| Property | Type | Default |
|----------|------|---------|
| `_FontAtlas` | `Texture2D` | — |
| `_PxRange` | `float` | 4.0 |
| `_FaceColor` | `Color` | White |
| `_OutlineColor` | `Color` | Black |
| `_OutlineWidth` | `float` | 0.0 |
| `_Softness` | `float` | 0.0 |
| `_UnderlayColor` | `Color` | transparent |
| `_UnderlayOffsetX` | `float` | 0.0 |
| `_UnderlayOffsetY` | `float` | 0.0 |
| `_UnderlayDilate` | `float` | 0.0 |
| `_UnderlaySoftness` | `float` | 0.0 |

### 7.2 Rasterizer State

- **Blend:** `SrcAlpha` / `OneMinusSrcAlpha` (standard transparency).
- **Depth write:** Off (transparent text should not write depth).
- **Depth test:** LessEqual (for world-space text that respects scene depth).
- **Cull:** Back (or None for two-sided billboards).
- **Tag:** `RenderType=Transparent`, `Queue=Transparent` to integrate with `DefaultRenderPipeline` transparent pass.

---

## 8. Phase 6 — The `TextRenderer` MonoBehaviour

### 8.1 Component Design

**File:** `Prowl.Runtime/Components/TextRenderer.cs`

```csharp
[AddComponentMenu("Rendering/Text Renderer")]
public class TextRenderer : MonoBehaviour, IRenderable
{
    // --- Serialized Fields ---
    [SerializeField] private string _text = "New Text";
    [SerializeField] private FontAsset _font;
    [SerializeField] private float _fontSize = 36f;
    [SerializeField] private Color _color = Color.White;
    [SerializeField] private TextAlignment _alignment = TextAlignment.Left;
    [SerializeField] private VerticalAlignment _verticalAlignment = VerticalAlignment.Top;
    [SerializeField] private TextOverflowMode _overflow = TextOverflowMode.Overflow;
    [SerializeField] private Float2 _rectSize = new(10f, 10f);
    [SerializeField] private bool _richText = true;
    [SerializeField] private bool _autoSize = false;
    [SerializeField] private float _autoSizeMin = 10f;
    [SerializeField] private float _autoSizeMax = 72f;
    [SerializeField] private float _characterSpacing = 0f;
    [SerializeField] private float _lineSpacing = 0f;
    [SerializeField] private float _paragraphSpacing = 0f;
    [SerializeField] private float _wordSpacing = 0f;

    // --- Outline & Effects ---
    [SerializeField] private float _outlineWidth = 0f;
    [SerializeField] private Color _outlineColor = Color.Black;
    [SerializeField] private float _softness = 0f;

    // --- Underlay (drop shadow) ---
    [SerializeField] private Color _underlayColor = new(0, 0, 0, 0.5f);
    [SerializeField] private Float2 _underlayOffset = Float2.Zero;
    [SerializeField] private float _underlayDilate = 0f;
    [SerializeField] private float _underlaySoftness = 0f;

    // --- Runtime State (not serialized) ---
    private Mesh _mesh;
    private Material _material;
    private PropertyState _properties;
    private TextLayout _layout;
    private bool _isDirty = true;
    private int _textHash;
}
```

### 8.2 Lifecycle

| Method | Action |
|--------|--------|
| `OnEnable` | Create/allocate `Mesh`, `Material` (from `DefaultShader.SDF`), `PropertyState`. Mark dirty. |
| `Update` | Check dirty flag. If dirty, run `TextShaper` → `TextMeshBuilder` → update `Mesh`. Push `IRenderable` to scene via `GameObject.Scene.PushRenderable(this)`. |
| `OnDisable` | Dispose `Mesh`, `Material`. |
| `OnValidate` (editor) | Mark dirty when inspector values change. |

### 8.3 `IRenderable` Implementation

```csharp
public Material GetMaterial() => _material;
public int GetLayer() => GameObject.LayerIndex;
public Float3 GetPosition() => Transform.Position;

public void GetRenderingData(
    ViewerData viewer,
    out PropertyState properties,
    out Mesh mesh,
    out Float4x4 model,
    out InstanceData[]? instanceData)
{
    properties = _properties;
    mesh = _mesh;
    model = Transform.LocalToWorldMatrix;
    instanceData = null;
}

public void GetCullingData(out bool isRenderable, out AABB bounds)
{
    isRenderable = _font.IsValid() && !string.IsNullOrEmpty(_text);
    bounds = _mesh?.bounds ?? default;
}
```

### 8.4 Public API Surface

| Property / Method | Description |
|-------------------|-------------|
| `string Text { get; set; }` | The displayed text. Setting marks dirty. |
| `FontAsset Font { get; set; }` | Primary font asset. |
| `float FontSize { get; set; }` | Size in world units (or points for UI). |
| `Color Color { get; set; }` | Base face color. |
| `TextAlignment Alignment { get; set; }` | Horizontal alignment. |
| `TextOverflowMode Overflow { get; set; }` | Wrapping / overflow behavior. |
| `Float2 RectSize { get; set; }` | Layout constraint box. |
| `bool RichText { get; set; }` | Enable/disable rich text parsing. |
| `TextLayout GetTextLayout()` | Returns the last computed layout (for hit-testing, caret positioning). |
| `int GetCharacterIndexAtPosition(Float2 localPos)` | Hit-test for input fields. |
| `Float2 GetCursorPosition(int charIndex)` | For caret rendering. |
| `void ForceMeshUpdate()` | Rebuild immediately (skip lazy dirty check). |
| `Mesh GetMesh()` | Access the generated mesh directly. |

---

## 9. Phase 7 — Rich Text Markup Parser

### 9.1 Tag Specification

**File:** `Prowl.Runtime/Text/RichTextParser.cs`

Parse inline XML-like tags from the text string and produce a list of style runs.

**Supported tags (TMPro parity):**

| Tag | Example | Effect |
|-----|---------|--------|
| `<b>` | `<b>bold</b>` | Bold weight |
| `<i>` | `<i>italic</i>` | Italic style |
| `<u>` | `<u>underline</u>` | Underline decoration |
| `<s>` | `<s>strike</s>` | Strikethrough |
| `<color>` | `<color=#FF0000>red</color>` | Per-character color |
| `<size>` | `<size=24>big</size>` | Font size override |
| `<font>` | `<font="AltFont">text</font>` | Font asset swap |
| `<mark>` | `<mark=#FFFF00AA>highlight</mark>` | Background highlight |
| `<alpha>` | `<alpha=#80>semi</alpha>` | Alpha override |
| `<cspace>` | `<cspace=2>spaced</cspace>` | Character spacing |
| `<mspace>` | `<mspace=12>mono</mspace>` | Monospace override |
| `<line-height>` | `<line-height=120%>` | Line height override |
| `<sprite>` | `<sprite=0>` | Inline sprite from sprite atlas |
| `<link>` | `<link="id">click</link>` | Clickable link region |
| `<sup>` / `<sub>` | `<sup>2</sup>` | Superscript / subscript |
| `<noparse>` | `<noparse><b></noparse>` | Escape tags |
| `<br>` | `line1<br>line2` | Explicit line break |
| `<indent>` | `<indent=20>` | Left indent |
| `<align>` | `<align=center>` | Per-paragraph alignment |

### 9.2 Parser Output

```csharp
public struct StyleRun
{
    public int StartIndex;         // char index into stripped (no-tag) text
    public int Length;
    public FontAsset? FontOverride;
    public float? SizeOverride;
    public Color? ColorOverride;
    public float? AlphaOverride;
    public int StyleFlags;         // Bold | Italic | Underline | Strikethrough | Superscript | Subscript
    public float? CharacterSpacingOverride;
    public float? MonoSpaceOverride;
    public string? LinkId;
    public int? SpriteIndex;
    public Color? MarkColor;
}

public struct ParsedText
{
    public string StrippedText;      // text with all tags removed
    public StyleRun[] Runs;
}
```

### 9.3 Parser Design

- Single-pass forward scanner; no regex for performance.
- Stack-based nesting (push on open tag, pop on close tag).
- Produce `ParsedText` which feeds into `TextShaper`.
- Tags that are malformed or unrecognized are rendered as literal text.
- `<noparse>` disables parsing until `</noparse>`.

---

## 10. Phase 8 — Text Effects & Animations

### 10.1 Per-Vertex Animation System

**File:** `Prowl.Runtime/Text/TextEffect.cs`

Abstract base class for text effects that modify the mesh post-generation:

```csharp
public abstract class TextEffect
{
    public abstract void Apply(
        Span<Float3> positions,
        Span<Color> colors,
        Span<Float2> uvs,
        TextLayout layout,
        float time);
}
```

### 10.2 Built-in Effects

| Effect | Description |
|--------|-------------|
| `WaveEffect` | Sinusoidal vertical oscillation per character |
| `ShakeEffect` | Random position jitter per character |
| `FadeInEffect` | Per-character alpha fade with configurable delay |
| `TypewriterEffect` | Reveal characters over time (for dialogue) |
| `RainbowEffect` | HSV-cycling per-character color |
| `ScaleEffect` | Per-character scale pulse |
| `RotateEffect` | Per-character Z rotation |

### 10.3 Rich Text Integration

Effects can be triggered via tags: `<wave>wavy text</wave>`, `<shake a=5>shaky</shake>`.

The parser emits effect markers in the `StyleRun` list. During mesh update, active effects are applied as a post-pass.

---

## 11. Phase 9 — Font Fallbacks & Dynamic Atlas Expansion

### 11.1 Fallback Chain

When a codepoint is not found in the primary `FontAsset`, walk the `_fallbackFonts` array. If found in a fallback:

- The glyph is sourced from that fallback's atlas.
- The mesh builder emits a separate sub-mesh for each unique atlas texture.
- The `TextRenderer` pushes one `IRenderable` per sub-mesh (same pattern as `MeshRenderer` multi-material).

### 11.2 Dynamic Atlas (Runtime)

For games that need user-input text with unpredictable codepoints (chat, localization):

1. Ship the `.ttf` file data in the `FontAsset`.
2. At runtime, when a missing codepoint is encountered:
   a. Rasterize the glyph using FreeType (runtime dependency).
   b. Insert into a growable atlas region.
   c. Update the `Texture2D` subregion.
   d. Add to `_glyphTable` and `_characterTable`.
3. Use a LRU cache to evict rarely-used glyphs if the atlas is full.

> **Trade-off:** This adds a runtime FreeType dependency. Consider making this opt-in via a separate `DynamicFontAtlas` class, keeping the core `FontAsset` static-only.

---

## 12. Phase 10 — UI / Screen-Space Integration

### 12.1 `UITextRenderer`

For integration with Prowl's UI system (Quill / PaperRenderer), provide a `UITextRenderer` that renders text as screen-space quads using the same `TextShaper` + `TextMeshBuilder` pipeline but outputs into the UI draw list instead of a 3D `Mesh`.

**Interface with `PaperRenderer`:**

- Add a `DrawText(FontAsset font, string text, Float2 position, float size, Color color, ...)` method to `PaperRenderer` or `ICanvasRenderer`.
- Internally this calls the text pipeline and appends vertices/indices to the UI batch.
- The SDF shader can be a variant of the UI shader with SDF sampling added.

### 12.2 Input Field Support

The `TextLayout` and hit-testing APIs (`GetCharacterIndexAtPosition`, `GetCursorPosition`) enable building input fields on top of the text component. This phase defines the API contract; the actual input field component is a future effort.

---

## 13. Phase 11 — Editor Tooling

### 13.1 Font Asset Inspector

- Preview the atlas texture with glyph bounding boxes overlaid.
- Table view of all glyphs with metrics.
- Kerning pair editor.
- Reimport button with parameter tweaking.
- Character set coverage indicator (show missing ranges).

### 13.2 Text Renderer Inspector

- Rich text preview in the inspector (rendered live).
- Multi-line text input field with tag auto-completion.
- Visual rect overlay in the scene view showing the layout bounds.
- Gizmo drawing the text bounding box and baseline.
- Style presets (save/load `TextStyle` ScriptableObjects).

### 13.3 Font Asset Creator Window

A dedicated editor window (like TMPro's Font Asset Creator) that:

1. Lets the user drag-drop a `.ttf` / `.otf`.
2. Configure generation settings (character set, atlas size, SDF range, padding).
3. Preview the atlas before committing.
4. Generate and save the `FontAsset`.

---

## 14. Phase 12 — Testing & Benchmarking

### 14.1 Unit Tests (`Prowl.Runtime.Test`)

| Test Class | Coverage |
|------------|----------|
| `GlyphDataTests` | GlyphData struct serialization round-trip |
| `TextShaperTests` | Layout computation: wrapping, alignment, kerning, fallback |
| `TextMeshBuilderTests` | Vertex/index count, UV correctness, sub-mesh splitting |
| `RichTextParserTests` | Tag parsing, nesting, malformed tags, noparse, escape |
| `FontAssetSerializationTests` | ScriptableObject round-trip via Prowl.Echo |
| `TextEffectTests` | Wave/shake/typewriter produce expected vertex deltas |

### 14.2 Benchmarks (`Prowl.Benchmarks`)

| Benchmark | What it measures |
|-----------|-----------------|
| `TextShaper_Layout_1000Chars` | Layout throughput for typical paragraph |
| `TextMeshBuilder_Build_1000Chars` | Mesh generation throughput |
| `RichTextParser_Parse_ComplexMarkup` | Parser throughput with heavily tagged text |
| `FontAtlasLookup_100K` | Glyph table lookup amortized cost |
| `TextRenderer_DirtyRebuild` | Full pipeline: text change → mesh ready |

### 14.3 Visual Tests (Samples)

Create a `Samples/TextDemo` project that exercises:

- Basic world-space text at various sizes and distances.
- All rich text tags.
- Outline, shadow (underlay), glow.
- Animated effects (wave, typewriter).
- Fallback fonts (mixed Latin + CJK).
- Screen-space UI text.
- Performance stress test (1000+ TextRenderer objects).

---

## 15. Phase 13 — Polish & Optimization

### 15.1 Batching

Text objects sharing the same `FontAsset` and `Material` should be batchable. Since the `PropertyState` hash drives batching in the render pipeline:

- Per-instance uniforms (face color, outline, underlay) go into `PropertyState`.
- Per-vertex data (character color, UV) is baked into the mesh.
- Objects with identical `PropertyState` hashes and the same material can be GPU-instanced.

### 15.2 Atlas Compression

- Ship atlas textures with BC4 compression (single channel SDF) or BC7 (MSDF).
- Ensure the importer generates mipmaps with SDF-aware downsampling (avoid standard bilinear which corrupts distance fields — use `max` or distance-preserving filter).

### 15.3 Font Preloading

- `FontAsset.WarmupCharacters(string chars)` — ensure all characters in the string are in the atlas (triggers dynamic generation if needed) before first frame of rendering.

### 15.4 Threading

- `TextShaper` and `TextMeshBuilder` are stateless and allocation-minimal. They can run on background threads for bulk text updates (dialogue systems, chat).
- Use `System.Buffers.ArrayPool<T>` for temporary buffers.

### 15.5 Memory

- `GlyphData[]` is a flat array (cache-friendly).
- `_characterTable` uses `Dictionary<uint, int>` (codepoint → index). For hot paths, consider a direct-mapped array for ASCII range [0..127] with dictionary fallback for higher codepoints.

---

## 16. Event System Integration

The text rendering feature must use Prowl's source-generated event system (`[EventDomain]`) for all cross-system communication rather than raw C# `event` delegates, `Action` fields, or static callbacks. This section specifies exactly which existing event domains to subscribe to, which new domain to create, and the subscription/lifetime patterns to follow.

### 16.1 Consuming Existing Event Domains

The text subsystem is a **consumer** of several engine-wide events. The table below lists each integration point, which component subscribes, and why.

| Existing Domain | Event | Subscriber | Purpose | Priority Guidance |
|----------------|-------|------------|---------|-------------------|
| `AssetEvents` | `OnAssetsRefreshed` | `TextRenderer` | When the asset database is refreshed (editor), check if the referenced `FontAsset` was reimported and mark the mesh dirty so the updated atlas/metrics are picked up next frame. | Default (0) |
| `AssetEvents` | `OnAssetDeleted` | `TextRenderer` | Detect when the assigned `FontAsset` is deleted. Null-out the reference and log a warning via `Debug.LogWarning`. | Default (0) |
| `SceneManagerEvents` | `OnSceneLoaded` | `DynamicFontAtlas` (Phase 9) | After an additive scene load, pre-warm the dynamic atlas with characters found in all `TextRenderer` components of the newly loaded scene by calling `FontAsset.WarmupCharacters()`. | Default (0) |
| `DpiEvents` | `OnDpiChanged` | `UITextRenderer` (Phase 10) | Screen-space text must recompute its layout when DPI scale changes, as font size in physical pixels is affected. Mark dirty on scale change. | Default (0) |
| `GameLoopEvents` | `OnClosing` | `DynamicFontAtlas` (Phase 9) | Flush any pending dynamic atlas writes and dispose native FreeType handles cleanly before the window closes. | Default (0) |
| `RenderingEvents` | `OnBeginRender` | `TextRenderer` (animated text, Phase 8) | For text with active `TextEffect` animations, apply per-vertex modifications to the mesh vertex buffer at the start of each render frame, after `Update` has finalized the layout but before the render pipeline reads the mesh. | Default (0) |

#### Subscription Pattern

All subscriptions from `MonoBehaviour` components (`TextRenderer`, `UITextRenderer`) **must** use the lifecycle-aware overload so handlers auto-unsubscribe when the owning `EngineObject` is disposed:

```csharp
// In TextRenderer.OnEnable()
_assetRefreshSub = AssetEvents.SubscribeOnAssetsRefreshed(OnAssetsRefreshed);
_assetDeletedSub = AssetEvents.SubscribeOnAssetDeleted(OnAssetDeleted);

// In TextRenderer.OnDisable()
_assetRefreshSub?.Dispose();
_assetDeletedSub?.Dispose();
```

Alternatively, use `Manager.AddNewDelegate(this, ...)` with the `EngineObject` owner parameter for fully automatic lifetime binding:

```csharp
// In TextRenderer.OnEnable() — auto-disposes when this MonoBehaviour is destroyed
AssetEvents.Manager.AddNewDelegate(
    this,
    AssetEvents.EventType.OnAssetsRefreshed,
    (Unit _) => MarkDirty(),
    priority: 0);
```

The `IDisposable` pattern is preferred when the component can be enabled/disabled multiple times, since it gives explicit control over when handlers are active. The `AddNewDelegate(owner)` pattern is preferred for set-and-forget subscriptions that should live until the object is destroyed.

### 16.2 New Event Domain: `TextEvents`

A **new static event domain** is needed for text-specific cross-system communication. This domain belongs in `Prowl.Runtime` because it is consumed by both runtime components and editor tooling.

**File:** `Prowl.Runtime/EventSystem/TextEvents.cs`

```csharp
using Prowl.Runtime.Resources;

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Events raised by the text rendering subsystem.
/// Subscribe via <c>TextEvents.SubscribeOnXxx(...)</c>.
/// </summary>
[EventDomain]
public static partial class TextEvents
{
    /// <summary>
    /// Raised after a <see cref="FontAsset"/>'s atlas has been rebuilt or expanded
    /// (e.g., dynamic atlas added new glyphs). All TextRenderers using this font
    /// should mark their meshes dirty.
    /// </summary>
    [EventArgs(typeof(FontAtlasChangedArgs))]
    private static readonly EventKey _OnFontAtlasChanged = new();

    /// <summary>
    /// Raised when a codepoint is requested but not found in any font in the
    /// fallback chain. Editor tooling can listen to this to surface missing
    /// glyph warnings in the console.
    /// </summary>
    [EventArgs(typeof(GlyphMissingArgs))]
    private static readonly EventKey _OnGlyphMissing = new();

    /// <summary>
    /// Raised after a <see cref="TextRenderer"/>'s mesh has been rebuilt.
    /// Useful for editor inspectors that need to refresh a live preview, and
    /// for gameplay systems that react to text bounds changes (e.g., dialogue
    /// bubble sizing).
    /// </summary>
    [EventArgs(typeof(TextMeshRebuiltArgs))]
    private static readonly EventKey _OnTextMeshRebuilt = new();

    /// <summary>
    /// Raised when a <c>&lt;link="id"&gt;</c> region is clicked or hovered
    /// (input field / UI integration). Gameplay code subscribes to implement
    /// hyperlink behavior.
    /// </summary>
    [EventArgs(typeof(TextLinkInteractionArgs))]
    private static readonly EventKey _OnTextLinkInteraction = new();
}
```

**Argument types** (same file, below the domain class):

```csharp
/// <param name="FontAsset">The font whose atlas changed.</param>
/// <param name="AddedCodepoints">Codepoints that were newly added (empty for full rebuild).</param>
public readonly record struct FontAtlasChangedArgs(
    FontAsset FontAsset,
    uint[] AddedCodepoints);

/// <param name="Codepoint">The Unicode codepoint that could not be resolved.</param>
/// <param name="FontAsset">The primary font that was searched.</param>
/// <param name="SourceText">The full text string containing the missing codepoint (for diagnostics).</param>
public readonly record struct GlyphMissingArgs(
    uint Codepoint,
    FontAsset FontAsset,
    string SourceText);

/// <param name="Renderer">The TextRenderer whose mesh was rebuilt.</param>
/// <param name="TextBounds">The new bounding box of the rendered text.</param>
/// <param name="CharacterCount">Number of visible characters after layout.</param>
public readonly record struct TextMeshRebuiltArgs(
    MonoBehaviour Renderer,
    AABB TextBounds,
    int CharacterCount);

/// <param name="LinkId">The link ID from <c>&lt;link="id"&gt;</c>.</param>
/// <param name="InteractionType">Click, hover enter, hover exit.</param>
/// <param name="CharacterIndex">Start character index of the link region.</param>
public readonly record struct TextLinkInteractionArgs(
    string LinkId,
    TextLinkInteraction InteractionType,
    int CharacterIndex);

/// <summary>Type of interaction with a text link region.</summary>
public enum TextLinkInteraction
{
    Click,
    HoverEnter,
    HoverExit
}
```

#### Design Rationale

- **Static domain, not instance domain.** Text events are cross-cutting: an editor inspector needs to observe `OnTextMeshRebuilt` from *any* `TextRenderer`, and the dynamic atlas notifies *all* renderers of a given font. A static domain with `TextEvents.SubscribeOnXxx(...)` keeps this simple. Individual component identity is carried in the args structs (`FontAsset`, `Renderer`).
- **Not `Global = true`.** These events are scoped to the text subsystem, not engine-wide lifecycle events that need `GlobalInvoke` broadcast. Standard `InvokeOnXxx` is sufficient.
- **`readonly record struct` args.** Follows the engine convention (see `PhysicsStepArgs`, `DpiChangedArgs`, `SceneEventArgs`). Immutable, value-type, descriptive parameter names.

### 16.3 Invoking `TextEvents` — Who Fires What

| Event | Fired By | When | Code Location |
|-------|----------|------|---------------|
| `OnFontAtlasChanged` | `DynamicFontAtlas` | After new glyphs are rasterized and inserted into the atlas texture. | `Prowl.Runtime/Text/DynamicFontAtlas.cs` |
| `OnFontAtlasChanged` | `FontAssetImporter` (editor) | After the editor reimports a font and regenerates the atlas. | `Prowl.Editor/AssetImporters/FontAssetImporter.cs` |
| `OnGlyphMissing` | `TextShaper` | During layout, when a codepoint is not found in the primary font or any fallback. | `Prowl.Runtime/Text/TextShaper.cs` |
| `OnTextMeshRebuilt` | `TextRenderer` | After `TextMeshBuilder` completes and the `Mesh` is updated. | `Prowl.Runtime/Components/TextRenderer.cs` |
| `OnTextLinkInteraction` | `UITextRenderer` / input field | When hit-testing detects a pointer event over a `<link>` region. | `Prowl.Runtime/Components/UITextRenderer.cs` or input field |

**Invocation example:**

```csharp
// In TextRenderer, after mesh rebuild:
TextEvents.InvokeOnTextMeshRebuilt(new TextMeshRebuiltArgs(
    this,
    _mesh.bounds,
    _layout.Glyphs.Length));
```

```csharp
// In DynamicFontAtlas, after expanding:
TextEvents.InvokeOnFontAtlasChanged(new FontAtlasChangedArgs(
    _fontAsset,
    newCodepoints.ToArray()));
```

### 16.4 Reacting to `TextEvents` — Who Subscribes

| Event | Subscriber | Reaction |
|-------|-----------|----------|
| `OnFontAtlasChanged` | Every active `TextRenderer` using the changed `FontAsset` | Compare `args.FontAsset` to own `_font`. If match, mark mesh dirty and rebuild UVs (atlas coordinates may have shifted). |
| `OnFontAtlasChanged` | Editor Font Asset Inspector (Phase 11) | Refresh the atlas preview texture and glyph table. |
| `OnGlyphMissing` | Editor Console Logger | Log `Debug.LogWarning($"Missing glyph U+{args.Codepoint:X4} in font '{args.FontAsset.Name}'")`. |
| `OnGlyphMissing` | `DynamicFontAtlas` (Phase 9) | Trigger on-demand rasterization of the missing codepoint. |
| `OnTextMeshRebuilt` | Editor Text Renderer Inspector (Phase 11) | Refresh the live preview and bounds overlay gizmo. |
| `OnTextMeshRebuilt` | Gameplay systems (e.g., dialogue bubble) | Resize/reposition speech bubble background to match `args.TextBounds`. |
| `OnTextLinkInteraction` | Gameplay code | Handle hyperlink clicks (e.g., open URL, trigger quest log entry). |

**Subscription example in `TextRenderer`:**

```csharp
private IDisposable? _atlasChangedSub;

public override void OnEnable()
{
    // ... mesh/material allocation ...

    _atlasChangedSub = TextEvents.SubscribeOnFontAtlasChanged(OnFontAtlasChanged);
}

public override void OnDisable()
{
    _atlasChangedSub?.Dispose();
    _atlasChangedSub = null;

    // ... mesh/material disposal ...
}

private void OnFontAtlasChanged(FontAtlasChangedArgs args)
{
    if (args.FontAsset == _font || IsFallbackFont(args.FontAsset))
        _isDirty = true;
}
```

### 16.5 Event Patterns to Use and Avoid

#### ✅ Do

- **Use `IDisposable` subscriptions** in `OnEnable`/`OnDisable` for all `TextRenderer` and `UITextRenderer` event handlers. This ensures handlers are active only while the component is enabled and prevents leaks from destroyed GameObjects.
- **Use `readonly record struct`** for all new args types. Follow the existing convention (`PhysicsStepArgs`, `DpiChangedArgs`).
- **Fire `OnGlyphMissing` at most once per unique (codepoint, font) pair per layout pass.** Use a `HashSet<(uint, int)>` in `TextShaper` to deduplicate within a single `Shape()` call and avoid flooding the event bus.
- **Use priority ordering** when a subscriber must react before another. Example: `DynamicFontAtlas` should handle `OnGlyphMissing` at priority `-10` (runs first, so it rasterizes the glyph before other subscribers try to use it), while the editor console logger uses default priority `0`.
- **Use `TextEvents.InvokeOnXxx()`** — the generated convenience methods. Never call `EventManager.InvokeEvent<T>` directly.
- **Check `args.FontAsset == _font`** (or use `.InstanceID`) before reacting to `OnFontAtlasChanged`. Avoid rebuilding every `TextRenderer` in the scene when only one font's atlas changed.

#### ❌ Do Not

- **Do not** add `Action<string> OnTextChanged` fields or C# `event` delegates to `TextRenderer` for notifying external systems of text changes. Use `TextEvents.OnTextMeshRebuilt` instead.
- **Do not** subscribe to `BaseEvents.OnAfterUpdate` or `GameLoopEvents.OnFrameBegin` for mesh rebuilds. The `TextRenderer` component already has `Update()` / `LateUpdate()` lifecycle methods from `MonoBehaviour` — use those for dirty-check-and-rebuild. Events are for cross-system decoupled communication, not for driving a component's own update loop.
- **Do not** create a new event domain for each text-related component (`TextRendererEvents`, `FontAssetEvents`, etc.). Group all text events into the single `TextEvents` domain — the event system is designed for domain-level grouping, not per-class granularity.
- **Do not** fire `OnTextMeshRebuilt` during `OnEnable` initialization. Only fire it after a genuine layout+build pass completes. Subscribers should not receive spurious notifications during scene load.
- **Do not** forget to `Dispose()` subscriptions. Every `SubscribeOnXxx` return value stored in a field must have a corresponding `Dispose()` call in `OnDisable()` (or use the `AddNewDelegate(this, ...)` owner overload for automatic cleanup).
- **Do not** make event args mutable. The `readonly record struct` pattern enforces this. If a subscriber needs to communicate back (e.g., cancelling a link interaction), use `ICancellable`.

### 16.6 Event System Integration per Phase

| Phase | Event Integration Work |
|-------|------------------------|
| Phase 0 | None. |
| Phase 1 | None (FontAsset is a pure data container at this stage). |
| Phase 2 | None (atlas generation is a synchronous editor pipeline). |
| Phase 3 | `TextShaper` fires `TextEvents.InvokeOnGlyphMissing()` when a codepoint is unresolved. Deduplicate per layout pass. |
| Phase 4 | None (`TextMeshBuilder` is a pure function: layout in → mesh out). |
| Phase 5 | None (shader is static data). |
| Phase 6 | **Core integration.** `TextRenderer.OnEnable()` subscribes to `TextEvents.OnFontAtlasChanged` and `AssetEvents.OnAssetsRefreshed` / `OnAssetDeleted`. `TextRenderer` fires `TextEvents.InvokeOnTextMeshRebuilt()` after each rebuild. `OnDisable()` disposes all subscriptions. |
| Phase 7 | None (parser is a pure function). |
| Phase 8 | Animated `TextEffect` instances subscribe to `RenderingEvents.OnBeginRender` (via lifecycle-aware `AddNewDelegate`) to apply per-vertex deformations each frame. |
| Phase 9 | `DynamicFontAtlas` subscribes to `TextEvents.OnGlyphMissing` at priority `-10`. After rasterization, fires `TextEvents.InvokeOnFontAtlasChanged()`. Subscribes to `GameLoopEvents.OnClosing` for cleanup. |
| Phase 10 | `UITextRenderer` subscribes to `DpiEvents.OnDpiChanged`. Link hit-testing fires `TextEvents.InvokeOnTextLinkInteraction()`. |
| Phase 11 | Editor Font Asset Inspector subscribes to `TextEvents.OnFontAtlasChanged`. Editor Text Renderer Inspector subscribes to `TextEvents.OnTextMeshRebuilt`. Font Asset Creator Window subscribes to `AssetEvents.OnAssetsImported` to auto-refresh after re-import. |
| Phase 12 | Test suite verifies event fire/subscribe contracts for all integration points above. |
| Phase 13 | No new events. Verify batched scene loads use `EventManager.BeginBatch()` / `EndBatch()` around bulk `TextRenderer` subscriptions during scene deserialization. |

---

## Appendix A — File & Namespace Layout

```
Prowl.Runtime/
├── Resources/
│   └── FontAsset.cs                    # ScriptableObject for font data
├── Text/
│   ├── TextShaper.cs                   # Layout engine
│   ├── TextMeshBuilder.cs              # Mesh generation
│   ├── RichTextParser.cs               # Tag parser
│   ├── TextLayout.cs                   # Layout result structs
│   ├── TextEnums.cs                    # Alignment, overflow, style flags
│   ├── GlyphData.cs                    # Glyph metric struct
│   ├── RectPacker.cs                   # Atlas rect packing
│   └── Effects/
│       ├── TextEffect.cs              # Abstract base
│       ├── WaveEffect.cs
│       ├── ShakeEffect.cs
│       ├── TypewriterEffect.cs
│       └── RainbowEffect.cs
├── Components/
│   └── TextRenderer.cs                 # MonoBehaviour component
├── Rendering/
│   └── DefaultShaders/
│       └── SDF.shader                  # SDF text shader

Prowl.Editor/
├── AssetImporters/
│   └── FontAssetImporter.cs            # .ttf/.otf → FontAsset
├── Windows/
│   └── FontAssetCreatorWindow.cs       # Dedicated atlas generation UI
├── Inspector/
│   └── TextRendererInspector.cs        # Custom inspector
└── Inspector/
    └── FontAssetInspector.cs           # Atlas preview & glyph table

Prowl.Runtime.Test/
└── Text/
    ├── TextShaperTests.cs
    ├── TextMeshBuilderTests.cs
    ├── RichTextParserTests.cs
    └── FontAssetSerializationTests.cs

Prowl.Benchmarks/
└── TextBenchmarks.cs

Samples/
└── TextDemo/
    └── Program.cs
```

All runtime text types live under `namespace Prowl.Runtime.Text;` (or `Prowl.Runtime;` directly if the team prefers flat namespaces — match existing convention where `Components` and `Resources` are directly in `Prowl.Runtime`).

---

## Appendix B — Risk Register

| # | Risk | Impact | Mitigation |
|---|------|--------|------------|
| 1 | **Native dependency (FreeType) on all platforms** | Build complexity, platform support gaps | Keep FreeType editor-only for atlas baking. Runtime uses pre-baked data. Dynamic atlas (Phase 9) is opt-in. |
| 2 | **MSDF quality for small glyph sizes** | Visual artifacts at very small or very large render sizes | Allow fallback to bitmap atlas for fixed-size UI text. Expose SDF range tuning. |
| 3 | **Atlas size explosion for CJK** | Memory pressure | Multi-page atlases, dynamic LRU atlas, configurable character sets, fallback font chain. |
| 4 | **Complex script shaping (Arabic, Thai, Devanagari)** | Incorrect rendering for non-Latin text | Phase 1 targets Latin/Cyrillic/Greek. Design `ITextShaper` interface so HarfBuzz can be plugged in later for complex scripts. |
| 5 | **Vulkan + OpenGL shader compatibility** | Shader code must work on both backends | Use the engine's existing shader abstraction (`ShaderParser`). Test on both backends early. |
| 6 | **Mesh rebuild cost for animated text** | Frame drops with many animated TextRenderers | Per-vertex animation modifies only the vertex buffer (position/color), not topology. Use `Mesh.SetVertices()` partial update path. |
| 7 | **Rich text parser edge cases** | Unclosed tags, deeply nested styles, malformed input | Fuzz-test the parser. Stack depth limit. Graceful fallback to literal rendering. |
| 8 | **UI integration with Quill/PaperRenderer** | Different vertex format, different draw submission path | Define a clean `ITextBackend` interface that can target both 3D Mesh and 2D UI canvas. |

---

## Implementation Order Summary

| Priority | Phase | Deliverable | Est. Effort |
|----------|-------|-------------|-------------|
| **P0** | 0 | NuGet deps, DefaultShader.SDF enum, vertex format audit | 1 day |
| **P0** | 1 | `FontAsset` ScriptableObject + `FontAssetImporter` | 3–4 days |
| **P0** | 2 | SDF atlas generation (rect packer + SDF compute) | 3–4 days |
| **P0** | 3 | `TextShaper` (basic Latin, word wrap, alignment) | 3–4 days |
| **P0** | 4 | `TextMeshBuilder` | 2–3 days |
| **P0** | 5 | SDF shader (face + outline + underlay) | 2–3 days |
| **P0** | 6 | `TextRenderer` MonoBehaviour + `IRenderable` | 2–3 days |
| **P1** | 7 | Rich text parser | 3–4 days |
| **P1** | 8 | Text effects (wave, typewriter, etc.) | 2–3 days |
| **P1** | 9 | Fallback fonts + dynamic atlas | 3–5 days |
| **P2** | 10 | UI / screen-space integration | 3–4 days |
| **P2** | 11 | Editor tooling (inspectors, creator window) | 3–4 days |
| **P2** | 12 | Tests + benchmarks + sample project | 2–3 days |
| **P3** | 13 | Batching, compression, threading polish | 2–3 days |

**Total estimated effort: ~5–7 weeks** for a single developer, with P0 phases taking ~2.5 weeks to reach a functional world-space text renderer.

---

## Appendix C — Progress Tracker

Use this checklist to track implementation progress. Each item maps to a concrete deliverable from the phases above.

### Phase 0 — Prerequisites & Groundwork

- [ ] Evaluate and select FreeType binding (`FreeTypeSharp` vs `SharpFont` vs managed port)
- [ ] Evaluate and select MSDF library (`msdfgen` native, `Msdf.NET`, or managed port)
- [ ] Add NuGet package references to the appropriate project files
- [x] Add `SDF` entry to `DefaultShader` enum in `DefaultAssets.cs`
- [x] Audit `VertexFormat` / `Mesh` for UV1 and per-vertex Color32 support
- [x] Extend `Mesh` if needed (second UV channel, vertex colors)

### Phase 1 — Font Asset Pipeline

- [x] Create `GlyphData` readonly record struct (`Prowl.Runtime/Text/GlyphData.cs`)
- [x] Create `AtlasType` enum (`SDF`, `MSDF`, `Bitmap`)
- [x] Create `FontAsset` ScriptableObject (`Prowl.Runtime/Resources/FontAsset.cs`)
- [x] Implement glyph lookup API (`TryGetGlyph`, `TryGetKerning`)
- [x] Implement fallback chain walking
- [ ] Create `FontAssetImporter` editor class (`Prowl.Editor/AssetImporters/FontAssetImporter.cs`)
- [ ] Implement `.ttf` / `.otf` file reading via FreeType
- [ ] Implement configurable character set (ASCII, Latin Extended, custom string)
- [ ] Implement importer inspector (character set, atlas size, SDF range, padding, preview)
- [x] Verify `FontAsset` serialization round-trip via Prowl.Echo

### Phase 2 — SDF Font Atlas Generation

- [x] Implement `RectPacker` (Skyline Bottom-Left algorithm)
- [ ] Implement single-channel SDF generation from glyph outlines
- [ ] Implement multi-channel MSDF generation
- [ ] Implement bitmap fallback rasterization path
- [ ] Integrate packer + SDF generator into `FontAssetImporter` pipeline
- [ ] Generate `Texture2D` atlas with correct format (`R8` / `RGB8` / `RGBA8`)
- [ ] Write atlas metrics into `FontAsset` glyph table

### Phase 3 — Text Shaping & Layout Engine

- [x] Define `TextLayout`, `GlyphPlacement`, `LineInfo` structs (`Prowl.Runtime/Text/TextLayout.cs`)
- [x] Define `TextAlignment`, `VerticalAlignment`, `TextOverflowMode` enums (`Prowl.Runtime/Text/TextEnums.cs`)
- [x] Implement `TextShaper` core — codepoint → glyph mapping (`Prowl.Runtime/Text/TextShaper.cs`)
- [x] Implement kerning pair application
- [x] Implement word wrapping (word boundary detection)
- [x] Implement character wrapping
- [x] Implement truncation + ellipsis overflow mode
- [x] Implement horizontal alignment (left, center, right, justified)
- [x] Implement vertical alignment (top, middle, bottom)
- [x] Implement line height and paragraph spacing
- [x] Implement character spacing and word spacing
- [x] Fire `TextEvents.InvokeOnGlyphMissing()` with deduplication

### Phase 4 — Mesh Generation

- [x] Implement `TextMeshBuilder` core — layout → mesh (`Prowl.Runtime/Text/TextMeshBuilder.cs`)
- [x] Generate quad-per-glyph (4 verts + 6 indices)
- [x] Compute UV0 from atlas rect coordinates
- [x] Set per-vertex color from `GlyphPlacement.Color`
- [x] Generate underline geometry
- [x] Generate strikethrough geometry
- [x] Generate background highlight (mark) geometry
- [x] Handle `IndexFormat.UInt16` vs `UInt32` based on vertex count
- [x] Handle multi-atlas sub-mesh splitting (for fallback fonts)
- [x] Implement scratch buffer reuse (`ArrayPool` / persistent lists)

### Phase 5 — SDF Shader

- [x] Create `SDF.shader` file in Prowl shader format
- [x] Implement vertex shader (position, UV, color pass-through)
- [x] Implement fragment shader — SDF `median()` sampling
- [x] Implement screen-space anti-aliasing (`fwidth`-based)
- [x] Implement outline rendering
- [x] Implement underlay / drop shadow
- [x] Configure rasterizer state (blend, depth, cull)
- [x] Set correct pass tags (`RenderType=Transparent`, `Queue=Transparent`)
- [ ] Test on OpenGL backend
- [ ] Test on Vulkan backend
- [x] Register as `DefaultShader.SDF` in the engine's default asset loader

### Phase 6 — TextRenderer MonoBehaviour

- [x] Create `TextRenderer` component (`Prowl.Runtime/Components/TextRenderer.cs`)
- [x] Implement `IRenderable` interface (`GetMaterial`, `GetRenderingData`, `GetCullingData`)
- [x] Implement `OnEnable` — allocate Mesh, Material, PropertyState
- [x] Implement `OnDisable` — dispose Mesh, Material, subscriptions
- [x] Implement `Update` — dirty check → TextShaper → TextMeshBuilder → push renderable
- [x] Implement dirty tracking (hash/version on all mutable properties)
- [x] Implement `PropertyState` setup (atlas texture, px range, face color, outline, underlay)
- [x] Implement public API (`Text`, `Font`, `FontSize`, `Color`, `Alignment`, etc.)
- [x] Implement `ForceMeshUpdate()` for immediate rebuild
- [x] Implement `GetTextLayout()` for external access to layout data
- [x] Implement `GetCharacterIndexAtPosition()` hit-test API
- [x] Implement `GetCursorPosition()` for caret rendering
- [x] Subscribe to `TextEvents.OnFontAtlasChanged` (with `IDisposable`)
- [x] Subscribe to `AssetEvents.OnAssetsRefreshed` / `OnAssetDeleted`
- [x] Fire `TextEvents.InvokeOnTextMeshRebuilt()` after each rebuild
- [x] Dispose all event subscriptions in `OnDisable`
- [x] Implement `DrawGizmos` — bounding box and baseline overlay

### Phase 7 — Rich Text Markup Parser

- [x] Create `RichTextParser` (`Prowl.Runtime/Text/RichTextParser.cs`)
- [x] Define `StyleRun` and `ParsedText` structs
- [x] Implement single-pass forward scanner (no regex)
- [x] Implement stack-based tag nesting
- [x] Support `<b>`, `<i>`, `<u>`, `<s>` tags
- [x] Support `<color>` tag (hex + named colors)
- [x] Support `<size>` tag
- [x] Support `<font>` tag (font asset swap)
- [x] Support `<alpha>` tag
- [x] Support `<mark>` tag (background highlight)
- [x] Support `<cspace>`, `<mspace>` tags
- [x] Support `<line-height>` tag
- [x] Support `<sprite>` tag (inline sprite)
- [x] Support `<link>` tag (clickable region)
- [x] Support `<sup>` / `<sub>` tags
- [x] Support `<noparse>` escape tag
- [x] Support `<br>` explicit line break
- [x] Support `<indent>`, `<align>` tags
- [x] Handle malformed/unrecognized tags (render as literal)
- [x] Integrate `ParsedText` output into `TextShaper` pipeline

### Phase 8 — Text Effects & Animations

- [x] Create `TextEffect` abstract base class (`Prowl.Runtime/Text/Effects/TextEffect.cs`)
- [x] Implement `WaveEffect`
- [x] Implement `ShakeEffect`
- [x] Implement `FadeInEffect`
- [x] Implement `TypewriterEffect` (character reveal over time)
- [x] Implement `RainbowEffect`
- [x] Implement `ScaleEffect`
- [x] Implement `RotateEffect`
- [x] Integrate effect tags in `RichTextParser` (`<wave>`, `<shake>`, etc.)
- [x] Subscribe animated effects to `RenderingEvents.OnBeginRender` via lifecycle-aware `AddNewDelegate`
- [x] Ensure per-vertex animation modifies only vertex buffer, not topology

### Phase 9 — Font Fallbacks & Dynamic Atlas Expansion

- [x] Implement multi-atlas sub-mesh rendering path in `TextRenderer` (one `IRenderable` per atlas)
- [ ] Create `DynamicFontAtlas` class (`Prowl.Runtime/Text/DynamicFontAtlas.cs`)
- [ ] Implement on-demand glyph rasterization via FreeType (runtime)
- [ ] Implement growable atlas region with `Texture2D` subregion updates
- [ ] Implement LRU eviction for full atlases
- [ ] Subscribe to `TextEvents.OnGlyphMissing` at priority `-10`
- [ ] Fire `TextEvents.InvokeOnFontAtlasChanged()` after expansion
- [ ] Subscribe to `GameLoopEvents.OnClosing` for native handle cleanup
- [ ] Subscribe to `SceneManagerEvents.OnSceneLoaded` for pre-warming
- [x] Implement `FontAsset.WarmupCharacters(string)`

### Phase 10 — UI / Screen-Space Integration

- [ ] Create `UITextRenderer` component or `PaperRenderer.DrawText()` API
- [ ] Integrate `TextShaper` + `TextMeshBuilder` output into UI vertex batch
- [ ] Create SDF variant of the UI shader (or keyword toggle in existing UI shader)
- [ ] Subscribe to `DpiEvents.OnDpiChanged` for DPI-aware layout
- [ ] Implement link hit-testing for UI text
- [ ] Fire `TextEvents.InvokeOnTextLinkInteraction()` on pointer events
- [ ] Define input field API contract (`GetCharacterIndexAtPosition`, `GetCursorPosition`)

### Phase 11 — Editor Tooling

- [ ] Create Font Asset Inspector (`Prowl.Editor/Inspector/FontAssetInspector.cs`)
- [ ] Implement atlas preview with glyph bounding boxes
- [ ] Implement glyph metrics table view
- [ ] Implement kerning pair editor
- [ ] Implement character set coverage indicator
- [ ] Subscribe to `TextEvents.OnFontAtlasChanged` for live preview refresh
- [ ] Create Text Renderer Inspector (`Prowl.Editor/Inspector/TextRendererInspector.cs`)
- [ ] Implement live rich text preview in inspector
- [ ] Implement multi-line text input with tag auto-completion
- [ ] Subscribe to `TextEvents.OnTextMeshRebuilt` for preview refresh
- [ ] Create Font Asset Creator Window (`Prowl.Editor/Windows/FontAssetCreatorWindow.cs`)
- [ ] Implement drag-drop `.ttf` / `.otf` workflow
- [ ] Implement generation settings UI
- [ ] Implement atlas preview before commit
- [ ] Subscribe to `AssetEvents.OnAssetsImported` for auto-refresh
- [ ] Create `TextStyle` ScriptableObject for style presets

### Phase 12 — Testing & Benchmarking

- [x] `GlyphDataTests` — struct serialization round-trip
- [x] `TextShaperTests` — wrapping, alignment, kerning, fallback
- [x] `TextMeshBuilderTests` — vertex/index count, UV correctness, sub-mesh splitting
- [x] `RichTextParserTests` — tag parsing, nesting, malformed tags, noparse, escape
- [x] `FontAssetSerializationTests` — ScriptableObject round-trip via Prowl.Echo
- [x] `TextEffectTests` — wave/shake/typewriter produce expected vertex deltas
- [x] `TextEventTests` — verify `OnFontAtlasChanged`, `OnGlyphMissing`, `OnTextMeshRebuilt` fire/subscribe contracts
- [x] `TextEventTests` — verify `IDisposable` subscription cleanup prevents leaks
- [ ] `TextEventTests` — verify `DynamicFontAtlas` priority `-10` processes `OnGlyphMissing` before other subscribers
- [x] `TextShaper_Layout_1000Chars` benchmark
- [x] `TextMeshBuilder_Build_1000Chars` benchmark
- [x] `RichTextParser_Parse_ComplexMarkup` benchmark
- [x] `FontAtlasLookup_100K` benchmark
- [ ] `TextRenderer_DirtyRebuild` benchmark
- [ ] Create `Samples/TextDemo` project
- [ ] Visual test: world-space text at various sizes/distances
- [ ] Visual test: all rich text tags
- [ ] Visual test: outline, shadow, glow
- [ ] Visual test: animated effects (wave, typewriter)
- [ ] Visual test: fallback fonts (mixed Latin + CJK)
- [ ] Visual test: screen-space UI text
- [ ] Visual test: performance stress (1000+ TextRenderers)

### Phase 13 — Polish & Optimization

- [ ] Verify `PropertyState` hash-based batching works for text objects with identical uniforms
- [ ] Implement BC4 atlas compression (SDF) in importer
- [ ] Implement BC7 atlas compression (MSDF) in importer
- [ ] Implement SDF-aware mipmap generation (distance-preserving filter)
- [x] Implement `FontAsset.WarmupCharacters()` preloading API
- [x] Implement ASCII-range direct-mapped array optimization in glyph lookup
- [x] Verify `TextShaper` / `TextMeshBuilder` are thread-safe for background use
- [x] Implement `ArrayPool<T>` for temporary buffers in shaper and mesh builder
- [ ] Verify `EventManager.BeginBatch()` / `EndBatch()` is used during bulk scene loads with many TextRenderers
- [ ] Final performance profiling pass (target: 1000 TextRenderers < 2ms total rebuild)
