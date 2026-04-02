// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Vector;

namespace Prowl.Runtime.Text.Effects;

/// <summary>
/// Applies a sinusoidal vertical wave to each character.
/// </summary>
public class WaveEffect : TextEffect
{
    /// <summary> Amplitude of the wave in world units. </summary>
    public float Amplitude { get; set; } = 0.1f;

    /// <summary> Frequency of the wave (cycles per unit of horizontal distance). </summary>
    public float Frequency { get; set; } = 4f;

    /// <summary> Speed of the wave animation. </summary>
    public float Speed { get; set; } = 2f;

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

            float charX = layout.Glyphs[g].Position.X;
            float offset = Amplitude * (float)Math.Sin(charX * Frequency + time * Speed);

            for (int v = start; v < start + count; v++)
            {
                Float3 pos = positions[v];
                positions[v] = new Float3(pos.X, pos.Y + offset, pos.Z);
            }
        }
    }
}
