// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Numerics;
using ImGuiNET;

namespace Prowl.Editor.Icons;

/// <summary>
/// An <see cref="IIcon"/> that renders a Unicode glyph (e.g. from FontAwesome or a symbol font)
/// via <c>ImDrawList.AddText</c>. This requires the glyph to be present in the currently
/// loaded ImGui font atlas.
/// </summary>
public sealed class FontIcon : IIcon
{
    /// <summary>The Unicode character(s) to render (UTF-16 string).</summary>
    public string Character { get; }

    /// <summary>Default color when no tint is specified.</summary>
    public Vector4 DefaultColor { get; }

    /// <param name="character">
    /// One or more UTF-16 characters representing the glyph.
    /// For FontAwesome, pass the Unicode code point as a string, e.g. <c>"\uF04B"</c> (play).
    /// </param>
    /// <param name="defaultColor">
    /// Optional default tint. Defaults to opaque white if null.
    /// </param>
    public FontIcon(string character, Vector4? defaultColor = null)
    {
        Character = character ?? throw new ArgumentNullException(nameof(character));
        DefaultColor = defaultColor ?? new Vector4(1f, 1f, 1f, 1f);
    }

    /// <inheritdoc />
    public void Draw(Vector2 position, float size, Vector4? tint = null)
    {
        var color = tint ?? DefaultColor;
        var drawList = ImGui.GetWindowDrawList();

        // Use the current ImGui font at the requested size.
        var font = ImGui.GetFont();

        // Offset Y so the glyph is vertically centred within the requested
        // line height. The additional downward nudge for Phosphor glyph metrics
        // is handled globally via GlyphOffset in the font config at load time.
        float glyphHeight = font.FontSize;
        float yOffset = (size - glyphHeight) * 0.5f;
        var pos = new Vector2(position.X, position.Y + yOffset);

        drawList.AddText(font, size, pos, ImGui.GetColorU32(color), Character);
    }
}
