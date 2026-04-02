// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Text;
using Prowl.Vector;

using Xunit;

namespace Prowl.Runtime.Test;

public class RichTextParserTests
{
    [Fact]
    public void Parse_EmptyString_ReturnsEmptyStrippedText()
    {
        ParsedText result = RichTextParser.Parse("");

        Assert.Equal(string.Empty, result.StrippedText);
        Assert.NotNull(result.Runs);
    }

    [Fact]
    public void Parse_NullString_ReturnsEmptyStrippedText()
    {
        ParsedText result = RichTextParser.Parse(null!);

        Assert.Equal(string.Empty, result.StrippedText);
    }

    [Fact]
    public void Parse_PlainText_ReturnsSameText()
    {
        ParsedText result = RichTextParser.Parse("Hello World");

        Assert.Equal("Hello World", result.StrippedText);
        Assert.NotEmpty(result.Runs);
        Assert.Equal(0, result.Runs[0].StartIndex);
        Assert.Equal(11, result.Runs[0].Length);
    }

    [Fact]
    public void Parse_BoldTag_SetsBoldFlag()
    {
        ParsedText result = RichTextParser.Parse("normal<b>bold</b>after");

        Assert.Equal("normalboldafter", result.StrippedText);

        // Find the run that covers "bold"
        StyleRun? boldRun = FindRunCovering(result, 6); // "bold" starts at index 6
        Assert.NotNull(boldRun);
        Assert.True((boldRun.Value.StyleFlags & TextStyleFlags.Bold) != 0);
    }

    [Fact]
    public void Parse_ItalicTag_SetsItalicFlag()
    {
        ParsedText result = RichTextParser.Parse("<i>italic</i>");

        Assert.Equal("italic", result.StrippedText);

        StyleRun? italicRun = FindRunCovering(result, 0);
        Assert.NotNull(italicRun);
        Assert.True((italicRun.Value.StyleFlags & TextStyleFlags.Italic) != 0);
    }

    [Fact]
    public void Parse_UnderlineTag_SetsUnderlineFlag()
    {
        ParsedText result = RichTextParser.Parse("<u>underlined</u>");

        Assert.Equal("underlined", result.StrippedText);

        StyleRun? underlineRun = FindRunCovering(result, 0);
        Assert.NotNull(underlineRun);
        Assert.True((underlineRun.Value.StyleFlags & TextStyleFlags.Underline) != 0);
    }

    [Fact]
    public void Parse_StrikethroughTag_SetsStrikethroughFlag()
    {
        ParsedText result = RichTextParser.Parse("<s>struck</s>");

        Assert.Equal("struck", result.StrippedText);

        StyleRun? sRun = FindRunCovering(result, 0);
        Assert.NotNull(sRun);
        Assert.True((sRun.Value.StyleFlags & TextStyleFlags.Strikethrough) != 0);
    }

    [Fact]
    public void Parse_SuperscriptTag_SetsSuperscriptFlag()
    {
        ParsedText result = RichTextParser.Parse("x<sup>2</sup>");

        Assert.Equal("x2", result.StrippedText);

        StyleRun? supRun = FindRunCovering(result, 1);
        Assert.NotNull(supRun);
        Assert.True((supRun.Value.StyleFlags & TextStyleFlags.Superscript) != 0);
    }

    [Fact]
    public void Parse_SubscriptTag_SetsSubscriptFlag()
    {
        ParsedText result = RichTextParser.Parse("H<sub>2</sub>O");

        Assert.Equal("H2O", result.StrippedText);

        StyleRun? subRun = FindRunCovering(result, 1);
        Assert.NotNull(subRun);
        Assert.True((subRun.Value.StyleFlags & TextStyleFlags.Subscript) != 0);
    }

    [Fact]
    public void Parse_ColorHex_SetsColorOverride()
    {
        ParsedText result = RichTextParser.Parse("<color=#FF0000>red</color>");

        Assert.Equal("red", result.StrippedText);

        StyleRun? colorRun = FindRunCovering(result, 0);
        Assert.NotNull(colorRun);
        Assert.True(colorRun.Value.ColorOverride.HasValue);
        Assert.Equal(1f, colorRun.Value.ColorOverride.Value.R, 0.01);
        Assert.Equal(0f, colorRun.Value.ColorOverride.Value.G, 0.01);
        Assert.Equal(0f, colorRun.Value.ColorOverride.Value.B, 0.01);
    }

    [Fact]
    public void Parse_ColorName_SetsColorOverride()
    {
        ParsedText result = RichTextParser.Parse("<color=blue>blue text</color>");

        Assert.Equal("blue text", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True(run.Value.ColorOverride.HasValue);
        Assert.Equal(0f, run.Value.ColorOverride.Value.R, 0.01);
        Assert.Equal(0f, run.Value.ColorOverride.Value.G, 0.01);
        Assert.Equal(1f, run.Value.ColorOverride.Value.B, 0.01);
    }

    [Fact]
    public void Parse_SizeTag_SetsSizeOverride()
    {
        ParsedText result = RichTextParser.Parse("<size=24>big</size>");

        Assert.Equal("big", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True(run.Value.SizeOverride.HasValue);
        Assert.Equal(24f, run.Value.SizeOverride.Value, 0.001);
    }

    [Fact]
    public void Parse_AlphaTag_SetsAlphaOverride()
    {
        ParsedText result = RichTextParser.Parse("<alpha=#80>faded</alpha>");

        Assert.Equal("faded", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True(run.Value.AlphaOverride.HasValue);
        Assert.InRange(run.Value.AlphaOverride.Value, 0.49f, 0.51f); // 0x80 / 255 ≈ 0.502
    }

    [Fact]
    public void Parse_LinkTag_SetsLinkId()
    {
        ParsedText result = RichTextParser.Parse("<link=\"mylink\">click here</link>");

        Assert.Equal("click here", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.Equal("mylink", run.Value.LinkId);
    }

    [Fact]
    public void Parse_CspaceTag_SetsCharacterSpacingOverride()
    {
        ParsedText result = RichTextParser.Parse("<cspace=2.5>spaced</cspace>");

        Assert.Equal("spaced", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True(run.Value.CharacterSpacingOverride.HasValue);
        Assert.Equal(2.5f, run.Value.CharacterSpacingOverride.Value, 0.001);
    }

    [Fact]
    public void Parse_MspaceTag_SetsMonoSpaceOverride()
    {
        ParsedText result = RichTextParser.Parse("<mspace=12>mono</mspace>");

        Assert.Equal("mono", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True(run.Value.MonoSpaceOverride.HasValue);
        Assert.Equal(12f, run.Value.MonoSpaceOverride.Value, 0.001);
    }

    [Fact]
    public void Parse_MarkTag_SetsMarkColor()
    {
        ParsedText result = RichTextParser.Parse("<mark=#FFFF00AA>highlight</mark>");

        Assert.Equal("highlight", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True(run.Value.MarkColor.HasValue);
    }

    [Fact]
    public void Parse_SpriteTag_SetsSpriteIndex()
    {
        ParsedText result = RichTextParser.Parse("text<sprite=5>more");

        // Sprite tag is consumed; text before and after should be preserved
        Assert.Contains("text", result.StrippedText);
    }

    [Fact]
    public void Parse_BrTag_InsertsNewline()
    {
        ParsedText result = RichTextParser.Parse("line1<br>line2");

        Assert.Equal("line1\nline2", result.StrippedText);
    }

    [Fact]
    public void Parse_NoparseTag_RendersTagsAsLiteral()
    {
        ParsedText result = RichTextParser.Parse("<noparse><b>not bold</b></noparse>");

        Assert.Equal("<b>not bold</b>", result.StrippedText);
    }

    [Fact]
    public void Parse_NestedTags_MaintainsStack()
    {
        ParsedText result = RichTextParser.Parse("<b><i>bold italic</i> just bold</b>");

        Assert.Equal("bold italic just bold", result.StrippedText);

        // "bold italic" should have both bold + italic
        StyleRun? biRun = FindRunCovering(result, 0);
        Assert.NotNull(biRun);
        Assert.True((biRun.Value.StyleFlags & TextStyleFlags.Bold) != 0);
        Assert.True((biRun.Value.StyleFlags & TextStyleFlags.Italic) != 0);
    }

    [Fact]
    public void Parse_MalformedTag_RenderedAsLiteral()
    {
        ParsedText result = RichTextParser.Parse("before<unknown>after");

        // Unrecognized tags are rendered as literal text
        Assert.Contains("before", result.StrippedText);
        Assert.Contains("after", result.StrippedText);
        Assert.Contains("<unknown>", result.StrippedText);
    }

    [Fact]
    public void Parse_UnterminatedTag_RenderedAsLiteral()
    {
        ParsedText result = RichTextParser.Parse("hello<b world");

        // No closing '>' found — '<' rendered as literal
        Assert.Contains("hello", result.StrippedText);
        Assert.Contains("<", result.StrippedText);
    }

    [Fact]
    public void Parse_FontTag_SetsFontName()
    {
        ParsedText result = RichTextParser.Parse("<font=\"AltFont\">text</font>");

        Assert.Equal("text", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.Equal("AltFont", run.Value.FontName);
    }

    [Fact]
    public void Parse_AlignTag_SetsAlignmentOverride()
    {
        ParsedText result = RichTextParser.Parse("<align=center>centered</align>");

        Assert.Equal("centered", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True(run.Value.AlignmentOverride.HasValue);
        Assert.Equal(TextAlignment.Center, run.Value.AlignmentOverride.Value);
    }

    [Fact]
    public void Parse_IndentTag_SetsIndentOverride()
    {
        ParsedText result = RichTextParser.Parse("<indent=20>indented</indent>");

        Assert.Equal("indented", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True(run.Value.IndentOverride.HasValue);
        Assert.Equal(20f, run.Value.IndentOverride.Value, 0.001);
    }

    [Fact]
    public void Parse_LineHeightTag_SetsLineHeightOverride()
    {
        ParsedText result = RichTextParser.Parse("<line-height=120>tall</line-height>");

        Assert.Equal("tall", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True(run.Value.LineHeightOverride.HasValue);
        Assert.Equal(120f, run.Value.LineHeightOverride.Value, 0.001);
    }

    [Fact]
    public void Parse_ColorHexShort_ParsesCorrectly()
    {
        ParsedText result = RichTextParser.Parse("<color=#F00>red</color>");

        Assert.Equal("red", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True(run.Value.ColorOverride.HasValue);
        Assert.Equal(1f, run.Value.ColorOverride.Value.R, 0.01);
    }

    [Fact]
    public void Parse_ColorHexWithAlpha_ParsesCorrectly()
    {
        ParsedText result = RichTextParser.Parse("<color=#FF000080>half-red</color>");

        Assert.Equal("half-red", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True(run.Value.ColorOverride.HasValue);
        Assert.InRange(run.Value.ColorOverride.Value.A, 0.49f, 0.52f); // 0x80 / 255 ≈ 0.502
    }

    // ── Effect Tags ────────────────────────────────────────

    [Fact]
    public void Parse_WaveTag_SetsWaveFlag()
    {
        ParsedText result = RichTextParser.Parse("normal<wave>wavy</wave>after");

        Assert.Equal("normalwavyafter", result.StrippedText);

        StyleRun? wavyRun = FindRunCovering(result, 6); // "wavy" starts at 6
        Assert.NotNull(wavyRun);
        Assert.True((wavyRun.Value.StyleFlags & TextStyleFlags.Wave) != 0);
    }

    [Fact]
    public void Parse_WaveTagWithParams_SetsAmplitudeAndFrequency()
    {
        ParsedText result = RichTextParser.Parse("<wave a=5 f=3>text</wave>");

        Assert.Equal("text", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True((run.Value.StyleFlags & TextStyleFlags.Wave) != 0);
        Assert.True(run.Value.WaveAmplitude.HasValue);
        Assert.Equal(5f, run.Value.WaveAmplitude.Value, 0.001);
        Assert.True(run.Value.WaveFrequency.HasValue);
        Assert.Equal(3f, run.Value.WaveFrequency.Value, 0.001);
    }

    [Fact]
    public void Parse_ShakeTag_SetsShakeFlag()
    {
        ParsedText result = RichTextParser.Parse("<shake>shaky</shake>");

        Assert.Equal("shaky", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True((run.Value.StyleFlags & TextStyleFlags.Shake) != 0);
    }

    [Fact]
    public void Parse_ShakeTagWithParams_SetsIntensity()
    {
        ParsedText result = RichTextParser.Parse("<shake a=2.5>shaky</shake>");

        Assert.Equal("shaky", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True((run.Value.StyleFlags & TextStyleFlags.Shake) != 0);
        Assert.True(run.Value.ShakeIntensity.HasValue);
        Assert.Equal(2.5f, run.Value.ShakeIntensity.Value, 0.001);
    }

    [Fact]
    public void Parse_FadeTag_SetsFadeFlag()
    {
        ParsedText result = RichTextParser.Parse("<fade>fading</fade>");

        Assert.Equal("fading", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True((run.Value.StyleFlags & TextStyleFlags.Fade) != 0);
    }

    [Fact]
    public void Parse_TypewriterTag_SetsTypewriterFlag()
    {
        ParsedText result = RichTextParser.Parse("<typewriter>reveal</typewriter>");

        Assert.Equal("reveal", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True((run.Value.StyleFlags & TextStyleFlags.Typewriter) != 0);
    }

    [Fact]
    public void Parse_RainbowTag_SetsRainbowFlag()
    {
        ParsedText result = RichTextParser.Parse("<rainbow>colorful</rainbow>");

        Assert.Equal("colorful", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True((run.Value.StyleFlags & TextStyleFlags.Rainbow) != 0);
    }

    [Fact]
    public void Parse_ScaleTag_SetsScaleEffectFlag()
    {
        ParsedText result = RichTextParser.Parse("<scale>pulsing</scale>");

        Assert.Equal("pulsing", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True((run.Value.StyleFlags & TextStyleFlags.ScaleEffect) != 0);
    }

    [Fact]
    public void Parse_RotateTag_SetsRotateEffectFlag()
    {
        ParsedText result = RichTextParser.Parse("<rotate>spinning</rotate>");

        Assert.Equal("spinning", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True((run.Value.StyleFlags & TextStyleFlags.RotateEffect) != 0);
    }

    [Fact]
    public void Parse_NestedEffectAndStyle_BothFlagsSet()
    {
        ParsedText result = RichTextParser.Parse("<b><wave>bold wavy</wave></b>");

        Assert.Equal("bold wavy", result.StrippedText);

        StyleRun? run = FindRunCovering(result, 0);
        Assert.NotNull(run);
        Assert.True((run.Value.StyleFlags & TextStyleFlags.Bold) != 0);
        Assert.True((run.Value.StyleFlags & TextStyleFlags.Wave) != 0);
    }

    [Fact]
    public void Parse_EffectTagClosing_ResetsFlag()
    {
        ParsedText result = RichTextParser.Parse("<wave>wavy</wave>normal");

        Assert.Equal("wavynormal", result.StrippedText);

        StyleRun? afterRun = FindRunCovering(result, 4); // "normal" starts at 4
        Assert.NotNull(afterRun);
        Assert.True((afterRun.Value.StyleFlags & TextStyleFlags.Wave) == 0);
    }

    // ── Helpers ──────────────────────────────────────────────

    private static StyleRun? FindRunCovering(ParsedText parsed, int charIndex)
    {
        foreach (StyleRun run in parsed.Runs)
        {
            if (charIndex >= run.StartIndex && charIndex < run.StartIndex + run.Length)
                return run;
        }
        return null;
    }
}
