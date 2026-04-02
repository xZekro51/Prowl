// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Vector;

namespace Prowl.Runtime.Text.Effects;

/// <summary>
/// Abstract base for per-vertex text effects that modify mesh data post-generation.
/// Effects operate on the positioned vertex arrays without changing topology.
/// </summary>
public abstract class TextEffect
{
    /// <summary> Whether this effect is currently active. </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Applies the effect to the mesh vertex data.
    /// </summary>
    /// <param name="positions">Vertex positions (4 per glyph, interleaved with decoration verts).</param>
    /// <param name="colors">Vertex colors.</param>
    /// <param name="uvs">Vertex UVs.</param>
    /// <param name="layout">The text layout for glyph-to-vertex mapping.</param>
    /// <param name="time">Current time in seconds (for animation).</param>
    public abstract void Apply(
        Span<Float3> positions,
        Span<Color> colors,
        Span<Float2> uvs,
        TextLayout layout,
        float time);

    /// <summary>
    /// Returns the vertex index range for a given glyph index.
    /// Assumes 4 vertices per glyph, starting after any decoration geometry.
    /// </summary>
    protected static (int start, int count) GetGlyphVertexRange(int glyphIndex, int decorationVertCount)
    {
        int start = decorationVertCount + glyphIndex * 4;
        return (start, 4);
    }
}
