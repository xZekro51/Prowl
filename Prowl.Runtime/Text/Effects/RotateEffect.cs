// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Vector;

namespace Prowl.Runtime.Text.Effects;

/// <summary>
/// Applies per-character Z-axis rotation around each glyph's center.
/// </summary>
public class RotateEffect : TextEffect
{
    /// <summary> Maximum rotation angle in degrees. </summary>
    public float AngleDegrees { get; set; } = 15f;

    /// <summary> Speed of the rotation oscillation. </summary>
    public float Speed { get; set; } = 3f;

    /// <summary> Phase offset between adjacent characters. </summary>
    public float CharacterOffset { get; set; } = 0.4f;

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

            float angle = AngleDegrees * (float)Math.Sin(time * Speed + g * CharacterOffset);
            float rad = angle * ((float)Math.PI / 180f);
            float cos = (float)Math.Cos(rad);
            float sin = (float)Math.Sin(rad);

            // Compute center of this glyph's quad
            Float3 center = Float3.Zero;
            for (int v = start; v < start + count; v++)
            {
                center += positions[v];
            }
            center /= count;

            // Rotate each vertex around the center in the XY plane
            for (int v = start; v < start + count; v++)
            {
                Float3 pos = positions[v];
                float dx = pos.X - center.X;
                float dy = pos.Y - center.Y;
                positions[v] = new Float3(
                    center.X + dx * cos - dy * sin,
                    center.Y + dx * sin + dy * cos,
                    pos.Z);
            }
        }
    }
}
