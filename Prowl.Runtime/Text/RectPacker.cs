// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.Text;

/// <summary>
/// A skyline bottom-left rect packer for efficiently packing glyph rectangles into an atlas.
/// Maintains a horizon (skyline) of the tallest packed rects and places new rects at the
/// lowest available position.
/// </summary>
public class RectPacker
{
    private readonly int _width;
    private readonly int _height;
    private readonly int[] _skyline;

    /// <summary>
    /// Creates a new rect packer with the given atlas dimensions.
    /// </summary>
    /// <param name="width">Atlas width in pixels.</param>
    /// <param name="height">Atlas height in pixels.</param>
    public RectPacker(int width, int height)
    {
        _width = width;
        _height = height;
        _skyline = new int[width];
    }

    /// <summary>
    /// Tries to pack a rectangle of the given dimensions into the atlas.
    /// </summary>
    /// <param name="w">Width of the rectangle to pack.</param>
    /// <param name="h">Height of the rectangle to pack.</param>
    /// <param name="x">The X position where the rectangle was placed.</param>
    /// <param name="y">The Y position where the rectangle was placed.</param>
    /// <returns><c>true</c> if the rectangle was successfully packed; <c>false</c> if no space remains.</returns>
    public bool TryPack(int w, int h, out int x, out int y)
    {
        x = 0;
        y = 0;

        if (w <= 0 || h <= 0 || w > _width || h > _height)
            return false;

        int bestX = -1;
        int bestY = _height; // Start with worst case

        // Scan all valid horizontal positions
        int maxStartX = _width - w;
        for (int sx = 0; sx <= maxStartX; sx++)
        {
            // Find the maximum skyline height across the span [sx, sx + w)
            int maxH = 0;
            for (int i = sx; i < sx + w; i++)
            {
                if (_skyline[i] > maxH)
                    maxH = _skyline[i];
            }

            // Check if the rect fits vertically and is a better position
            if (maxH + h <= _height && maxH < bestY)
            {
                bestX = sx;
                bestY = maxH;
            }
        }

        if (bestX < 0)
            return false;

        // Place the rect and update the skyline
        for (int i = bestX; i < bestX + w; i++)
        {
            _skyline[i] = bestY + h;
        }

        x = bestX;
        y = bestY;
        return true;
    }

    /// <summary>
    /// Resets the packer, clearing all packed rectangles.
    /// </summary>
    public void Reset()
    {
        System.Array.Clear(_skyline, 0, _skyline.Length);
    }

    /// <summary> The atlas width. </summary>
    public int Width => _width;

    /// <summary> The atlas height. </summary>
    public int Height => _height;

    /// <summary>
    /// Returns the current maximum height of the skyline (the tallest point used so far).
    /// </summary>
    public int UsedHeight
    {
        get
        {
            int max = 0;
            for (int i = 0; i < _skyline.Length; i++)
            {
                if (_skyline[i] > max)
                    max = _skyline[i];
            }
            return max;
        }
    }
}
