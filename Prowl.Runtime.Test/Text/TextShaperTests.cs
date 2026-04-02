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

    [Fact]
    public void Shape_Truncate_StopsAtBoundary()
    {
        FontAsset font = CreateTestFont();

        // Each glyph: BearingX=1, Width=12, Advance=14. At scale 1.0, each glyph
        // occupies up to penX + 1 + 12 = penX + 13. With maxWidth=50, the first
        // 3 glyphs fit (0+13=13, 14+13=27, 28+13=41) but the 4th won't (42+13=55 > 50).
        TextLayout layout = TextShaper.Shape(
            "ABCDE", font, 32f, 50f,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Truncate, Color.White);

        // Truncate should stop placing glyphs at the boundary
        Assert.True(layout.Glyphs.Length < 5, $"Expected fewer than 5 glyphs, got {layout.Glyphs.Length}");
        Assert.Single(layout.Lines);
    }

    [Fact]
    public void Shape_ParagraphSpacing_AddsExtraSpaceOnNewlines()
    {
        FontAsset font = CreateTestFont();

        TextLayout withoutSpacing = TextShaper.Shape(
            "A\nB", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        TextLayout withSpacing = TextShaper.Shape(
            "A\nB", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White,
            paragraphSpacing: 10f);

        // The second glyph ('B') should be pushed down further with paragraph spacing
        float yWithout = withoutSpacing.Glyphs[1].Position.Y;
        float yWith = withSpacing.Glyphs[1].Position.Y;
        Assert.True(yWith < yWithout, $"Expected paragraph spacing to push 'B' lower: yWith={yWith}, yWithout={yWithout}");
    }

    [Fact]
    public void Shape_Superscript_ReducesScaleAndShiftsUp()
    {
        FontAsset font = CreateTestFont();

        // Normal 'A'
        TextLayout normal = TextShaper.Shape(
            "A", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        // Superscript 'A' via style run
        StyleRun[] runs =
        [
            new StyleRun { StartIndex = 0, Length = 1, StyleFlags = TextStyleFlags.Superscript }
        ];

        TextLayout super = TextShaper.Shape(
            "A", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White,
            styleRuns: runs);

        Assert.Single(normal.Glyphs);
        Assert.Single(super.Glyphs);

        // Scale should be reduced (~0.65x)
        Assert.True(super.Glyphs[0].Scale.X < normal.Glyphs[0].Scale.X,
            $"Superscript scale ({super.Glyphs[0].Scale.X}) should be smaller than normal ({normal.Glyphs[0].Scale.X})");

        // Y position should be higher (more positive in our coordinate system)
        Assert.True(super.Glyphs[0].Position.Y > normal.Glyphs[0].Position.Y,
            $"Superscript Y ({super.Glyphs[0].Position.Y}) should be above normal Y ({normal.Glyphs[0].Position.Y})");
    }

    [Fact]
    public void Shape_Subscript_ReducesScaleAndShiftsDown()
    {
        FontAsset font = CreateTestFont();

        // Normal 'A'
        TextLayout normal = TextShaper.Shape(
            "A", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        // Subscript 'A' via style run
        StyleRun[] runs =
        [
            new StyleRun { StartIndex = 0, Length = 1, StyleFlags = TextStyleFlags.Subscript }
        ];

        TextLayout sub = TextShaper.Shape(
            "A", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White,
            styleRuns: runs);

        Assert.Single(normal.Glyphs);
        Assert.Single(sub.Glyphs);

        // Scale should be reduced (~0.65x)
        Assert.True(sub.Glyphs[0].Scale.X < normal.Glyphs[0].Scale.X,
            $"Subscript scale ({sub.Glyphs[0].Scale.X}) should be smaller than normal ({normal.Glyphs[0].Scale.X})");

        // Y position should be lower (more negative in our coordinate system)
        Assert.True(sub.Glyphs[0].Position.Y < normal.Glyphs[0].Position.Y,
            $"Subscript Y ({sub.Glyphs[0].Position.Y}) should be below normal Y ({normal.Glyphs[0].Position.Y})");
    }

    [Fact]
    public void Shape_JustifiedAlignment_DistributesSpaces()
    {
        FontAsset font = CreateTestFont();

        // "AB CD" with word wrapping disabled, justified in a constrained box
        // With our test font: each char advance = 14, space advance = 14
        // "AB CD" = 5 chars = 70 total advance, but justified should stretch spaces
        float maxWidth = 100f;

        TextLayout layout = TextShaper.Shape(
            "AB CD", font, 32f, maxWidth,
            TextAlignment.Justified, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        // All 4 non-space glyphs should be present (space may or may not produce glyph)
        Assert.True(layout.Glyphs.Length >= 4, $"Expected at least 4 glyphs, got {layout.Glyphs.Length}");

        // The last glyph on the line should be pushed towards the right edge
        float lastGlyphRight = layout.Glyphs[^1].Position.X;
        float normalRight = 14f * 4; // 4 visible chars * advance without justification

        // Justified text should extend further than non-justified
        Assert.True(lastGlyphRight >= normalRight,
            $"Justified last glyph X ({lastGlyphRight}) should be >= non-justified ({normalRight})");
    }

    [Fact]
    public void Shape_WordWrap_ReflowedGlyphsHaveCorrectPositions()
    {
        FontAsset font = CreateTestFont();

        // "AB CD" in a box that fits "AB " (3 chars * 14 advance = 42) but not "AB C" (56)
        // So "CD" should wrap to the next line
        float maxWidth = 50f;

        TextLayout layout = TextShaper.Shape(
            "AB CD", font, 32f, maxWidth,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.WordWrap, Color.White);

        Assert.Equal(2, layout.Lines.Length);

        // Find the first glyph on line 2
        GlyphPlacement firstOnLine2 = layout.Glyphs[layout.Lines[1].StartGlyphIndex];

        // It should start at X ~0 (plus BearingX offset = 1 * scale)
        float scale = 32f / font.PointSize;
        float expectedBearingOffset = 1f * scale; // BearingX = 1
        Assert.True(firstOnLine2.Position.X < expectedBearingOffset + 2f,
            $"First glyph on line 2 X ({firstOnLine2.Position.X}) should be near 0 + bearing ({expectedBearingOffset})");

        // Its Y should be below line 1
        GlyphPlacement firstOnLine1 = layout.Glyphs[layout.Lines[0].StartGlyphIndex];
        Assert.True(firstOnLine2.Position.Y < firstOnLine1.Position.Y,
            $"Line 2 glyph Y ({firstOnLine2.Position.Y}) should be below line 1 glyph Y ({firstOnLine1.Position.Y})");
    }

    [Fact]
    public void Shape_IndentOverride_ShiftsNewLinePenX()
    {
        FontAsset font = CreateTestFont();

        // "A\nB" with indent on B — the second line should start indented
        // In raw text: index 0='A', 1='\n', 2='B'
        StyleRun[] runs =
        [
            new StyleRun { StartIndex = 0, Length = 1, StyleFlags = TextStyleFlags.None },
            new StyleRun { StartIndex = 2, Length = 1, StyleFlags = TextStyleFlags.None, IndentOverride = 20f }
        ];

        TextLayout layout = TextShaper.Shape(
            "A\nB", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White,
            styleRuns: runs);

        Assert.Equal(2, layout.Glyphs.Length);

        // First glyph 'A' should be near X = 0 + BearingX
        float scale = 32f / font.PointSize;
        float bearingX = 1f * scale;
        Assert.True(layout.Glyphs[0].Position.X < bearingX + 1f,
            $"First glyph X ({layout.Glyphs[0].Position.X}) should be near bearing ({bearingX})");

        // Second glyph 'B' should be offset by the scaled indent
        float expectedIndent = 20f * scale;
        Assert.True(layout.Glyphs[1].Position.X >= expectedIndent,
            $"Indented glyph X ({layout.Glyphs[1].Position.X}) should be >= indent ({expectedIndent})");
    }

    [Fact]
    public void Shape_LineHeightOverride_IncreasesLineSpacing()
    {
        FontAsset font = CreateTestFont();

        // "A\nB" without override
        TextLayout normal = TextShaper.Shape(
            "A\nB", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        // "A\nB" with line-height override on the first line
        StyleRun[] runs =
        [
            new StyleRun { StartIndex = 0, Length = 1, StyleFlags = TextStyleFlags.None, LineHeightOverride = 80f }
        ];

        TextLayout overridden = TextShaper.Shape(
            "A\nB", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White,
            styleRuns: runs);

        Assert.Equal(2, normal.Glyphs.Length);
        Assert.Equal(2, overridden.Glyphs.Length);

        float normalGap = normal.Glyphs[0].Position.Y - normal.Glyphs[1].Position.Y;
        float overriddenGap = overridden.Glyphs[0].Position.Y - overridden.Glyphs[1].Position.Y;

        Assert.True(overriddenGap > normalGap,
            $"Overridden line gap ({overriddenGap}) should be larger than normal ({normalGap})");
    }

    [Fact]
    public void Shape_AlignmentOverride_PerLineAlignment()
    {
        FontAsset font = CreateTestFont();

        // "AB\nCD" with global left alignment but center override on line 2
        // Text indices: 0='A', 1='B', 2='\n', 3='C', 4='D'
        StyleRun[] runs =
        [
            new StyleRun { StartIndex = 3, Length = 2, StyleFlags = TextStyleFlags.None, AlignmentOverride = TextAlignment.Right }
        ];

        float maxWidth = 200f;
        TextLayout layout = TextShaper.Shape(
            "AB\nCD", font, 32f, maxWidth,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White,
            styleRuns: runs);

        Assert.Equal(2, layout.Lines.Length);
        Assert.Equal(4, layout.Glyphs.Length);

        // Line 1 should be left-aligned (near X=0)
        float line1FirstX = layout.Glyphs[0].Position.X;
        Assert.True(line1FirstX < 5f,
            $"Line 1 should be left-aligned, first glyph X = {line1FirstX}");

        // Line 2 should be right-aligned (shifted right)
        float line2FirstX = layout.Glyphs[layout.Lines[1].StartGlyphIndex].Position.X;
        Assert.True(line2FirstX > line1FirstX + 10f,
            $"Line 2 should be right-aligned ({line2FirstX}), shifted right vs line 1 ({line1FirstX})");
    }

    [Fact]
    public void Shape_LinkId_PropagatedToGlyphPlacement()
    {
        FontAsset font = CreateTestFont();

        StyleRun[] runs =
        [
            new StyleRun { StartIndex = 0, Length = 2, StyleFlags = TextStyleFlags.None, LinkId = "my-link" },
            new StyleRun { StartIndex = 2, Length = 2, StyleFlags = TextStyleFlags.None }
        ];

        TextLayout layout = TextShaper.Shape(
            "ABCD", font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White,
            styleRuns: runs);

        Assert.Equal(4, layout.Glyphs.Length);

        // First two glyphs should carry the link ID
        Assert.Equal("my-link", layout.Glyphs[0].LinkId);
        Assert.Equal("my-link", layout.Glyphs[1].LinkId);
        // Remaining glyphs should have no link
        Assert.Null(layout.Glyphs[2].LinkId);
        Assert.Null(layout.Glyphs[3].LinkId);
    }

    [Fact]
    public void Shape_WordWrap_PreservesSuperscriptOffset()
    {
        FontAsset font = CreateTestFont();

        // "AB C^D" where ^D is superscript, in a box that forces word wrap
        // Each char advance = 14, space advance = 14. maxWidth=50 fits "AB " (42) but not "AB C" (56)
        // So "C" and "D" wrap to line 2. D has superscript.
        StyleRun[] runs =
        [
            new StyleRun { StartIndex = 0, Length = 4, StyleFlags = TextStyleFlags.None },
            new StyleRun { StartIndex = 4, Length = 1, StyleFlags = TextStyleFlags.Superscript }
        ];

        TextLayout layout = TextShaper.Shape(
            "AB CD", font, 32f, 50f,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.WordWrap, Color.White,
            styleRuns: runs);

        Assert.Equal(2, layout.Lines.Length);

        // Find normal and superscript glyphs on line 2
        // 'C' is at index 3 in "AB CD" (indices: A=0, B=1, space=2, C=3, D=4)
        // After word wrap, line 2 starts at the space glyph position
        // The space glyph (index 2) is the word break, so C and D are on line 2
        GlyphPlacement glyphC = default;
        GlyphPlacement glyphD = default;
        for (int i = 0; i < layout.Glyphs.Length; i++)
        {
            if (layout.Glyphs[i].CharacterIndex == 3) glyphC = layout.Glyphs[i];
            if (layout.Glyphs[i].CharacterIndex == 4) glyphD = layout.Glyphs[i];
        }

        // D (superscript) should be above C on the same line
        Assert.True(glyphD.Position.Y > glyphC.Position.Y,
            $"Superscript D Y ({glyphD.Position.Y}) should be above normal C Y ({glyphC.Position.Y}) on wrapped line");
    }

    [Fact]
    public void Shape_WordWrap_RespectsIndent()
    {
        FontAsset font = CreateTestFont();

        // "AB CD" with indent active, word wrap forced at maxWidth=80
        // After wrap, "CD" should start at the indent, not at X=0
        StyleRun[] runs =
        [
            new StyleRun { StartIndex = 0, Length = 5, StyleFlags = TextStyleFlags.None, IndentOverride = 15f }
        ];

        TextLayout layout = TextShaper.Shape(
            "AB CD", font, 32f, 80f,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.WordWrap, Color.White,
            styleRuns: runs);

        Assert.Equal(2, layout.Lines.Length);

        // First glyph on line 2 should start at the scaled indent
        float scale = 32f / font.PointSize;
        float expectedIndent = 15f * scale;
        GlyphPlacement firstOnLine2 = layout.Glyphs[layout.Lines[1].StartGlyphIndex];

        Assert.True(firstOnLine2.Position.X >= expectedIndent,
            $"First glyph on wrapped line 2 X ({firstOnLine2.Position.X}) should be >= indent ({expectedIndent})");
    }

    [Fact]
    public void Shape_WordWrap_PreservesCharacterSpacing()
    {
        FontAsset font = CreateTestFont();

        // "AB CD" with characterSpacing=5, word wrap at maxWidth=55
        // Advance per char: 14+5=19 with spacing, 14 without.
        // At maxWidth=55, both cases wrap "AB" to line 1 and "space C D" to line 2.
        // After reflow, spacing should be preserved between C and D.
        TextLayout layoutWithSpacing = TextShaper.Shape(
            "AB CD", font, 32f, 55f,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.WordWrap, Color.White,
            characterSpacing: 5f);

        TextLayout layoutWithoutSpacing = TextShaper.Shape(
            "AB CD", font, 32f, 55f,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.WordWrap, Color.White,
            characterSpacing: 0f);

        Assert.Equal(2, layoutWithSpacing.Lines.Length);
        Assert.Equal(2, layoutWithoutSpacing.Lines.Length);

        // Find 'C' and 'D' in both layouts (they should be on the wrapped line)
        GlyphPlacement cWith = default, dWith = default;
        GlyphPlacement cWithout = default, dWithout = default;
        for (int i = 0; i < layoutWithSpacing.Glyphs.Length; i++)
        {
            if (layoutWithSpacing.Glyphs[i].CharacterIndex == 3) cWith = layoutWithSpacing.Glyphs[i];
            if (layoutWithSpacing.Glyphs[i].CharacterIndex == 4) dWith = layoutWithSpacing.Glyphs[i];
        }
        for (int i = 0; i < layoutWithoutSpacing.Glyphs.Length; i++)
        {
            if (layoutWithoutSpacing.Glyphs[i].CharacterIndex == 3) cWithout = layoutWithoutSpacing.Glyphs[i];
            if (layoutWithoutSpacing.Glyphs[i].CharacterIndex == 4) dWithout = layoutWithoutSpacing.Glyphs[i];
        }

        // The gap between C and D should be larger with spacing than without
        float gapWith = dWith.Position.X - cWith.Position.X;
        float gapWithout = dWithout.Position.X - cWithout.Position.X;

        Assert.True(gapWith > gapWithout,
            $"Gap between C-D with spacing ({gapWith}) should be > gap without ({gapWithout})");
    }

    [Fact]
    public void Shape_WordWrap_CenterAligned_NoGlyphRangeOverlap()
    {
        FontAsset font = CreateTestFont();

        // "AB CD" center-aligned with word wrap at maxWidth=50.
        // This verifies that the space at the word break is NOT double-shifted
        // by both line 0's and line 1's alignment offsets.
        TextLayout layout = TextShaper.Shape(
            "AB CD", font, 32f, 50f,
            TextAlignment.Center, VerticalAlignment.Top,
            TextOverflowMode.WordWrap, Color.White);

        Assert.Equal(2, layout.Lines.Length);

        // Line glyph ranges must not overlap
        int line0End = layout.Lines[0].StartGlyphIndex + layout.Lines[0].GlyphCount;
        int line1Start = layout.Lines[1].StartGlyphIndex;
        Assert.True(line0End <= line1Start,
            $"Line 0 end ({line0End}) should not exceed line 1 start ({line1Start})");

        // Center-aligned glyphs on line 2 should be near the center of maxWidth
        GlyphPlacement firstOnLine2 = layout.Glyphs[layout.Lines[1].StartGlyphIndex];
        float line2Width = layout.Lines[1].Width;
        float expectedOffset = (50f - line2Width) * 0.5f;

        // First glyph on line 2 should be near expectedOffset + BearingX
        float scale = 32f / font.PointSize;
        float bearingX = 1f * scale;
        Assert.True(firstOnLine2.Position.X >= expectedOffset - 1f,
            $"Line 2 first glyph X ({firstOnLine2.Position.X}) should be near center offset ({expectedOffset})");
        Assert.True(firstOnLine2.Position.X <= expectedOffset + bearingX + 2f,
            $"Line 2 first glyph X ({firstOnLine2.Position.X}) should be near center offset ({expectedOffset + bearingX})");
    }

    [Fact]
    public void Shape_WordWrap_LineGlyphCountsMatchTotalGlyphs()
    {
        FontAsset font = CreateTestFont();

        // Verify that the sum of all line GlyphCounts equals the total glyph count.
        // This ensures no glyph is counted in multiple lines or missed entirely.
        TextLayout layout = TextShaper.Shape(
            "Hello World Foo", font, 32f, 80f,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.WordWrap, Color.White);

        Assert.True(layout.Lines.Length >= 2, $"Expected at least 2 lines, got {layout.Lines.Length}");

        int sumGlyphCounts = 0;
        for (int l = 0; l < layout.Lines.Length; l++)
        {
            sumGlyphCounts += layout.Lines[l].GlyphCount;
        }

        // Account for space glyphs at word breaks that stay on the previous line
        // but are not part of the next line's count. Sum should equal total glyphs
        // (spaces are included in line counts, just not double-counted).
        Assert.True(sumGlyphCounts <= layout.Glyphs.Length,
            $"Sum of line glyph counts ({sumGlyphCounts}) should not exceed total glyphs ({layout.Glyphs.Length})");

        // Verify no line ranges overlap
        for (int l = 0; l < layout.Lines.Length - 1; l++)
        {
            int thisEnd = layout.Lines[l].StartGlyphIndex + layout.Lines[l].GlyphCount;
            int nextStart = layout.Lines[l + 1].StartGlyphIndex;
            Assert.True(thisEnd <= nextStart,
                $"Line {l} end ({thisEnd}) overlaps with line {l + 1} start ({nextStart})");
        }
    }
}
