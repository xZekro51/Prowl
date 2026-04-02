// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Runtime.Text;

/// <summary>
/// Performs text shaping and layout: maps codepoints to glyphs, applies kerning,
/// handles word/character wrapping, and produces a <see cref="TextLayout"/>.
/// </summary>
public static class TextShaper
{
    /// <summary>
    /// Shapes and lays out a text string using the given font and constraints.
    /// </summary>
    /// <param name="text">The text to shape (with rich text tags already stripped).</param>
    /// <param name="font">The primary font asset.</param>
    /// <param name="fontSize">Desired font size in world/screen units.</param>
    /// <param name="maxWidth">Maximum line width for wrapping. Use <see cref="float.MaxValue"/> for no limit.</param>
    /// <param name="alignment">Horizontal text alignment.</param>
    /// <param name="verticalAlignment">Vertical text alignment.</param>
    /// <param name="overflow">How text handles overflow.</param>
    /// <param name="color">Default text color.</param>
    /// <param name="characterSpacing">Extra spacing between characters.</param>
    /// <param name="lineSpacing">Extra spacing between lines.</param>
    /// <param name="wordSpacing">Extra spacing after word-break characters.</param>
    /// <param name="styleRuns">Optional rich text style runs to apply.</param>
    /// <returns>A <see cref="TextLayout"/> containing positioned glyphs and line info.</returns>
    public static TextLayout Shape(
        string text,
        FontAsset font,
        float fontSize,
        float maxWidth,
        TextAlignment alignment,
        VerticalAlignment verticalAlignment,
        TextOverflowMode overflow,
        Color color,
        float characterSpacing = 0f,
        float lineSpacing = 0f,
        float wordSpacing = 0f,
        StyleRun[]? styleRuns = null)
    {
        if (string.IsNullOrEmpty(text) || font.IsNotValid())
        {
            return new TextLayout
            {
                Glyphs = [],
                Lines = [],
                TextBounds = Float2.Zero,
                PreferredSize = Float2.Zero
            };
        }

        float scale = fontSize / font.PointSize;
        float scaledLineHeight = font.LineHeight * scale + lineSpacing;
        float scaledAscender = font.Ascender * scale;
        float scaledDescender = font.Descender * scale;

        List<GlyphPlacement> glyphs = new(text.Length);
        List<LineInfo> lines = [];

        // Track missing glyphs to deduplicate OnGlyphMissing events
        HashSet<(uint, int)>? missingSet = null;

        // Current pen position
        float penX = 0f;
        float penY = -scaledAscender; // Start from top, first baseline

        int lineStartGlyph = 0;
        int lastWordBreak = -1;
        float lineWidthAtWordBreak = 0f;
        int glyphCountAtWordBreak = 0;

        bool shouldWrap = overflow == TextOverflowMode.WordWrap ||
                          overflow == TextOverflowMode.CharacterWrap ||
                          overflow == TextOverflowMode.Ellipsis;

        uint prevGlyphIndex = 0;
        int styleRunIndex = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            // Handle newlines
            if (c == '\n')
            {
                FinishLine(lines, glyphs, lineStartGlyph, penX, scaledAscender, scaledDescender, penY);
                lineStartGlyph = glyphs.Count;
                penX = 0f;
                penY -= scaledLineHeight;
                prevGlyphIndex = 0;
                lastWordBreak = -1;
                continue;
            }

            // Skip carriage returns
            if (c == '\r')
                continue;

            // Find current style run
            Color charColor = color;
            TextStyleFlags flags = TextStyleFlags.None;
            float sizeOverride = fontSize;
            float charCharSpacing = characterSpacing;
            float charMonoSpace = -1f;
            Color? charMarkColor = null;

            if (styleRuns != null)
            {
                while (styleRunIndex < styleRuns.Length - 1 &&
                       i >= styleRuns[styleRunIndex].StartIndex + styleRuns[styleRunIndex].Length)
                {
                    styleRunIndex++;
                }

                if (styleRunIndex < styleRuns.Length)
                {
                    StyleRun run = styleRuns[styleRunIndex];
                    if (i >= run.StartIndex && i < run.StartIndex + run.Length)
                    {
                        if (run.ColorOverride.HasValue)
                            charColor = run.ColorOverride.Value;
                        if (run.AlphaOverride.HasValue)
                            charColor = new Color(charColor.R, charColor.G, charColor.B, run.AlphaOverride.Value);
                        flags = run.StyleFlags;
                        if (run.SizeOverride.HasValue)
                            sizeOverride = run.SizeOverride.Value;
                        if (run.CharacterSpacingOverride.HasValue)
                            charCharSpacing = run.CharacterSpacingOverride.Value;
                        if (run.MonoSpaceOverride.HasValue)
                            charMonoSpace = run.MonoSpaceOverride.Value;
                        charMarkColor = run.MarkColor;
                    }
                }
            }

            float charScale = sizeOverride / font.PointSize;

            // Track word boundaries for wrapping
            if (c == ' ' || c == '\t')
            {
                lastWordBreak = glyphs.Count;
                lineWidthAtWordBreak = penX;
                glyphCountAtWordBreak = glyphs.Count - lineStartGlyph;
            }

            // Look up glyph
            uint codepoint = (uint)c;
            if (!font.TryGetGlyphWithFallback(codepoint, out GlyphData glyph, out int fontIndex))
            {
                // Fire missing glyph event (deduplicated)
                missingSet ??= [];
                if (missingSet.Add((codepoint, font.InstanceID)))
                {
                    TextEvents.InvokeOnGlyphMissing(new GlyphMissingArgs(codepoint, font, text));
                }
                continue;
            }

            // Apply kerning
            if (prevGlyphIndex != 0 && font.TryGetKerning(prevGlyphIndex, glyph.GlyphIndex, out float kern))
            {
                penX += kern * charScale;
            }

            // Word wrap check
            float glyphAdvance;
            if (charMonoSpace > 0)
                glyphAdvance = charMonoSpace * charScale;
            else
                glyphAdvance = glyph.Advance * charScale + charCharSpacing;
            if (c == ' ' || c == '\t')
                glyphAdvance += wordSpacing;

            if (shouldWrap && penX + glyph.BearingX * charScale + glyph.Width * charScale > maxWidth && penX > 0)
            {
                if (overflow == TextOverflowMode.Ellipsis)
                {
                    // Truncate and add ellipsis
                    TryAddEllipsis(glyphs, font, charScale, penX, penY, charColor, lines.Count);
                    FinishLine(lines, glyphs, lineStartGlyph, penX, scaledAscender, scaledDescender, penY);
                    goto LayoutComplete;
                }

                if (overflow == TextOverflowMode.WordWrap && lastWordBreak > lineStartGlyph)
                {
                    // Move glyphs after word break to new line
                    int moveStart = lastWordBreak;
                    FinishLine(lines, glyphs, lineStartGlyph, lineWidthAtWordBreak, scaledAscender, scaledDescender, penY);

                    penY -= scaledLineHeight;
                    penX = ReflowGlyphs(glyphs, moveStart, penY);
                    lineStartGlyph = moveStart;
                    lastWordBreak = -1;
                }
                else
                {
                    // Character wrap
                    FinishLine(lines, glyphs, lineStartGlyph, penX, scaledAscender, scaledDescender, penY);
                    lineStartGlyph = glyphs.Count;
                    penX = 0f;
                    penY -= scaledLineHeight;
                    lastWordBreak = -1;
                }
            }

            // Place glyph
            GlyphPlacement placement = new()
            {
                GlyphIndex = (int)glyph.GlyphIndex,
                FontAssetIndex = fontIndex,
                Position = new Float2(penX + glyph.BearingX * charScale, penY + glyph.BearingY * charScale),
                Scale = new Float2(charScale, charScale),
                Color = charColor,
                StyleFlags = flags,
                MarkColor = charMarkColor,
                LineIndex = lines.Count,
                CharacterIndex = i
            };

            glyphs.Add(placement);
            penX += glyphAdvance;
            prevGlyphIndex = glyph.GlyphIndex;
        }

        // Finish last line
        if (glyphs.Count > lineStartGlyph || lines.Count == 0)
        {
            FinishLine(lines, glyphs, lineStartGlyph, penX, scaledAscender, scaledDescender, penY);
        }

    LayoutComplete:

        // Apply horizontal alignment
        LineInfo[] lineArray = lines.ToArray();
        GlyphPlacement[] glyphArray = glyphs.ToArray();

        ApplyAlignment(glyphArray, lineArray, alignment, maxWidth, text);

        // Compute bounds
        float totalHeight = lineArray.Length * scaledLineHeight;
        float maxLineWidth = 0f;
        for (int i = 0; i < lineArray.Length; i++)
        {
            if (lineArray[i].Width > maxLineWidth)
                maxLineWidth = lineArray[i].Width;
        }

        // Apply vertical alignment offset
        if (verticalAlignment != VerticalAlignment.Top && lineArray.Length > 0)
        {
            float offsetY = 0f;
            if (verticalAlignment == VerticalAlignment.Middle)
                offsetY = totalHeight * 0.5f;
            else if (verticalAlignment == VerticalAlignment.Bottom)
                offsetY = totalHeight;

            for (int i = 0; i < glyphArray.Length; i++)
            {
                glyphArray[i].Position = new Float2(glyphArray[i].Position.X, glyphArray[i].Position.Y + offsetY);
            }

            for (int i = 0; i < lineArray.Length; i++)
            {
                lineArray[i].Baseline += offsetY;
            }
        }

        return new TextLayout
        {
            Glyphs = glyphArray,
            Lines = lineArray,
            TextBounds = new Float2(maxLineWidth, totalHeight),
            PreferredSize = new Float2(maxLineWidth, totalHeight)
        };
    }

    private static void FinishLine(
        List<LineInfo> lines,
        List<GlyphPlacement> glyphs,
        int lineStartGlyph,
        float lineWidth,
        float ascender,
        float descender,
        float baseline)
    {
        lines.Add(new LineInfo
        {
            StartGlyphIndex = lineStartGlyph,
            GlyphCount = glyphs.Count - lineStartGlyph,
            Width = lineWidth,
            Ascender = ascender,
            Descender = descender,
            Baseline = baseline
        });
    }

    private static float ReflowGlyphs(List<GlyphPlacement> glyphs, int startIndex, float newBaseline)
    {
        float penX = 0f;
        for (int i = startIndex; i < glyphs.Count; i++)
        {
            GlyphPlacement g = glyphs[i];
            float offsetX = g.Position.X - (i > startIndex ? glyphs[startIndex].Position.X : g.Position.X);
            g.Position = new Float2(offsetX, newBaseline + (g.Position.Y - glyphs[i].Position.Y));
            g.LineIndex++;
            glyphs[i] = g;
            // Track furthest extent for penX
            // This is a simplification; actual advance should be used
            penX = offsetX + 1f; // Approximate
        }
        return penX;
    }

    private static void ApplyAlignment(GlyphPlacement[] glyphs, LineInfo[] lines, TextAlignment alignment, float maxWidth, string text)
    {
        if (alignment == TextAlignment.Left)
            return;

        for (int l = 0; l < lines.Length; l++)
        {
            LineInfo line = lines[l];
            float offset = 0f;
            float availableWidth = maxWidth < float.MaxValue ? maxWidth : line.Width;
            int end = line.StartGlyphIndex + line.GlyphCount;

            switch (alignment)
            {
                case TextAlignment.Center:
                    offset = (availableWidth - line.Width) * 0.5f;
                    break;
                case TextAlignment.Right:
                    offset = availableWidth - line.Width;
                    break;
                case TextAlignment.Justified:
                    // Don't justify the last line — leave it left-aligned
                    if (l == lines.Length - 1)
                        break;
                    if (maxWidth >= float.MaxValue || maxWidth <= 0)
                        break;

                    float extraSpace = availableWidth - line.Width;
                    if (extraSpace <= 0.001f)
                        break;

                    // Count word spaces on this line
                    int spaceCount = 0;
                    for (int g = line.StartGlyphIndex; g < end && g < glyphs.Length; g++)
                    {
                        int ci = glyphs[g].CharacterIndex;
                        if (ci >= 0 && ci < text.Length && text[ci] == ' ')
                            spaceCount++;
                    }

                    if (spaceCount == 0)
                        break;

                    float spaceAdd = extraSpace / spaceCount;
                    float accumulated = 0f;
                    for (int g = line.StartGlyphIndex; g < end && g < glyphs.Length; g++)
                    {
                        glyphs[g].Position = new Float2(glyphs[g].Position.X + accumulated, glyphs[g].Position.Y);
                        int ci = glyphs[g].CharacterIndex;
                        if (ci >= 0 && ci < text.Length && text[ci] == ' ')
                            accumulated += spaceAdd;
                    }
                    continue; // Skip the generic offset loop below
            }

            if (offset == 0f)
                continue;

            for (int g = line.StartGlyphIndex; g < end && g < glyphs.Length; g++)
            {
                glyphs[g].Position = new Float2(glyphs[g].Position.X + offset, glyphs[g].Position.Y);
            }
        }
    }

    private static void TryAddEllipsis(
        List<GlyphPlacement> glyphs,
        FontAsset font,
        float scale,
        float penX,
        float penY,
        Color color,
        int lineIndex)
    {
        // Try to add '…' (U+2026) or fall back to three dots
        uint ellipsisCodepoint = 0x2026;
        if (!font.TryGetGlyph(ellipsisCodepoint, out GlyphData ellipsisGlyph))
        {
            // Fall back to '.'
            ellipsisCodepoint = '.';
            if (!font.TryGetGlyph(ellipsisCodepoint, out ellipsisGlyph))
                return;

            // Add three dots
            for (int d = 0; d < 3; d++)
            {
                glyphs.Add(new GlyphPlacement
                {
                    GlyphIndex = (int)ellipsisGlyph.GlyphIndex,
                    FontAssetIndex = 0,
                    Position = new Float2(penX + ellipsisGlyph.BearingX * scale, penY + ellipsisGlyph.BearingY * scale),
                    Scale = new Float2(scale, scale),
                    Color = color,
                    StyleFlags = TextStyleFlags.None,
                    LineIndex = lineIndex,
                    CharacterIndex = -1
                });
                penX += ellipsisGlyph.Advance * scale;
            }
            return;
        }

        glyphs.Add(new GlyphPlacement
        {
            GlyphIndex = (int)ellipsisGlyph.GlyphIndex,
            FontAssetIndex = 0,
            Position = new Float2(penX + ellipsisGlyph.BearingX * scale, penY + ellipsisGlyph.BearingY * scale),
            Scale = new Float2(scale, scale),
            Color = color,
            StyleFlags = TextStyleFlags.None,
            LineIndex = lineIndex,
            CharacterIndex = -1
        });
    }
}
