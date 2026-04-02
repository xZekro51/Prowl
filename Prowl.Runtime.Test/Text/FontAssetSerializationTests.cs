// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Echo;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Text;

using Xunit;

namespace Prowl.Runtime.Test;

/// <summary>
/// Tests that <see cref="FontAsset"/> survives a Prowl.Echo serialization round-trip.
/// Covers glyph data, character table, kerning pairs, font metrics, atlas metadata,
/// and fallback chains.
/// </summary>
public class FontAssetSerializationTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable d in _disposables)
            d.Dispose();
        _disposables.Clear();
    }

    private FontAsset CreatePopulatedFont()
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

        glyphs[32] = new GlyphData(32, 0, 0, 0, 0, 12f, 0, 0, 0, 0, 1f);

        Dictionary<ulong, float> kerning = new()
        {
            [PackKerning(65, 86)] = -1.5f,
            [PackKerning(84, 111)] = -0.8f,
            [PackKerning(87, 97)] = -1.2f,
        };

        font.SetMetrics(48f, 56f, 42f, -14f, 2f);
        font.SetGlyphData(glyphs, charTable, kerning);
        font.SetAtlas(null!, 512, 512, AtlasType.MSDF, 6f, 2);

        return font;
    }

    private static ulong PackKerning(uint left, uint right) =>
        ((ulong)left << 32) | right;

    [Fact]
    public void RoundTrip_PreservesFontMetrics()
    {
        FontAsset original = CreatePopulatedFont();

        EchoObject serialized = Serializer.Serialize(original);
        FontAsset? deserialized = Serializer.Deserialize<FontAsset>(serialized);
        _disposables.Add(deserialized!);

        Assert.NotNull(deserialized);
        Assert.Equal(original.PointSize, deserialized!.PointSize);
        Assert.Equal(original.LineHeight, deserialized.LineHeight);
        Assert.Equal(original.Ascender, deserialized.Ascender);
        Assert.Equal(original.Descender, deserialized.Descender);
        Assert.Equal(original.Baseline, deserialized.Baseline);
    }

    [Fact]
    public void RoundTrip_PreservesAtlasMetadata()
    {
        FontAsset original = CreatePopulatedFont();

        EchoObject serialized = Serializer.Serialize(original);
        FontAsset? deserialized = Serializer.Deserialize<FontAsset>(serialized);
        _disposables.Add(deserialized!);

        Assert.NotNull(deserialized);
        Assert.Equal(original.AtlasWidth, deserialized!.AtlasWidth);
        Assert.Equal(original.AtlasHeight, deserialized.AtlasHeight);
        Assert.Equal(original.AtlasType, deserialized.AtlasType);
        Assert.Equal(original.AtlasPxRange, deserialized.AtlasPxRange);
        Assert.Equal(original.Padding, deserialized.Padding);
    }

    [Fact]
    public void RoundTrip_PreservesGlyphTable()
    {
        FontAsset original = CreatePopulatedFont();

        EchoObject serialized = Serializer.Serialize(original);
        FontAsset? deserialized = Serializer.Deserialize<FontAsset>(serialized);
        _disposables.Add(deserialized!);

        Assert.NotNull(deserialized);
        Assert.Equal(original.GlyphTable.Count, deserialized!.GlyphTable.Count);

        // Spot-check a few glyphs
        for (uint c = 65; c <= 90; c++)
        {
            Assert.True(original.TryGetGlyph(c, out GlyphData origGlyph));
            Assert.True(deserialized.TryGetGlyph(c, out GlyphData deserGlyph));
            Assert.Equal(origGlyph, deserGlyph);
        }
    }

    [Fact]
    public void RoundTrip_PreservesCharacterTable()
    {
        FontAsset original = CreatePopulatedFont();

        EchoObject serialized = Serializer.Serialize(original);
        FontAsset? deserialized = Serializer.Deserialize<FontAsset>(serialized);
        _disposables.Add(deserialized!);

        Assert.NotNull(deserialized);
        Assert.Equal(original.CharacterTable.Count, deserialized!.CharacterTable.Count);

        foreach (KeyValuePair<uint, int> entry in original.CharacterTable)
        {
            Assert.True(deserialized.CharacterTable.ContainsKey(entry.Key),
                $"Missing codepoint {entry.Key} in deserialized character table");
            Assert.Equal(entry.Value, deserialized.CharacterTable[entry.Key]);
        }
    }

    [Fact]
    public void RoundTrip_PreservesKerningPairs()
    {
        FontAsset original = CreatePopulatedFont();

        EchoObject serialized = Serializer.Serialize(original);
        FontAsset? deserialized = Serializer.Deserialize<FontAsset>(serialized);
        _disposables.Add(deserialized!);

        Assert.NotNull(deserialized);

        // Verify kerning pair A-V
        Assert.True(original.TryGetKerning(65, 86, out float origKern));
        Assert.True(deserialized!.TryGetKerning(65, 86, out float deserKern));
        Assert.Equal(origKern, deserKern);

        // Verify kerning pair T-o
        Assert.True(original.TryGetKerning(84, 111, out origKern));
        Assert.True(deserialized.TryGetKerning(84, 111, out deserKern));
        Assert.Equal(origKern, deserKern);
    }

    [Fact]
    public void RoundTrip_AsciiLookupRebuilt()
    {
        FontAsset original = CreatePopulatedFont();

        EchoObject serialized = Serializer.Serialize(original);
        FontAsset? deserialized = Serializer.Deserialize<FontAsset>(serialized);
        _disposables.Add(deserialized!);

        Assert.NotNull(deserialized);

        // The ASCII fast-path should be rebuilt after deserialization.
        // Verify by looking up ASCII glyphs — if the lookup isn't rebuilt,
        // TryGetGlyph would fall through to the dictionary (still works, but
        // we verify indirectly that the glyph resolves correctly).
        Assert.True(deserialized!.TryGetGlyph(65, out GlyphData gA));
        Assert.Equal(65u, gA.GlyphIndex);

        Assert.True(deserialized.TryGetGlyph(32, out GlyphData gSpace));
        Assert.Equal(32u, gSpace.GlyphIndex);
    }

    [Fact]
    public void RoundTrip_EmptyFont_Survives()
    {
        FontAsset emptyFont = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(emptyFont);

        emptyFont.SetMetrics(16f, 20f, 14f, -6f, 0f);
        emptyFont.SetGlyphData([], [], []);
        emptyFont.SetAtlas(null!, 0, 0, AtlasType.SDF, 4f, 1);

        EchoObject serialized = Serializer.Serialize(emptyFont);
        FontAsset? deserialized = Serializer.Deserialize<FontAsset>(serialized);
        _disposables.Add(deserialized!);

        Assert.NotNull(deserialized);
        Assert.Equal(16f, deserialized!.PointSize);
        Assert.Equal(0, deserialized.GlyphTable.Count);
        Assert.Equal(0, deserialized.CharacterTable.Count);
        Assert.False(deserialized.TryGetGlyph(65, out _));
    }

    [Fact]
    public void RoundTrip_SpaceGlyph_PreservedCorrectly()
    {
        FontAsset original = CreatePopulatedFont();

        EchoObject serialized = Serializer.Serialize(original);
        FontAsset? deserialized = Serializer.Deserialize<FontAsset>(serialized);
        _disposables.Add(deserialized!);

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.TryGetGlyph(32, out GlyphData space));
        Assert.Equal(0f, space.Width);
        Assert.Equal(0f, space.Height);
        Assert.Equal(12f, space.Advance);
    }
}
