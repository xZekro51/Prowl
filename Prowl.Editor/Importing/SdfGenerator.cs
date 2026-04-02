// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.CompilerServices;

namespace Prowl.Editor.Importing;

/// <summary>
/// Generates Signed Distance Field (SDF) and Multi-channel Signed Distance Field (MSDF)
/// atlas data from glyph bitmaps or outlines. All algorithms are pure managed C#.
/// </summary>
public static class SdfGenerator
{
    // ── Bitmap-based SDF (single-channel) ─────────────────────

    /// <summary>
    /// Generates a single-channel SDF from a high-resolution binary bitmap using the
    /// 8-point Sequential Euclidean Distance Transform (8SSEDT).
    /// </summary>
    /// <param name="bitmap">Grayscale bitmap (0 = outside, 255 = inside).</param>
    /// <param name="bmpW">Bitmap width.</param>
    /// <param name="bmpH">Bitmap height.</param>
    /// <param name="sdfW">Output SDF width.</param>
    /// <param name="sdfH">Output SDF height.</param>
    /// <param name="spread">Distance spread in source bitmap pixels.</param>
    /// <returns>Byte array of sdfW*sdfH with 0.5 at the edge, 1.0 inside, 0.0 outside.</returns>
    public static byte[] GenerateSdfFromBitmap(
        byte[] bitmap, int bmpW, int bmpH,
        int sdfW, int sdfH, float spread)
    {
        // Compute distance fields for inside and outside using Felzenszwalb EDT.
        // outsideDist: distance from outside pixels to the nearest inside pixel.
        // insideDist:  distance from inside pixels to the nearest outside pixel.
        float[] outsideDist = new float[bmpW * bmpH];
        float[] insideDist = new float[bmpW * bmpH];

        const float INF = 1e10f;
        const byte THRESHOLD = 128;

        // Initialize: for outsideDist, seed 0 where pixel is inside, INF where outside.
        //             for insideDist,  seed 0 where pixel is outside, INF where inside.
        // The EDT will propagate these to compute actual squared distances.
        for (int i = 0; i < bmpW * bmpH; i++)
        {
            bool isInside = bitmap[i] >= THRESHOLD;
            outsideDist[i] = isInside ? 0f : INF;
            insideDist[i] = isInside ? INF : 0f;
        }

        // Apply Euclidean distance transform to both maps
        ApplyEdt(outsideDist, bmpW, bmpH);
        ApplyEdt(insideDist, bmpW, bmpH);

        // Compute signed distance and downsample to output size
        byte[] sdf = new byte[sdfW * sdfH];
        float scaleX = (float)bmpW / sdfW;
        float scaleY = (float)bmpH / sdfH;

        for (int sy = 0; sy < sdfH; sy++)
        {
            for (int sx = 0; sx < sdfW; sx++)
            {
                // Sample from the center of the source region
                int bx = Math.Clamp((int)(sx * scaleX + scaleX * 0.5f), 0, bmpW - 1);
                int by = Math.Clamp((int)(sy * scaleY + scaleY * 0.5f), 0, bmpH - 1);
                int bi = by * bmpW + bx;

                bool inside = bitmap[bi] >= THRESHOLD;
                float dist = inside
                    ? MathF.Sqrt(insideDist[bi])
                    : -MathF.Sqrt(outsideDist[bi]);

                // Normalize: 0.5 at border, 1.0 at +spread, 0.0 at -spread
                float normalized = dist / spread * 0.5f + 0.5f;
                sdf[sy * sdfW + sx] = (byte)Math.Clamp((int)(normalized * 255f + 0.5f), 0, 255);
            }
        }

        return sdf;
    }

    /// <summary>
    /// Applies a 2D Euclidean distance transform (squared distances) in-place
    /// using separable 1D transforms (Felzenszwalb &amp; Huttenlocher algorithm).
    /// Input: 0.0 at seed pixels, large value (INF) elsewhere.
    /// Output: squared Euclidean distance to nearest seed pixel.
    /// </summary>
    private static void ApplyEdt(float[] grid, int w, int h)
    {
        float[] temp = new float[Math.Max(w, h)];
        int[] v = new int[Math.Max(w, h)];
        float[] z = new float[Math.Max(w, h) + 1];

        // Transform columns
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
                temp[y] = grid[y * w + x];

            Edt1D(temp, h, v, z);

            for (int y = 0; y < h; y++)
                grid[y * w + x] = temp[y];
        }

        // Transform rows
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                temp[x] = grid[y * w + x];

            Edt1D(temp, w, v, z);

            for (int x = 0; x < w; x++)
                grid[y * w + x] = temp[x];
        }
    }

    /// <summary>
    /// 1D squared Euclidean distance transform using the parabola envelope method
    /// (Felzenszwalb &amp; Huttenlocher 2012).
    /// </summary>
    private static void Edt1D(float[] f, int n, int[] v, float[] z)
    {
        const float INF = 1e10f;
        v[0] = 0;
        z[0] = -INF;
        z[1] = INF;
        int k = 0;

        for (int q = 1; q < n; q++)
        {
            float s;
            while (true)
            {
                int r = v[k];
                s = ((f[q] + q * q) - (f[r] + r * r)) / (2f * q - 2f * r);
                if (s > z[k]) break;
                k--;
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = INF;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            int d = q - v[k];
            f[q] = d * d + f[v[k]];
        }
    }

    // ── MSDF from glyph outlines ─────────────────────────────

    /// <summary>
    /// Generates an MSDF (Multi-channel Signed Distance Field) from glyph outline contours.
    /// Returns RGB byte data (3 bytes per pixel, sdfW*sdfH pixels).
    /// </summary>
    /// <param name="contours">The glyph outline contours with edge segments.</param>
    /// <param name="sdfW">Output width.</param>
    /// <param name="sdfH">Output height.</param>
    /// <param name="pxRange">Distance field range in output pixels.</param>
    /// <param name="scale">Scale from font units to output pixels.</param>
    /// <param name="translateX">X translation to center glyph in its cell.</param>
    /// <param name="translateY">Y translation to center glyph in its cell.</param>
    public static byte[] GenerateMsdf(
        List<Contour> contours, int sdfW, int sdfH,
        float pxRange, float scale, float translateX, float translateY)
    {
        // Color the edges
        ColorEdges(contours);

        // Compute per-pixel distances
        byte[] output = new byte[sdfW * sdfH * 3];
        float invRange = 1f / pxRange;

        for (int py = 0; py < sdfH; py++)
        {
            for (int px = 0; px < sdfW; px++)
            {
                // Map pixel center to font-unit space
                float x = (px + 0.5f - translateX) / scale;
                float y = (py + 0.5f - translateY) / scale;

                float minDistR = float.MaxValue;
                float minDistG = float.MaxValue;
                float minDistB = float.MaxValue;
                float signedR = 1f;
                float signedG = 1f;
                float signedB = 1f;

                foreach (Contour contour in contours)
                {
                    foreach (EdgeSegment edge in contour.Edges)
                    {
                        float dist = edge.SignedDistance(x, y, out float sign);

                        if ((edge.Channel & EdgeColor.Red) != 0 && dist < minDistR)
                        {
                            minDistR = dist;
                            signedR = sign;
                        }
                        if ((edge.Channel & EdgeColor.Green) != 0 && dist < minDistG)
                        {
                            minDistG = dist;
                            signedG = sign;
                        }
                        if ((edge.Channel & EdgeColor.Blue) != 0 && dist < minDistB)
                        {
                            minDistB = dist;
                            signedB = sign;
                        }
                    }
                }

                // Convert to signed distance in pixel space, then to [0,1]
                float dr = signedR * MathF.Sqrt(minDistR) * scale * invRange * 0.5f + 0.5f;
                float dg = signedG * MathF.Sqrt(minDistG) * scale * invRange * 0.5f + 0.5f;
                float db = signedB * MathF.Sqrt(minDistB) * scale * invRange * 0.5f + 0.5f;

                int i = (py * sdfW + px) * 3;
                output[i + 0] = (byte)Math.Clamp((int)(dr * 255f + 0.5f), 0, 255);
                output[i + 1] = (byte)Math.Clamp((int)(dg * 255f + 0.5f), 0, 255);
                output[i + 2] = (byte)Math.Clamp((int)(db * 255f + 0.5f), 0, 255);
            }
        }

        return output;
    }

    // ── Edge Coloring ─────────────────────────────────────────

    /// <summary>
    /// Assigns MSDF channel colors to edges. At corners (sharp angles between edges),
    /// the channel switches to ensure at least two channels differ at every corner.
    /// </summary>
    private static void ColorEdges(List<Contour> contours)
    {
        // Simple edge coloring: cycle through channel pairs for each contour
        // This follows the basic msdfgen coloring strategy
        EdgeColor[] colors = [EdgeColor.Cyan, EdgeColor.Magenta, EdgeColor.Yellow];

        foreach (Contour contour in contours)
        {
            int edgeCount = contour.Edges.Count;
            if (edgeCount == 0) continue;

            if (edgeCount == 1)
            {
                // Single edge: must carry all three channels
                contour.Edges[0].Channel = EdgeColor.White;
                continue;
            }

            if (edgeCount == 2)
            {
                // Two edges: one gets Cyan+Magenta, other gets Yellow+Magenta
                contour.Edges[0].Channel = EdgeColor.Cyan | EdgeColor.Magenta;
                contour.Edges[1].Channel = EdgeColor.Yellow | EdgeColor.Magenta;
                continue;
            }

            // 3+ edges: cycle through color pairs, switching at corners
            int colorIdx = 0;
            for (int i = 0; i < edgeCount; i++)
            {
                contour.Edges[i].Channel = colors[colorIdx % colors.Length];

                // Check if there's a corner between this edge and the next
                int nextI = (i + 1) % edgeCount;
                EdgeSegment current = contour.Edges[i];
                EdgeSegment next = contour.Edges[nextI];

                // Get direction at the junction point
                (float dx1, float dy1) = current.DirectionAtEnd();
                (float dx2, float dy2) = next.DirectionAtStart();

                // Compute angle between directions
                float cross = dx1 * dy2 - dy1 * dx2;
                float dot = dx1 * dx2 + dy1 * dy2;
                float angle = MathF.Atan2(MathF.Abs(cross), dot);

                // If angle is sharp enough (> ~30 degrees), switch color
                if (angle > 0.523f) // ~30 degrees in radians
                    colorIdx++;
            }
        }
    }

    // ── Outline types ─────────────────────────────────────────

    [Flags]
    public enum EdgeColor : byte
    {
        Red = 1,
        Green = 2,
        Blue = 4,
        Cyan = Green | Blue,
        Magenta = Red | Blue,
        Yellow = Red | Green,
        White = Red | Green | Blue,
    }

    /// <summary> A closed contour in a glyph outline. </summary>
    public class Contour
    {
        public List<EdgeSegment> Edges { get; } = [];
    }

    /// <summary> Base class for glyph outline edge segments. </summary>
    public abstract class EdgeSegment
    {
        public EdgeColor Channel { get; set; } = EdgeColor.White;

        /// <summary>
        /// Returns the squared unsigned distance from point (px, py) to this segment,
        /// and the sign (+1 if outside, -1 if inside based on edge orientation).
        /// </summary>
        public abstract float SignedDistance(float px, float py, out float sign);

        /// <summary> Direction vector at the start of the segment (normalized). </summary>
        public abstract (float dx, float dy) DirectionAtStart();

        /// <summary> Direction vector at the end of the segment (normalized). </summary>
        public abstract (float dx, float dy) DirectionAtEnd();
    }

    /// <summary> A straight line segment from P0 to P1. </summary>
    public class LinearSegment : EdgeSegment
    {
        public float X0, Y0, X1, Y1;

        public LinearSegment(float x0, float y0, float x1, float y1)
        {
            X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
        }

        public override float SignedDistance(float px, float py, out float sign)
        {
            float ex = X1 - X0, ey = Y1 - Y0;
            float dx = px - X0, dy = py - Y0;
            float len2 = ex * ex + ey * ey;

            float t = len2 > 0 ? Math.Clamp((dx * ex + dy * ey) / len2, 0f, 1f) : 0f;
            float closestX = X0 + t * ex;
            float closestY = Y0 + t * ey;

            float distX = px - closestX;
            float distY = py - closestY;

            // Sign from cross product (positive = left side = outside for CW contours)
            float cross = ex * (py - Y0) - ey * (px - X0);
            sign = cross >= 0 ? 1f : -1f;

            return distX * distX + distY * distY;
        }

        public override (float dx, float dy) DirectionAtStart()
        {
            float dx = X1 - X0, dy = Y1 - Y0;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            return len > 0 ? (dx / len, dy / len) : (0, 0);
        }

        public override (float dx, float dy) DirectionAtEnd() => DirectionAtStart();
    }

    /// <summary> A quadratic Bézier curve from P0 through control P1 to P2. </summary>
    public class QuadraticBezierSegment : EdgeSegment
    {
        public float X0, Y0, X1, Y1, X2, Y2;

        public QuadraticBezierSegment(float x0, float y0, float x1, float y1, float x2, float y2)
        {
            X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
        }

        public override float SignedDistance(float px, float py, out float sign)
        {
            // Find closest point on quadratic bezier by sampling + refinement
            float minDist2 = float.MaxValue;
            float bestT = 0f;

            // Initial sampling
            const int SAMPLES = 16;
            for (int i = 0; i <= SAMPLES; i++)
            {
                float t = i / (float)SAMPLES;
                EvalAt(t, out float bx, out float by);
                float d2 = (px - bx) * (px - bx) + (py - by) * (py - by);
                if (d2 < minDist2)
                {
                    minDist2 = d2;
                    bestT = t;
                }
            }

            // Newton refinement
            for (int iter = 0; iter < 4; iter++)
            {
                bestT = RefineQuadratic(px, py, bestT);
                bestT = Math.Clamp(bestT, 0f, 1f);
            }

            EvalAt(bestT, out float cx, out float cy);
            float finalDist2 = (px - cx) * (px - cx) + (py - cy) * (py - cy);

            // Sign from pseudo-distance using tangent cross product
            TangentAt(bestT, out float tx, out float ty);
            float cross = tx * (py - cy) - ty * (px - cx);
            sign = cross >= 0 ? 1f : -1f;

            return finalDist2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EvalAt(float t, out float x, out float y)
        {
            float u = 1f - t;
            x = u * u * X0 + 2f * u * t * X1 + t * t * X2;
            y = u * u * Y0 + 2f * u * t * Y1 + t * t * Y2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TangentAt(float t, out float tx, out float ty)
        {
            float u = 1f - t;
            tx = 2f * (u * (X1 - X0) + t * (X2 - X1));
            ty = 2f * (u * (Y1 - Y0) + t * (Y2 - Y1));
        }

        private float RefineQuadratic(float px, float py, float t)
        {
            EvalAt(t, out float bx, out float by);
            TangentAt(t, out float tx, out float ty);

            float dx = bx - px, dy = by - py;
            float num = dx * tx + dy * ty;
            float u = 1f - t;

            // Second derivative
            float ddx = 2f * (X2 - 2f * X1 + X0);
            float ddy = 2f * (Y2 - 2f * Y1 + Y0);
            float den = tx * tx + ty * ty + dx * ddx + dy * ddy;

            return den != 0 ? t - num / den : t;
        }

        public override (float dx, float dy) DirectionAtStart()
        {
            float dx = X1 - X0, dy = Y1 - Y0;
            if (dx == 0 && dy == 0) { dx = X2 - X0; dy = Y2 - Y0; }
            float len = MathF.Sqrt(dx * dx + dy * dy);
            return len > 0 ? (dx / len, dy / len) : (0, 0);
        }

        public override (float dx, float dy) DirectionAtEnd()
        {
            float dx = X2 - X1, dy = Y2 - Y1;
            if (dx == 0 && dy == 0) { dx = X2 - X0; dy = Y2 - Y0; }
            float len = MathF.Sqrt(dx * dx + dy * dy);
            return len > 0 ? (dx / len, dy / len) : (0, 0);
        }
    }

    // ── SDF-Aware Mipmap Generation ──────────────────────────

    /// <summary>
    /// Generates CPU-side mipmaps for an SDF or MSDF atlas using a distance-preserving
    /// downsampling filter. Standard bilinear downsampling corrupts distance fields by
    /// averaging signed distances, which can eliminate thin features and create incorrect
    /// boundaries. This method instead selects the texel whose absolute distance is
    /// smallest (closest to the edge) from each 2x2 block, preserving edge detail.
    /// <para>
    /// <b>Note:</b> In practice, SDF/MSDF font atlases typically do not use mipmaps because
    /// the SDF shader's <c>fwidth</c>-based anti-aliasing provides resolution-independent
    /// rendering. This utility is provided for cases where mipmaps are explicitly needed
    /// (e.g., world-space text viewed from extreme distances). For most use cases, set
    /// <see cref="FontImportSettings.GenerateMipmaps"/> to <c>false</c> (the default for
    /// SDF/MSDF atlases).
    /// </para>
    /// </summary>
    /// <param name="level0">The base mip level pixel data.</param>
    /// <param name="width">Base level width (must be power of two for full chain).</param>
    /// <param name="height">Base level height (must be power of two for full chain).</param>
    /// <param name="channels">Number of channels per pixel (1 for SDF, 3 for MSDF, 4 for RGBA).</param>
    /// <returns>
    /// A list of mip level byte arrays, starting from level 1 (half-size) down to 1×1.
    /// Level 0 is <paramref name="level0"/> itself and is NOT included in the result.
    /// </returns>
    public static List<byte[]> GenerateSdfMipmaps(byte[] level0, int width, int height, int channels)
    {
        List<byte[]> mipLevels = [];

        byte[] current = level0;
        int w = width;
        int h = height;

        while (w > 1 || h > 1)
        {
            int newW = Math.Max(1, w / 2);
            int newH = Math.Max(1, h / 2);
            byte[] next = new byte[newW * newH * channels];

            for (int y = 0; y < newH; y++)
            {
                for (int x = 0; x < newW; x++)
                {
                    // Sample up to 4 texels from the source level
                    int sx = x * 2;
                    int sy = y * 2;
                    int sx1 = Math.Min(sx + 1, w - 1);
                    int sy1 = Math.Min(sy + 1, h - 1);

                    // For each channel, pick the value closest to 0.5 (the edge)
                    // This preserves thin features better than averaging
                    for (int c = 0; c < channels; c++)
                    {
                        byte v00 = current[(sy * w + sx) * channels + c];
                        byte v10 = current[(sy * w + sx1) * channels + c];
                        byte v01 = current[(sy1 * w + sx) * channels + c];
                        byte v11 = current[(sy1 * w + sx1) * channels + c];

                        // Distance from edge (0.5 in normalized space = 128 in byte space)
                        int d00 = Math.Abs(v00 - 128);
                        int d10 = Math.Abs(v10 - 128);
                        int d01 = Math.Abs(v01 - 128);
                        int d11 = Math.Abs(v11 - 128);

                        // Select the texel closest to the edge (minimum absolute distance)
                        byte result = v00;
                        int minDist = d00;

                        if (d10 < minDist) { result = v10; minDist = d10; }
                        if (d01 < minDist) { result = v01; minDist = d01; }
                        if (d11 < minDist) { result = v11; }

                        next[(y * newW + x) * channels + c] = result;
                    }
                }
            }

            mipLevels.Add(next);
            current = next;
            w = newW;
            h = newH;
        }

        return mipLevels;
    }

    // ── Outline types ─────────────────────────────────────────

    /// <summary> A cubic Bézier curve from P0 through controls P1, P2 to P3. </summary>
    public class CubicBezierSegment : EdgeSegment
    {
        public float X0, Y0, X1, Y1, X2, Y2, X3, Y3;

        public CubicBezierSegment(float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3)
        {
            X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; X3 = x3; Y3 = y3;
        }

        public override float SignedDistance(float px, float py, out float sign)
        {
            float minDist2 = float.MaxValue;
            float bestT = 0f;

            const int SAMPLES = 24;
            for (int i = 0; i <= SAMPLES; i++)
            {
                float t = i / (float)SAMPLES;
                EvalAt(t, out float bx, out float by);
                float d2 = (px - bx) * (px - bx) + (py - by) * (py - by);
                if (d2 < minDist2)
                {
                    minDist2 = d2;
                    bestT = t;
                }
            }

            for (int iter = 0; iter < 4; iter++)
            {
                bestT = RefineCubic(px, py, bestT);
                bestT = Math.Clamp(bestT, 0f, 1f);
            }

            EvalAt(bestT, out float cx, out float cy);
            float finalDist2 = (px - cx) * (px - cx) + (py - cy) * (py - cy);

            TangentAt(bestT, out float tx, out float ty);
            float cross = tx * (py - cy) - ty * (px - cx);
            sign = cross >= 0 ? 1f : -1f;

            return finalDist2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EvalAt(float t, out float x, out float y)
        {
            float u = 1f - t;
            float u2 = u * u, t2 = t * t;
            float u3 = u2 * u, t3 = t2 * t;
            x = u3 * X0 + 3f * u2 * t * X1 + 3f * u * t2 * X2 + t3 * X3;
            y = u3 * Y0 + 3f * u2 * t * Y1 + 3f * u * t2 * Y2 + t3 * Y3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TangentAt(float t, out float tx, out float ty)
        {
            float u = 1f - t;
            tx = 3f * (u * u * (X1 - X0) + 2f * u * t * (X2 - X1) + t * t * (X3 - X2));
            ty = 3f * (u * u * (Y1 - Y0) + 2f * u * t * (Y2 - Y1) + t * t * (Y3 - Y2));
        }

        private float RefineCubic(float px, float py, float t)
        {
            EvalAt(t, out float bx, out float by);
            TangentAt(t, out float tx, out float ty);

            float dx = bx - px, dy = by - py;
            float num = dx * tx + dy * ty;

            // Second derivative of cubic bezier
            float u = 1f - t;
            float ddx = 6f * (u * (X2 - 2f * X1 + X0) + t * (X3 - 2f * X2 + X1));
            float ddy = 6f * (u * (Y2 - 2f * Y1 + Y0) + t * (Y3 - 2f * Y2 + Y1));
            float den = tx * tx + ty * ty + dx * ddx + dy * ddy;

            return den != 0 ? t - num / den : t;
        }

        public override (float dx, float dy) DirectionAtStart()
        {
            float dx = X1 - X0, dy = Y1 - Y0;
            if (dx == 0 && dy == 0) { dx = X2 - X0; dy = Y2 - Y0; }
            if (dx == 0 && dy == 0) { dx = X3 - X0; dy = Y3 - Y0; }
            float len = MathF.Sqrt(dx * dx + dy * dy);
            return len > 0 ? (dx / len, dy / len) : (0, 0);
        }

        public override (float dx, float dy) DirectionAtEnd()
        {
            float dx = X3 - X2, dy = Y3 - Y2;
            if (dx == 0 && dy == 0) { dx = X3 - X1; dy = Y3 - Y1; }
            if (dx == 0 && dy == 0) { dx = X3 - X0; dy = Y3 - Y0; }
            float len = MathF.Sqrt(dx * dx + dy * dy);
            return len > 0 ? (dx / len, dy / len) : (0, 0);
        }
    }
}
