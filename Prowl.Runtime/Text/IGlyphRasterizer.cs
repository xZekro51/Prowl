// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Runtime.Text;

/// <summary>
/// Result of rasterizing a single glyph, including pixel data and metrics.
/// </summary>
public readonly struct RasterizedGlyph
{
    /// <summary> The FreeType (or equivalent) glyph index. </summary>
    public readonly uint GlyphIndex;

    /// <summary> The Unicode codepoint this glyph represents. </summary>
    public readonly uint Codepoint;

    /// <summary> Glyph width in font units (scaled). </summary>
    public readonly float Width;

    /// <summary> Glyph height in font units (scaled). </summary>
    public readonly float Height;

    /// <summary> Horizontal bearing in font units. </summary>
    public readonly float BearingX;

    /// <summary> Vertical bearing in font units. </summary>
    public readonly float BearingY;

    /// <summary> Horizontal advance in font units. </summary>
    public readonly float Advance;

    /// <summary>
    /// Pixel data for the glyph cell (SDF or bitmap).
    /// Format is RGBA (4 bytes per pixel), row-major, top-to-bottom.
    /// For SDF, the distance value is replicated across all channels.
    /// For MSDF, RGB hold the multi-channel distances, A = 255.
    /// May be empty for whitespace glyphs (space, tab).
    /// </summary>
    public readonly byte[] PixelData;

    /// <summary> Width of the pixel data in pixels. </summary>
    public readonly int PixelWidth;

    /// <summary> Height of the pixel data in pixels. </summary>
    public readonly int PixelHeight;

    public RasterizedGlyph(
        uint glyphIndex, uint codepoint,
        float width, float height, float bearingX, float bearingY, float advance,
        byte[] pixelData, int pixelWidth, int pixelHeight)
    {
        GlyphIndex = glyphIndex;
        Codepoint = codepoint;
        Width = width;
        Height = height;
        BearingX = bearingX;
        BearingY = bearingY;
        Advance = advance;
        PixelData = pixelData;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }
}

/// <summary>
/// Abstraction for rasterizing individual glyphs from font data at runtime.
/// Implementations may use FreeType, platform-specific APIs, or other font engines.
/// </summary>
public interface IGlyphRasterizer : IDisposable
{
    /// <summary>
    /// Initializes the rasterizer with raw font file data (.ttf/.otf bytes).
    /// Must be called before <see cref="RasterizeGlyph"/>.
    /// </summary>
    /// <param name="fontData">The raw bytes of the font file.</param>
    /// <param name="pointSize">The point size to render glyphs at.</param>
    /// <param name="atlasType">The type of atlas being generated (SDF, MSDF, or Bitmap).</param>
    /// <param name="pxRange">SDF distance range in pixels (used for SDF/MSDF).</param>
    /// <param name="padding">Padding around each glyph cell in pixels.</param>
    /// <returns><c>true</c> if initialization succeeded.</returns>
    bool Initialize(byte[] fontData, float pointSize, AtlasType atlasType, float pxRange, int padding);

    /// <summary>
    /// Rasterizes a single glyph for the given Unicode codepoint.
    /// The returned pixel data is ready to be blitted into the atlas texture.
    /// </summary>
    /// <param name="codepoint">The Unicode codepoint to rasterize.</param>
    /// <param name="glyph">The rasterized glyph data if successful.</param>
    /// <returns><c>true</c> if the glyph was found and rasterized; <c>false</c> if the codepoint is not in the font.</returns>
    bool RasterizeGlyph(uint codepoint, out RasterizedGlyph glyph);

    /// <summary>
    /// Gets the kerning advance between two glyph indices.
    /// </summary>
    /// <param name="leftGlyphIndex">Left glyph index.</param>
    /// <param name="rightGlyphIndex">Right glyph index.</param>
    /// <param name="kerning">The kerning advance if found.</param>
    /// <returns><c>true</c> if a kerning value exists for the pair.</returns>
    bool TryGetKerning(uint leftGlyphIndex, uint rightGlyphIndex, out float kerning);
}
