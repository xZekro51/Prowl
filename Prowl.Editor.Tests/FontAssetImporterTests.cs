// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Importing;
using Prowl.Runtime.Text;

using Xunit;

namespace Prowl.Editor.Tests;

/// <summary>
/// Tests for <see cref="SdfGenerator"/> — the managed SDF/MSDF atlas generation algorithms.
/// </summary>
public class SdfGeneratorTests
{
    [Fact]
    public void GenerateSdfFromBitmap_AllBlack_ReturnsAllBelowMidpoint()
    {
        // A fully "outside" bitmap should produce values below 0.5 (128)
        int size = 16;
        byte[] bitmap = new byte[size * size]; // all 0 = outside
        byte[] sdf = SdfGenerator.GenerateSdfFromBitmap(bitmap, size, size, size, size, 4f);

        Assert.Equal(size * size, sdf.Length);
        foreach (byte val in sdf)
            Assert.True(val <= 128, $"Expected <=128 for outside pixel, got {val}");
    }

    [Fact]
    public void GenerateSdfFromBitmap_AllWhite_ReturnsAllAboveMidpoint()
    {
        // A fully "inside" bitmap should produce values above 0.5 (128)
        int size = 16;
        byte[] bitmap = new byte[size * size];
        Array.Fill(bitmap, (byte)255);
        byte[] sdf = SdfGenerator.GenerateSdfFromBitmap(bitmap, size, size, size, size, 4f);

        Assert.Equal(size * size, sdf.Length);
        foreach (byte val in sdf)
            Assert.True(val >= 128, $"Expected >=128 for inside pixel, got {val}");
    }

    [Fact]
    public void GenerateSdfFromBitmap_Circle_CenterIsMaxInsideBorderIsMidpoint()
    {
        // Create a circle bitmap — center should have high SDF value, edge ~128
        int size = 64;
        byte[] bitmap = new byte[size * size];
        float cx = size / 2f, cy = size / 2f, radius = 20f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy <= radius * radius)
                    bitmap[y * size + x] = 255;
            }
        }

        byte[] sdf = SdfGenerator.GenerateSdfFromBitmap(bitmap, size, size, size, size, 8f);

        // Center pixel should be well inside (high value)
        byte centerVal = sdf[(size / 2) * size + (size / 2)];
        Assert.True(centerVal > 180, $"Center value should be high inside, got {centerVal}");

        // Corner pixel (0,0) should be outside (low value)
        byte cornerVal = sdf[0];
        Assert.True(cornerVal < 80, $"Corner value should be low outside, got {cornerVal}");
    }

    [Fact]
    public void GenerateSdfFromBitmap_DifferentOutputSize_ProducesCorrectDimensions()
    {
        int bmpSize = 64;
        int sdfSize = 16;
        byte[] bitmap = new byte[bmpSize * bmpSize];

        byte[] sdf = SdfGenerator.GenerateSdfFromBitmap(bitmap, bmpSize, bmpSize, sdfSize, sdfSize, 4f);

        Assert.Equal(sdfSize * sdfSize, sdf.Length);
    }

    [Fact]
    public void GenerateMsdf_EmptyContours_ProducesCorrectSize()
    {
        List<SdfGenerator.Contour> contours = [];
        byte[] msdf = SdfGenerator.GenerateMsdf(contours, 16, 16, 4f, 1f, 0f, 0f);

        // 3 bytes per pixel (RGB)
        Assert.Equal(16 * 16 * 3, msdf.Length);
    }

    [Fact]
    public void GenerateMsdf_SquareContour_CenterIsInsideCornerIsOutside()
    {
        // Create a simple square contour
        SdfGenerator.Contour contour = new();
        contour.Edges.Add(new SdfGenerator.LinearSegment(2, 2, 14, 2));   // bottom
        contour.Edges.Add(new SdfGenerator.LinearSegment(14, 2, 14, 14)); // right
        contour.Edges.Add(new SdfGenerator.LinearSegment(14, 14, 2, 14)); // top
        contour.Edges.Add(new SdfGenerator.LinearSegment(2, 14, 2, 2));   // left

        List<SdfGenerator.Contour> contours = [contour];
        byte[] msdf = SdfGenerator.GenerateMsdf(contours, 16, 16, 4f, 1f, 0f, 0f);

        // The center pixel (8,8) should have high values (inside the square)
        int centerIdx = (8 * 16 + 8) * 3;
        float centerMedian = Median(msdf[centerIdx], msdf[centerIdx + 1], msdf[centerIdx + 2]);
        Assert.True(centerMedian > 128, $"Center median should be >128 (inside), got {centerMedian}");

        // The corner pixel (0,0) should have low values (outside the square)
        int cornerIdx = 0;
        float cornerMedian = Median(msdf[cornerIdx], msdf[cornerIdx + 1], msdf[cornerIdx + 2]);
        Assert.True(cornerMedian < 128, $"Corner median should be <128 (outside), got {cornerMedian}");
    }

    [Fact]
    public void LinearSegment_SignedDistance_ReturnsCorrectValues()
    {
        SdfGenerator.LinearSegment seg = new(0, 0, 10, 0);

        // Point on the line
        float distOnLine = seg.SignedDistance(5, 0, out float signOn);
        Assert.True(distOnLine < 0.01f, $"Distance should be ~0, got {distOnLine}");

        // Point above the line
        float distAbove = seg.SignedDistance(5, 3, out float signAbove);
        Assert.True(MathF.Abs(distAbove - 9f) < 0.01f, $"Squared distance should be 9, got {distAbove}");

        // Point below the line
        seg.SignedDistance(5, -3, out float signBelow);
        Assert.NotEqual(signAbove, signBelow); // Different sides → different signs
    }

    [Fact]
    public void QuadraticBezier_SignedDistance_PointOnCurveIsZero()
    {
        SdfGenerator.QuadraticBezierSegment seg = new(0, 0, 5, 10, 10, 0);

        // Start point should have ~0 distance
        float distStart = seg.SignedDistance(0, 0, out _);
        Assert.True(distStart < 0.1f, $"Distance at start should be ~0, got {distStart}");

        // End point should have ~0 distance
        float distEnd = seg.SignedDistance(10, 0, out _);
        Assert.True(distEnd < 0.1f, $"Distance at end should be ~0, got {distEnd}");
    }

    private static float Median(byte r, byte g, byte b)
    {
        return Math.Max(Math.Min(r, g), Math.Min(Math.Max(r, g), b));
    }
}

/// <summary>
/// Tests for <see cref="FontImportSettings"/>.
/// </summary>
public class FontImportSettingsTests
{
    [Fact]
    public void GetCodepoints_ASCII_ReturnsExpectedRange()
    {
        FontImportSettings settings = new() { CharacterSet = "ASCII" };
        HashSet<uint> codepoints = settings.GetCodepoints();

        // Printable ASCII: 32-126
        Assert.Equal(95, codepoints.Count);
        Assert.Contains(32u, codepoints);  // space
        Assert.Contains(65u, codepoints);  // 'A'
        Assert.Contains(126u, codepoints); // '~'
        Assert.DoesNotContain(0u, codepoints);
        Assert.DoesNotContain(127u, codepoints);
    }

    [Fact]
    public void GetCodepoints_LatinExtended_IncludesExtendedCharacters()
    {
        FontImportSettings settings = new() { CharacterSet = "LatinExtended" };
        HashSet<uint> codepoints = settings.GetCodepoints();

        Assert.Contains(65u, codepoints);    // 'A' (ASCII)
        Assert.Contains(0x00E9u, codepoints); // 'é' (Latin-1 Supplement)
        Assert.Contains(0x0100u, codepoints); // 'Ā' (Latin Extended-A)
        Assert.True(codepoints.Count > 95);
    }

    [Fact]
    public void GetCodepoints_Custom_ReturnsSpecifiedCharacters()
    {
        FontImportSettings settings = new()
        {
            CharacterSet = "Custom",
            CustomCharacters = "Hello"
        };
        HashSet<uint> codepoints = settings.GetCodepoints();

        Assert.Contains((uint)'H', codepoints);
        Assert.Contains((uint)'e', codepoints);
        Assert.Contains((uint)'l', codepoints);
        Assert.Contains((uint)'o', codepoints);
        Assert.Equal(4, codepoints.Count); // H, e, l, o (deduplicated)
    }

    [Fact]
    public void WriteTo_ReadBack_RoundTrips()
    {
        FontImportSettings original = new()
        {
            AtlasType = AtlasType.MSDF,
            PointSize = 64,
            AtlasResolution = 2048,
            PxRange = 8f,
            Padding = 4,
            CharacterSet = "LatinExtended",
            SdfOversample = 6,
        };

        Dictionary<string, object?> dict = [];
        original.WriteTo(dict);

        FontImportSettings loaded = FontImportSettings.FromMeta(dict);

        Assert.Equal(original.AtlasType, loaded.AtlasType);
        Assert.Equal(original.PointSize, loaded.PointSize);
        Assert.Equal(original.AtlasResolution, loaded.AtlasResolution);
        Assert.Equal(original.PxRange, loaded.PxRange);
        Assert.Equal(original.Padding, loaded.Padding);
        Assert.Equal(original.CharacterSet, loaded.CharacterSet);
        Assert.Equal(original.SdfOversample, loaded.SdfOversample);
    }

    [Fact]
    public void Clone_ProducesSeparateCopy()
    {
        FontImportSettings original = new()
        {
            AtlasType = AtlasType.SDF,
            PointSize = 32,
        };

        FontImportSettings clone = original.Clone();
        clone.PointSize = 64;

        Assert.Equal(32, original.PointSize);
        Assert.Equal(64, clone.PointSize);
    }
}

/// <summary>
/// Integration tests for <see cref="FontAssetImporter"/> using a real .ttf file.
/// These tests require the Inter-Regular.ttf file from the runtime defaults.
/// </summary>
public class FontAssetImporterTests
{
    private static string? FindInterFont()
    {
        // Search for Inter-Regular.ttf in the repo
        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Prowl.Runtime", "Assets", "Defaults", "Inter-Regular.ttf")),
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "Prowl.Runtime", "Assets", "Defaults", "Inter-Regular.ttf")),
        ];

        foreach (string path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    [Fact]
    public void IsFontFile_RecognizesFontExtensions()
    {
        Assert.True(FontAssetImporter.IsFontFile(".ttf"));
        Assert.True(FontAssetImporter.IsFontFile(".otf"));
        Assert.True(FontAssetImporter.IsFontFile(".TTF"));
        Assert.False(FontAssetImporter.IsFontFile(".png"));
        Assert.False(FontAssetImporter.IsFontFile(".txt"));
    }

    [Fact]
    public void Import_NonexistentFile_ReturnsNull()
    {
        var result = FontAssetImporter.Import(@"C:\nonexistent_font.ttf");
        Assert.Null(result);
    }

    [Fact]
    public void Import_InterRegular_SDF_ProducesValidFontAsset()
    {
        string? fontPath = FindInterFont();
        if (fontPath == null)
            return; // Skip: Inter-Regular.ttf not found in workspace

        FontImportSettings settings = new()
        {
            AtlasType = AtlasType.SDF,
            PointSize = 32,
            AtlasResolution = 512,
            PxRange = 4f,
            Padding = 2,
            CharacterSet = "ASCII",
            SdfOversample = 2,
        };

        var fontAsset = FontAssetImporter.Import(fontPath!, settings);

        Assert.NotNull(fontAsset);
        Assert.Equal("Inter-Regular", fontAsset!.Name);

        // Should have atlas texture
        Assert.NotNull(fontAsset.AtlasTexture);
        Assert.Equal(AtlasType.SDF, fontAsset.AtlasType);
        Assert.True(fontAsset.AtlasWidth > 0);
        Assert.True(fontAsset.AtlasHeight > 0);

        // Should have glyphs
        Assert.True(fontAsset.GlyphTable.Count > 50, $"Expected >50 glyphs for ASCII, got {fontAsset.GlyphTable.Count}");

        // Should be able to look up common characters
        Assert.True(fontAsset.TryGetGlyph((uint)'A', out var glyphA));
        Assert.True(glyphA.Width > 0);
        Assert.True(glyphA.Advance > 0);

        Assert.True(fontAsset.TryGetGlyph((uint)' ', out var glyphSpace));
        Assert.True(glyphSpace.Advance > 0);

        // Metrics should be set
        Assert.True(fontAsset.Ascender > 0);
        Assert.True(fontAsset.Descender < 0); // typically negative
        Assert.True(fontAsset.LineHeight > 0);
        Assert.Equal(32f, fontAsset.PointSize);

        // Clean up
        fontAsset.AtlasTexture?.Dispose();
        fontAsset.Dispose();
    }

    [Fact]
    public void Import_InterRegular_Bitmap_ProducesValidFontAsset()
    {
        string? fontPath = FindInterFont();
        if (fontPath == null)
            return; // Skip: Inter-Regular.ttf not found in workspace

        FontImportSettings settings = new()
        {
            AtlasType = AtlasType.Bitmap,
            PointSize = 24,
            AtlasResolution = 512,
            Padding = 1,
            CharacterSet = "ASCII",
        };

        var fontAsset = FontAssetImporter.Import(fontPath!, settings);

        Assert.NotNull(fontAsset);
        Assert.Equal(AtlasType.Bitmap, fontAsset!.AtlasType);
        Assert.True(fontAsset.GlyphTable.Count > 50);
        Assert.True(fontAsset.TryGetGlyph((uint)'Z', out _));

        fontAsset.AtlasTexture?.Dispose();
        fontAsset.Dispose();
    }
}
