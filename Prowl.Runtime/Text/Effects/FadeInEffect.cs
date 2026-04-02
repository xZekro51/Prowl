// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Vector;

namespace Prowl.Runtime.Text.Effects;

/// <summary>
/// Fades in characters one by one with a configurable delay between each.
/// Alpha ramps from 0 to 1 over <see cref="FadeDuration"/> seconds per character.
/// </summary>
public class FadeInEffect : TextEffect
{
    /// <summary> Time in seconds before the first character starts fading in. </summary>
    public float StartDelay { get; set; } = 0f;

    /// <summary> Delay in seconds between the start of each character's fade. </summary>
    public float CharacterDelay { get; set; } = 0.05f;

    /// <summary> Duration in seconds for a single character's alpha ramp. </summary>
    public float FadeDuration { get; set; } = 0.2f;

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

            float charTime = time - StartDelay - g * CharacterDelay;
            float alpha = FadeDuration > 0f
                ? Math.Clamp(charTime / FadeDuration, 0f, 1f)
                : (charTime >= 0f ? 1f : 0f);

            for (int v = start; v < start + count; v++)
            {
                Color c = colors[v];
                colors[v] = new Color(c.R, c.G, c.B, c.A * alpha);
            }
        }
    }
}
