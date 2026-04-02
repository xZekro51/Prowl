// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Vector;

namespace Prowl.Runtime.Text;

/// <summary>
/// The result of a text shaping and layout pass.
/// Produced by <see cref="TextShaper"/> and consumed by <see cref="TextMeshBuilder"/>.
/// </summary>
public struct TextLayout
{
    /// <summary> Positioned glyphs ready for mesh generation. </summary>
    public GlyphPlacement[] Glyphs;

    /// <summary> Per-line metadata (width, ascender, descender). </summary>
    public LineInfo[] Lines;

    /// <summary> Total bounding box of the laid-out text. </summary>
    public Float2 TextBounds;

    /// <summary> The size the text would prefer if unconstrained. </summary>
    public Float2 PreferredSize;
}

/// <summary>
/// A single positioned glyph within a <see cref="TextLayout"/>.
/// </summary>
public struct GlyphPlacement
{
    /// <summary> Index into the <see cref="FontAsset"/> glyph table. </summary>
    public int GlyphIndex;

    /// <summary> 0 = primary font, 1+ = fallback font index. </summary>
    public int FontAssetIndex;

    /// <summary> Baseline-relative position in local space. </summary>
    public Float2 Position;

    /// <summary> Scale factor (for size overrides). </summary>
    public Float2 Scale;

    /// <summary> Per-character color. </summary>
    public Color Color;

    /// <summary> Style flags (bold, italic, underline, etc.). </summary>
    public TextStyleFlags StyleFlags;

    /// <summary> Background highlight color from mark tag, or <c>null</c>. </summary>
    public Color? MarkColor;

    /// <summary> Which line this glyph belongs to. </summary>
    public int LineIndex;

    /// <summary> Index into the source (stripped) text string. </summary>
    public int CharacterIndex;
}

/// <summary>
/// Metadata for a single line of text within a <see cref="TextLayout"/>.
/// </summary>
public struct LineInfo
{
    /// <summary> Index of the first glyph on this line. </summary>
    public int StartGlyphIndex;

    /// <summary> Number of glyphs on this line. </summary>
    public int GlyphCount;

    /// <summary> Total advance width of the line. </summary>
    public float Width;

    /// <summary> Maximum ascender of all glyphs on this line. </summary>
    public float Ascender;

    /// <summary> Maximum descender (typically negative) of all glyphs on this line. </summary>
    public float Descender;

    /// <summary> Y position of the baseline for this line. </summary>
    public float Baseline;
}
