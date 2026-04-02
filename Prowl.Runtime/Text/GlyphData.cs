// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Echo;

namespace Prowl.Runtime.Text;

/// <summary>
/// Stores the metrics and atlas coordinates for a single glyph in a <see cref="FontAsset"/>.
/// </summary>
/// <remarks>
/// Uses explicit fields rather than record struct primary constructor parameters
/// so that Prowl.Echo (which serializes public fields) can round-trip the data.
/// </remarks>
public struct GlyphData : IEquatable<GlyphData>
{
    public uint GlyphIndex;
    public float Width;
    public float Height;
    public float BearingX;
    public float BearingY;
    public float Advance;
    public float AtlasX;
    public float AtlasY;
    public float AtlasWidth;
    public float AtlasHeight;
    public float Scale;

    public GlyphData(
        uint GlyphIndex,
        float Width,
        float Height,
        float BearingX,
        float BearingY,
        float Advance,
        float AtlasX,
        float AtlasY,
        float AtlasWidth,
        float AtlasHeight,
        float Scale)
    {
        this.GlyphIndex = GlyphIndex;
        this.Width = Width;
        this.Height = Height;
        this.BearingX = BearingX;
        this.BearingY = BearingY;
        this.Advance = Advance;
        this.AtlasX = AtlasX;
        this.AtlasY = AtlasY;
        this.AtlasWidth = AtlasWidth;
        this.AtlasHeight = AtlasHeight;
        this.Scale = Scale;
    }

    public bool Equals(GlyphData other) =>
        GlyphIndex == other.GlyphIndex &&
        Width == other.Width &&
        Height == other.Height &&
        BearingX == other.BearingX &&
        BearingY == other.BearingY &&
        Advance == other.Advance &&
        AtlasX == other.AtlasX &&
        AtlasY == other.AtlasY &&
        AtlasWidth == other.AtlasWidth &&
        AtlasHeight == other.AtlasHeight &&
        Scale == other.Scale;

    public override bool Equals(object? obj) => obj is GlyphData other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(GlyphIndex);
        hash.Add(Width);
        hash.Add(Height);
        hash.Add(BearingX);
        hash.Add(BearingY);
        hash.Add(Advance);
        hash.Add(AtlasX);
        hash.Add(AtlasY);
        hash.Add(AtlasWidth);
        hash.Add(AtlasHeight);
        hash.Add(Scale);
        return hash.ToHashCode();
    }

    public static bool operator ==(GlyphData left, GlyphData right) => left.Equals(right);
    public static bool operator !=(GlyphData left, GlyphData right) => !left.Equals(right);

    public override string ToString() =>
        $"GlyphData {{ GlyphIndex = {GlyphIndex}, Width = {Width}, Height = {Height}, " +
        $"BearingX = {BearingX}, BearingY = {BearingY}, Advance = {Advance}, " +
        $"AtlasX = {AtlasX}, AtlasY = {AtlasY}, AtlasWidth = {AtlasWidth}, " +
        $"AtlasHeight = {AtlasHeight}, Scale = {Scale} }}";
}
