// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Text;

using Xunit;

namespace Prowl.Runtime.Test;

public class GlyphDataTests
{
    [Fact]
    public void GlyphData_Equality_WorksCorrectly()
    {
        GlyphData a = new(1, 10, 20, 1, 18, 12, 0, 0, 10, 20, 1f);
        GlyphData b = new(1, 10, 20, 1, 18, 12, 0, 0, 10, 20, 1f);

        Assert.Equal(a, b);
    }

    [Fact]
    public void GlyphData_Inequality_DetectsDifferences()
    {
        GlyphData a = new(1, 10, 20, 1, 18, 12, 0, 0, 10, 20, 1f);
        GlyphData b = new(2, 10, 20, 1, 18, 12, 0, 0, 10, 20, 1f);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GlyphData_RecordProperties_AreReadable()
    {
        GlyphData g = new(
            GlyphIndex: 65,
            Width: 12f,
            Height: 20f,
            BearingX: 1f,
            BearingY: 18f,
            Advance: 14f,
            AtlasX: 100f,
            AtlasY: 200f,
            AtlasWidth: 16f,
            AtlasHeight: 24f,
            Scale: 0.5f);

        Assert.Equal(65u, g.GlyphIndex);
        Assert.Equal(12f, g.Width);
        Assert.Equal(20f, g.Height);
        Assert.Equal(1f, g.BearingX);
        Assert.Equal(18f, g.BearingY);
        Assert.Equal(14f, g.Advance);
        Assert.Equal(100f, g.AtlasX);
        Assert.Equal(200f, g.AtlasY);
        Assert.Equal(16f, g.AtlasWidth);
        Assert.Equal(24f, g.AtlasHeight);
        Assert.Equal(0.5f, g.Scale);
    }

    [Fact]
    public void GlyphData_Default_HasZeroValues()
    {
        GlyphData g = default;

        Assert.Equal(0u, g.GlyphIndex);
        Assert.Equal(0f, g.Width);
        Assert.Equal(0f, g.Advance);
    }
}
