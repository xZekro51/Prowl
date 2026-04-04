// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;

using Prowl.Echo;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Graphite;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Rendering.Shaders;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Text;
using Prowl.Vector;

using TextAlignment = Prowl.Runtime.Text.TextAlignment;
using TextOverflowMode = Prowl.Runtime.Text.TextOverflowMode;
using VerticalAlignment = Prowl.Runtime.Text.VerticalAlignment;

namespace Prowl.Runtime.UI;

/// <summary>
/// Renders SDF text in screen-space UI. Uses the same <see cref="TextShaper"/> +
/// <see cref="TextMeshBuilder"/> pipeline as <see cref="TextRenderer"/> but outputs
/// into the UI rendering pass instead of a 3D mesh.
/// </summary>
/// <remarks>
/// Requires a <see cref="RectTransform"/> on the same GameObject.
/// The component subscribes to <see cref="DpiEvents.OnDpiChanged"/> to recompute
/// layout when screen DPI changes, and fires
/// <see cref="TextEvents.InvokeOnTextLinkInteraction"/> when link regions are clicked.
/// </remarks>
public class UITextRenderer : UIBehaviour
{
    // ── Serialized Fields ─────────────────────────────────────

    [SerializeField] private string _text = "New Text";
    [SerializeField] private FontAsset? _font;
    [SerializeField] private float _fontSize = 16f;
    [SerializeField] private Color _color = Color.White;
    [SerializeField] private TextAlignment _alignment = TextAlignment.Left;
    [SerializeField] private VerticalAlignment _verticalAlignment = VerticalAlignment.Top;
    [SerializeField] private TextOverflowMode _overflow = TextOverflowMode.WordWrap;
    [SerializeField] private bool _richText = true;
    [SerializeField] private float _characterSpacing = 0f;
    [SerializeField] private float _lineSpacing = 0f;
    [SerializeField] private float _wordSpacing = 0f;
    [SerializeField] private float _paragraphSpacing = 0f;

    // ── Outline & Effects ─────────────────────────────────────

    [SerializeField] private float _outlineWidth = 0f;
    [SerializeField] private Color _outlineColor = new(0f, 0f, 0f, 1f);
    [SerializeField] private float _softness = 0f;

    // ── Underlay (drop shadow) ────────────────────────────────

    [SerializeField] private Color _underlayColor = new(0f, 0f, 0f, 0.5f);
    [SerializeField] private Float2 _underlayOffset = Float2.Zero;
    [SerializeField] private float _underlayDilate = 0f;
    [SerializeField] private float _underlaySoftness = 0f;

    // ── Runtime State (not serialized) ────────────────────────

    [SerializeIgnore] private Mesh? _mesh;
    [SerializeIgnore] private Material? _material;
    [SerializeIgnore] private PropertyState _properties = new();
    [SerializeIgnore] private TextLayout _layout;
    [SerializeIgnore] private bool _isDirty = true;
    [SerializeIgnore] private float _dpiScale = 1f;
    [SerializeIgnore] private Rect _lastRect;
    [SerializeIgnore] private float _lastContextAlpha = 1f;

    // Event subscriptions
    [SerializeIgnore] private IDisposable? _dpiChangedSub;
    [SerializeIgnore] private IDisposable? _atlasChangedSub;

    // Static collection of pending UI text renders for the current frame.
    // Populated during BuildUI, flushed during the render phase.
    private static readonly List<UITextRenderRequest> s_pendingRenders = [];

    // ── Public Properties ─────────────────────────────────────

    /// <summary> The text to display. Setting this marks the layout dirty. </summary>
    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; _isDirty = true; } }
    }

    /// <summary> The font asset to use for rendering. </summary>
    public FontAsset? Font
    {
        get => _font;
        set { if (_font != value) { _font = value; _isDirty = true; } }
    }

    /// <summary> Font size in screen-space pixels. </summary>
    public float FontSize
    {
        get => _fontSize;
        set { if (MathF.Abs(_fontSize - value) > 0.001f) { _fontSize = value; _isDirty = true; } }
    }

    /// <summary> Base text color. </summary>
    public Color TextColor
    {
        get => _color;
        set { if (_color != value) { _color = value; _isDirty = true; } }
    }

    /// <summary> Horizontal text alignment. </summary>
    public TextAlignment Alignment
    {
        get => _alignment;
        set { if (_alignment != value) { _alignment = value; _isDirty = true; } }
    }

    /// <summary> Vertical text alignment. </summary>
    public VerticalAlignment VerticalAlign
    {
        get => _verticalAlignment;
        set { if (_verticalAlignment != value) { _verticalAlignment = value; _isDirty = true; } }
    }

    /// <summary> How text handles overflow. </summary>
    public TextOverflowMode Overflow
    {
        get => _overflow;
        set { if (_overflow != value) { _overflow = value; _isDirty = true; } }
    }

    /// <summary> Enable/disable rich text tag parsing. </summary>
    public bool RichText
    {
        get => _richText;
        set { if (_richText != value) { _richText = value; _isDirty = true; } }
    }

    /// <summary> Extra spacing between characters. </summary>
    public float CharacterSpacing
    {
        get => _characterSpacing;
        set { if (MathF.Abs(_characterSpacing - value) > 0.001f) { _characterSpacing = value; _isDirty = true; } }
    }

    /// <summary> Extra spacing between lines. </summary>
    public float LineSpacing
    {
        get => _lineSpacing;
        set { if (MathF.Abs(_lineSpacing - value) > 0.001f) { _lineSpacing = value; _isDirty = true; } }
    }

    /// <summary> Extra spacing between words. </summary>
    public float WordSpacing
    {
        get => _wordSpacing;
        set { if (MathF.Abs(_wordSpacing - value) > 0.001f) { _wordSpacing = value; _isDirty = true; } }
    }

    /// <summary> Extra spacing between paragraphs. </summary>
    public float ParagraphSpacing
    {
        get => _paragraphSpacing;
        set { if (MathF.Abs(_paragraphSpacing - value) > 0.001f) { _paragraphSpacing = value; _isDirty = true; } }
    }

    /// <summary> SDF outline width (0 = no outline). </summary>
    public float OutlineWidth
    {
        get => _outlineWidth;
        set { _outlineWidth = value; UpdateMaterialProperties(); }
    }

    /// <summary> Outline color. </summary>
    public Color OutlineColor
    {
        get => _outlineColor;
        set { _outlineColor = value; UpdateMaterialProperties(); }
    }

    /// <summary> Anti-aliasing softness. </summary>
    public float Softness
    {
        get => _softness;
        set { _softness = value; UpdateMaterialProperties(); }
    }

    /// <summary> Drop shadow color. </summary>
    public Color UnderlayColor
    {
        get => _underlayColor;
        set { _underlayColor = value; UpdateMaterialProperties(); }
    }

    /// <summary> Drop shadow offset. </summary>
    public Float2 UnderlayOffset
    {
        get => _underlayOffset;
        set { _underlayOffset = value; UpdateMaterialProperties(); }
    }

    /// <summary> Drop shadow dilation. </summary>
    public float UnderlayDilate
    {
        get => _underlayDilate;
        set { _underlayDilate = value; UpdateMaterialProperties(); }
    }

    /// <summary> Drop shadow softness. </summary>
    public float UnderlaySoftness
    {
        get => _underlaySoftness;
        set { _underlaySoftness = value; UpdateMaterialProperties(); }
    }

    // ── Resource Initialization ──────────────────────────────

    /// <summary>
    /// Ensures the mesh exists, creating it lazily if needed.
    /// </summary>
    private void EnsureMesh()
    {
        if (_mesh.IsValid())
            return;

        _mesh = new Mesh();
        _mesh.Name = "UITextMesh";
    }

    /// <summary>
    /// Ensures the SDF material exists, creating it lazily if needed.
    /// </summary>
    private void EnsureMaterial()
    {
        if (_material.IsValid())
            return;

        try
        {
            Shader sdfuiShader = Shader.LoadDefault(DefaultShader.SDFUI);
            _material = new Material(sdfuiShader);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[UIText] Failed to create SDFUI material: {ex.Message}");
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────

    public override void OnEnable()
    {
        EnsureMesh();
        EnsureMaterial();

        _properties ??= new PropertyState();
        _isDirty = true;

        // Subscribe to events
        _dpiChangedSub = DpiEvents.SubscribeOnDpiChanged(OnDpiChanged);
        _atlasChangedSub = TextEvents.SubscribeOnFontAtlasChanged(OnFontAtlasChanged);
    }

    public override void OnDisable()
    {
        _dpiChangedSub?.Dispose();
        _dpiChangedSub = null;
        _atlasChangedSub?.Dispose();
        _atlasChangedSub = null;

        _mesh?.Dispose();
        _mesh = null;
        _material?.Dispose();
        _material = null;
    }

    // ── BuildUI (called by Canvas) ────────────────────────────

    public override void BuildUI(Paper paper, UIContext context)
    {
        RectTransform? rt = GameObject.RectTransform;
        if (rt == null || _font.IsNotValid())
            return;

        Rect rect = rt.ComputedRect;
        float w = rect.Size.X;
        float h = rect.Size.Y;
        if (w <= 0 || h <= 0)
            return;

        // Ensure resources are initialized (handles missed OnEnable or dispose/re-enable cycles)
        EnsureMesh();
        EnsureMaterial();

        _lastContextAlpha = context.Alpha;

        // Check if the layout rect changed (triggers rebuild)
        if (_lastRect.Min != rect.Min || _lastRect.Size != rect.Size)
        {
            _lastRect = rect;
            _isDirty = true;
        }

        // Rebuild mesh if dirty
        if (_isDirty)
            RebuildMesh(rect);

        // Create a transparent Paper box at the rect for layout participation.
        // The actual SDF rendering is deferred to the static render pass.
        paper.Box($"uitxt_{InstanceID}")
            .PositionType(PositionType.SelfDirected)
            .Left(rect.Min.X)
            .Top(rect.Min.Y)
            .Width(w)
            .Height(h)
            .Enter()
            .Dispose();

        // Register for deferred SDF rendering
        if (_mesh.IsValid() && _mesh.VertexCount > 0 && _material.IsValid())
        {
            s_pendingRenders.Add(new UITextRenderRequest
            {
                Mesh = _mesh,
                Material = _material!,
                Properties = _properties,
                Alpha = context.Alpha,
            });
        }

        // Handle link hit-testing for pointer events
        HandleLinkInteraction(rect);
    }

    // ── Mesh Rebuild ──────────────────────────────────────────

    private void RebuildMesh(Rect rect)
    {
        _isDirty = false;

        if (_font.IsNotValid() || _mesh.IsNotValid())
            return;

        // Apply DPI scale to font size
        float scaledFontSize = _fontSize * _dpiScale;

        // Parse rich text if enabled
        string displayText = _text;
        StyleRun[]? styleRuns = null;

        if (_richText && !string.IsNullOrEmpty(_text))
        {
            ParsedText parsed = RichTextParser.Parse(_text);
            displayText = parsed.StrippedText;
            styleRuns = parsed.Runs;
        }

        // The rect size is in screen pixels; use it directly as the layout constraint
        float maxWidth = _overflow == TextOverflowMode.Overflow
            ? float.MaxValue
            : rect.Size.X;

        // Shape and layout
        _layout = TextShaper.Shape(
            displayText,
            _font,
            scaledFontSize,
            maxWidth,
            _alignment,
            _verticalAlignment,
            _overflow,
            _color,
            _characterSpacing,
            _lineSpacing,
            _wordSpacing,
            _paragraphSpacing,
            styleRuns);

        // Build mesh — positions are relative to (0,0), we offset by rect origin in the vertex shader via projection
        _mesh = TextMeshBuilder.Build(_layout, _font, _mesh, out _);

        // Offset mesh vertices by the screen-space rect origin so they appear
        // at the correct position when rendered with the orthographic projection.
        if (_mesh.Vertices != null)
        {
            Float3[] verts = _mesh.Vertices;
            float offsetX = rect.Min.X;
            float offsetY = rect.Min.Y;
            for (int i = 0; i < verts.Length; i++)
            {
                verts[i] = new Float3(verts[i].X + offsetX, verts[i].Y + offsetY, verts[i].Z);
            }
            _mesh.Vertices = verts;
        }

        _mesh.Upload();

        // Update material properties
        UpdateMaterialProperties();

        // Fire event
        TextEvents.InvokeOnTextMeshRebuilt(new TextMeshRebuiltArgs(
            this,
            _mesh.bounds,
            _layout.Glyphs?.Length ?? 0));
    }

    private void UpdateMaterialProperties()
    {
        if (_material.IsNotValid() || _font.IsNotValid())
            return;

        _properties.Clear();

        if (_font.AtlasTexture.IsValid())
            _properties.SetTexture("_FontAtlas", _font.AtlasTexture);

        _properties.SetFloat("_PxRange", _font.AtlasPxRange);
        _properties.SetColor("_FaceColor", new Color(_color.R, _color.G, _color.B, _color.A * _lastContextAlpha));
        _properties.SetColor("_OutlineColor", _outlineColor);
        _properties.SetFloat("_OutlineWidth", _outlineWidth);
        _properties.SetFloat("_Softness", _softness);
        _properties.SetColor("_UnderlayColor", _underlayColor);
        _properties.SetFloat("_UnderlayOffsetX", _underlayOffset.X);
        _properties.SetFloat("_UnderlayOffsetY", _underlayOffset.Y);
        _properties.SetFloat("_UnderlayDilate", _underlayDilate);
        _properties.SetFloat("_UnderlaySoftness", _underlaySoftness);
    }

    // ── Event Handlers ────────────────────────────────────────

    private void OnDpiChanged(DpiChangedArgs args)
    {
        _dpiScale = args.NewScale;
        _isDirty = true;
    }

    private void OnFontAtlasChanged(FontAtlasChangedArgs args)
    {
        if (args.FontAsset == _font)
            _isDirty = true;
    }

    // ── Link Hit-Testing ──────────────────────────────────────

    private void HandleLinkInteraction(Rect rect)
    {
        if (_layout.Glyphs == null || _layout.Glyphs.Length == 0)
            return;

        // Convert screen mouse position to local text coordinates
        Int2 mousePos = Input.MousePosition;
        Float2 localPos = new(mousePos.X - rect.Min.X, mousePos.Y - rect.Min.Y);

        // Find the link at this position
        string? linkId = GetLinkAtPosition(localPos);
        if (linkId == null)
            return;

        // Fire interaction events based on mouse state
        if (Input.GetMouseButtonDown(0))
        {
            TextEvents.InvokeOnTextLinkInteraction(new TextLinkInteractionArgs(
                linkId,
                TextLinkInteraction.Click,
                GetCharacterIndexAtPosition(localPos)));
        }
    }

    // ── Hit-Testing API ───────────────────────────────────────

    /// <summary>
    /// Returns the last computed text layout (for external hit-testing, caret positioning).
    /// </summary>
    public TextLayout GetTextLayout() => _layout;

    /// <summary>
    /// Returns the character index at the given local-space position (relative to the rect origin),
    /// or -1 if none found.
    /// </summary>
    public int GetCharacterIndexAtPosition(Float2 localPos)
    {
        if (_layout.Glyphs == null || _layout.Glyphs.Length == 0 || _font.IsNotValid())
            return -1;

        float closestDist = float.MaxValue;
        int closestIndex = -1;

        for (int i = 0; i < _layout.Glyphs.Length; i++)
        {
            GlyphPlacement g = _layout.Glyphs[i];
            float dx = localPos.X - g.Position.X;
            float dy = localPos.Y - g.Position.Y;
            float dist = dx * dx + dy * dy;

            if (dist < closestDist)
            {
                closestDist = dist;
                closestIndex = g.CharacterIndex;
            }
        }

        return closestIndex;
    }

    /// <summary>
    /// Returns the local-space position of the cursor at the given character index.
    /// </summary>
    public Float2 GetCursorPosition(int charIndex)
    {
        if (_layout.Glyphs == null || _layout.Glyphs.Length == 0)
            return Float2.Zero;

        for (int i = 0; i < _layout.Glyphs.Length; i++)
        {
            if (_layout.Glyphs[i].CharacterIndex == charIndex)
                return _layout.Glyphs[i].Position;
        }

        // Past end — return position after last glyph
        if (_layout.Glyphs.Length > 0)
        {
            GlyphPlacement last = _layout.Glyphs[^1];
            return new Float2(last.Position.X + last.Scale.X, last.Position.Y);
        }

        return Float2.Zero;
    }

    /// <summary>
    /// Returns the link ID at the given local-space position, or <c>null</c> if the
    /// closest glyph is not part of a <c>&lt;link&gt;</c> region.
    /// </summary>
    public string? GetLinkAtPosition(Float2 localPos)
    {
        if (_layout.Glyphs == null || _layout.Glyphs.Length == 0 || _font.IsNotValid())
            return null;

        float closestDist = float.MaxValue;
        string? closestLinkId = null;

        for (int i = 0; i < _layout.Glyphs.Length; i++)
        {
            GlyphPlacement g = _layout.Glyphs[i];
            float dx = localPos.X - g.Position.X;
            float dy = localPos.Y - g.Position.Y;
            float dist = dx * dx + dy * dy;

            if (dist < closestDist)
            {
                closestDist = dist;
                closestLinkId = g.LinkId;
            }
        }

        return closestLinkId;
    }

    /// <summary> Forces an immediate mesh rebuild, bypassing the dirty check. </summary>
    public void ForceMeshUpdate()
    {
        _isDirty = true;
        if (_lastRect.Size.X > 0 && _lastRect.Size.Y > 0)
            RebuildMesh(_lastRect);
    }

    // ── Static Rendering API ──────────────────────────────────

    /// <summary>
    /// Flushes all pending UI text render requests to the swapchain.
    /// Called after Paper's <c>EndFrame()</c> to overlay SDF text on top of the UI canvas.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for invoking this at the correct point in the
    /// frame (after Paper rendering, before present). The <see cref="Game"/>
    /// class calls this automatically.
    /// </remarks>
    public static void FlushPendingRenders()
    {
        FlushPendingRenders(null);
    }

    /// <summary>
    /// Flushes all pending UI text render requests into the given render target.
    /// When <paramref name="target"/> is <c>null</c>, renders to the swapchain.
    /// </summary>
    /// <param name="target">
    /// An optional <see cref="RenderTexture"/> to render into. Pass <c>null</c>
    /// to render directly to the swapchain (standalone game default).
    /// The editor passes the game-view render texture here so the SDF text
    /// composites correctly inside the game panel.
    /// </param>
    public static void FlushPendingRenders(RenderTexture? target)
    {
        if (s_pendingRenders.Count == 0)
            return;

        if (!Graphics.IsGraphiteReady)
        {
            s_pendingRenders.Clear();
            return;
        }

        // Determine render target and dimensions
        Graphite.Texture colorTarget;
        float screenW, screenH;

        if (target != null && target.MainTexture.IsValid() &&
            target.MainTexture.Handle?.GraphiteTexture != null)
        {
            colorTarget = target.MainTexture.Handle.GraphiteTexture;
            screenW = target.Width;
            screenH = target.Height;
        }
        else
        {
            colorTarget = Graphics.Graphite.GetSwapchainTexture();
            screenW = Window.InternalWindow.FramebufferSize.X;
            screenH = Window.InternalWindow.FramebufferSize.Y;
        }

        Float4x4 projection = Float4x4.CreateOrthoOffCenter(0, screenW, screenH, 0, -1, 1);

        using RenderCommandBuffer cmd = new("UITextRenderer");

        // The DefaultRenderPipeline's blit pass transitions the render texture
        // to ShaderResource (ShaderReadOnlyOptimal) for downstream sampling
        // (e.g. ImGui).  We must transition it back to RenderTarget
        // (ColorAttachmentOptimal) before using it as a color attachment with
        // LoadOp.Load, otherwise the Vulkan render pass's initialLayout will
        // not match the image's actual layout — an undefined-behaviour
        // violation that causes ErrorDeviceLost on many drivers.
        cmd.ResourceBarrier(new Graphite.ResourceBarrier(
            colorTarget, Graphite.ResourceState.ShaderResource, Graphite.ResourceState.RenderTarget));

        Graphite.RenderPassColorAttachment colorAtt = Graphite.RenderPassColorAttachment.Load(colorTarget);
        Graphite.RenderPassDescriptor desc = new()
        {
            ColorAttachments = [colorAtt],
        };
        RenderPassLayout passLayout = new([colorTarget.Format]);
        cmd.BeginRenderPass(in desc, passLayout);
        cmd.SetViewportRaw(0, 0, screenW, screenH);
        cmd.SetScissor(0, 0, (uint)screenW, (uint)screenH);

        for (int i = 0; i < s_pendingRenders.Count; i++)
        {
            UITextRenderRequest req = s_pendingRenders[i];

            // Get the shader pass and program
            if (req.Material.IsNotValid())
                continue;

            Shader shader = req.Material.Shader;
            if (shader.IsNotValid() || !shader.Passes.Any())
                continue;

            ShaderPass pass = shader.GetPass(0);
            if (!pass.TryGetVariantProgram(null, out GraphicsProgram? program) || program == null)
                continue;

            // Set the screen projection uniform
            req.Properties.SetMatrix("_ScreenProjection", projection);

            // Get vertex layout from the mesh's VAO
            req.Mesh.Upload();
            GraphicsVertexArray? vao = req.Mesh.VertexArrayObject;
            if (vao?.GraphiteVertexLayout == null)
                continue;

            // Create bind group from SPIR-V reflection (Vulkan) or use GL uniforms
            BindGroupLayout? bgl = program.GetOrCreateBindGroupLayout();
            BindGroup? bindGroup = null;
            if (program.Reflection != null && bgl != null)
            {
                bindGroup = GraphiteMaterialBinder.CreateBindGroup(
                    program.Reflection, bgl, req.Properties, null);
            }

            // Resolve pipeline
            Graphite.PipelineState pipeline = PipelineStateCache.GetOrCreate(
                program,
                vao.GraphiteVertexLayout.Value,
                pass.State,
                Topology.Triangles,
                passLayout,
                bgl != null ? [bgl] : null);

            cmd.SetPipeline(pipeline);

            // Bind uniforms via bind group (Vulkan) or legacy GL path
            if (bindGroup != null)
            {
                cmd.SetBindGroup(0, bindGroup);
            }
            else if (Graphics.IsOpenGL)
            {
                // On OpenGL, apply uniforms directly via the legacy GL path
                program.Use();
                PropertyState.Apply(req.Properties, program);
            }
            else
            {
                // Vulkan requires a valid bind group — skip this draw if
                // bind group creation failed to avoid drawing without
                // descriptor sets, which causes ErrorDeviceLost.
                continue;
            }

            // Draw the mesh
            cmd.DrawMeshIndexed(req.Mesh);
        }

        cmd.EndRenderPass();

        // Transition the color target back to ShaderResource so downstream
        // consumers (e.g. ImGui sampling the game RT) see the correct layout.
        // This mirrors the pattern used by PaperRenderer after its render pass.
        if (!Graphics.IsOpenGL)
        {
            cmd.ResourceBarrier(new Graphite.ResourceBarrier(
                colorTarget, Graphite.ResourceState.RenderTarget, Graphite.ResourceState.ShaderResource));
        }

        cmd.Submit();

        s_pendingRenders.Clear();
    }

    // ── Internal Types ────────────────────────────────────────

    private struct UITextRenderRequest
    {
        public Mesh Mesh;
        public Material Material;
        public PropertyState Properties;
        public float Alpha;
    }
}
