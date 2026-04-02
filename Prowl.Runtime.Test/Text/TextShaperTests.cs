// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Prowl.Runtime.Resources;
using Prowl.Runtime.Text;
using Prowl.Vector;

using Xunit;

namespace Prowl.Runtime.Test;

public class TextShaperTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable d in _disposables)
            d.Dispose();
        _disposables.Clear();
    }

    private FontAsset CreateTestFont(float pointSize = 32f, float lineHeight = 40f, float ascender = 30f, float descender = -10f)
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(font);

        // Create simple ASCII glyph data
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

        // Space glyph has no visible area
        glyphs[32] = new GlyphData(32, 0, 0, 0, 0, 14f, 0, 0, 0, 0, 1f);

        font.SetMetrics(pointSize, lineHeight, ascender, descender, 0f);
        font.SetGlyphData(glyphs, charTable, []);
        font.SetAtlas(null!, 320, 192, AtlasType.MSDF, 4f, 1);

        return font;
    }

    [Fact]
    public void Shape_EmptyString_ReturnsEmptyLayout()
    {
        FontAsset font = CreateTestFont();

        TextLayout layout = TextShaper.Shape(
            "", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        Assert.NotNull(layout.Glyphs);
        Assert.Empty(layout.Glyphs);
        Assert.NotNull(layout.Lines);
        Assert.Empty(layout.Lines);
    }

    [Fact]
    public void Shape_NullFont_ReturnsEmptyLayout()
    {
        TextLayout layout = TextShaper.Shape(
            "Hello", null!, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        Assert.Empty(layout.Glyphs);
    }

    [Fact]
    public void Shape_SingleLine_ProducesCorrectGlyphCount()
    {
        FontAsset font = CreateTestFont();

        TextLayout layout = TextShaper.Shape(
            "Hello", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        Assert.Equal(5, layout.Glyphs.Length);
        Assert.Single(layout.Lines);
    }

    [Fact]
    public void Shape_NewlineProducesTwoLines()
    {
        FontAsset font = CreateTestFont();

        TextLayout layout = TextShaper.Shape(
            "Hi\nBye", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        Assert.Equal(5, layout.Glyphs.Length); // H, i, B, y, e
        Assert.Equal(2, layout.Lines.Length);
    }

    [Fact]
    public void Shape_CenterAlignment_ShiftsGlyphs()
    {
        FontAsset font = CreateTestFont();

        TextLayout leftLayout = TextShaper.Shape(
            "AB", font, 32f, 100f,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        TextLayout centerLayout = TextShaper.Shape(
            "AB", font, 32f, 100f,
            TextAlignment.Center, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        // Center-aligned glyphs should be shifted right compared to left-aligned
        Assert.True(centerLayout.Glyphs[0].Position.X > leftLayout.Glyphs[0].Position.X);
    }

    [Fact]
    public void Shape_RightAlignment_ShiftsGlyphs()
    {
        FontAsset font = CreateTestFont();

        TextLayout leftLayout = TextShaper.Shape(
            "AB", font, 32f, 100f,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        TextLayout rightLayout = TextShaper.Shape(
            "AB", font, 32f, 100f,
            TextAlignment.Right, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        Assert.True(rightLayout.Glyphs[0].Position.X > leftLayout.Glyphs[0].Position.X);
    }

    [Fact]
    public void Shape_WordWrap_BreaksAtWordBoundary()
    {
        FontAsset font = CreateTestFont();

        // Each character advance = 14f * (32/32) = 14f
        // "Hello World" = 11 chars (including space), total advance ~154
        // maxWidth = 80 should force a wrap
        TextLayout layout = TextShaper.Shape(
            "Hello World", font, 32f, 80f,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.WordWrap, Color.White);

        Assert.True(layout.Lines.Length >= 2, "Expected at least 2 lines with word wrap.");
    }

    [Fact]
    public void Shape_CharacterWrap_BreaksAtCharacter()
    {
        FontAsset font = CreateTestFont();

        TextLayout layout = TextShaper.Shape(
            "ABCDEFGHIJ", font, 32f, 50f,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.CharacterWrap, Color.White);

        Assert.True(layout.Lines.Length >= 2, "Expected at least 2 lines with character wrap.");
    }

    [Fact]
    public void Shape_Ellipsis_TruncatesText()
    {
        FontAsset font = CreateTestFont();

        TextLayout layout = TextShaper.Shape(
            "This is a very long string", font, 32f, 80f,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Ellipsis, Color.White);

        // Should be truncated — fewer glyphs than input characters (excluding spaces)
        Assert.True(layout.Glyphs.Length < 26, "Ellipsis should truncate the text.");
        Assert.Single(layout.Lines);
    }

    [Fact]
    public void Shape_ColorPreserved()
    {
        FontAsset font = CreateTestFont();
        Color expected = new(1f, 0f, 0f, 1f);

        TextLayout layout = TextShaper.Shape(
            "A", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, expected);

        Assert.Single(layout.Glyphs);
        Assert.Equal(expected, layout.Glyphs[0].Color);
    }

    [Fact]
    public void Shape_CharacterSpacing_IncreasesWidth()
    {
        FontAsset font = CreateTestFont();

        TextLayout normalLayout = TextShaper.Shape(
            "ABC", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White,
            characterSpacing: 0f);

        TextLayout spacedLayout = TextShaper.Shape(
            "ABC", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White,
            characterSpacing: 5f);

        Assert.True(spacedLayout.TextBounds.X > normalLayout.TextBounds.X);
    }

    [Fact]
    public void Shape_StyleRuns_ApplyColorOverride()
    {
        FontAsset font = CreateTestFont();
        Color overrideColor = new(0f, 1f, 0f, 1f);

        StyleRun[] runs = [
            new StyleRun { StartIndex = 0, Length = 2, ColorOverride = overrideColor }
        ];

        TextLayout layout = TextShaper.Shape(
            "ABCD", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White,
            styleRuns: runs);

        // First two glyphs should have the override color
        Assert.Equal(overrideColor, layout.Glyphs[0].Color);
        Assert.Equal(overrideColor, layout.Glyphs[1].Color);
        // Remaining glyphs should have default color
        Assert.Equal(Color.White, layout.Glyphs[2].Color);
    }

    [Fact]
    public void Shape_VerticalMiddle_OffsetsGlyphs()
    {
        FontAsset font = CreateTestFont();

        TextLayout topLayout = TextShaper.Shape(
            "A", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        TextLayout midLayout = TextShaper.Shape(
            "A", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Middle,
            TextOverflowMode.Overflow, Color.White);

        Assert.True(midLayout.Glyphs[0].Position.Y > topLayout.Glyphs[0].Position.Y);
    }
}
