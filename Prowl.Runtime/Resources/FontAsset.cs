// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Echo;
using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Text;

namespace Prowl.Runtime.Resources;

/// <summary>
/// A baked font asset containing an SDF/MSDF atlas and glyph metrics.
/// Created by the editor's FontAssetImporter from a .ttf/.otf source file.
/// </summary>
[CreateAssetMenu(MenuName = "Text/Font Asset", FileName = "NewFontAsset")]
public class FontAsset : ScriptableObject
{
    // ── Atlas ──────────────────────────────────────────────────

    /// <summary> The baked atlas texture (SDF, MSDF, or bitmap). </summary>
    [SerializeField] private Texture2D? _atlasTexture;

    /// <summary> Atlas width in pixels. </summary>
    [SerializeField] private int _atlasWidth;

    /// <summary> Atlas height in pixels. </summary>
    [SerializeField] private int _atlasHeight;

    /// <summary> The type of distance field stored in the atlas. </summary>
    [SerializeField] private AtlasType _atlasType = AtlasType.MSDF;

    /// <summary> SDF distance range in atlas pixels (used by the shader). </summary>
    [SerializeField] private float _atlasPxRange = 4f;

    /// <summary> Pixel padding around each glyph in the atlas. </summary>
    [SerializeField] private int _padding = 1;

    // ── Font metrics ──────────────────────────────────────────

    /// <summary> The point size used when the atlas was generated. </summary>
    [SerializeField] private float _pointSize = 32f;

    /// <summary> Default line height in font units. </summary>
    [SerializeField] private float _lineHeight;

    /// <summary> Typographic ascender in font units. </summary>
    [SerializeField] private float _ascender;

    /// <summary> Typographic descender (typically negative) in font units. </summary>
    [SerializeField] private float _descender;

    /// <summary> Baseline offset in font units. </summary>
    [SerializeField] private float _baseline;

    // ── Glyph data ────────────────────────────────────────────

    /// <summary> Flat array of glyph metrics, indexed by glyph index. </summary>
    [SerializeField] private GlyphData[] _glyphTable = [];

    /// <summary>
    /// Maps Unicode codepoints to glyph indices in <see cref="_glyphTable"/>.
    /// </summary>
    [SerializeField] private Dictionary<uint, int> _characterTable = [];

    /// <summary>
    /// Kerning pair table. Key = packed pair (left &lt;&lt; 32 | right), value = kerning advance.
    /// </summary>
    [SerializeField] private Dictionary<ulong, float> _kerningPairs = [];

    /// <summary>
    /// Direct-mapped ASCII fast-path array (codepoints 0-127).
    /// -1 means no glyph; otherwise the value is an index into <see cref="_glyphTable"/>.
    /// Rebuilt on <see cref="SetGlyphData"/> and <see cref="OnAfterDeserialize"/>.
    /// </summary>
    [SerializeIgnore] private int[] _asciiLookup = [];

    // ── Fallback chain ────────────────────────────────────────

    /// <summary> Fallback fonts searched when a codepoint is missing from this font. </summary>
    [SerializeField] private FontAsset[] _fallbackFonts = [];

    // ── Public accessors (read-only) ──────────────────────────

    public Texture2D? AtlasTexture => _atlasTexture;
    public int AtlasWidth => _atlasWidth;
    public int AtlasHeight => _atlasHeight;
    public AtlasType AtlasType => _atlasType;
    public float AtlasPxRange => _atlasPxRange;
    public int Padding => _padding;
    public float PointSize => _pointSize;
    public float LineHeight => _lineHeight;
    public float Ascender => _ascender;
    public float Descender => _descender;
    public float Baseline => _baseline;
    public IReadOnlyList<GlyphData> GlyphTable => _glyphTable;
    public IReadOnlyDictionary<uint, int> CharacterTable => _characterTable;
    public IReadOnlyDictionary<ulong, float> KerningPairs => _kerningPairs;
    public IReadOnlyList<FontAsset> FallbackFonts => _fallbackFonts;

    // ── Lookup API ────────────────────────────────────────────

    /// <summary>
    /// Tries to find a glyph for the given Unicode codepoint.
    /// </summary>
    /// <param name="codepoint">The Unicode codepoint to look up.</param>
    /// <param name="glyph">The glyph data if found.</param>
    /// <returns><c>true</c> if the glyph was found; otherwise <c>false</c>.</returns>
    public bool TryGetGlyph(uint codepoint, out GlyphData glyph)
    {
        // ASCII fast path — direct array lookup, no hashing
        if (codepoint < (uint)_asciiLookup.Length)
        {
            int idx = _asciiLookup[codepoint];
            if (idx >= 0)
            {
                glyph = _glyphTable[idx];
                return true;
            }
            glyph = default;
            return false;
        }

        if (_characterTable.TryGetValue(codepoint, out int index) &&
            index >= 0 && index < _glyphTable.Length)
        {
            glyph = _glyphTable[index];
            return true;
        }

        glyph = default;
        return false;
    }

    /// <summary>
    /// Tries to find a glyph for the given codepoint, walking the fallback chain.
    /// </summary>
    /// <param name="codepoint">The Unicode codepoint to look up.</param>
    /// <param name="glyph">The glyph data if found.</param>
    /// <param name="fontIndex">0 = this font, 1+ = index into <see cref="_fallbackFonts"/>.</param>
    /// <returns><c>true</c> if the glyph was found in this font or any fallback.</returns>
    public bool TryGetGlyphWithFallback(uint codepoint, out GlyphData glyph, out int fontIndex)
    {
        if (TryGetGlyph(codepoint, out glyph))
        {
            fontIndex = 0;
            return true;
        }

        for (int i = 0; i < _fallbackFonts.Length; i++)
        {
            FontAsset? fallback = _fallbackFonts[i];
            if (fallback.IsNotValid())
                continue;

            if (fallback.TryGetGlyph(codepoint, out glyph))
            {
                fontIndex = i + 1;
                return true;
            }
        }

        glyph = default;
        fontIndex = -1;
        return false;
    }

    /// <summary>
    /// Tries to get the kerning advance for a pair of glyphs.
    /// </summary>
    /// <param name="leftGlyphIndex">Glyph index of the left character.</param>
    /// <param name="rightGlyphIndex">Glyph index of the right character.</param>
    /// <param name="kerning">The kerning advance if found.</param>
    /// <returns><c>true</c> if a kerning pair was found.</returns>
    public bool TryGetKerning(uint leftGlyphIndex, uint rightGlyphIndex, out float kerning)
    {
        ulong key = ((ulong)leftGlyphIndex << 32) | rightGlyphIndex;
        return _kerningPairs.TryGetValue(key, out kerning);
    }

    /// <summary>
    /// Gets the font asset for a given font index (0 = this, 1+ = fallback).
    /// </summary>
    public FontAsset? GetFontByIndex(int fontIndex)
    {
        if (fontIndex == 0)
            return this;
        int fallbackIdx = fontIndex - 1;
        if (fallbackIdx >= 0 && fallbackIdx < _fallbackFonts.Length)
            return _fallbackFonts[fallbackIdx];
        return null;
    }

    // ── Builder API (for importers / dynamic atlas) ───────────

    /// <summary>
    /// Sets the atlas texture and dimensions. Intended for editor importers.
    /// </summary>
    public void SetAtlas(Texture2D texture, int width, int height, AtlasType type, float pxRange, int padding)
    {
        _atlasTexture = texture;
        _atlasWidth = width;
        _atlasHeight = height;
        _atlasType = type;
        _atlasPxRange = pxRange;
        _padding = padding;
    }

    /// <summary>
    /// Sets the font metrics. Intended for editor importers.
    /// </summary>
    public void SetMetrics(float pointSize, float lineHeight, float ascender, float descender, float baseline)
    {
        _pointSize = pointSize;
        _lineHeight = lineHeight;
        _ascender = ascender;
        _descender = descender;
        _baseline = baseline;
    }

    /// <summary>
    /// Sets the glyph and character tables. Intended for editor importers.
    /// </summary>
    public void SetGlyphData(GlyphData[] glyphs, Dictionary<uint, int> characterTable, Dictionary<ulong, float> kerningPairs)
    {
        _glyphTable = glyphs ?? [];
        _characterTable = characterTable ?? [];
        _kerningPairs = kerningPairs ?? [];
        RebuildAsciiLookup();
    }

    /// <summary>
    /// Sets or updates a single kerning pair. Intended for editor kerning pair editing.
    /// </summary>
    /// <param name="leftGlyphIndex">Glyph index of the left character.</param>
    /// <param name="rightGlyphIndex">Glyph index of the right character.</param>
    /// <param name="kerning">The kerning advance value.</param>
    public void SetKerningPair(uint leftGlyphIndex, uint rightGlyphIndex, float kerning)
    {
        ulong key = ((ulong)leftGlyphIndex << 32) | rightGlyphIndex;
        _kerningPairs[key] = kerning;
    }

    /// <summary>
    /// Removes a single kerning pair.
    /// </summary>
    /// <param name="leftGlyphIndex">Glyph index of the left character.</param>
    /// <param name="rightGlyphIndex">Glyph index of the right character.</param>
    /// <returns><c>true</c> if the pair was found and removed.</returns>
    public bool RemoveKerningPair(uint leftGlyphIndex, uint rightGlyphIndex)
    {
        ulong key = ((ulong)leftGlyphIndex << 32) | rightGlyphIndex;
        return _kerningPairs.Remove(key);
    }

    /// <summary>
    /// Sets the fallback font chain.
    /// </summary>
    public void SetFallbackFonts(FontAsset[] fallbacks)
    {
        _fallbackFonts = fallbacks ?? [];
    }

    // ── ASCII fast-path rebuild ───────────────────────────────

    /// <summary>
    /// Rebuilds the direct-mapped ASCII lookup array from the character table.
    /// </summary>
    private void RebuildAsciiLookup()
    {
        const int AsciiSize = 128;
        if (_asciiLookup.Length != AsciiSize)
            _asciiLookup = new int[AsciiSize];

        for (int i = 0; i < AsciiSize; i++)
            _asciiLookup[i] = -1;

        foreach (KeyValuePair<uint, int> entry in _characterTable)
        {
            if (entry.Key < AsciiSize && entry.Value >= 0 && entry.Value < _glyphTable.Length)
                _asciiLookup[entry.Key] = entry.Value;
        }
    }

    /// <summary>
    /// Called after Prowl.Echo deserialization to rebuild transient data structures.
    /// </summary>
    public void OnAfterDeserialize()
    {
        RebuildAsciiLookup();
    }

    // ── Preloading API ────────────────────────────────────────

    /// <summary>
    /// Pre-warms the glyph lookup by touching every unique codepoint in the given string.
    /// For static atlases this is a no-op beyond verifying coverage.
    /// When a dynamic atlas is attached, this ensures all needed glyphs are rasterized
    /// and packed before rendering begins, avoiding frame-time atlas rebuilds.
    /// </summary>
    /// <param name="characters">A string containing all characters to warm up.</param>
    /// <returns>The number of missing codepoints that could not be resolved.</returns>
    public int WarmupCharacters(string characters)
    {
        if (string.IsNullOrEmpty(characters))
            return 0;

        int missing = 0;
        HashSet<uint> seen = [];

        for (int i = 0; i < characters.Length; i++)
        {
            uint codepoint = (uint)characters[i];
            if (!seen.Add(codepoint))
                continue;

            if (!TryGetGlyphWithFallback(codepoint, out _, out _))
            {
                missing++;
                TextEvents.InvokeOnGlyphMissing(new GlyphMissingArgs(codepoint, this, characters));
            }
        }

        return missing;
    }
}
