// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Vector;

namespace Prowl.Runtime.Text.Effects;

/// <summary>
/// Reveals characters over time like a typewriter. Characters before the cursor
/// are fully visible; characters after are fully transparent.
/// </summary>
public class TypewriterEffect : TextEffect
{
    /// <summary> Characters revealed per second. </summary>
    public float CharactersPerSecond { get; set; } = 30f;

    /// <summary> Time in seconds before the first character appears. </summary>
    public float StartDelay { get; set; } = 0f;

    /// <summary>
    /// If <c>true</c>, characters fade in over a short duration rather than appearing instantly.
    /// </summary>
    public bool SoftReveal { get; set; } = false;

    /// <summary> Duration of the fade when <see cref="SoftReveal"/> is enabled. </summary>
    public float SoftRevealDuration { get; set; } = 0.1f;

    /// <summary> Returns the number of currently visible characters at the given time. </summary>
    public int GetVisibleCount(int totalGlyphs, float time)
    {
        float elapsed = time - StartDelay;
        if (elapsed <= 0f)
            return 0;
        int count = (int)(elapsed * CharactersPerSecond);
        return Math.Min(count, totalGlyphs);
    }

    public override void Apply(
        Span<Float3> positions,
        Span<Color> colors,
        Span<Float2> uvs,
        TextLayout layout,
        float time)
    {
        if (!Enabled || layout.Glyphs == null)
            return;

        float elapsed = time - StartDelay;

        for (int g = 0; g < layout.Glyphs.Length; g++)
        {
            (int start, int count) = GetGlyphVertexRange(g, 0);
            if (start + count > colors.Length)
                break;

            float charTime = elapsed - g / Math.Max(CharactersPerSecond, 0.001f);
            float alpha;

            if (SoftReveal && SoftRevealDuration > 0f)
            {
                alpha = Math.Clamp(charTime / SoftRevealDuration, 0f, 1f);
            }
            else
            {
                alpha = charTime >= 0f ? 1f : 0f;
            }

            for (int v = start; v < start + count; v++)
            {
                Color c = colors[v];
                colors[v] = new Color(c.R, c.G, c.B, c.A * alpha);
            }
        }
    }
}
