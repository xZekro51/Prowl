// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.InteropServices;
using System.Text;

using FreeTypeSharp;

using Prowl.Editor.Services;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Text;

namespace Prowl.Editor.Importing;

/// <summary>
/// Imports .ttf / .otf font files and produces <see cref="FontAsset"/> ScriptableObjects
/// with baked SDF/MSDF/Bitmap atlases. Uses FreeTypeSharp for font file parsing and
/// glyph metric extraction, and <see cref="SdfGenerator"/> for managed distance field
/// computation.
/// </summary>
public static class FontAssetImporter
{
    private static readonly HashSet<string> s_fontExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ttf", ".otf"
    };

    /// <summary> Returns true if the file extension represents a font file. </summary>
    public static bool IsFontFile(string extension)
        => s_fontExtensions.Contains(extension);

    /// <summary>
    /// Imports a font file and produces a <see cref="FontAsset"/> with a baked atlas.
    /// </summary>
    /// <param name="absolutePath">Full path to the .ttf or .otf file.</param>
    /// <param name="settings">Import settings. If null, settings are loaded from the .meta file (or defaults are used).</param>
    /// <returns>The generated FontAsset, or null on failure.</returns>
    public static FontAsset? Import(string absolutePath, FontImportSettings? settings = null)
    {
        if (!File.Exists(absolutePath))
        {
            Debug.LogError($"[FontAssetImporter] Font file not found: {absolutePath}");
            return null;
        }

        settings ??= LoadSettings(absolutePath);

        try
        {
            return ImportInternal(absolutePath, settings);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[FontAssetImporter] Failed to import '{Path.GetFileName(absolutePath)}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads import settings for a font from its .meta file.
    /// Returns default settings if no .meta file or no import settings exist.
    /// </summary>
    public static FontImportSettings LoadSettings(string absolutePath)
    {
        string metaPath = MetaFile.GetMetaPath(absolutePath);
        MetaFile? meta = MetaFile.Load(metaPath);
        if (meta != null && meta.ImportSettings.Count > 0)
            return FontImportSettings.FromMeta(meta.ImportSettings);
        return new FontImportSettings();
    }

    /// <summary>
    /// Saves import settings for a font into its .meta file.
    /// </summary>
    public static void SaveSettings(string absolutePath, FontImportSettings settings)
    {
        string metaPath = MetaFile.GetMetaPath(absolutePath);
        MetaFile? meta = MetaFile.Load(metaPath);
        meta ??= MetaFile.CreateNew();
        settings.WriteTo(meta.ImportSettings);
        meta.Save(metaPath);
    }

    // ── Internal implementation ───────────────────────────────

    private static unsafe FontAsset? ImportInternal(string absolutePath, FontImportSettings settings)
    {
        // Initialize FreeType
        FT_Error err;
        FT_LibraryRec_* library;
        err = FT.FT_Init_FreeType(&library);
        if (err != FT_Error.FT_Err_Ok)
        {
            Debug.LogError($"[FontAssetImporter] Failed to initialize FreeType: {err}");
            return null;
        }

        try
        {
            return ImportWithLibrary(library, absolutePath, settings);
        }
        finally
        {
            FT.FT_Done_FreeType(library);
        }
    }

    private static unsafe FontAsset? ImportWithLibrary(
        FT_LibraryRec_* library, string absolutePath, FontImportSettings settings)
    {
        // Load font face
        byte[] pathBytes = Encoding.UTF8.GetBytes(absolutePath + '\0');
        FT_FaceRec_* face;
        FT_Error err;

        fixed (byte* pathPtr = pathBytes)
        {
            err = FT.FT_New_Face(library, pathPtr, 0, &face);
        }

        if (err != FT_Error.FT_Err_Ok)
        {
            Debug.LogError($"[FontAssetImporter] Failed to load font face: {err}");
            return null;
        }

        try
        {
            return BuildFontAsset(library, face, absolutePath, settings);
        }
        finally
        {
            FT.FT_Done_Face(face);
        }
    }

    private static unsafe FontAsset? BuildFontAsset(
        FT_LibraryRec_* library, FT_FaceRec_* face,
        string absolutePath, FontImportSettings settings)
    {
        int pointSize = settings.PointSize;

        // Set pixel size for glyph rendering
        FT_Error err = FT.FT_Set_Pixel_Sizes(face, 0, (uint)pointSize);
        if (err != FT_Error.FT_Err_Ok)
        {
            Debug.LogError($"[FontAssetImporter] Failed to set pixel size: {err}");
            return null;
        }

        // Get font metrics (in 26.6 fixed point from size->metrics)
        FT_Size_Metrics_ sizeMetrics = face->size->metrics;
        float metricsScale = 1f / 64f; // 26.6 fixed point to float

        float ascender = (int)sizeMetrics.ascender * metricsScale;
        float descender = (int)sizeMetrics.descender * metricsScale;
        float lineHeight = (int)sizeMetrics.height * metricsScale;

        // Get codepoints to include
        HashSet<uint> codepoints = settings.GetCodepoints();

        // Phase 1: Load all glyph metrics and optionally outline/bitmap data
        List<GlyphBuildData> glyphBuildList = [];
        Dictionary<uint, uint> codepointToGlyphIndex = [];

        foreach (uint codepoint in codepoints)
        {
            uint glyphIndex = FT.FT_Get_Char_Index(face, (UIntPtr)codepoint);
            if (glyphIndex == 0)
                continue; // Glyph not in font

            // Avoid processing the same glyph index twice (multiple codepoints may map to same glyph)
            if (codepointToGlyphIndex.ContainsValue(glyphIndex))
            {
                codepointToGlyphIndex[codepoint] = glyphIndex;
                continue;
            }

            codepointToGlyphIndex[codepoint] = glyphIndex;

            // Load glyph
            FT_LOAD loadFlags = settings.AtlasType == AtlasType.MSDF
                ? FT_LOAD.FT_LOAD_NO_BITMAP | FT_LOAD.FT_LOAD_NO_HINTING
                : FT_LOAD.FT_LOAD_RENDER;

            err = FT.FT_Load_Glyph(face, glyphIndex, loadFlags);
            if (err != FT_Error.FT_Err_Ok)
                continue;

            FT_GlyphSlotRec_* slot = face->glyph;
            FT_Glyph_Metrics_ metrics = slot->metrics;

            float width = (int)metrics.width * metricsScale;
            float height = (int)metrics.height * metricsScale;
            float bearingX = (int)metrics.horiBearingX * metricsScale;
            float bearingY = (int)metrics.horiBearingY * metricsScale;
            float advance = (int)metrics.horiAdvance * metricsScale;

            GlyphBuildData buildData = new()
            {
                GlyphIndex = glyphIndex,
                Codepoint = codepoint,
                Width = width,
                Height = height,
                BearingX = bearingX,
                BearingY = bearingY,
                Advance = advance,
            };

            // For MSDF, extract outline contours
            if (settings.AtlasType == AtlasType.MSDF && width > 0 && height > 0)
            {
                buildData.Contours = ExtractOutline(slot);
            }

            // For SDF or Bitmap, render to bitmap
            if (settings.AtlasType != AtlasType.MSDF && width > 0 && height > 0)
            {
                // For SDF, render at higher resolution
                if (settings.AtlasType == AtlasType.SDF)
                {
                    int oversample = settings.SdfOversample;
                    err = FT.FT_Set_Pixel_Sizes(face, 0, (uint)(pointSize * oversample));
                    if (err == FT_Error.FT_Err_Ok)
                    {
                        err = FT.FT_Load_Glyph(face, glyphIndex, FT_LOAD.FT_LOAD_RENDER);
                        if (err == FT_Error.FT_Err_Ok)
                        {
                            FT_Bitmap_ bitmap = face->glyph->bitmap;
                            buildData.BitmapData = ExtractBitmap(bitmap);
                            buildData.BitmapWidth = (int)bitmap.width;
                            buildData.BitmapHeight = (int)bitmap.rows;
                        }
                        // Restore original size
                        FT.FT_Set_Pixel_Sizes(face, 0, (uint)pointSize);
                    }
                }
                else // Bitmap mode
                {
                    FT_Bitmap_ bitmap = slot->bitmap;
                    buildData.BitmapData = ExtractBitmap(bitmap);
                    buildData.BitmapWidth = (int)bitmap.width;
                    buildData.BitmapHeight = (int)bitmap.rows;
                }
            }

            glyphBuildList.Add(buildData);
        }

        if (glyphBuildList.Count == 0)
        {
            Debug.LogWarning($"[FontAssetImporter] No glyphs found in font for the selected character set.");
            return null;
        }

        // Phase 2: Compute atlas glyph cell sizes and pack
        int padding = settings.Padding;
        float pxRange = settings.PxRange;
        int extraPadding = settings.AtlasType == AtlasType.MSDF ? (int)MathF.Ceiling(pxRange) : 0;
        int totalPadding = padding + extraPadding;

        // Compute cell size for each glyph
        foreach (GlyphBuildData glyph in glyphBuildList)
        {
            if (glyph.Width <= 0 || glyph.Height <= 0)
            {
                glyph.CellWidth = 0;
                glyph.CellHeight = 0;
                continue;
            }

            // Cell size = glyph pixel size + padding on each side
            glyph.CellWidth = (int)MathF.Ceiling(glyph.Width) + totalPadding * 2;
            glyph.CellHeight = (int)MathF.Ceiling(glyph.Height) + totalPadding * 2;
        }

        // Determine atlas size
        int atlasSize = settings.AtlasResolution;
        if (atlasSize <= 0)
            atlasSize = EstimateAtlasSize(glyphBuildList, totalPadding);

        // Pack glyphs into atlas — retry with larger atlas if packing fails
        bool packed = false;
        while (!packed && atlasSize <= 8192)
        {
            RectPacker packer = new(atlasSize, atlasSize);
            packed = true;
            foreach (GlyphBuildData glyph in glyphBuildList)
            {
                if (glyph.CellWidth <= 0 || glyph.CellHeight <= 0)
                    continue;

                if (!packer.TryPack(glyph.CellWidth, glyph.CellHeight, out glyph.AtlasX, out glyph.AtlasY))
                {
                    packed = false;
                    atlasSize *= 2;
                    break;
                }
            }
        }

        if (!packed)
        {
            Debug.LogWarning("[FontAssetImporter] Could not pack all glyphs into atlas (max 8192). Some glyphs may be missing.");
        }

        // Phase 3: Generate atlas pixel data
        byte[] atlasPixels;
        TextureImageFormat textureFormat;

        switch (settings.AtlasType)
        {
            case AtlasType.MSDF:
                atlasPixels = GenerateMsdfAtlas(glyphBuildList, atlasSize, pxRange, totalPadding, pointSize);
                textureFormat = TextureImageFormat.Color4b; // RGBA, with MSDF in RGB and A=255
                break;

            case AtlasType.SDF:
                atlasPixels = GenerateSdfAtlas(glyphBuildList, atlasSize, pxRange, totalPadding, settings.SdfOversample);
                textureFormat = TextureImageFormat.Color4b; // Store SDF value in all RGBA channels
                break;

            default: // Bitmap
                atlasPixels = GenerateBitmapAtlas(glyphBuildList, atlasSize, totalPadding);
                textureFormat = TextureImageFormat.Color4b;
                break;
        }

        // Phase 4: Create Texture2D
        // For SDF/MSDF atlases, mipmaps are generally NOT used because the SDF shader's
        // fwidth-based anti-aliasing provides resolution-independent rendering. Standard
        // GPU bilinear mipmap generation corrupts distance fields by averaging signed
        // distances, which can eliminate thin features and create incorrect boundaries.
        // For Bitmap atlases, standard GPU mipmaps are fine and improve quality at small sizes.
        bool useMipmaps = settings.GenerateMipmaps && settings.AtlasType == AtlasType.Bitmap;
        Texture2D atlasTexture = new((uint)atlasSize, (uint)atlasSize, useMipmaps, textureFormat);
        atlasTexture.SetData<byte>(atlasPixels.AsMemory());

        if (useMipmaps)
        {
            atlasTexture.SetTextureFilters(TextureMin.LinearMipmapLinear, TextureMag.Linear);
        }
        else
        {
            atlasTexture.SetTextureFilters(TextureMin.Linear, TextureMag.Linear);
        }

        atlasTexture.SetWrapModes(TextureWrap.ClampToEdge, TextureWrap.ClampToEdge);
        atlasTexture.Name = Path.GetFileNameWithoutExtension(absolutePath) + "_Atlas";

        // Phase 5: Build glyph table and character table
        List<GlyphData> glyphDataList = [];
        Dictionary<uint, int> characterTable = [];
        Dictionary<uint, int> glyphIndexToTableIndex = []; // FreeType glyph index → our table index

        for (int i = 0; i < glyphBuildList.Count; i++)
        {
            GlyphBuildData g = glyphBuildList[i];
            int tableIndex = glyphDataList.Count;

            float atlasX = g.AtlasX + totalPadding;
            float atlasY = g.AtlasY + totalPadding;
            float cellW = MathF.Ceiling(g.Width);
            float cellH = MathF.Ceiling(g.Height);

            GlyphData data = new(
                GlyphIndex: g.GlyphIndex,
                Width: g.Width,
                Height: g.Height,
                BearingX: g.BearingX,
                BearingY: g.BearingY,
                Advance: g.Advance,
                AtlasX: atlasX / atlasSize,
                AtlasY: atlasY / atlasSize,
                AtlasWidth: cellW / atlasSize,
                AtlasHeight: cellH / atlasSize,
                Scale: 1f / pointSize
            );

            glyphDataList.Add(data);
            glyphIndexToTableIndex[g.GlyphIndex] = tableIndex;
        }

        // Map codepoints to table indices
        foreach (KeyValuePair<uint, uint> pair in codepointToGlyphIndex)
        {
            if (glyphIndexToTableIndex.TryGetValue(pair.Value, out int tableIdx))
                characterTable[pair.Key] = tableIdx;
        }

        // Phase 6: Extract kerning pairs
        Dictionary<ulong, float> kerningPairs = [];
        bool hasKerning = ((long)face->face_flags & (long)FT_FACE_FLAG.FT_FACE_FLAG_KERNING) != 0;

        if (hasKerning)
        {
            uint[] glyphIndices = glyphIndexToTableIndex.Keys.ToArray();
            foreach (uint left in glyphIndices)
            {
                foreach (uint right in glyphIndices)
                {
                    FT_Vector_ kerning;
                    err = FT.FT_Get_Kerning(face, left, right, FT_Kerning_Mode_.FT_KERNING_DEFAULT, &kerning);
                    if (err == FT_Error.FT_Err_Ok && kerning.x != 0)
                    {
                        float kernValue = (int)kerning.x * metricsScale;
                        if (MathF.Abs(kernValue) > 0.001f)
                        {
                            ulong key = ((ulong)left << 32) | right;
                            kerningPairs[key] = kernValue;
                        }
                    }
                }
            }
        }

        // Phase 7: Create FontAsset
        FontAsset fontAsset = ScriptableObject.CreateInstance<FontAsset>();
        fontAsset.Name = Path.GetFileNameWithoutExtension(absolutePath);
        fontAsset.SetAtlas(atlasTexture, atlasSize, atlasSize, settings.AtlasType, pxRange, padding);
        fontAsset.SetMetrics(pointSize, lineHeight, ascender, descender, 0f);
        fontAsset.SetGlyphData(glyphDataList.ToArray(), characterTable, kerningPairs);

        Debug.Log($"[FontAssetImporter] Imported '{fontAsset.Name}': {glyphDataList.Count} glyphs, " +
                  $"{kerningPairs.Count} kerning pairs, {atlasSize}x{atlasSize} {settings.AtlasType} atlas.");

        return fontAsset;
    }

    // ── Outline extraction ────────────────────────────────────

    private static unsafe List<SdfGenerator.Contour>? ExtractOutline(FT_GlyphSlotRec_* slot)
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

            // Read all points and tags for this contour
            float[] px = new float[contourLen];
            float[] py = new float[contourLen];
            byte[] tags = new byte[contourLen];

            for (int i = 0; i < contourLen; i++)
            {
                int pi = contourStart + i;
                // FreeType coordinates are in 26.6 fixed point (after FT_Load_Glyph with NO_HINTING)
                px[i] = (int)outline.points[pi].x / 64f;
                py[i] = (int)outline.points[pi].y / 64f;
                tags[i] = outline.tags[pi];
            }

            // Parse the contour into edge segments
            // FreeType uses: tag & 1 = on-curve, tag & 2 = cubic control
            int i2 = 0;
            while (i2 < contourLen)
            {
                int cur = i2;
                bool onCurve = (tags[cur] & 1) != 0;

                if (!onCurve)
                {
                    // Off-curve point at start — this shouldn't normally happen for well-formed outlines
                    i2++;
                    continue;
                }

                // Find the next on-curve point, consuming control points
                int next = (cur + 1) % contourLen;

                if ((tags[next] & 1) != 0)
                {
                    // Line segment
                    contour.Edges.Add(new SdfGenerator.LinearSegment(px[cur], py[cur], px[next], py[next]));
                    i2++;
                    continue;
                }

                // We have at least one off-curve point
                bool isCubic = (tags[next] & 2) != 0;

                if (isCubic)
                {
                    // Cubic bezier: on, cubic-off, cubic-off, on
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
                    // Quadratic bezier(s): may have implicit on-curve points between consecutive conic points
                    int ctrl = next;
                    int afterCtrl = (ctrl + 1) % contourLen;

                    if ((tags[afterCtrl] & 1) != 0)
                    {
                        // Simple quadratic: on, conic-off, on
                        contour.Edges.Add(new SdfGenerator.QuadraticBezierSegment(
                            px[cur], py[cur],
                            px[ctrl], py[ctrl],
                            px[afterCtrl], py[afterCtrl]));
                        i2 += 2;
                    }
                    else
                    {
                        // Two consecutive conic points — implicit on-curve midpoint
                        float midX = (px[ctrl] + px[afterCtrl]) * 0.5f;
                        float midY = (py[ctrl] + py[afterCtrl]) * 0.5f;

                        contour.Edges.Add(new SdfGenerator.QuadraticBezierSegment(
                            px[cur], py[cur],
                            px[ctrl], py[ctrl],
                            midX, midY));
                        // Don't advance past the second conic — it becomes the start of the next segment
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

    private static unsafe byte[] ExtractBitmap(FT_Bitmap_ bitmap)
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

    // ── Atlas generation ──────────────────────────────────────

    private static byte[] GenerateMsdfAtlas(
        List<GlyphBuildData> glyphs, int atlasSize, float pxRange, int totalPadding, int pointSize)
    {
        // RGBA atlas (R/G/B = MSDF channels, A = 255)
        byte[] atlas = new byte[atlasSize * atlasSize * 4];

        // Initialize to midpoint (128 = 0.5 distance = on the edge)
        for (int i = 0; i < atlas.Length; i += 4)
        {
            atlas[i + 0] = 0; // R
            atlas[i + 1] = 0; // G
            atlas[i + 2] = 0; // B
            atlas[i + 3] = 255; // A
        }

        foreach (GlyphBuildData glyph in glyphs)
        {
            if (glyph.Contours == null || glyph.CellWidth <= 0 || glyph.CellHeight <= 0)
                continue;

            int cellW = glyph.CellWidth;
            int cellH = glyph.CellHeight;

            // Scale: from font units to atlas pixel space within the cell
            float scale = (float)pointSize; // font units to pixels at pointSize
            float translateX = totalPadding - glyph.BearingX * scale / pointSize;
            float translateY = totalPadding + (glyph.Height - glyph.BearingY) * scale / pointSize;

            // Flip Y for atlas (atlas Y goes top-to-bottom, font Y goes bottom-to-top)
            // We need to adjust for the flipped coordinate system
            float scaleForMsdf = 1f; // Already in pixel space from FreeType metrics

            byte[] msdf = SdfGenerator.GenerateMsdf(
                glyph.Contours, cellW, cellH, pxRange,
                scaleForMsdf, translateX, translateY);

            // Copy MSDF data into atlas at the glyph's packed position
            for (int y = 0; y < cellH; y++)
            {
                int atlasY = glyph.AtlasY + y;
                if (atlasY < 0 || atlasY >= atlasSize) continue;

                for (int x = 0; x < cellW; x++)
                {
                    int atlasX = glyph.AtlasX + x;
                    if (atlasX < 0 || atlasX >= atlasSize) continue;

                    int srcIdx = (y * cellW + x) * 3;
                    int dstIdx = (atlasY * atlasSize + atlasX) * 4;

                    atlas[dstIdx + 0] = msdf[srcIdx + 0]; // R
                    atlas[dstIdx + 1] = msdf[srcIdx + 1]; // G
                    atlas[dstIdx + 2] = msdf[srcIdx + 2]; // B
                    atlas[dstIdx + 3] = 255;                // A
                }
            }
        }

        return atlas;
    }

    private static byte[] GenerateSdfAtlas(
        List<GlyphBuildData> glyphs, int atlasSize, float pxRange, int totalPadding, int oversample)
    {
        // RGBA atlas with SDF value in all channels
        byte[] atlas = new byte[atlasSize * atlasSize * 4];

        foreach (GlyphBuildData glyph in glyphs)
        {
            if (glyph.BitmapData == null || glyph.CellWidth <= 0 || glyph.CellHeight <= 0)
                continue;

            int cellW = glyph.CellWidth;
            int cellH = glyph.CellHeight;

            // Generate SDF from the high-res bitmap
            float spread = pxRange * oversample; // spread in high-res bitmap pixels
            byte[] sdf = SdfGenerator.GenerateSdfFromBitmap(
                glyph.BitmapData, glyph.BitmapWidth, glyph.BitmapHeight,
                cellW, cellH, spread);

            // Copy into atlas
            for (int y = 0; y < cellH; y++)
            {
                int atlasY = glyph.AtlasY + y;
                if (atlasY < 0 || atlasY >= atlasSize) continue;

                for (int x = 0; x < cellW; x++)
                {
                    int atlasX = glyph.AtlasX + x;
                    if (atlasX < 0 || atlasX >= atlasSize) continue;

                    byte val = sdf[y * cellW + x];
                    int dstIdx = (atlasY * atlasSize + atlasX) * 4;
                    atlas[dstIdx + 0] = val; // R
                    atlas[dstIdx + 1] = val; // G
                    atlas[dstIdx + 2] = val; // B
                    atlas[dstIdx + 3] = 255; // A
                }
            }
        }

        return atlas;
    }

    private static byte[] GenerateBitmapAtlas(
        List<GlyphBuildData> glyphs, int atlasSize, int totalPadding)
    {
        byte[] atlas = new byte[atlasSize * atlasSize * 4];

        foreach (GlyphBuildData glyph in glyphs)
        {
            if (glyph.BitmapData == null || glyph.CellWidth <= 0 || glyph.CellHeight <= 0)
                continue;

            // Copy bitmap into cell (centered with padding)
            int srcW = glyph.BitmapWidth;
            int srcH = glyph.BitmapHeight;

            for (int y = 0; y < srcH && y < glyph.CellHeight; y++)
            {
                int atlasY = glyph.AtlasY + totalPadding + y;
                if (atlasY < 0 || atlasY >= atlasSize) continue;

                for (int x = 0; x < srcW && x < glyph.CellWidth; x++)
                {
                    int atlasX = glyph.AtlasX + totalPadding + x;
                    if (atlasX < 0 || atlasX >= atlasSize) continue;

                    byte val = glyph.BitmapData[y * srcW + x];
                    int dstIdx = (atlasY * atlasSize + atlasX) * 4;
                    atlas[dstIdx + 0] = 255; // R (white)
                    atlas[dstIdx + 1] = 255; // G
                    atlas[dstIdx + 2] = 255; // B
                    atlas[dstIdx + 3] = val; // A = coverage
                }
            }
        }

        return atlas;
    }

    // ── Helpers ───────────────────────────────────────────────

    private static int EstimateAtlasSize(List<GlyphBuildData> glyphs, int padding)
    {
        // Estimate total area needed and find a power-of-two size that fits
        int totalArea = 0;
        foreach (GlyphBuildData g in glyphs)
            totalArea += (g.CellWidth + padding) * (g.CellHeight + padding);

        // Start from smallest power-of-two that could fit
        int size = 256;
        while (size * size < totalArea * 1.3f && size < 8192) // 30% overhead for packing inefficiency
            size *= 2;

        return size;
    }

    // ── Build data ────────────────────────────────────────────

    private class GlyphBuildData
    {
        public uint GlyphIndex;
        public uint Codepoint;
        public float Width, Height;
        public float BearingX, BearingY;
        public float Advance;
        public int CellWidth, CellHeight;
        public int AtlasX, AtlasY;
        public List<SdfGenerator.Contour>? Contours;
        public byte[]? BitmapData;
        public int BitmapWidth, BitmapHeight;
    }
}
