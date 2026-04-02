// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Runtime.Resources;
using Prowl.Runtime.Text;

using Xunit;

namespace Prowl.Runtime.Test;

/// <summary>
/// Tests that <see cref="FontAsset"/> creation via the builder API
/// (<see cref="FontAsset.SetMetrics"/>, <see cref="FontAsset.SetGlyphData"/>,
/// <see cref="FontAsset.SetAtlas"/>) works correctly. Covers glyph lookup,
/// ASCII fast-path, kerning pairs, fallback chain, and edge cases.
/// </summary>
public class FontAssetCreationTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable d in _disposables)
            d.Dispose();
        _disposables.Clear();
    }

    /// <summary>
    /// Creates a font with ASCII glyphs 32-127, matching the TextDemo pattern.
    /// </summary>
    private FontAsset CreateBasicFont()
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(font);

        GlyphData[] glyphs = new GlyphData[128];
        Dictionary<uint, int> charTable = [];

        for (uint c = 32; c < 128; c++)
        {
            int idx = (int)c;
            glyphs[idx] = new GlyphData(
                GlyphIndex: c,
                Width: 12f,
                Height: 20f,
                BearingX: 1f,
                BearingY: 18f,
                Advance: 14f,
                AtlasX: (c % 16) * 20f,
                AtlasY: (c / 16) * 24f,
                AtlasWidth: 14f,
                AtlasHeight: 22f,
                Scale: 1f);
            charTable[c] = idx;
        }

        // Space glyph — no visible area
        glyphs[32] = new GlyphData(32, 0, 0, 0, 0, 14f, 0, 0, 0, 0, 1f);

        font.SetMetrics(32f, 40f, 30f, -10f, 0f);
        font.SetGlyphData(glyphs, charTable, []);
        font.SetAtlas(null!, 320, 192, AtlasType.MSDF, 4f, 1);

        return font;
    }

    // ── SetMetrics ──────────────────────────────────────────────

    [Fact]
    public void SetMetrics_StoresAllValues()
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(font);

        font.SetMetrics(48f, 56f, 42f, -14f, 2f);

        Assert.Equal(48f, font.PointSize);
        Assert.Equal(56f, font.LineHeight);
        Assert.Equal(42f, font.Ascender);
        Assert.Equal(-14f, font.Descender);
        Assert.Equal(2f, font.Baseline);
    }

    [Fact]
    public void SetMetrics_OverwritesPreviousValues()
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(font);

        font.SetMetrics(16f, 20f, 14f, -6f, 0f);
        font.SetMetrics(48f, 56f, 42f, -14f, 2f);

        Assert.Equal(48f, font.PointSize);
        Assert.Equal(56f, font.LineHeight);
        Assert.Equal(42f, font.Ascender);
        Assert.Equal(-14f, font.Descender);
        Assert.Equal(2f, font.Baseline);
    }

    // ── SetAtlas ────────────────────────────────────────────────

    [Fact]
    public void SetAtlas_StoresAllMetadata()
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(font);

        font.SetAtlas(null!, 512, 256, AtlasType.MSDF, 6f, 2);

        Assert.Equal(512, font.AtlasWidth);
        Assert.Equal(256, font.AtlasHeight);
        Assert.Equal(AtlasType.MSDF, font.AtlasType);
        Assert.Equal(6f, font.AtlasPxRange);
        Assert.Equal(2, font.Padding);
    }

    [Theory]
    [InlineData(AtlasType.SDF)]
    [InlineData(AtlasType.MSDF)]
    [InlineData(AtlasType.Bitmap)]
    public void SetAtlas_AcceptsAllAtlasTypes(AtlasType type)
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(font);

        font.SetAtlas(null!, 256, 256, type, 4f, 1);

        Assert.Equal(type, font.AtlasType);
    }

    // ── SetGlyphData — basic glyph storage ──────────────────────

    [Fact]
    public void SetGlyphData_StoresGlyphTable()
    {
        FontAsset font = CreateBasicFont();

        // 128 entries (indices 0-127, though only 32-127 are populated)
        Assert.Equal(128, font.GlyphTable.Count);
    }

    [Fact]
    public void SetGlyphData_StoresCharacterTable()
    {
        FontAsset font = CreateBasicFont();

        // 96 entries (codepoints 32-127)
        Assert.Equal(96, font.CharacterTable.Count);
    }

    [Fact]
    public void SetGlyphData_SpaceGlyph_HasNoVisibleArea()
    {
        FontAsset font = CreateBasicFont();

        Assert.True(font.TryGetGlyph(32, out GlyphData space));
        Assert.Equal(0f, space.Width);
        Assert.Equal(0f, space.Height);
        Assert.Equal(14f, space.Advance);
    }

    [Fact]
    public void SetGlyphData_PrintableGlyphs_HaveCorrectMetrics()
    {
        FontAsset font = CreateBasicFont();

        // Verify 'A' (codepoint 65)
        Assert.True(font.TryGetGlyph(65, out GlyphData glyphA));
        Assert.Equal(65u, glyphA.GlyphIndex);
        Assert.Equal(12f, glyphA.Width);
        Assert.Equal(20f, glyphA.Height);
        Assert.Equal(1f, glyphA.BearingX);
        Assert.Equal(18f, glyphA.BearingY);
        Assert.Equal(14f, glyphA.Advance);
        Assert.Equal(1f, glyphA.Scale);
    }

    [Fact]
    public void SetGlyphData_AtlasCoordinates_ComputedCorrectly()
    {
        FontAsset font = CreateBasicFont();

        // Verify atlas positions follow the (c % 16) * 20 / (c / 16) * 24 pattern
        Assert.True(font.TryGetGlyph(65, out GlyphData glyphA));
        Assert.Equal((65 % 16) * 20f, glyphA.AtlasX);
        Assert.Equal((65 / 16) * 24f, glyphA.AtlasY);
        Assert.Equal(14f, glyphA.AtlasWidth);
        Assert.Equal(22f, glyphA.AtlasHeight);
    }

    // ── TryGetGlyph — ASCII fast-path ───────────────────────────

    [Fact]
    public void TryGetGlyph_AsciiRange_UsesAsciiLookup()
    {
        FontAsset font = CreateBasicFont();

        // All printable ASCII should resolve
        for (uint c = 32; c < 128; c++)
        {
            Assert.True(font.TryGetGlyph(c, out GlyphData glyph),
                $"Codepoint {c} ('{(char)c}') should resolve via ASCII fast-path");
            Assert.Equal(c, glyph.GlyphIndex);
        }
    }

    [Fact]
    public void TryGetGlyph_ControlCharacters_ReturnFalse()
    {
        FontAsset font = CreateBasicFont();

        // Control characters 0-31 should not resolve (no glyphs defined)
        for (uint c = 0; c < 32; c++)
        {
            Assert.False(font.TryGetGlyph(c, out _),
                $"Control codepoint {c} should not have a glyph");
        }
    }

    [Fact]
    public void TryGetGlyph_NonExistentCodepoint_ReturnsFalse()
    {
        FontAsset font = CreateBasicFont();

        // Codepoint beyond ASCII range, not in character table
        Assert.False(font.TryGetGlyph(256, out _));
        Assert.False(font.TryGetGlyph(0x1F600, out _)); // emoji
    }

    // ── Kerning ─────────────────────────────────────────────────

    [Fact]
    public void SetGlyphData_WithKerning_StoresKerningPairs()
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(font);

        Dictionary<ulong, float> kerning = new()
        {
            [PackKerning(65, 86)] = -1.5f,   // A-V
            [PackKerning(84, 111)] = -0.8f,   // T-o
            [PackKerning(87, 97)] = -1.2f,    // W-a
        };

        font.SetGlyphData([], [], kerning);

        Assert.Equal(3, font.KerningPairs.Count);
        Assert.True(font.TryGetKerning(65, 86, out float kern));
        Assert.Equal(-1.5f, kern);
    }

    [Fact]
    public void TryGetKerning_MissingPair_ReturnsFalse()
    {
        FontAsset font = CreateBasicFont();

        Assert.False(font.TryGetKerning(65, 66, out _));
    }

    [Fact]
    public void SetKerningPair_AddsNewPair()
    {
        FontAsset font = CreateBasicFont();

        font.SetKerningPair(65, 86, -2.0f);

        Assert.True(font.TryGetKerning(65, 86, out float kern));
        Assert.Equal(-2.0f, kern);
    }

    [Fact]
    public void SetKerningPair_OverwritesExistingPair()
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(font);

        Dictionary<ulong, float> kerning = new()
        {
            [PackKerning(65, 86)] = -1.5f,
        };
        font.SetGlyphData([], [], kerning);

        font.SetKerningPair(65, 86, -3.0f);

        Assert.True(font.TryGetKerning(65, 86, out float kern));
        Assert.Equal(-3.0f, kern);
    }

    [Fact]
    public void RemoveKerningPair_RemovesExistingPair()
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(font);

        Dictionary<ulong, float> kerning = new()
        {
            [PackKerning(65, 86)] = -1.5f,
        };
        font.SetGlyphData([], [], kerning);

        Assert.True(font.RemoveKerningPair(65, 86));
        Assert.False(font.TryGetKerning(65, 86, out _));
    }

    [Fact]
    public void RemoveKerningPair_NonExistent_ReturnsFalse()
    {
        FontAsset font = CreateBasicFont();

        Assert.False(font.RemoveKerningPair(65, 86));
    }

    // ── Fallback chain ──────────────────────────────────────────

    [Fact]
    public void TryGetGlyphWithFallback_PrimaryFont_ReturnsFontIndexZero()
    {
        FontAsset font = CreateBasicFont();

        Assert.True(font.TryGetGlyphWithFallback(65, out GlyphData glyph, out int fontIndex));
        Assert.Equal(0, fontIndex);
        Assert.Equal(65u, glyph.GlyphIndex);
    }

    [Fact]
    public void TryGetGlyphWithFallback_FallbackFont_ReturnsCorrectIndex()
    {
        // Primary font: only has space (32)
        FontAsset primary = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(primary);

        GlyphData[] primaryGlyphs = new GlyphData[128];
        Dictionary<uint, int> primaryCharTable = [];
        primaryGlyphs[32] = new GlyphData(32, 0, 0, 0, 0, 14f, 0, 0, 0, 0, 1f);
        primaryCharTable[32] = 32;
        primary.SetMetrics(32f, 40f, 30f, -10f, 0f);
        primary.SetGlyphData(primaryGlyphs, primaryCharTable, []);

        // Fallback font: has 'A' (65)
        FontAsset fallback = CreateBasicFont();

        primary.SetFallbackFonts([fallback]);

        // 'A' not in primary, but in fallback
        Assert.True(primary.TryGetGlyphWithFallback(65, out GlyphData glyph, out int fontIndex));
        Assert.Equal(1, fontIndex);
        Assert.Equal(65u, glyph.GlyphIndex);
    }

    [Fact]
    public void TryGetGlyphWithFallback_NoMatch_ReturnsFalse()
    {
        FontAsset font = CreateBasicFont();
        font.SetFallbackFonts([]);

        Assert.False(font.TryGetGlyphWithFallback(0x1F600, out _, out int fontIndex));
        Assert.Equal(-1, fontIndex);
    }

    // ── GetFontByIndex ──────────────────────────────────────────

    [Fact]
    public void GetFontByIndex_Zero_ReturnsSelf()
    {
        FontAsset font = CreateBasicFont();

        Assert.Same(font, font.GetFontByIndex(0));
    }

    [Fact]
    public void GetFontByIndex_Fallback_ReturnsCorrectFont()
    {
        FontAsset primary = CreateBasicFont();
        FontAsset fallback = CreateBasicFont();
        primary.SetFallbackFonts([fallback]);

        Assert.Same(fallback, primary.GetFontByIndex(1));
    }

    [Fact]
    public void GetFontByIndex_OutOfRange_ReturnsNull()
    {
        FontAsset font = CreateBasicFont();

        Assert.Null(font.GetFontByIndex(1));
        Assert.Null(font.GetFontByIndex(99));
    }

    // ── WarmupCharacters ────────────────────────────────────────

    [Fact]
    public void WarmupCharacters_AllPresent_ReturnsZeroMissing()
    {
        FontAsset font = CreateBasicFont();

        int missing = font.WarmupCharacters("Hello");

        Assert.Equal(0, missing);
    }

    [Fact]
    public void WarmupCharacters_SomeMissing_ReturnsCorrectCount()
    {
        FontAsset font = CreateBasicFont();

        // U+00E9 (é) and U+00F1 (ñ) are outside ASCII and not in the character table
        int missing = font.WarmupCharacters("Hé\u00F1lo");

        Assert.Equal(2, missing);
    }

    [Fact]
    public void WarmupCharacters_EmptyString_ReturnsZero()
    {
        FontAsset font = CreateBasicFont();

        Assert.Equal(0, font.WarmupCharacters(""));
        Assert.Equal(0, font.WarmupCharacters(null!));
    }

    [Fact]
    public void WarmupCharacters_DuplicateCodepoints_CountsMissingOnce()
    {
        FontAsset font = CreateBasicFont();

        // U+00E9 (é) appears three times but should only count once
        int missing = font.WarmupCharacters("\u00E9\u00E9\u00E9");

        Assert.Equal(1, missing);
    }

    // ── Edge cases ──────────────────────────────────────────────

    [Fact]
    public void EmptyFont_TryGetGlyph_ReturnsFalse()
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(font);
        font.SetGlyphData([], [], []);

        Assert.False(font.TryGetGlyph(65, out _));
    }

    [Fact]
    public void SetGlyphData_NullArguments_DefaultsToEmptyCollections()
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(font);

        font.SetGlyphData(null!, null!, null!);

        Assert.Equal(0, font.GlyphTable.Count);
        Assert.Equal(0, font.CharacterTable.Count);
        Assert.Equal(0, font.KerningPairs.Count);
    }

    [Fact]
    public void SetGlyphData_CalledTwice_ReplacesOldData()
    {
        FontAsset font = CreateBasicFont();

        // Verify 'A' exists
        Assert.True(font.TryGetGlyph(65, out _));

        // Replace with empty data
        font.SetGlyphData([], [], []);

        Assert.False(font.TryGetGlyph(65, out _));
        Assert.Equal(0, font.CharacterTable.Count);
    }

    [Fact]
    public void SetFallbackFonts_NullArray_DefaultsToEmpty()
    {
        FontAsset font = CreateBasicFont();

        font.SetFallbackFonts(null!);

        Assert.Equal(0, font.FallbackFonts.Count);
    }

    [Fact]
    public void OnAfterDeserialize_RebuildAsciiLookup_RestoresGlyphAccess()
    {
        FontAsset font = CreateBasicFont();

        // Simulate deserialization callback
        font.OnAfterDeserialize();

        // ASCII lookup should still work after rebuild
        Assert.True(font.TryGetGlyph(65, out GlyphData glyph));
        Assert.Equal(65u, glyph.GlyphIndex);
    }

    [Fact]
    public void VaryingGlyphMetrics_StoredCorrectly()
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(font);

        GlyphData[] glyphs = new GlyphData[128];
        Dictionary<uint, int> charTable = [];

        // Create glyphs with varying metrics (similar to TextDemo's procedural font pattern)
        for (uint c = 32; c < 128; c++)
        {
            int idx = (int)c;
            glyphs[idx] = new GlyphData(
                GlyphIndex: c,
                Width: 10f + (c % 5),
                Height: 18f + (c % 3),
                BearingX: 1f,
                BearingY: 16f,
                Advance: 12f + (c % 4),
                AtlasX: (c % 16) * 20f,
                AtlasY: (c / 16) * 24f,
                AtlasWidth: 14f,
                AtlasHeight: 22f,
                Scale: 1f);
            charTable[c] = idx;
        }

        font.SetMetrics(32f, 40f, 30f, -10f, 0f);
        font.SetGlyphData(glyphs, charTable, []);

        // Verify varying widths for different codepoints
        Assert.True(font.TryGetGlyph(65, out GlyphData gA));
        Assert.True(font.TryGetGlyph(66, out GlyphData gB));
        Assert.True(font.TryGetGlyph(67, out GlyphData gC));

        Assert.Equal(10f + (65 % 5), gA.Width);
        Assert.Equal(10f + (66 % 5), gB.Width);
        Assert.Equal(10f + (67 % 5), gC.Width);

        Assert.Equal(12f + (65 % 4), gA.Advance);
        Assert.Equal(12f + (66 % 4), gB.Advance);
        Assert.Equal(12f + (67 % 4), gC.Advance);
    }

    [Fact]
    public void MultipleFallbacks_SearchesInOrder()
    {
        FontAsset primary = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(primary);
        primary.SetMetrics(32f, 40f, 30f, -10f, 0f);
        primary.SetGlyphData([], [], []);

        // First fallback: has codepoint 200
        FontAsset fallback1 = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(fallback1);
        GlyphData[] glyphs1 = new GlyphData[201];
        glyphs1[200] = new GlyphData(200, 10, 20, 1, 18, 14, 0, 0, 14, 22, 1f);
        Dictionary<uint, int> ct1 = new() { [200] = 200 };
        fallback1.SetMetrics(32f, 40f, 30f, -10f, 0f);
        fallback1.SetGlyphData(glyphs1, ct1, []);

        // Second fallback: has codepoint 300
        FontAsset fallback2 = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(fallback2);
        GlyphData[] glyphs2 = new GlyphData[301];
        glyphs2[300] = new GlyphData(300, 10, 20, 1, 18, 14, 0, 0, 14, 22, 1f);
        Dictionary<uint, int> ct2 = new() { [300] = 300 };
        fallback2.SetMetrics(32f, 40f, 30f, -10f, 0f);
        fallback2.SetGlyphData(glyphs2, ct2, []);

        primary.SetFallbackFonts([fallback1, fallback2]);

        // Codepoint 200 → found in fallback1 (index 1)
        Assert.True(primary.TryGetGlyphWithFallback(200, out GlyphData g200, out int idx200));
        Assert.Equal(1, idx200);
        Assert.Equal(200u, g200.GlyphIndex);

        // Codepoint 300 → found in fallback2 (index 2)
        Assert.True(primary.TryGetGlyphWithFallback(300, out GlyphData g300, out int idx300));
        Assert.Equal(2, idx300);
        Assert.Equal(300u, g300.GlyphIndex);

        // Codepoint 400 → not found
        Assert.False(primary.TryGetGlyphWithFallback(400, out _, out int idxMissing));
        Assert.Equal(-1, idxMissing);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static ulong PackKerning(uint left, uint right) =>
        ((ulong)left << 32) | right;
}
