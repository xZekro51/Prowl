// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Vector;

namespace Prowl.Runtime.Text.Effects;

/// <summary>
/// Cycles each character's color through the HSV spectrum, offset by character position.
/// </summary>
public class RainbowEffect : TextEffect
{
    /// <summary> Speed of the color cycling (hue rotations per second). </summary>
    public float Speed { get; set; } = 1f;

    /// <summary> Hue offset between adjacent characters (0..1 range). </summary>
    public float CharacterOffset { get; set; } = 0.05f;

    /// <summary> Saturation of the rainbow colors (0..1). </summary>
    public float Saturation { get; set; } = 1f;

    /// <summary> Value/brightness of the rainbow colors (0..1). </summary>
    public float Value { get; set; } = 1f;

    public override void Apply(
        Span<Float3> positions,
        Span<Color> colors,
        Span<Float2> uvs,
        TextLayout layout,
        float time)
    {
        if (!Enabled || layout.Glyphs == null)
            return;

        for (int g = 0; g < layout.Glyphs.Length; g++)
        {
            (int start, int count) = GetGlyphVertexRange(g, 0);
            if (start + count > colors.Length)
                break;

            float hue = (time * Speed + g * CharacterOffset) % 1f;
            if (hue < 0f) hue += 1f;

            Color rgb = HsvToRgb(hue, Saturation, Value);

            for (int v = start; v < start + count; v++)
            {
                Color c = colors[v];
                // Preserve the original alpha
                colors[v] = new Color(rgb.R, rgb.G, rgb.B, c.A);
            }
        }
    }

    private static Color HsvToRgb(float h, float s, float v)
    {
        float c = v * s;
        float x = c * (1f - Math.Abs(h * 6f % 2f - 1f));
        float m = v - c;

        float r, g, b;
        int sector = (int)(h * 6f) % 6;
        switch (sector)
        {
            case 0: r = c; g = x; b = 0; break;
            case 1: r = x; g = c; b = 0; break;
            case 2: r = 0; g = c; b = x; break;
            case 3: r = 0; g = x; b = c; break;
            case 4: r = x; g = 0; b = c; break;
            default: r = c; g = 0; b = x; break;
        }

        return new Color(r + m, g + m, b + m, 1f);
    }
}
