// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;

using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Runtime.Text;

/// <summary>
/// A contiguous run of text sharing the same style properties.
/// Produced by <see cref="RichTextParser"/> and consumed by <see cref="TextShaper"/>.
/// </summary>
public struct StyleRun
{
    /// <summary> Character index into the stripped (no-tag) text. </summary>
    public int StartIndex;

    /// <summary> Number of characters in this run. </summary>
    public int Length;

    /// <summary> Font override from <c>&lt;font&gt;</c> tag, or <c>null</c>. </summary>
    public FontAsset? FontOverride;

    /// <summary> Font size override from <c>&lt;size&gt;</c> tag. </summary>
    public float? SizeOverride;

    /// <summary> Color override from <c>&lt;color&gt;</c> tag. </summary>
    public Color? ColorOverride;

    /// <summary> Alpha override from <c>&lt;alpha&gt;</c> tag. </summary>
    public float? AlphaOverride;

    /// <summary> Bitwise combination of bold, italic, underline, etc. </summary>
    public TextStyleFlags StyleFlags;

    /// <summary> Character spacing override from <c>&lt;cspace&gt;</c> tag. </summary>
    public float? CharacterSpacingOverride;

    /// <summary> Monospace width override from <c>&lt;mspace&gt;</c> tag. </summary>
    public float? MonoSpaceOverride;

    /// <summary> Link identifier from <c>&lt;link&gt;</c> tag. </summary>
    public string? LinkId;

    /// <summary> Sprite index from <c>&lt;sprite&gt;</c> tag. </summary>
    public int? SpriteIndex;

    /// <summary> Background highlight color from <c>&lt;mark&gt;</c> tag. </summary>
    public Color? MarkColor;

    /// <summary> Raw font name from <c>&lt;font&gt;</c> tag, for external resolution. </summary>
    public string? FontName;

    /// <summary> Line height override from <c>&lt;line-height&gt;</c> tag. </summary>
    public float? LineHeightOverride;

    /// <summary> Left indent override from <c>&lt;indent&gt;</c> tag. </summary>
    public float? IndentOverride;

    /// <summary> Alignment override from <c>&lt;align&gt;</c> tag. </summary>
    public TextAlignment? AlignmentOverride;

    /// <summary> Wave effect amplitude from <c>&lt;wave&gt;</c> tag. </summary>
    public float? WaveAmplitude;

    /// <summary> Wave effect frequency from <c>&lt;wave&gt;</c> tag. </summary>
    public float? WaveFrequency;

    /// <summary> Shake effect intensity from <c>&lt;shake&gt;</c> tag. </summary>
    public float? ShakeIntensity;
}

/// <summary>
/// The result of parsing rich text markup.
/// Contains the display text (tags removed) and the style runs.
/// </summary>
public struct ParsedText
{
    /// <summary> Text with all tags removed. </summary>
    public string StrippedText;

    /// <summary> Style runs describing per-character formatting. </summary>
    public StyleRun[] Runs;
}

/// <summary>
/// Single-pass forward scanner that parses TMPro-compatible rich text tags
/// and produces <see cref="ParsedText"/> (stripped text + style runs).
/// </summary>
public static class RichTextParser
{
    /// <summary>
    /// Parses the input text, stripping rich text tags and producing style runs.
    /// </summary>
    /// <param name="input">Raw text potentially containing rich text tags.</param>
    /// <returns>A <see cref="ParsedText"/> with the stripped text and style runs.</returns>
    public static ParsedText Parse(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return new ParsedText
            {
                StrippedText = string.Empty,
                Runs = [new StyleRun { StartIndex = 0, Length = 0, StyleFlags = TextStyleFlags.None }]
            };
        }

        List<StyleRun> runs = [];
        char[] stripped = new char[input.Length];
        int strippedLen = 0;

        // Style state stack
        Stack<StyleState> stateStack = new();
        StyleState current = new();

        int runStart = 0;
        bool noparse = false;

        int i = 0;
        while (i < input.Length)
        {
            if (input[i] == '<' && !noparse)
            {
                int tagEnd = input.IndexOf('>', i + 1);
                if (tagEnd < 0)
                {
                    // No closing '>' — treat as literal
                    stripped[strippedLen++] = input[i];
                    i++;
                    continue;
                }

                ReadOnlySpan<char> tagContent = input.AsSpan(i + 1, tagEnd - i - 1);

                // Capture state before parsing so closing tags emit the pre-pop style
                StyleState preTagState = current;

                if (TryParseTag(tagContent, stateStack, ref current, ref noparse, stripped, ref strippedLen, out bool consumed))
                {
                    if (consumed)
                    {
                        // Emit run for text up to this point using the pre-tag state
                        if (strippedLen > runStart)
                        {
                            runs.Add(CreateRun(runStart, strippedLen - runStart, preTagState));
                            runStart = strippedLen;
                        }
                    }
                    i = tagEnd + 1;
                    continue;
                }

                // Unrecognized tag — render as literal text
                for (int t = i; t <= tagEnd; t++)
                    stripped[strippedLen++] = input[t];
                i = tagEnd + 1;
                continue;
            }

            if (input[i] == '<' && noparse)
            {
                // Check for </noparse>
                int tagEnd = input.IndexOf('>', i + 1);
                if (tagEnd >= 0)
                {
                    ReadOnlySpan<char> tagContent = input.AsSpan(i + 1, tagEnd - i - 1);
                    if (tagContent.Equals("/noparse", StringComparison.OrdinalIgnoreCase))
                    {
                        noparse = false;
                        i = tagEnd + 1;
                        continue;
                    }
                }

                // In noparse mode, render as literal
                stripped[strippedLen++] = input[i];
                i++;
                continue;
            }

            stripped[strippedLen++] = input[i];
            i++;
        }

        // Emit final run
        if (strippedLen > runStart)
        {
            runs.Add(CreateRun(runStart, strippedLen - runStart, current));
        }

        // Ensure at least one run exists
        if (runs.Count == 0)
        {
            runs.Add(new StyleRun { StartIndex = 0, Length = strippedLen, StyleFlags = TextStyleFlags.None });
        }

        return new ParsedText
        {
            StrippedText = new string(stripped, 0, strippedLen),
            Runs = runs.ToArray()
        };
    }

    private static StyleRun CreateRun(int start, int length, StyleState state)
    {
        return new StyleRun
        {
            StartIndex = start,
            Length = length,
            FontOverride = state.FontOverride,
            SizeOverride = state.SizeOverride,
            ColorOverride = state.ColorOverride,
            AlphaOverride = state.AlphaOverride,
            StyleFlags = state.StyleFlags,
            CharacterSpacingOverride = state.CharacterSpacingOverride,
            MonoSpaceOverride = state.MonoSpaceOverride,
            LinkId = state.LinkId,
            SpriteIndex = state.SpriteIndex,
            MarkColor = state.MarkColor,
            FontName = state.FontName,
            LineHeightOverride = state.LineHeightOverride,
            IndentOverride = state.IndentOverride,
            AlignmentOverride = state.AlignmentOverride,
            WaveAmplitude = state.WaveAmplitude,
            WaveFrequency = state.WaveFrequency,
            ShakeIntensity = state.ShakeIntensity
        };
    }

    private static bool TryParseTag(
        ReadOnlySpan<char> tagContent,
        Stack<StyleState> stateStack,
        ref StyleState current,
        ref bool noparse,
        char[] stripped,
        ref int strippedLen,
        out bool consumed)
    {
        consumed = true;

        // Self-closing tags
        if (tagContent.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            stripped[strippedLen++] = '\n';
            consumed = false; // Don't create a new run, just add newline
            return true;
        }

        // Opening tags
        if (tagContent.Equals("b", StringComparison.OrdinalIgnoreCase))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.Bold;
            return true;
        }

        if (tagContent.Equals("i", StringComparison.OrdinalIgnoreCase))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.Italic;
            return true;
        }

        if (tagContent.Equals("u", StringComparison.OrdinalIgnoreCase))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.Underline;
            return true;
        }

        if (tagContent.Equals("s", StringComparison.OrdinalIgnoreCase))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.Strikethrough;
            return true;
        }

        if (tagContent.Equals("sup", StringComparison.OrdinalIgnoreCase))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.Superscript;
            return true;
        }

        if (tagContent.Equals("sub", StringComparison.OrdinalIgnoreCase))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.Subscript;
            return true;
        }

        if (tagContent.Equals("noparse", StringComparison.OrdinalIgnoreCase))
        {
            noparse = true;
            consumed = false;
            return true;
        }

        // Effect tags (no-value variants use defaults)
        if (tagContent.Equals("wave", StringComparison.OrdinalIgnoreCase))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.Wave;
            current.WaveAmplitude ??= 1f;
            current.WaveFrequency ??= 2f;
            return true;
        }

        if (tagContent.Equals("shake", StringComparison.OrdinalIgnoreCase))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.Shake;
            current.ShakeIntensity ??= 1f;
            return true;
        }

        if (tagContent.Equals("fade", StringComparison.OrdinalIgnoreCase))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.Fade;
            return true;
        }

        if (tagContent.Equals("typewriter", StringComparison.OrdinalIgnoreCase))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.Typewriter;
            return true;
        }

        if (tagContent.Equals("rainbow", StringComparison.OrdinalIgnoreCase))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.Rainbow;
            return true;
        }

        if (tagContent.Equals("scale", StringComparison.OrdinalIgnoreCase))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.ScaleEffect;
            return true;
        }

        if (tagContent.Equals("rotate", StringComparison.OrdinalIgnoreCase))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.RotateEffect;
            return true;
        }

        // Closing tags
        if (tagContent.Equals("/b", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/i", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/u", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/s", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/sup", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/sub", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/color", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/size", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/font", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/alpha", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/mark", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/cspace", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/mspace", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/link", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/line-height", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/indent", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/align", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/wave", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/shake", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/fade", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/typewriter", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/rainbow", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/scale", StringComparison.OrdinalIgnoreCase) ||
            tagContent.Equals("/rotate", StringComparison.OrdinalIgnoreCase))
        {
            if (stateStack.Count > 0)
                current = stateStack.Pop();
            return true;
        }

        // Value tags: <color=#RRGGBB>, <size=24>, etc.
        if (TryParseValueTag(tagContent, "color=", out ReadOnlySpan<char> colorVal))
        {
            if (TryParseColor(colorVal, out Color c))
            {
                stateStack.Push(current);
                current.ColorOverride = c;
                return true;
            }
            return false;
        }

        if (TryParseValueTag(tagContent, "size=", out ReadOnlySpan<char> sizeVal))
        {
            if (float.TryParse(sizeVal, NumberStyles.Float, CultureInfo.InvariantCulture, out float size))
            {
                stateStack.Push(current);
                current.SizeOverride = size;
                return true;
            }
            return false;
        }

        if (TryParseValueTag(tagContent, "alpha=", out ReadOnlySpan<char> alphaVal))
        {
            if (TryParseHexByte(alphaVal, out byte a))
            {
                stateStack.Push(current);
                current.AlphaOverride = a / 255f;
                return true;
            }
            return false;
        }

        if (TryParseValueTag(tagContent, "cspace=", out ReadOnlySpan<char> cspaceVal))
        {
            if (float.TryParse(cspaceVal, NumberStyles.Float, CultureInfo.InvariantCulture, out float cs))
            {
                stateStack.Push(current);
                current.CharacterSpacingOverride = cs;
                return true;
            }
            return false;
        }

        if (TryParseValueTag(tagContent, "mspace=", out ReadOnlySpan<char> mspaceVal))
        {
            if (float.TryParse(mspaceVal, NumberStyles.Float, CultureInfo.InvariantCulture, out float ms))
            {
                stateStack.Push(current);
                current.MonoSpaceOverride = ms;
                return true;
            }
            return false;
        }

        if (TryParseValueTag(tagContent, "mark=", out ReadOnlySpan<char> markVal))
        {
            if (TryParseColor(markVal, out Color mc))
            {
                stateStack.Push(current);
                current.MarkColor = mc;
                return true;
            }
            return false;
        }

        if (TryParseValueTag(tagContent, "link=", out ReadOnlySpan<char> linkVal))
        {
            stateStack.Push(current);
            // Strip surrounding quotes if present
            string linkId = linkVal.ToString();
            if (linkId.Length >= 2 && linkId[0] == '"' && linkId[^1] == '"')
                linkId = linkId[1..^1];
            current.LinkId = linkId;
            return true;
        }

        if (TryParseValueTag(tagContent, "sprite=", out ReadOnlySpan<char> spriteVal))
        {
            if (int.TryParse(spriteVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sprIdx))
            {
                stateStack.Push(current);
                current.SpriteIndex = sprIdx;
                return true;
            }
            return false;
        }

        if (TryParseValueTag(tagContent, "font=", out ReadOnlySpan<char> fontVal))
        {
            stateStack.Push(current);
            string fontName = fontVal.ToString();
            if (fontName.Length >= 2 && fontName[0] == '"' && fontName[^1] == '"')
                fontName = fontName[1..^1];
            current.FontName = fontName;
            return true;
        }

        if (TryParseValueTag(tagContent, "line-height=", out ReadOnlySpan<char> lineHeightVal))
        {
            if (float.TryParse(lineHeightVal, NumberStyles.Float, CultureInfo.InvariantCulture, out float lh))
            {
                stateStack.Push(current);
                current.LineHeightOverride = lh;
                return true;
            }
            return false;
        }

        if (TryParseValueTag(tagContent, "indent=", out ReadOnlySpan<char> indentVal))
        {
            if (float.TryParse(indentVal, NumberStyles.Float, CultureInfo.InvariantCulture, out float indent))
            {
                stateStack.Push(current);
                current.IndentOverride = indent;
                return true;
            }
            return false;
        }

        if (TryParseValueTag(tagContent, "align=", out ReadOnlySpan<char> alignVal))
        {
            string alignStr = alignVal.ToString().ToLowerInvariant();
            TextAlignment? parsedAlign = alignStr switch
            {
                "left" => TextAlignment.Left,
                "center" => TextAlignment.Center,
                "right" => TextAlignment.Right,
                "justified" => TextAlignment.Justified,
                _ => null
            };
            if (parsedAlign.HasValue)
            {
                stateStack.Push(current);
                current.AlignmentOverride = parsedAlign.Value;
                return true;
            }
            return false;
        }

        // Effect value tags: <wave a=5>, <shake a=2>, etc.
        if (TryParseValueTag(tagContent, "wave ", out ReadOnlySpan<char> waveParams))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.Wave;
            current.WaveAmplitude = ParseNamedFloat(waveParams, "a") ?? 1f;
            current.WaveFrequency = ParseNamedFloat(waveParams, "f") ?? 2f;
            return true;
        }

        if (TryParseValueTag(tagContent, "shake ", out ReadOnlySpan<char> shakeParams))
        {
            stateStack.Push(current);
            current.StyleFlags |= TextStyleFlags.Shake;
            current.ShakeIntensity = ParseNamedFloat(shakeParams, "a") ?? 1f;
            return true;
        }

        return false;
    }

    private static bool TryParseValueTag(ReadOnlySpan<char> tagContent, string prefix, out ReadOnlySpan<char> value)
    {
        if (tagContent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = tagContent.Slice(prefix.Length);
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryParseColor(ReadOnlySpan<char> value, out Color color)
    {
        color = default;

        if (value.Length == 0)
            return false;

        // Hex color: #RGB, #RRGGBB, #RRGGBBAA
        if (value[0] == '#')
        {
            ReadOnlySpan<char> hex = value.Slice(1);

            if (hex.Length == 6 || hex.Length == 8)
            {
                if (byte.TryParse(hex.Slice(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
                    byte.TryParse(hex.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
                    byte.TryParse(hex.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    byte a = 255;
                    if (hex.Length == 8)
                        byte.TryParse(hex.Slice(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a);

                    color = new Color(r / 255f, g / 255f, b / 255f, a / 255f);
                    return true;
                }
            }
            else if (hex.Length == 3)
            {
                if (byte.TryParse(hex.Slice(0, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
                    byte.TryParse(hex.Slice(1, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
                    byte.TryParse(hex.Slice(2, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    color = new Color((r * 17) / 255f, (g * 17) / 255f, (b * 17) / 255f, 1f);
                    return true;
                }
            }

            return false;
        }

        // Named colors
        string name = value.ToString().ToLowerInvariant();
        switch (name)
        {
            case "red": color = new Color(1f, 0f, 0f, 1f); return true;
            case "green": color = new Color(0f, 1f, 0f, 1f); return true;
            case "blue": color = new Color(0f, 0f, 1f, 1f); return true;
            case "white": color = Color.White; return true;
            case "black": color = new Color(0f, 0f, 0f, 1f); return true;
            case "yellow": color = new Color(1f, 1f, 0f, 1f); return true;
            case "cyan": color = new Color(0f, 1f, 1f, 1f); return true;
            case "magenta": color = new Color(1f, 0f, 1f, 1f); return true;
            case "orange": color = new Color(1f, 0.647f, 0f, 1f); return true;
            case "purple": color = new Color(0.5f, 0f, 0.5f, 1f); return true;
            default: return false;
        }
    }

    private static bool TryParseHexByte(ReadOnlySpan<char> value, out byte result)
    {
        result = 0;
        if (value.Length == 0)
            return false;

        // Strip leading '#' if present
        ReadOnlySpan<char> hex = value[0] == '#' ? value.Slice(1) : value;

        if (hex.Length == 2)
            return byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);

        return false;
    }

    /// <summary>
    /// Mutable state tracked during parsing. Pushed/popped on tag open/close.
    /// </summary>
    private struct StyleState
    {
        public FontAsset? FontOverride;
        public float? SizeOverride;
        public Color? ColorOverride;
        public float? AlphaOverride;
        public TextStyleFlags StyleFlags;
        public float? CharacterSpacingOverride;
        public float? MonoSpaceOverride;
        public string? LinkId;
        public int? SpriteIndex;
        public Color? MarkColor;
        public string? FontName;
        public float? LineHeightOverride;
        public float? IndentOverride;
        public TextAlignment? AlignmentOverride;
        public float? WaveAmplitude;
        public float? WaveFrequency;
        public float? ShakeIntensity;
    }

    /// <summary>
    /// Parses a named float parameter from a space-separated attribute string.
    /// E.g., "a=5 f=3" with name "a" returns 5.
    /// </summary>
    private static float? ParseNamedFloat(ReadOnlySpan<char> parameters, string name)
    {
        string paramStr = parameters.ToString();
        string prefix = name + "=";
        int idx = paramStr.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        int start = idx + prefix.Length;
        int end = paramStr.IndexOf(' ', start);
        if (end < 0)
            end = paramStr.Length;

        if (float.TryParse(paramStr.AsSpan(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
            return result;

        return null;
    }
}
