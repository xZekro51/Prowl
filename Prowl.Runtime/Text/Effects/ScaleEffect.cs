// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Vector;

namespace Prowl.Runtime.Text.Effects;

/// <summary>
/// Applies a pulsing scale effect to each character around its center.
/// </summary>
public class ScaleEffect : TextEffect
{
    /// <summary> Base scale multiplier (1.0 = no change). </summary>
    public float BaseScale { get; set; } = 1f;

    /// <summary> Amplitude of the scale pulse (added to BaseScale). </summary>
    public float Amplitude { get; set; } = 0.2f;

    /// <summary> Speed of the pulse animation. </summary>
    public float Speed { get; set; } = 3f;

    /// <summary> Phase offset between adjacent characters. </summary>
    public float CharacterOffset { get; set; } = 0.3f;

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
            if (start + count > positions.Length)
                break;

            float scale = BaseScale + Amplitude * (float)Math.Sin(time * Speed + g * CharacterOffset);

            // Compute center of this glyph's quad
            Float3 center = Float3.Zero;
            for (int v = start; v < start + count; v++)
            {
                center += positions[v];
            }
            center /= count;

            // Scale each vertex around the center
            for (int v = start; v < start + count; v++)
            {
                Float3 pos = positions[v];
                Float3 offset = pos - center;
                positions[v] = center + offset * scale;
            }
        }
    }
}
