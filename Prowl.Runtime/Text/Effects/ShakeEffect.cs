// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Vector;

namespace Prowl.Runtime.Text.Effects;

/// <summary>
/// Applies random position jitter to each character, simulating a shaking effect.
/// </summary>
public class ShakeEffect : TextEffect
{
    /// <summary> Maximum jitter amplitude in world units. </summary>
    public float Amplitude { get; set; } = 0.05f;

    /// <summary> Speed of the shake (how rapidly it changes). </summary>
    public float Speed { get; set; } = 20f;

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

            // Hash-based pseudo-random per character, varying over time
            int seed = HashCombine(g, (int)(time * Speed));
            float ox = PseudoRandom(seed) * Amplitude;
            float oy = PseudoRandom(seed + 1) * Amplitude;

            for (int v = start; v < start + count; v++)
            {
                Float3 pos = positions[v];
                positions[v] = new Float3(pos.X + ox, pos.Y + oy, pos.Z);
            }
        }
    }

    private static int HashCombine(int a, int b)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + a;
            hash = hash * 31 + b;
            return hash;
        }
    }

    private static float PseudoRandom(int seed)
    {
        // Simple hash → [-1, 1] range
        unchecked
        {
            seed = (seed ^ 61) ^ (seed >> 16);
            seed *= 9;
            seed = seed ^ (seed >> 4);
            seed *= 0x27d4eb2d;
            seed = seed ^ (seed >> 15);
        }
        return (seed & 0x7FFFFFFF) / (float)0x7FFFFFFF * 2f - 1f;
    }
}
