// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Echo;
using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Text;
using Prowl.Runtime.Text.Effects;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// Renders SDF text in world space. The primary text rendering component,
/// equivalent to Unity's TextMeshPro 3D text.
/// </summary>
public class TextRenderer : MonoBehaviour, IRenderable
{
    // ── Serialized Fields ─────────────────────────────────────

    [SerializeField] private string _text = "New Text";
    [SerializeField] private FontAsset? _font;
    [SerializeField] private float _fontSize = 1f;
    [SerializeField] private Color _color = Color.White;
    [SerializeField] private TextAlignment _alignment = TextAlignment.Left;
    [SerializeField] private VerticalAlignment _verticalAlignment = VerticalAlignment.Top;
    [SerializeField] private TextOverflowMode _overflow = TextOverflowMode.Overflow;
    [SerializeField] private Float2 _rectSize = new(10f, 10f);
    [SerializeField] private bool _richText = true;
    [SerializeField] private float _characterSpacing = 0f;
    [SerializeField] private float _lineSpacing = 0f;
    [SerializeField] private float _wordSpacing = 0f;

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
    [SerializeIgnore] private int _lastTextHash;
    [SerializeIgnore] private int _lastSettingsHash;

    // Multi-atlas sub-mesh mapping (null when only the primary font is used)
    [SerializeIgnore] private int[]? _subMeshFontIndices;

    // Event subscriptions
    [SerializeIgnore] private IDisposable? _atlasChangedSub;
    [SerializeIgnore] private IDisposable? _assetRefreshSub;
    [SerializeIgnore] private IDisposable? _assetDeletedSub;
    [SerializeIgnore] private IDisposable? _beginRenderSub;

    // Text effects
    [SerializeIgnore] private readonly List<TextEffect> _effects = [];
    [SerializeIgnore] private bool _hasActiveEffects;

    // ── Public Properties ─────────────────────────────────────

    /// <summary> The text to display. Setting this marks the mesh dirty. </summary>
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

    /// <summary> Font size in world units. </summary>
    public float FontSize
    {
        get => _fontSize;
        set { if (_fontSize != value) { _fontSize = value; _isDirty = true; } }
    }

    /// <summary> Base face color. </summary>
    public Color Color
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

    /// <summary> Layout constraint box size. </summary>
    public Float2 RectSize
    {
        get => _rectSize;
        set { if (_rectSize != value) { _rectSize = value; _isDirty = true; } }
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
        set { if (_characterSpacing != value) { _characterSpacing = value; _isDirty = true; } }
    }

    /// <summary> Extra spacing between lines. </summary>
    public float LineSpacing
    {
        get => _lineSpacing;
        set { if (_lineSpacing != value) { _lineSpacing = value; _isDirty = true; } }
    }

    /// <summary> Extra spacing between words. </summary>
    public float WordSpacing
    {
        get => _wordSpacing;
        set { if (_wordSpacing != value) { _wordSpacing = value; _isDirty = true; } }
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

    // ── Text Effects ─────────────────────────────────────────

    /// <summary> The list of active text effects applied to this renderer. </summary>
    public IReadOnlyList<TextEffect> Effects => _effects;

    /// <summary> Adds a text effect to this renderer. </summary>
    public void AddEffect(TextEffect effect)
    {
        _effects.Add(effect);
        UpdateEffectSubscription();
    }

    /// <summary> Removes a text effect from this renderer. </summary>
    public bool RemoveEffect(TextEffect effect)
    {
        bool removed = _effects.Remove(effect);
        if (removed)
            UpdateEffectSubscription();
        return removed;
    }

    /// <summary> Removes all text effects from this renderer. </summary>
    public void ClearEffects()
    {
        _effects.Clear();
        UpdateEffectSubscription();
    }

    // ── Lifecycle ─────────────────────────────────────────────

    public override void OnEnable()
    {
        _mesh = new Mesh();
        _mesh.Name = "TextMesh";

        Shader sdfShader = Shader.LoadDefault(DefaultShader.SDF);
        _material = new Material(sdfShader);

        _properties = new PropertyState();
        _isDirty = true;

        // Subscribe to events
        _atlasChangedSub = TextEvents.SubscribeOnFontAtlasChanged(OnFontAtlasChanged);
        _assetRefreshSub = AssetEvents.SubscribeOnAssetsRefreshed(OnAssetsRefreshed);
        _assetDeletedSub = AssetEvents.SubscribeOnAssetDeleted(OnAssetDeleted);
    }

    public override void OnDisable()
    {
        // Dispose event subscriptions
        _atlasChangedSub?.Dispose();
        _atlasChangedSub = null;
        _assetRefreshSub?.Dispose();
        _assetRefreshSub = null;
        _assetDeletedSub?.Dispose();
        _assetDeletedSub = null;
        _beginRenderSub?.Dispose();
        _beginRenderSub = null;

        // Dispose GPU resources
        _mesh?.Dispose();
        _mesh = null;
        _material?.Dispose();
        _material = null;
    }

    public override void Update()
    {
        if (_font.IsNotValid() || _material.IsNotValid())
            return;

        if (_isDirty)
            RebuildMesh();

        // Push renderable every frame the component is active
        if (!_mesh.IsValid() || _mesh.VertexCount <= 0)
            return;

        if (_subMeshFontIndices != null && _subMeshFontIndices.Length > 1)
        {
            // Multi-atlas path: push one MeshRenderable per sub-mesh,
            // each with its own PropertyState pointing to the correct font atlas.
            Float4x4 model = Transform.LocalToWorldMatrix;
            int layer = GameObject.LayerIndex;

            for (int i = 0; i < _subMeshFontIndices.Length; i++)
            {
                int fai = _subMeshFontIndices[i];
                FontAsset? subFont = _font.GetFontByIndex(fai);
                if (subFont.IsNotValid())
                    continue;

                PropertyState props = BuildPropertyStateForAtlas(subFont);
                GameObject.Scene.PushRenderable(new MeshRenderable(
                    _mesh, _material!, model, layer, props, i));
            }
        }
        else
        {
            // Single-atlas path: push this component directly
            GameObject.Scene.PushRenderable(this);
        }
    }

    public override void OnValidate()
    {
        _isDirty = true;
    }

    public override void DrawGizmos()
    {
        // Draw the layout rect as a wireframe box
        Float3 pos = Transform.Position;
        Float3 halfSize = new(_rectSize.X * 0.5f, _rectSize.Y * 0.5f, 0.01f);
        Debug.DrawWireCube(pos, halfSize, Color.Yellow);
    }

    // ── Mesh Rebuild ──────────────────────────────────────────

    /// <summary> Forces an immediate mesh rebuild, bypassing the dirty check. </summary>
    public void ForceMeshUpdate()
    {
        _isDirty = true;
        RebuildMesh();
    }

    /// <summary> Returns the last computed text layout (for hit-testing, caret positioning). </summary>
    public TextLayout GetTextLayout() => _layout;

    /// <summary> Access the generated mesh directly. </summary>
    public Mesh? GetMesh() => _mesh;

    private void RebuildMesh()
    {
        _isDirty = false;

        if (_font.IsNotValid() || _mesh.IsNotValid())
            return;

        // Parse rich text if enabled
        string displayText = _text;
        StyleRun[]? styleRuns = null;

        if (_richText && !string.IsNullOrEmpty(_text))
        {
            ParsedText parsed = RichTextParser.Parse(_text);
            displayText = parsed.StrippedText;
            styleRuns = parsed.Runs;
        }

        // Determine wrap width
        float maxWidth = _overflow == TextOverflowMode.Overflow
            ? float.MaxValue
            : _rectSize.X;

        // Shape and layout
        _layout = TextShaper.Shape(
            displayText,
            _font,
            _fontSize,
            maxWidth,
            _alignment,
            _verticalAlignment,
            _overflow,
            _color,
            _characterSpacing,
            _lineSpacing,
            _wordSpacing,
            styleRuns);

        // Build mesh
        _mesh = TextMeshBuilder.Build(_layout, _font, _mesh, out _subMeshFontIndices);

        // Upload to GPU
        _mesh.Upload();

        // Update material properties
        UpdateMaterialProperties();

        // Build effects from rich text tags
        BuildEffectsFromStyleRuns(styleRuns);

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
        _properties.SetInt("_ObjectID", InstanceID);

        if (_font.AtlasTexture.IsValid())
            _properties.SetTexture("_FontAtlas", _font.AtlasTexture);

        _properties.SetFloat("_PxRange", _font.AtlasPxRange);
        _properties.SetColor("_FaceColor", _color);
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

    private void OnFontAtlasChanged(FontAtlasChangedArgs args)
    {
        if (args.FontAsset == _font || IsFallbackFont(args.FontAsset))
            _isDirty = true;
    }

    private void OnAssetsRefreshed()
    {
        // After an asset refresh, the font may have been reimported
        if (_font.IsValid())
            _isDirty = true;
    }

    private void OnAssetDeleted(AssetDeletedArgs args)
    {
        if (_font.IsValid() && _font.AssetPath == args.RelativePath)
        {
            Debug.LogWarning($"TextRenderer: Font asset '{args.RelativePath}' was deleted.");
            _font = null;
            _isDirty = true;
        }
    }

    private bool IsFallbackFont(FontAsset? fontAsset)
    {
        if (_font.IsNotValid() || fontAsset.IsNotValid())
            return false;

        foreach (FontAsset fallback in _font.FallbackFonts)
        {
            if (fallback == fontAsset)
                return true;
        }
        return false;
    }

    // ── Effect Management ─────────────────────────────────────

    private void UpdateEffectSubscription()
    {
        bool needsSub = _effects.Count > 0 && _effects.Exists(e => e.Enabled);
        _hasActiveEffects = needsSub;

        if (needsSub && _beginRenderSub == null)
        {
            _beginRenderSub = RenderingEvents.SubscribeOnBeginRender(() => OnBeginRender());
        }
        else if (!needsSub && _beginRenderSub != null)
        {
            _beginRenderSub.Dispose();
            _beginRenderSub = null;
        }
    }

    private void OnBeginRender()
    {
        if (!_hasActiveEffects || _mesh.IsNotValid() || _mesh.Vertices == null || _mesh.Vertices.Length == 0)
            return;

        Span<Float3> positions = _mesh.Vertices.AsSpan();
        Span<Color> meshColors = _mesh.Colors.AsSpan();
        Span<Float2> meshUVs = _mesh.UV.AsSpan();
        float time = (float)Time.TimeSinceStartup;

        for (int i = 0; i < _effects.Count; i++)
        {
            if (_effects[i].Enabled)
                _effects[i].Apply(positions, meshColors, meshUVs, _layout, time);
        }

        // Re-upload the modified vertex data
        _mesh.Upload();
    }

    /// <summary>
    /// Builds text effects from the parsed style runs.
    /// Called after mesh rebuild when rich text contains effect tags.
    /// </summary>
    private void BuildEffectsFromStyleRuns(StyleRun[]? runs)
    {
        _effects.Clear();

        if (runs == null || runs.Length == 0)
        {
            UpdateEffectSubscription();
            return;
        }

        // Check if any run has effect flags
        bool hasEffects = false;
        for (int i = 0; i < runs.Length; i++)
        {
            if ((runs[i].StyleFlags & TextStyleFlagExtensions.AllEffects) != TextStyleFlags.None)
            {
                hasEffects = true;
                break;
            }
        }

        if (!hasEffects)
        {
            UpdateEffectSubscription();
            return;
        }

        // Build effects for each distinct effect flag combination
        // For simplicity, create one effect per active flag type covering all glyphs with that flag
        if (HasEffectFlag(runs, TextStyleFlags.Wave))
        {
            float amp = GetEffectParam(runs, TextStyleFlags.Wave, r => r.WaveAmplitude) ?? 1f;
            float freq = GetEffectParam(runs, TextStyleFlags.Wave, r => r.WaveFrequency) ?? 2f;
            _effects.Add(new WaveEffect { Amplitude = amp, Frequency = freq });
        }

        if (HasEffectFlag(runs, TextStyleFlags.Shake))
        {
            float intensity = GetEffectParam(runs, TextStyleFlags.Shake, r => r.ShakeIntensity) ?? 1f;
            _effects.Add(new ShakeEffect { Amplitude = intensity });
        }

        if (HasEffectFlag(runs, TextStyleFlags.Fade))
            _effects.Add(new FadeInEffect());

        if (HasEffectFlag(runs, TextStyleFlags.Typewriter))
            _effects.Add(new TypewriterEffect());

        if (HasEffectFlag(runs, TextStyleFlags.Rainbow))
            _effects.Add(new RainbowEffect());

        if (HasEffectFlag(runs, TextStyleFlags.ScaleEffect))
            _effects.Add(new ScaleEffect());

        if (HasEffectFlag(runs, TextStyleFlags.RotateEffect))
            _effects.Add(new RotateEffect());

        UpdateEffectSubscription();
    }

    private static bool HasEffectFlag(StyleRun[] runs, TextStyleFlags flag)
    {
        for (int i = 0; i < runs.Length; i++)
        {
            if ((runs[i].StyleFlags & flag) != 0)
                return true;
        }
        return false;
    }

    private static float? GetEffectParam(StyleRun[] runs, TextStyleFlags flag, Func<StyleRun, float?> selector)
    {
        for (int i = 0; i < runs.Length; i++)
        {
            if ((runs[i].StyleFlags & flag) != 0)
            {
                float? val = selector(runs[i]);
                if (val.HasValue)
                    return val;
            }
        }
        return null;
    }

    // ── IRenderable (single-atlas path) ────────────────────────

    public Material GetMaterial() => _material!;
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
        mesh = _mesh!;
        model = Transform.LocalToWorldMatrix;
        instanceData = null;
    }

    public void GetCullingData(out bool isRenderable, out AABB bounds)
    {
        isRenderable = _font.IsValid() && !string.IsNullOrEmpty(_text) && _mesh.IsValid();
        bounds = isRenderable
            ? _mesh!.bounds.TransformBy(Transform.LocalToWorldMatrix)
            : default;
    }

    /// <summary>
    /// Builds a <see cref="PropertyState"/> configured for a specific font atlas.
    /// Used by the multi-atlas rendering path to set the correct atlas texture
    /// and SDF parameters for each sub-mesh.
    /// </summary>
    private PropertyState BuildPropertyStateForAtlas(FontAsset subFont)
    {
        PropertyState props = new();
        props.SetInt("_ObjectID", InstanceID);

        if (subFont.AtlasTexture.IsValid())
            props.SetTexture("_FontAtlas", subFont.AtlasTexture);

        props.SetFloat("_PxRange", subFont.AtlasPxRange);
        props.SetColor("_FaceColor", _color);
        props.SetColor("_OutlineColor", _outlineColor);
        props.SetFloat("_OutlineWidth", _outlineWidth);
        props.SetFloat("_Softness", _softness);
        props.SetColor("_UnderlayColor", _underlayColor);
        props.SetFloat("_UnderlayOffsetX", _underlayOffset.X);
        props.SetFloat("_UnderlayOffsetY", _underlayOffset.Y);
        props.SetFloat("_UnderlayDilate", _underlayDilate);
        props.SetFloat("_UnderlaySoftness", _underlaySoftness);
        return props;
    }

    // ── Hit-Testing API ───────────────────────────────────────

    /// <summary>
    /// Returns the character index at the given local-space position, or -1 if none.
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
}
