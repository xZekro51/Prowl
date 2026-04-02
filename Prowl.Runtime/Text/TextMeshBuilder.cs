// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.Collections.Generic;

using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Runtime.Text;

/// <summary>
/// Converts a <see cref="TextLayout"/> into a <see cref="Mesh"/> by generating
/// one quad (4 vertices, 6 indices) per glyph, plus geometry for underline,
/// strikethrough, and background highlight decorations.
/// </summary>
public static class TextMeshBuilder
{
    /// <summary>
    /// Builds (or rebuilds) a mesh from a text layout.
    /// </summary>
    /// <param name="layout">The positioned glyph layout produced by <see cref="TextShaper"/>.</param>
    /// <param name="font">The primary font asset (used to look up glyph atlas rects).</param>
    /// <param name="mesh">An existing mesh to reuse, or <c>null</c> to create a new one.</param>
    /// <param name="subMeshFontIndices">
    /// When the layout uses multiple font atlases (fallback fonts), this returns an array
    /// mapping each sub-mesh index to its <see cref="GlyphPlacement.FontAssetIndex"/>.
    /// <c>null</c> when only the primary font atlas is used.
    /// </param>
    /// <returns>The populated mesh.</returns>
    public static Mesh Build(TextLayout layout, FontAsset font, Mesh? mesh, out int[]? subMeshFontIndices)
    {
        subMeshFontIndices = null;

        if (mesh == null)
            mesh = new Mesh();
        else
            mesh.Clear();

        GlyphPlacement[] glyphs = layout.Glyphs;

        if (glyphs == null || glyphs.Length == 0)
        {
            // Empty text — set minimal valid mesh data
            mesh.Vertices = [Float3.Zero];
            mesh.UV = [Float2.Zero];
            mesh.Colors = [Color.White];
            mesh.Normals = [new Float3(0, 0, -1)];
            mesh.Indices = [0, 0, 0];
            mesh.IndexFormat = IndexFormat.UInt16;
            mesh.RecalculateBounds();
            return mesh;
        }

        // ── Determine which font asset indices are present ─────
        // Collect unique FontAssetIndex values (preserving order of first appearance).
        // Index 0 (primary) is always first.
        List<int>? fallbackIndices = null;
        for (int g = 0; g < glyphs.Length; g++)
        {
            int fai = glyphs[g].FontAssetIndex;
            if (fai != 0)
            {
                fallbackIndices ??= [];
                if (!fallbackIndices.Contains(fai))
                    fallbackIndices.Add(fai);
            }
        }

        bool hasMultipleAtlases = fallbackIndices != null && fallbackIndices.Count > 0;

        // Estimate sizes: 4 verts/glyph + decoration geometry
        int estimatedGlyphs = glyphs.Length;
        int vertCapacity = estimatedGlyphs * 4 + 128; // Extra for decorations + marks
        int indexCapacity = estimatedGlyphs * 6 + 192;

        Float3[] positions = ArrayPool<Float3>.Shared.Rent(vertCapacity);
        Float2[] uvs = ArrayPool<Float2>.Shared.Rent(vertCapacity);
        Float2[] uv2s = ArrayPool<Float2>.Shared.Rent(vertCapacity);
        Color[] colors = ArrayPool<Color>.Shared.Rent(vertCapacity);
        Float3[] normals = ArrayPool<Float3>.Shared.Rent(vertCapacity);
        uint[] indices = ArrayPool<uint>.Shared.Rent(indexCapacity);

        int vertCount = 0;
        int indexCount = 0;

        try
        {
            // ── Sub-mesh 0: mark geometry + primary glyphs + decorations ──

            // Mark (background highlight) geometry — part of primary atlas sub-mesh
            GenerateMarkGeometry(
                layout, font,
                positions, uvs, uv2s, colors, normals, indices,
                ref vertCount, ref indexCount);

            // Primary font glyph quads (FontAssetIndex == 0)
            EmitGlyphQuads(glyphs, font, 0,
                positions, uvs, uv2s, colors, normals, indices,
                ref vertCount, ref indexCount);

            // Underline / strikethrough decorations — part of primary atlas sub-mesh
            GenerateDecorations(
                layout, font,
                positions, uvs, uv2s, colors, normals, indices,
                ref vertCount, ref indexCount);

            int primaryIndexCount = indexCount;

            // ── Sub-meshes 1..N: fallback font glyph quads ──────────────

            List<(int fontAssetIndex, int indexStart, int indexCount)>? fallbackRanges = null;

            if (hasMultipleAtlases)
            {
                for (int f = 0; f < fallbackIndices!.Count; f++)
                {
                    int fai = fallbackIndices[f];
                    int rangeStart = indexCount;

                    EmitGlyphQuads(glyphs, font, fai,
                        positions, uvs, uv2s, colors, normals, indices,
                        ref vertCount, ref indexCount);

                    int rangeCount = indexCount - rangeStart;
                    if (rangeCount > 0)
                    {
                        fallbackRanges ??= [];
                        fallbackRanges.Add((fai, rangeStart, rangeCount));
                    }
                }
            }

            // ── Finalize mesh ──────────────────────────────────

            if (vertCount == 0)
            {
                mesh.Vertices = [Float3.Zero];
                mesh.UV = [Float2.Zero];
                mesh.Colors = [Color.White];
                mesh.Normals = [new Float3(0, 0, -1)];
                mesh.Indices = [0, 0, 0];
                mesh.IndexFormat = IndexFormat.UInt16;
                mesh.RecalculateBounds();
                return mesh;
            }

            // Copy to correctly sized arrays
            Float3[] finalPositions = new Float3[vertCount];
            Float2[] finalUVs = new Float2[vertCount];
            Float2[] finalUV2s = new Float2[vertCount];
            Color[] finalColors = new Color[vertCount];
            Float3[] finalNormals = new Float3[vertCount];
            uint[] finalIndices = new uint[indexCount];

            Array.Copy(positions, finalPositions, vertCount);
            Array.Copy(uvs, finalUVs, vertCount);
            Array.Copy(uv2s, finalUV2s, vertCount);
            Array.Copy(colors, finalColors, vertCount);
            Array.Copy(normals, finalNormals, vertCount);
            Array.Copy(indices, finalIndices, indexCount);

            mesh.IndexFormat = vertCount <= 65535 ? IndexFormat.UInt16 : IndexFormat.UInt32;
            mesh.Vertices = finalPositions;
            mesh.UV = finalUVs;
            mesh.UV2 = finalUV2s;
            mesh.Colors = finalColors;
            mesh.Normals = finalNormals;
            mesh.Indices = finalIndices;

            // Set up sub-mesh descriptors
            if (fallbackRanges != null && fallbackRanges.Count > 0)
            {
                int totalSubMeshes = 1 + fallbackRanges.Count;
                mesh.SetSubMeshCount(totalSubMeshes);
                mesh.SetSubMesh(0, new SubMeshDescriptor(0, primaryIndexCount));

                // Build the mapping array: subMeshFontIndices[i] = FontAssetIndex
                int[] mapping = new int[totalSubMeshes];
                mapping[0] = 0; // Primary font

                for (int i = 0; i < fallbackRanges.Count; i++)
                {
                    (int fai, int start, int count) = fallbackRanges[i];
                    mesh.SetSubMesh(i + 1, new SubMeshDescriptor(start, count));
                    mapping[i + 1] = fai;
                }

                subMeshFontIndices = mapping;
            }
            else
            {
                mesh.SetSubMeshCount(0);
            }

            mesh.RecalculateBounds();
        }
        finally
        {
            ArrayPool<Float3>.Shared.Return(positions);
            ArrayPool<Float2>.Shared.Return(uvs);
            ArrayPool<Float2>.Shared.Return(uv2s);
            ArrayPool<Color>.Shared.Return(colors);
            ArrayPool<Float3>.Shared.Return(normals);
            ArrayPool<uint>.Shared.Return(indices);
        }

        return mesh;
    }

    /// <summary>
    /// Convenience overload that discards the sub-mesh font index mapping.
    /// </summary>
    public static Mesh Build(TextLayout layout, FontAsset font, Mesh? mesh = null)
    {
        return Build(layout, font, mesh, out _);
    }

    /// <summary>
    /// Emits glyph quads for all glyphs matching the specified <paramref name="fontAssetIndex"/>.
    /// </summary>
    private static void EmitGlyphQuads(
        GlyphPlacement[] glyphs,
        FontAsset primaryFont,
        int fontAssetIndex,
        Float3[] positions, Float2[] uvs, Float2[] uv2s,
        Color[] colors, Float3[] normals, uint[] indices,
        ref int vertCount, ref int indexCount)
    {
        for (int g = 0; g < glyphs.Length; g++)
        {
            GlyphPlacement placement = glyphs[g];
            if (placement.FontAssetIndex != fontAssetIndex)
                continue;

            // Resolve the font asset for this glyph (primary or fallback)
            FontAsset? glyphFont = primaryFont.GetFontByIndex(placement.FontAssetIndex);
            if (glyphFont.IsNotValid())
                continue;

            IReadOnlyList<GlyphData> glyphTable = glyphFont.GlyphTable;
            if (placement.GlyphIndex < 0 || placement.GlyphIndex >= glyphTable.Count)
                continue;

            GlyphData glyph = glyphTable[placement.GlyphIndex];

            // Skip zero-size glyphs (spaces, control characters)
            if (glyph.Width <= 0 || glyph.Height <= 0)
                continue;

            float scaleX = placement.Scale.X;
            float scaleY = placement.Scale.Y;

            // Quad corners in local space
            float left = placement.Position.X;
            float top = placement.Position.Y;
            float right = left + glyph.Width * scaleX;
            float bottom = top - glyph.Height * scaleY;

            // Atlas UVs (normalized)
            float glyphAtlasW = glyphFont.AtlasWidth > 0 ? glyphFont.AtlasWidth : 1f;
            float glyphAtlasH = glyphFont.AtlasHeight > 0 ? glyphFont.AtlasHeight : 1f;
            float uvLeft = glyph.AtlasX / glyphAtlasW;
            float uvRight = (glyph.AtlasX + glyph.AtlasWidth) / glyphAtlasW;
            float uvTop = glyph.AtlasY / glyphAtlasH;
            float uvBottom = (glyph.AtlasY + glyph.AtlasHeight) / glyphAtlasH;

            // Ensure capacity
            if (vertCount + 4 > positions.Length)
            {
                GrowArrays(ref positions, ref uvs, ref uv2s, ref colors, ref normals, vertCount);
            }
            if (indexCount + 6 > indices.Length)
            {
                GrowArray(ref indices, indexCount);
            }

            uint baseVert = (uint)vertCount;
            Float3 normal = new(0, 0, -1);

            // TL
            positions[vertCount] = new Float3(left, top, 0);
            uvs[vertCount] = new Float2(uvLeft, uvTop);
            uv2s[vertCount] = Float2.Zero;
            colors[vertCount] = placement.Color;
            normals[vertCount] = normal;
            vertCount++;

            // TR
            positions[vertCount] = new Float3(right, top, 0);
            uvs[vertCount] = new Float2(uvRight, uvTop);
            uv2s[vertCount] = Float2.Zero;
            colors[vertCount] = placement.Color;
            normals[vertCount] = normal;
            vertCount++;

            // BR
            positions[vertCount] = new Float3(right, bottom, 0);
            uvs[vertCount] = new Float2(uvRight, uvBottom);
            uv2s[vertCount] = Float2.Zero;
            colors[vertCount] = placement.Color;
            normals[vertCount] = normal;
            vertCount++;

            // BL
            positions[vertCount] = new Float3(left, bottom, 0);
            uvs[vertCount] = new Float2(uvLeft, uvBottom);
            uv2s[vertCount] = Float2.Zero;
            colors[vertCount] = placement.Color;
            normals[vertCount] = normal;
            vertCount++;

            // Two triangles: TL-TR-BR, TL-BR-BL
            indices[indexCount++] = baseVert;
            indices[indexCount++] = baseVert + 1;
            indices[indexCount++] = baseVert + 2;
            indices[indexCount++] = baseVert;
            indices[indexCount++] = baseVert + 2;
            indices[indexCount++] = baseVert + 3;
        }
    }

    private static void GenerateMarkGeometry(
        TextLayout layout,
        FontAsset font,
        Float3[] positions,
        Float2[] uvs,
        Float2[] uv2s,
        Color[] colors,
        Float3[] normals,
        uint[] indices,
        ref int vertCount,
        ref int indexCount)
    {
        GlyphPlacement[] glyphs = layout.Glyphs;
        if (glyphs == null || glyphs.Length == 0)
            return;

        int runStart = -1;
        Color runMarkColor = default;
        int runLine = -1;

        for (int g = 0; g <= glyphs.Length; g++)
        {
            Color? currentMark = g < glyphs.Length ? glyphs[g].MarkColor : null;
            int currentLine = g < glyphs.Length ? glyphs[g].LineIndex : -1;

            bool endRun = runStart >= 0 &&
                (!currentMark.HasValue || !currentMark.Value.Equals(runMarkColor) || currentLine != runLine);

            if (endRun)
            {
                int runEnd = g - 1;
                if (runEnd >= runStart)
                {
                    float xStart = glyphs[runStart].Position.X;
                    float xEnd = glyphs[runEnd].Position.X;

                    FontAsset? glyphFont = font.GetFontByIndex(glyphs[runEnd].FontAssetIndex);
                    if (glyphFont.IsValid())
                    {
                        IReadOnlyList<GlyphData> gt = glyphFont.GlyphTable;
                        if (glyphs[runEnd].GlyphIndex >= 0 && glyphs[runEnd].GlyphIndex < gt.Count)
                            xEnd += gt[glyphs[runEnd].GlyphIndex].Advance * glyphs[runEnd].Scale.X;
                    }

                    float baseline = runLine >= 0 && runLine < layout.Lines.Length
                        ? layout.Lines[runLine].Baseline
                        : 0f;
                    float ascender = runLine >= 0 && runLine < layout.Lines.Length
                        ? layout.Lines[runLine].Ascender
                        : 0f;
                    float descender = runLine >= 0 && runLine < layout.Lines.Length
                        ? layout.Lines[runLine].Descender
                        : 0f;

                    float yCenter = baseline + (ascender + descender) * 0.5f;
                    float thickness = ascender - descender;

                    EmitDecorationQuad(
                        xStart, xEnd,
                        yCenter, thickness, runMarkColor,
                        positions, uvs, uv2s, colors, normals, indices,
                        ref vertCount, ref indexCount);
                }
                runStart = -1;
            }

            if (g < glyphs.Length && currentMark.HasValue)
            {
                if (runStart < 0)
                {
                    runStart = g;
                    runMarkColor = currentMark.Value;
                    runLine = currentLine;
                }
            }
        }
    }

    private static void GenerateDecorations(
        TextLayout layout,
        FontAsset font,
        Float3[] positions,
        Float2[] uvs,
        Float2[] uv2s,
        Color[] colors,
        Float3[] normals,
        uint[] indices,
        ref int vertCount,
        ref int indexCount)
    {
        GlyphPlacement[] glyphs = layout.Glyphs;
        if (glyphs == null || glyphs.Length == 0)
            return;

        float scale = glyphs.Length > 0 ? glyphs[0].Scale.Y : 1f;
        float underlineY = font.Descender * scale * 0.5f;
        float strikeY = font.Ascender * scale * 0.3f;
        float lineThickness = Maths.Max(scale * 0.05f, 0.01f);

        // Track decoration runs
        int runStart = -1;
        TextStyleFlags runFlags = TextStyleFlags.None;
        Color runColor = Color.White;
        int runLine = -1;

        for (int g = 0; g <= glyphs.Length; g++)
        {
            TextStyleFlags currentFlags = TextStyleFlags.None;
            Color currentColor = Color.White;
            int currentLine = -1;

            if (g < glyphs.Length)
            {
                currentFlags = glyphs[g].StyleFlags & (TextStyleFlags.Underline | TextStyleFlags.Strikethrough);
                currentColor = glyphs[g].Color;
                currentLine = glyphs[g].LineIndex;
            }

            bool flagsChanged = currentFlags != runFlags || currentLine != runLine;

            if (flagsChanged && runStart >= 0)
            {
                // Emit decoration quads for the previous run
                int runEnd = g - 1;
                if (runEnd >= runStart)
                {
                    float xStart = glyphs[runStart].Position.X;
                    float xEnd = glyphs[runEnd].Position.X;
                    // Approximate the right edge of the last glyph
                    FontAsset? decoFont = font.GetFontByIndex(glyphs[runEnd].FontAssetIndex);
                    IReadOnlyList<GlyphData> gt = decoFont.IsValid() ? decoFont.GlyphTable : font.GlyphTable;
                    if (glyphs[runEnd].GlyphIndex >= 0 && glyphs[runEnd].GlyphIndex < gt.Count)
                        xEnd += gt[glyphs[runEnd].GlyphIndex].Advance * glyphs[runEnd].Scale.X;

                    float baseline = runLine >= 0 && runLine < layout.Lines.Length
                        ? layout.Lines[runLine].Baseline
                        : 0f;

                    if ((runFlags & TextStyleFlags.Underline) != 0)
                    {
                        EmitDecorationQuad(
                            xStart, xEnd,
                            baseline + underlineY,
                            lineThickness, runColor,
                            positions, uvs, uv2s, colors, normals, indices,
                            ref vertCount, ref indexCount);
                    }

                    if ((runFlags & TextStyleFlags.Strikethrough) != 0)
                    {
                        EmitDecorationQuad(
                            xStart, xEnd,
                            baseline + strikeY,
                            lineThickness, runColor,
                            positions, uvs, uv2s, colors, normals, indices,
                            ref vertCount, ref indexCount);
                    }
                }
                runStart = -1;
            }

            if (g < glyphs.Length && currentFlags != TextStyleFlags.None)
            {
                if (runStart < 0)
                {
                    runStart = g;
                    runFlags = currentFlags;
                    runColor = currentColor;
                    runLine = currentLine;
                }
            }
        }
    }

    private static void EmitDecorationQuad(
        float xStart, float xEnd, float yCenter, float thickness,
        Color color,
        Float3[] positions, Float2[] uvs, Float2[] uv2s, Color[] colors, Float3[] normals, uint[] indices,
        ref int vertCount, ref int indexCount)
    {
        // Ensure capacity
        if (vertCount + 4 > positions.Length)
            return; // Skip if we'd overflow (safety)
        if (indexCount + 6 > indices.Length)
            return;

        float halfThick = thickness * 0.5f;
        uint baseVert = (uint)vertCount;
        Float3 normal = new(0, 0, -1);

        // Use UV (0.5, 0.5) which should be inside the SDF region for solid fill
        Float2 solidUV = new(0.5f, 0.5f);

        positions[vertCount] = new Float3(xStart, yCenter + halfThick, 0);
        uvs[vertCount] = solidUV;
        uv2s[vertCount] = Float2.Zero;
        colors[vertCount] = color;
        normals[vertCount] = normal;
        vertCount++;

        positions[vertCount] = new Float3(xEnd, yCenter + halfThick, 0);
        uvs[vertCount] = solidUV;
        uv2s[vertCount] = Float2.Zero;
        colors[vertCount] = color;
        normals[vertCount] = normal;
        vertCount++;

        positions[vertCount] = new Float3(xEnd, yCenter - halfThick, 0);
        uvs[vertCount] = solidUV;
        uv2s[vertCount] = Float2.Zero;
        colors[vertCount] = color;
        normals[vertCount] = normal;
        vertCount++;

        positions[vertCount] = new Float3(xStart, yCenter - halfThick, 0);
        uvs[vertCount] = solidUV;
        uv2s[vertCount] = Float2.Zero;
        colors[vertCount] = color;
        normals[vertCount] = normal;
        vertCount++;

        indices[indexCount++] = baseVert;
        indices[indexCount++] = baseVert + 1;
        indices[indexCount++] = baseVert + 2;
        indices[indexCount++] = baseVert;
        indices[indexCount++] = baseVert + 2;
        indices[indexCount++] = baseVert + 3;
    }

    private static void GrowArrays(
        ref Float3[] positions, ref Float2[] uvs, ref Float2[] uv2s,
        ref Color[] colors, ref Float3[] normals, int currentCount)
    {
        int newSize = positions.Length * 2;

        Float3[] newPositions = ArrayPool<Float3>.Shared.Rent(newSize);
        Float2[] newUVs = ArrayPool<Float2>.Shared.Rent(newSize);
        Float2[] newUV2s = ArrayPool<Float2>.Shared.Rent(newSize);
        Color[] newColors = ArrayPool<Color>.Shared.Rent(newSize);
        Float3[] newNormals = ArrayPool<Float3>.Shared.Rent(newSize);

        Array.Copy(positions, newPositions, currentCount);
        Array.Copy(uvs, newUVs, currentCount);
        Array.Copy(uv2s, newUV2s, currentCount);
        Array.Copy(colors, newColors, currentCount);
        Array.Copy(normals, newNormals, currentCount);

        ArrayPool<Float3>.Shared.Return(positions);
        ArrayPool<Float2>.Shared.Return(uvs);
        ArrayPool<Float2>.Shared.Return(uv2s);
        ArrayPool<Color>.Shared.Return(colors);
        ArrayPool<Float3>.Shared.Return(normals);

        positions = newPositions;
        uvs = newUVs;
        uv2s = newUV2s;
        colors = newColors;
        normals = newNormals;
    }

    private static void GrowArray(ref uint[] array, int currentCount)
    {
        int newSize = array.Length * 2;
        uint[] newArray = ArrayPool<uint>.Shared.Rent(newSize);
        Array.Copy(array, newArray, currentCount);
        ArrayPool<uint>.Shared.Return(array);
        array = newArray;
    }
}
