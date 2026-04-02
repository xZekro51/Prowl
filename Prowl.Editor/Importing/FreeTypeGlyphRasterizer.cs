// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.InteropServices;
using System.Text;

using FreeTypeSharp;

using Prowl.Runtime;
using Prowl.Runtime.Text;

namespace Prowl.Editor.Importing;

/// <summary>
/// FreeType-based implementation of <see cref="IGlyphRasterizer"/> for runtime
/// on-demand glyph rasterization. Produces SDF, MSDF, or bitmap pixel data
/// for individual glyphs.
/// </summary>
public sealed unsafe class FreeTypeGlyphRasterizer : IGlyphRasterizer
{
    private FT_LibraryRec_* _library;
    private FT_FaceRec_* _face;
    private byte[]? _fontDataPin;
    private GCHandle _fontDataHandle;
    private bool _initialized;
    private bool _disposed;

    private float _pointSize;
    private AtlasType _atlasType;
    private float _pxRange;
    private int _padding;
    private float _metricsScale;
    private int _sdfOversample;

    /// <inheritdoc/>
    public bool Initialize(byte[] fontData, float pointSize, AtlasType atlasType, float pxRange, int padding)
    {
        if (_initialized)
            return true;

        if (fontData == null || fontData.Length == 0)
            return false;

        _pointSize = pointSize;
        _atlasType = atlasType;
        _pxRange = pxRange;
        _padding = padding;
        _sdfOversample = atlasType == AtlasType.SDF ? 4 : 1;

        // Initialize FreeType library
        FT_Error err;
        FT_LibraryRec_* lib;
        err = FT.FT_Init_FreeType(&lib);
        if (err != FT_Error.FT_Err_Ok)
        {
            Debug.LogError($"[FreeTypeGlyphRasterizer] Failed to initialize FreeType: {err}");
            return false;
        }
        _library = lib;

        // Pin the font data and create a face from memory
        _fontDataPin = fontData;
        _fontDataHandle = GCHandle.Alloc(_fontDataPin, GCHandleType.Pinned);

        FT_FaceRec_* face;
        fixed (byte* dataPtr = _fontDataPin)
        {
            err = FT.FT_New_Memory_Face(_library, dataPtr, fontData.Length, 0, &face);
        }

        if (err != FT_Error.FT_Err_Ok)
        {
            Debug.LogError($"[FreeTypeGlyphRasterizer] Failed to create font face from memory: {err}");
            FT.FT_Done_FreeType(_library);
            _fontDataHandle.Free();
            _library = null;
            return false;
        }
        _face = face;

        // Set pixel size
        err = FT.FT_Set_Pixel_Sizes(_face, 0, (uint)pointSize);
        if (err != FT_Error.FT_Err_Ok)
        {
            Debug.LogError($"[FreeTypeGlyphRasterizer] Failed to set pixel size: {err}");
            Dispose();
            return false;
        }

        _metricsScale = 1f / 64f; // 26.6 fixed point
        _initialized = true;
        return true;
    }

    /// <inheritdoc/>
    public bool RasterizeGlyph(uint codepoint, out RasterizedGlyph glyph)
    {
        glyph = default;

        if (!_initialized || _disposed)
            return false;

        uint glyphIndex = FT.FT_Get_Char_Index(_face, (UIntPtr)codepoint);
        if (glyphIndex == 0)
            return false;

        // Load glyph metrics
        FT_LOAD loadFlags = _atlasType == AtlasType.MSDF
            ? FT_LOAD.FT_LOAD_NO_BITMAP | FT_LOAD.FT_LOAD_NO_HINTING
            : FT_LOAD.FT_LOAD_RENDER;

        FT_Error err = FT.FT_Load_Glyph(_face, glyphIndex, loadFlags);
        if (err != FT_Error.FT_Err_Ok)
            return false;

        FT_GlyphSlotRec_* slot = _face->glyph;
        FT_Glyph_Metrics_ metrics = slot->metrics;

        float width = (int)metrics.width * _metricsScale;
        float height = (int)metrics.height * _metricsScale;
        float bearingX = (int)metrics.horiBearingX * _metricsScale;
        float bearingY = (int)metrics.horiBearingY * _metricsScale;
        float advance = (int)metrics.horiAdvance * _metricsScale;

        // Handle whitespace glyphs (no pixels needed)
        if (width <= 0 || height <= 0)
        {
            glyph = new RasterizedGlyph(
                glyphIndex, codepoint,
                width, height, bearingX, bearingY, advance,
                [], 0, 0);
            return true;
        }

        // Compute cell size with padding
        int extraPadding = _atlasType == AtlasType.MSDF ? (int)MathF.Ceiling(_pxRange) : 0;
        int totalPadding = _padding + extraPadding;
        int cellW = (int)MathF.Ceiling(width) + totalPadding * 2;
        int cellH = (int)MathF.Ceiling(height) + totalPadding * 2;

        // Generate pixel data based on atlas type
        byte[] pixelData;

        switch (_atlasType)
        {
            case AtlasType.MSDF:
                pixelData = RasterizeMsdf(slot, cellW, cellH, totalPadding, width, height, bearingX, bearingY);
                break;

            case AtlasType.SDF:
                pixelData = RasterizeSdf(glyphIndex, cellW, cellH, totalPadding);
                break;

            default: // Bitmap
                pixelData = RasterizeBitmap(slot, cellW, cellH, totalPadding);
                break;
        }

        glyph = new RasterizedGlyph(
            glyphIndex, codepoint,
            width, height, bearingX, bearingY, advance,
            pixelData, cellW, cellH);
        return true;
    }

    /// <inheritdoc/>
    public bool TryGetKerning(uint leftGlyphIndex, uint rightGlyphIndex, out float kerning)
    {
        kerning = 0f;

        if (!_initialized || _disposed)
            return false;

        bool hasKerning = ((long)_face->face_flags & (long)FT_FACE_FLAG.FT_FACE_FLAG_KERNING) != 0;
        if (!hasKerning)
            return false;

        FT_Vector_ kernVec;
        FT_Error err = FT.FT_Get_Kerning(_face, leftGlyphIndex, rightGlyphIndex,
            FT_Kerning_Mode_.FT_KERNING_DEFAULT, &kernVec);

        if (err != FT_Error.FT_Err_Ok || kernVec.x == 0)
            return false;

        kerning = (int)kernVec.x * _metricsScale;
        return MathF.Abs(kerning) > 0.001f;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_face != null)
        {
            FT.FT_Done_Face(_face);
            _face = null;
        }

        if (_library != null)
        {
            FT.FT_Done_FreeType(_library);
            _library = null;
        }

        if (_fontDataHandle.IsAllocated)
            _fontDataHandle.Free();

        _fontDataPin = null;
        _initialized = false;
    }

    // ── Private rasterization methods ─────────────────────────

    private byte[] RasterizeMsdf(FT_GlyphSlotRec_* slot, int cellW, int cellH, int totalPadding,
        float width, float height, float bearingX, float bearingY)
    {
        List<SdfGenerator.Contour>? contours = ExtractOutline(slot);
        if (contours == null || contours.Count == 0)
            return new byte[cellW * cellH * 4];

        float scaleForMsdf = 1f;
        float translateX = totalPadding - bearingX * scaleForMsdf;
        float translateY = totalPadding + (height - bearingY) * scaleForMsdf;

        byte[] msdf = SdfGenerator.GenerateMsdf(contours, cellW, cellH, _pxRange,
            scaleForMsdf, translateX, translateY);

        // Convert RGB to RGBA
        byte[] rgba = new byte[cellW * cellH * 4];
        for (int i = 0; i < cellW * cellH; i++)
        {
            rgba[i * 4 + 0] = msdf[i * 3 + 0];
            rgba[i * 4 + 1] = msdf[i * 3 + 1];
            rgba[i * 4 + 2] = msdf[i * 3 + 2];
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    private byte[] RasterizeSdf(uint glyphIndex, int cellW, int cellH, int totalPadding)
    {
        // Render at higher resolution for SDF generation
        FT_Error err = FT.FT_Set_Pixel_Sizes(_face, 0, (uint)(_pointSize * _sdfOversample));
        if (err != FT_Error.FT_Err_Ok)
            return new byte[cellW * cellH * 4];

        err = FT.FT_Load_Glyph(_face, glyphIndex, FT_LOAD.FT_LOAD_RENDER);
        if (err != FT_Error.FT_Err_Ok)
        {
            FT.FT_Set_Pixel_Sizes(_face, 0, (uint)_pointSize);
            return new byte[cellW * cellH * 4];
        }

        FT_Bitmap_ bitmap = _face->glyph->bitmap;
        byte[] bitmapData = ExtractBitmap(bitmap);
        int bmpW = (int)bitmap.width;
        int bmpH = (int)bitmap.rows;

        // Restore original size
        FT.FT_Set_Pixel_Sizes(_face, 0, (uint)_pointSize);

        if (bitmapData.Length == 0)
            return new byte[cellW * cellH * 4];

        // Generate SDF from bitmap
        float spread = _pxRange * _sdfOversample;
        byte[] sdf = SdfGenerator.GenerateSdfFromBitmap(bitmapData, bmpW, bmpH, cellW, cellH, spread);

        // Convert single-channel to RGBA
        byte[] rgba = new byte[cellW * cellH * 4];
        for (int i = 0; i < cellW * cellH; i++)
        {
            byte val = sdf[i];
            rgba[i * 4 + 0] = val;
            rgba[i * 4 + 1] = val;
            rgba[i * 4 + 2] = val;
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    private byte[] RasterizeBitmap(FT_GlyphSlotRec_* slot, int cellW, int cellH, int totalPadding)
    {
        FT_Bitmap_ bitmap = slot->bitmap;
        byte[] bitmapData = ExtractBitmap(bitmap);
        int srcW = (int)bitmap.width;
        int srcH = (int)bitmap.rows;

        byte[] rgba = new byte[cellW * cellH * 4];

        for (int y = 0; y < srcH && y < cellH; y++)
        {
            for (int x = 0; x < srcW && x < cellW; x++)
            {
                int dstX = totalPadding + x;
                int dstY = totalPadding + y;
                if (dstX >= cellW || dstY >= cellH)
                    continue;

                byte val = bitmapData[y * srcW + x];
                int dstIdx = (dstY * cellW + dstX) * 4;
                rgba[dstIdx + 0] = 255;
                rgba[dstIdx + 1] = 255;
                rgba[dstIdx + 2] = 255;
                rgba[dstIdx + 3] = val;
            }
        }

        return rgba;
    }

    // ── FreeType helpers (mirrored from FontAssetImporter) ────

    private static List<SdfGenerator.Contour>? ExtractOutline(FT_GlyphSlotRec_* slot)
    {
        FT_Outline_ outline = slot->outline;
        if (outline.n_contours <= 0 || outline.n_points <= 0)
            return null;

        List<SdfGenerator.Contour> contours = [];
        int pointIdx = 0;

        for (int c = 0; c < outline.n_contours; c++)
        {
            SdfGenerator.Contour contour = new();
            int lastPointInContour = outline.contours[c];
            int contourStart = pointIdx;
            int contourLen = lastPointInContour - contourStart + 1;

            if (contourLen < 2)
            {
                pointIdx = lastPointInContour + 1;
                continue;
            }

            float[] px = new float[contourLen];
            float[] py = new float[contourLen];
            byte[] tags = new byte[contourLen];

            for (int i = 0; i < contourLen; i++)
            {
                int pi = contourStart + i;
                px[i] = (int)outline.points[pi].x / 64f;
                py[i] = (int)outline.points[pi].y / 64f;
                tags[i] = outline.tags[pi];
            }

            int i2 = 0;
            while (i2 < contourLen)
            {
                int cur = i2;
                bool onCurve = (tags[cur] & 1) != 0;

                if (!onCurve)
                {
                    i2++;
                    continue;
                }

                int next = (cur + 1) % contourLen;

                if ((tags[next] & 1) != 0)
                {
                    contour.Edges.Add(new SdfGenerator.LinearSegment(px[cur], py[cur], px[next], py[next]));
                    i2++;
                    continue;
                }

                bool isCubic = (tags[next] & 2) != 0;

                if (isCubic)
                {
                    int ctrl1 = next;
                    int ctrl2 = (next + 1) % contourLen;
                    int end = (next + 2) % contourLen;

                    contour.Edges.Add(new SdfGenerator.CubicBezierSegment(
                        px[cur], py[cur],
                        px[ctrl1], py[ctrl1],
                        px[ctrl2], py[ctrl2],
                        px[end], py[end]));
                    i2 += 3;
                }
                else
                {
                    int ctrl = next;
                    int afterCtrl = (ctrl + 1) % contourLen;

                    if ((tags[afterCtrl] & 1) != 0)
                    {
                        contour.Edges.Add(new SdfGenerator.QuadraticBezierSegment(
                            px[cur], py[cur],
                            px[ctrl], py[ctrl],
                            px[afterCtrl], py[afterCtrl]));
                        i2 += 2;
                    }
                    else
                    {
                        float midX = (px[ctrl] + px[afterCtrl]) * 0.5f;
                        float midY = (py[ctrl] + py[afterCtrl]) * 0.5f;

                        contour.Edges.Add(new SdfGenerator.QuadraticBezierSegment(
                            px[cur], py[cur],
                            px[ctrl], py[ctrl],
                            midX, midY));
                        i2++;
                    }
                }
            }

            if (contour.Edges.Count > 0)
                contours.Add(contour);

            pointIdx = lastPointInContour + 1;
        }

        return contours.Count > 0 ? contours : null;
    }

    private static byte[] ExtractBitmap(FT_Bitmap_ bitmap)
    {
        int width = (int)bitmap.width;
        int height = (int)bitmap.rows;
        int pitch = bitmap.pitch;

        if (width <= 0 || height <= 0 || bitmap.buffer == null)
            return [];

        byte[] pixels = new byte[width * height];

        for (int y = 0; y < height; y++)
        {
            byte* row = bitmap.buffer + y * pitch;
            for (int x = 0; x < width; x++)
            {
                pixels[y * width + x] = row[x];
            }
        }

        return pixels;
    }
}
