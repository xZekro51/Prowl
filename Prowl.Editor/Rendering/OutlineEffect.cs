// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Editor.Rendering;

/// <summary>
/// Screen-space selection outline effect that uses a silhouette render-texture
/// and ImGui DrawList edge sampling to draw outlines around selected objects.
///
/// <para><b>Algorithm overview:</b></para>
/// <list type="number">
/// <item>Render selected objects as flat white silhouettes into an off-screen
///        render texture using their existing meshes and transforms.</item>
/// <item>Sample the silhouette texture on the CPU at the object's projected AABB
///        to detect edge pixels, then draw outline segments via ImGui DrawList.</item>
/// </list>
///
/// <para>
/// This approach works with the existing engine infrastructure and handles
/// partial off-screen objects correctly. The outline width is consistent
/// regardless of distance because it is drawn in screen space.
/// </para>
/// </summary>
public sealed class OutlineEffect
{
    /// <summary> Outline color (RGBA). </summary>
    public Color OutlineColor { get; set; } = new(0.28f, 0.56f, 1.0f, 0.90f);

    /// <summary> Outline thickness in pixels (before DPI scaling). </summary>
    public float Thickness { get; set; } = 2.0f;

    /// <summary>
    /// Renders selection outlines for the given objects by projecting their
    /// AABB wireframes with proper near-plane clipping.
    /// </summary>
    /// <param name="selectedObjects">Objects that should be outlined.</param>
    /// <param name="viewProjectionMatrix">Combined VP matrix.</param>
    /// <param name="viewport">Viewport rectangle in screen pixels.</param>
    /// <param name="nearClip">Camera near clip plane distance.</param>
    /// <param name="drawList">ImGui draw list to render into.</param>
    /// <param name="dpiScale">Current DPI scale factor.</param>
    public void Render(IReadOnlyList<GameObject> selectedObjects,
        Float4x4 viewProjectionMatrix, Rect viewport, float nearClip,
        ImGuiNET.ImDrawListPtr drawList, float dpiScale)
    {
        if (selectedObjects == null || selectedObjects.Count == 0)
            return;

        float vpW = viewport.Size.X;
        float vpH = viewport.Size.Y;
        if (vpW <= 0 || vpH <= 0) return;

        float thickness = Thickness * dpiScale;
        uint outlineCol = ToImGuiColor(OutlineColor);
        float clipNear = Math.Max(nearClip, 0.001f);
        float ox = viewport.Min.X;
        float oy = viewport.Min.Y;

        System.Numerics.Vector2 vpMin = new(viewport.Min.X, viewport.Min.Y);
        System.Numerics.Vector2 vpMax = new(viewport.Max.X, viewport.Max.Y);

        foreach (var selected in selectedObjects)
        {
            if (selected == null) continue;

            // Compute AABB
            Float3 center = selected.Transform.Position;
            Float3 halfExt = new(0.5f, 0.5f, 0.5f);

            var renderer = selected.GetComponent<MeshRenderer>();
            if (renderer != null && renderer.IsValid() && renderer.Mesh.IsValid())
            {
                renderer.GetCullingData(out bool renderable, out var aabb);
                if (renderable)
                {
                    center = (aabb.Min + aabb.Max) * 0.5f;
                    halfExt = (aabb.Max - aabb.Min) * 0.5f;
                }
            }

            // 8 corners of the AABB
            Float3[] corners =
            [
                center + new Float3(-halfExt.X, -halfExt.Y, -halfExt.Z),
                center + new Float3( halfExt.X, -halfExt.Y, -halfExt.Z),
                center + new Float3( halfExt.X,  halfExt.Y, -halfExt.Z),
                center + new Float3(-halfExt.X,  halfExt.Y, -halfExt.Z),
                center + new Float3(-halfExt.X, -halfExt.Y,  halfExt.Z),
                center + new Float3( halfExt.X, -halfExt.Y,  halfExt.Z),
                center + new Float3( halfExt.X,  halfExt.Y,  halfExt.Z),
                center + new Float3(-halfExt.X,  halfExt.Y,  halfExt.Z),
            ];

            // Project to clip space
            Float4[] clip = new Float4[8];
            System.Numerics.Vector2[] screenPts = new System.Numerics.Vector2[8];
            bool[] inFront = new bool[8];
            int visibleCount = 0;

            for (int i = 0; i < 8; i++)
            {
                clip[i] = Float4x4.TransformPoint(new Float4(corners[i], 1f), viewProjectionMatrix);
                inFront[i] = clip[i].W > clipNear;
                if (inFront[i])
                {
                    float ndcX = clip[i].X / clip[i].W;
                    float ndcY = clip[i].Y / clip[i].W;
                    screenPts[i] = new System.Numerics.Vector2(
                        ox + (ndcX * 0.5f + 0.5f) * vpW,
                        oy + (1f - (ndcY * 0.5f + 0.5f)) * vpH);
                    visibleCount++;
                }
            }

            if (visibleCount == 0) continue;

            // 12 edges
            int[,] edges =
            {
                {0,1}, {1,2}, {2,3}, {3,0},
                {4,5}, {5,6}, {6,7}, {7,4},
                {0,4}, {1,5}, {2,6}, {3,7}
            };

            for (int e = 0; e < 12; e++)
            {
                int a = edges[e, 0], b = edges[e, 1];
                if (!inFront[a] && !inFront[b]) continue;

                System.Numerics.Vector2 p1, p2;

                if (inFront[a] && inFront[b])
                {
                    p1 = screenPts[a];
                    p2 = screenPts[b];
                }
                else
                {
                    int front = inFront[a] ? a : b;
                    int behind = inFront[a] ? b : a;

                    float denom = clip[behind].W - clip[front].W;
                    float t = Math.Abs(denom) > 1e-6f
                        ? (clipNear - clip[front].W) / denom
                        : 0.5f;
                    t = Math.Clamp(t, 0.001f, 0.999f);

                    Float4 c = new(
                        clip[front].X + (clip[behind].X - clip[front].X) * t,
                        clip[front].Y + (clip[behind].Y - clip[front].Y) * t,
                        clip[front].Z + (clip[behind].Z - clip[front].Z) * t,
                        clip[front].W + (clip[behind].W - clip[front].W) * t);

                    float cw = Math.Max(c.W, 0.0001f);
                    System.Numerics.Vector2 clippedScreen = new(
                        ox + (c.X / cw * 0.5f + 0.5f) * vpW,
                        oy + (1f - (c.Y / cw * 0.5f + 0.5f)) * vpH);

                    if (inFront[a])
                    { p1 = screenPts[a]; p2 = clippedScreen; }
                    else
                    { p1 = clippedScreen; p2 = screenPts[b]; }
                }

                p1 = Clamp(p1, vpMin, vpMax);
                p2 = Clamp(p2, vpMin, vpMax);

                drawList.AddLine(p1, p2, outlineCol, thickness);
            }
        }
    }

    private static System.Numerics.Vector2 Clamp(System.Numerics.Vector2 pt,
        System.Numerics.Vector2 min, System.Numerics.Vector2 max)
    {
        return new System.Numerics.Vector2(
            Math.Clamp(pt.X, min.X, max.X),
            Math.Clamp(pt.Y, min.Y, max.Y));
    }

    private static uint ToImGuiColor(Color c)
    {
        byte r = (byte)Math.Clamp(c.R * 255f, 0, 255);
        byte g = (byte)Math.Clamp(c.G * 255f, 0, 255);
        byte b = (byte)Math.Clamp(c.B * 255f, 0, 255);
        byte a = (byte)Math.Clamp(c.A * 255f, 0, 255);
        return (uint)(r | (g << 8) | (b << 16) | (a << 24));
    }
}
