// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Services;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Text;

using Xunit;

namespace Prowl.Editor.Tests;

/// <summary>
/// Tests that <see cref="ScriptableObjectSerializer"/> correctly saves and loads
/// <see cref="ScriptableObject"/> subclasses — in particular <see cref="FontAsset"/>
/// which was previously serialized as an empty asset reference instead of inline data.
/// </summary>
public sealed class ScriptableObjectSerializerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<IDisposable> _disposables = [];

    public ScriptableObjectSerializerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ProwlSOSerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (IDisposable d in _disposables)
            d.Dispose();
        _disposables.Clear();

        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Save_FontAsset_WritesGlyphData()
    {
        FontAsset font = CreatePopulatedFont();
        string path = Path.Combine(_tempDir, "test.asset");

        ScriptableObjectSerializer.Save(font, path);

        string json = File.ReadAllText(path);

        // The file must contain actual glyph/metric data, not just an empty $assetId reference
        Assert.Contains("_pointSize", json);
        Assert.Contains("_lineHeight", json);
        Assert.Contains("_glyphTable", json);
    }

    [Fact]
    public void Save_FontAsset_DoesNotSerializeAsAssetReference()
    {
        FontAsset font = CreatePopulatedFont();
        string path = Path.Combine(_tempDir, "ref_check.asset");

        ScriptableObjectSerializer.Save(font, path);

        string json = File.ReadAllText(path);

        // The root data should NOT be just { "$assetId": "..." }
        // It should contain the serialized fields
        Assert.Contains("_ascender", json);
        Assert.Contains("_descender", json);
        Assert.Contains("_characterTable", json);
    }

    [Fact]
    public void SaveAndLoad_FontAsset_PreservesMetrics()
    {
        FontAsset original = CreatePopulatedFont();
        string path = Path.Combine(_tempDir, "metrics.asset");

        ScriptableObjectSerializer.Save(original, path);
        ScriptableObject? loaded = ScriptableObjectSerializer.Load(path);

        Assert.NotNull(loaded);
        Assert.IsType<FontAsset>(loaded);

        FontAsset result = (FontAsset)loaded!;
        _disposables.Add(result);

        Assert.Equal(original.PointSize, result.PointSize);
        Assert.Equal(original.LineHeight, result.LineHeight);
        Assert.Equal(original.Ascender, result.Ascender);
        Assert.Equal(original.Descender, result.Descender);
        Assert.Equal(original.Baseline, result.Baseline);
    }

    [Fact]
    public void SaveAndLoad_FontAsset_PreservesAtlasMetadata()
    {
        FontAsset original = CreatePopulatedFont();
        string path = Path.Combine(_tempDir, "atlas.asset");

        ScriptableObjectSerializer.Save(original, path);
        ScriptableObject? loaded = ScriptableObjectSerializer.Load(path);

        Assert.NotNull(loaded);
        FontAsset result = (FontAsset)loaded!;
        _disposables.Add(result);

        Assert.Equal(original.AtlasWidth, result.AtlasWidth);
        Assert.Equal(original.AtlasHeight, result.AtlasHeight);
        Assert.Equal(original.AtlasType, result.AtlasType);
        Assert.Equal(original.AtlasPxRange, result.AtlasPxRange);
        Assert.Equal(original.Padding, result.Padding);
    }

    [Fact]
    public void SaveAndLoad_FontAsset_PreservesGlyphTable()
    {
        FontAsset original = CreatePopulatedFont();
        string path = Path.Combine(_tempDir, "glyphs.asset");

        ScriptableObjectSerializer.Save(original, path);
        ScriptableObject? loaded = ScriptableObjectSerializer.Load(path);

        Assert.NotNull(loaded);
        FontAsset result = (FontAsset)loaded!;
        _disposables.Add(result);

        Assert.Equal(original.GlyphTable.Count, result.GlyphTable.Count);

        // Verify a specific glyph round-trips
        Assert.True(result.TryGetGlyph((uint)'A', out GlyphData glyphA));
        Assert.True(original.TryGetGlyph((uint)'A', out GlyphData origA));
        Assert.Equal(origA.GlyphIndex, glyphA.GlyphIndex);
        Assert.Equal(origA.Width, glyphA.Width);
        Assert.Equal(origA.Height, glyphA.Height);
        Assert.Equal(origA.Advance, glyphA.Advance);
        Assert.Equal(origA.AtlasX, glyphA.AtlasX);
        Assert.Equal(origA.AtlasY, glyphA.AtlasY);
    }

    [Fact]
    public void SaveAndLoad_FontAsset_PreservesKerning()
    {
        FontAsset original = CreatePopulatedFont();
        string path = Path.Combine(_tempDir, "kerning.asset");

        ScriptableObjectSerializer.Save(original, path);
        ScriptableObject? loaded = ScriptableObjectSerializer.Load(path);

        Assert.NotNull(loaded);
        FontAsset result = (FontAsset)loaded!;
        _disposables.Add(result);

        Assert.Equal(original.KerningPairs.Count, result.KerningPairs.Count);

        // Verify a specific kerning pair
        Assert.True(result.TryGetKerning(65, 86, out float kern));
        Assert.Equal(-1.5f, kern);
    }

    [Fact]
    public void SaveAndLoad_FontAsset_PreservesCharacterTable()
    {
        FontAsset original = CreatePopulatedFont();
        string path = Path.Combine(_tempDir, "chartable.asset");

        ScriptableObjectSerializer.Save(original, path);
        ScriptableObject? loaded = ScriptableObjectSerializer.Load(path);

        Assert.NotNull(loaded);
        FontAsset result = (FontAsset)loaded!;
        _disposables.Add(result);

        Assert.Equal(original.CharacterTable.Count, result.CharacterTable.Count);

        // All ASCII printable characters should be present
        for (uint c = 33; c < 128; c++)
        {
            Assert.True(result.TryGetGlyph(c, out _), $"Missing glyph for codepoint U+{c:X4} ('{(char)c}')");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────

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

        // Space glyph (no pixels)
        glyphs[32] = new GlyphData(32, 0, 0, 0, 0, 12f, 0, 0, 0, 0, 1f);

        Dictionary<ulong, float> kerning = new()
        {
            [((ulong)65 << 32) | 86] = -1.5f,   // A-V
            [((ulong)84 << 32) | 111] = -0.8f,   // T-o
            [((ulong)87 << 32) | 97] = -1.2f,    // W-a
        };

        font.SetMetrics(48f, 56f, 42f, -14f, 2f);
        font.SetGlyphData(glyphs, charTable, kerning);
        font.SetAtlas(null!, 512, 512, AtlasType.MSDF, 6f, 2);

        return font;
    }
}
