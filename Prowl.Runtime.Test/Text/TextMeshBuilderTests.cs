// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Prowl.Runtime.Resources;
using Prowl.Runtime.Text;
using Prowl.Vector;

using Xunit;

namespace Prowl.Runtime.Test;

public class TextMeshBuilderTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable d in _disposables)
            d.Dispose();
        _disposables.Clear();
    }

    private FontAsset CreateTestFont()
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

        glyphs[32] = new GlyphData(32, 0, 0, 0, 0, 14f, 0, 0, 0, 0, 1f);

        font.SetMetrics(32f, 40f, 30f, -10f, 0f);
        font.SetGlyphData(glyphs, charTable, []);
        font.SetAtlas(null!, 320, 192, AtlasType.MSDF, 4f, 1);

        return font;
    }

    private TextLayout ShapeText(FontAsset font, string text)
    {
        return TextShaper.Shape(
            text, font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);
    }

    [Fact]
    public void Build_EmptyLayout_ReturnsValidMesh()
    {
        FontAsset font = CreateTestFont();

        TextLayout layout = new()
        {
            Glyphs = [],
            Lines = [],
            TextBounds = Float2.Zero,
            PreferredSize = Float2.Zero
        };

        Mesh mesh = TextMeshBuilder.Build(layout, font);
        _disposables.Add(mesh);

        Assert.NotNull(mesh);
        Assert.True(mesh.VertexCount > 0, "Empty layout should still produce valid mesh data.");
    }

    [Fact]
    public void Build_SingleGlyph_ProducesQuad()
    {
        FontAsset font = CreateTestFont();
        TextLayout layout = ShapeText(font, "A");

        Mesh mesh = TextMeshBuilder.Build(layout, font);
        _disposables.Add(mesh);

        // One visible glyph = 4 verts, 6 indices
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(6, mesh.IndexCount);
    }

    [Fact]
    public void Build_MultipleGlyphs_CorrectVertexCount()
    {
        FontAsset font = CreateTestFont();
        TextLayout layout = ShapeText(font, "Hello");

        Mesh mesh = TextMeshBuilder.Build(layout, font);
        _disposables.Add(mesh);

        // 5 visible characters × 4 verts = 20
        // Space is not visible (0 width), and 'Hello' has no spaces
        Assert.Equal(5 * 4, mesh.VertexCount);
        Assert.Equal(5 * 6, mesh.IndexCount);
    }

    [Fact]
    public void Build_WithSpace_SkipsZeroSizeGlyphs()
    {
        FontAsset font = CreateTestFont();
        TextLayout layout = ShapeText(font, "A B");

        Mesh mesh = TextMeshBuilder.Build(layout, font);
        _disposables.Add(mesh);

        // "A B" = 3 glyphs, but space (0 width) is skipped → 2 visible quads
        Assert.Equal(2 * 4, mesh.VertexCount);
        Assert.Equal(2 * 6, mesh.IndexCount);
    }

    [Fact]
    public void Build_UVsAreNormalized()
    {
        FontAsset font = CreateTestFont();
        TextLayout layout = ShapeText(font, "A");

        Mesh mesh = TextMeshBuilder.Build(layout, font);
        _disposables.Add(mesh);

        Float2[] uvArray = mesh.UV;
        foreach (Float2 uv in uvArray)
        {
            Assert.InRange(uv.X, 0f, 1f);
            Assert.InRange(uv.Y, 0f, 1f);
        }
    }

    [Fact]
    public void Build_VertexColorsMatchGlyphColor()
    {
        FontAsset font = CreateTestFont();
        Color expected = new(1f, 0f, 0f, 1f);

        TextLayout layout = TextShaper.Shape(
            "A", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, expected);

        Mesh mesh = TextMeshBuilder.Build(layout, font);
        _disposables.Add(mesh);

        Color[] colorsArr = mesh.Colors;
        foreach (Color c in colorsArr)
        {
            Assert.Equal(expected.R, c.R, 0.001);
            Assert.Equal(expected.G, c.G, 0.001);
            Assert.Equal(expected.B, c.B, 0.001);
        }
    }

    [Fact]
    public void Build_ReusesMesh()
    {
        FontAsset font = CreateTestFont();
        TextLayout layout1 = ShapeText(font, "AB");
        TextLayout layout2 = ShapeText(font, "XYZ");

        Mesh mesh = TextMeshBuilder.Build(layout1, font);
        _disposables.Add(mesh);

        Mesh reused = TextMeshBuilder.Build(layout2, font, mesh);

        Assert.Same(mesh, reused);
        Assert.Equal(3 * 4, reused.VertexCount);
    }

    [Fact]
    public void Build_IndexFormatUInt16_ForSmallMesh()
    {
        FontAsset font = CreateTestFont();
        TextLayout layout = ShapeText(font, "A");

        Mesh mesh = TextMeshBuilder.Build(layout, font);
        _disposables.Add(mesh);

        Assert.Equal(IndexFormat.UInt16, mesh.IndexFormat);
    }

    [Fact]
    public void Build_UnderlineDecoration_AddsExtraGeometry()
    {
        FontAsset font = CreateTestFont();

        StyleRun[] runs = [
            new StyleRun { StartIndex = 0, Length = 3, StyleFlags = TextStyleFlags.Underline }
        ];

        TextLayout layout = TextShaper.Shape(
            "ABC", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White,
            styleRuns: runs);

        Mesh mesh = TextMeshBuilder.Build(layout, font);
        _disposables.Add(mesh);

        // 3 glyph quads (12 verts) + at least 1 underline quad (4 verts)
        Assert.True(mesh.VertexCount > 3 * 4, "Underline should add extra vertices.");
    }

    [Fact]
    public void Build_StrikethroughDecoration_AddsExtraGeometry()
    {
        FontAsset font = CreateTestFont();

        StyleRun[] runs = [
            new StyleRun { StartIndex = 0, Length = 3, StyleFlags = TextStyleFlags.Strikethrough }
        ];

        TextLayout layout = TextShaper.Shape(
            "ABC", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White,
            styleRuns: runs);

        Mesh mesh = TextMeshBuilder.Build(layout, font);
        _disposables.Add(mesh);

        Assert.True(mesh.VertexCount > 3 * 4, "Strikethrough should add extra vertices.");
    }

    [Fact]
    public void Build_MarkHighlight_AddsBackgroundGeometry()
    {
        FontAsset font = CreateTestFont();

        Color markColor = new(1f, 1f, 0f, 0.5f);
        StyleRun[] runs = [
            new StyleRun { StartIndex = 0, Length = 2, MarkColor = markColor }
        ];

        TextLayout layout = TextShaper.Shape(
            "AB", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White,
            styleRuns: runs);

        Mesh mesh = TextMeshBuilder.Build(layout, font);
        _disposables.Add(mesh);

        // 2 glyph quads (8 verts) + at least 1 mark quad (4 verts)
        Assert.True(mesh.VertexCount > 2 * 4, "Mark highlight should add extra vertices.");
    }
}
