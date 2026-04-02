// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Text;

using Xunit;

namespace Prowl.Runtime.Test;

public class RectPackerTests
{
    [Fact]
    public void TryPack_SingleRect_Succeeds()
    {
        RectPacker packer = new(256, 256);

        bool result = packer.TryPack(32, 32, out int x, out int y);

        Assert.True(result);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void TryPack_MultipleRects_DoNotOverlap()
    {
        RectPacker packer = new(256, 256);

        Assert.True(packer.TryPack(100, 50, out int x1, out int y1));
        Assert.True(packer.TryPack(100, 50, out int x2, out int y2));

        // Rects should not overlap — either horizontally separated or vertically separated
        bool horizontallySeparated = x1 + 100 <= x2 || x2 + 100 <= x1;
        bool verticallySeparated = y1 + 50 <= y2 || y2 + 50 <= y1;
        Assert.True(horizontallySeparated || verticallySeparated,
            $"Rects overlap: ({x1},{y1},100,50) and ({x2},{y2},100,50)");
    }

    [Fact]
    public void TryPack_OversizedRect_Fails()
    {
        RectPacker packer = new(64, 64);

        bool result = packer.TryPack(128, 128, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryPack_ExactFit_Succeeds()
    {
        RectPacker packer = new(64, 64);

        bool result = packer.TryPack(64, 64, out int x, out int y);

        Assert.True(result);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void TryPack_SecondRectAfterExactFit_Fails()
    {
        RectPacker packer = new(64, 64);

        Assert.True(packer.TryPack(64, 64, out _, out _));
        Assert.False(packer.TryPack(1, 1, out _, out _));
    }

    [Fact]
    public void TryPack_ZeroDimensions_Fails()
    {
        RectPacker packer = new(256, 256);

        Assert.False(packer.TryPack(0, 10, out _, out _));
        Assert.False(packer.TryPack(10, 0, out _, out _));
        Assert.False(packer.TryPack(0, 0, out _, out _));
    }

    [Fact]
    public void TryPack_NegativeDimensions_Fails()
    {
        RectPacker packer = new(256, 256);

        Assert.False(packer.TryPack(-10, 10, out _, out _));
        Assert.False(packer.TryPack(10, -10, out _, out _));
    }

    [Fact]
    public void TryPack_ManySmallRects_PacksEfficiently()
    {
        RectPacker packer = new(128, 128);

        int packed = 0;
        for (int i = 0; i < 200; i++)
        {
            if (packer.TryPack(16, 16, out _, out _))
                packed++;
        }

        // 128x128 / (16x16) = 64 rects should fit perfectly
        Assert.Equal(64, packed);
    }

    [Fact]
    public void Reset_AllowsRepacking()
    {
        RectPacker packer = new(64, 64);

        Assert.True(packer.TryPack(64, 64, out _, out _));
        Assert.False(packer.TryPack(1, 1, out _, out _));

        packer.Reset();

        Assert.True(packer.TryPack(64, 64, out _, out _));
    }

    [Fact]
    public void UsedHeight_ReflectsPackedContent()
    {
        RectPacker packer = new(256, 256);

        Assert.Equal(0, packer.UsedHeight);

        packer.TryPack(32, 40, out _, out _);

        Assert.Equal(40, packer.UsedHeight);
    }

    [Fact]
    public void Width_And_Height_ReturnConstructorValues()
    {
        RectPacker packer = new(512, 1024);

        Assert.Equal(512, packer.Width);
        Assert.Equal(1024, packer.Height);
    }

    [Fact]
    public void TryPack_VariedSizes_AllFitWithinBounds()
    {
        RectPacker packer = new(256, 256);
        int[] sizes = [8, 16, 12, 20, 24, 10, 14, 32, 6, 18];

        foreach (int size in sizes)
        {
            if (packer.TryPack(size, size, out int x, out int y))
            {
                Assert.InRange(x, 0, 256 - size);
                Assert.InRange(y, 0, 256 - size);
            }
        }
    }
}
