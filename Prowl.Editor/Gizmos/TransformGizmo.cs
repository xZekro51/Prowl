// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using ImGuiNET;
using Prowl.Editor.Rendering;
using Prowl.Editor.Services;
using Prowl.Editor.Undo;
using Prowl.Editor.Undo.Commands;
using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Editor.Gizmos;

/// <summary>
/// Draws 2D transform-gizmo handles over the scene viewport using ImGui
/// DrawList and updates the selected object's transform when manipulated.
/// Modes: Translate, Rotate, Scale — switched via keyboard 1/2/3 or toolbar.
/// Enhanced with saturated axis colors, transparency, hover/active highlights,
/// rotation circles, and a corner axis indicator.
/// </summary>
public sealed class TransformGizmo
{
    private static float DesiredScreenLength => 100f * Game.DpiScale;
    private static float HandleThick  => 3f * Game.DpiScale;
    private static float HandleHitSize => 16f * Game.DpiScale;

    // ── High-saturation axis colors ──
    // Normal (full opacity for active axis, semi-transparent for inactive)
    private static Vector4 XColorVec   = new(0.95f, 0.15f, 0.15f, 1.00f);
    private static Vector4 YColorVec   = new(0.30f, 0.90f, 0.15f, 1.00f);
    private static Vector4 ZColorVec   = new(0.20f, 0.40f, 0.95f, 1.00f);

    // Hovered / active (brighter, fully opaque)
    private static Vector4 XColorHiVec = new(1.00f, 0.40f, 0.40f, 1.00f);
    private static Vector4 YColorHiVec = new(0.50f, 1.00f, 0.40f, 1.00f);
    private static Vector4 ZColorHiVec = new(0.45f, 0.60f, 1.00f, 1.00f);

    // Dimmed (used for non-hovered axes when another axis is active)
    private static Vector4 DimAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);

    public GizmoMode Mode { get; set; } = GizmoMode.Translate;
    public GizmoOrientation Orientation { get; set; } = GizmoOrientation.World;

    /// <summary> True while the user is dragging a gizmo handle. </summary>
    public bool IsActive => _activeAxis >= 0;

    /// <summary> True when the mouse is over the scene orientation gizmo in the corner. </summary>
    public bool IsSceneGizmoHovered { get; private set; }

    // Drag state
    private int _activeAxis = -1;        // -1=none, 0=X, 1=Y, 2=Z, 3=XY, 4=XZ, 5=YZ, 6=Uniform
    private Float2 _dragStart;
    private Float3 _dragStartValue;

    // Undo state: captured at drag start, committed at drag end
    private Float3 _undoLocalPos;
    private Float3 _undoLocalEuler;
    private Float3 _undoLocalScale;

    // Start rotation (world quaternion) for rotation gizmo
    private Prowl.Vector.Quaternion _dragStartRotation;

    // Axis direction captured at drag start (prevents drift when in Local orientation)
    private Float3 _dragStartAxisDir;

    // Second axis direction for plane drag
    private Float3 _dragStartAxisDir2;

    // Screen-space directions captured at drag start (for plane drags)
    private Float2 _dragStartScreenDir;
    private Float2 _dragStartScreenDir2;

    // Start angle of mouse around gizmo center (for rotation mode)
    private float _dragStartAngle;

    // Camera distance at drag start (for translate sensitivity scaling)
    private float _dragCameraDistance;

    /// <summary>
    /// Processes keyboard shortcuts for mode switching.
    /// </summary>
    public void ProcessShortcuts(IEditorInput input)
    {
        // Unity-style shortcuts: W = Translate, E = Rotate, R = Scale
        if (input.IsKeyDown(KeyCode.W)) Mode = GizmoMode.Translate;
        if (input.IsKeyDown(KeyCode.E)) Mode = GizmoMode.Rotate;
        if (input.IsKeyDown(KeyCode.R)) Mode = GizmoMode.Scale;
    }

    /// <summary>
    /// Draws the gizmo handles for the selected object using ImGui DrawList.
    /// Call inside the Scene panel after InvisibleButton so the DrawList overlays the viewport.
    /// </summary>
    public void Draw(GameObject? selected, SceneCamera camera,
        Rect vpRect, IEditorInput input)
    {
        if (selected == null)
        {
            DrawAxisIndicator(camera, vpRect, input);
            return;
        }

        Float3 worldPos = selected.Transform.Position;
        float vpW = vpRect.Size.X;
        float vpH = vpRect.Size.Y;
        Float3 screenPos = camera.WorldToViewport(worldPos, vpW, vpH);

        // Behind camera — only draw the orientation gizmo
        if (screenPos.Z < 0f)
        {
            DrawAxisIndicator(camera, vpRect, input);
            return;
        }

        // Convert viewport-local coords to screen coords for DrawList
        float ox = vpRect.Min.X;
        float oy = vpRect.Min.Y;
        float cx = ox + screenPos.X;
        float cy = oy + screenPos.Y;

        // Gizmo axis directions (world or local)
        Float3[] axisDirs;
        if (Orientation == GizmoOrientation.Local)
        {
            var rot = selected.Transform.Rotation;
            axisDirs = [
                QuatMulVec(rot, Float3.UnitX),
                QuatMulVec(rot, Float3.UnitY),
                QuatMulVec(rot, Float3.UnitZ)
            ];
        }
        else
        {
            axisDirs = [Float3.UnitX, Float3.UnitY, Float3.UnitZ];
        }

        // Perspective-correct handle size: compute a world-space length that
        // projects to a consistent screen-space size regardless of zoom.
        Float3 toCamera = camera.GetPosition() - worldPos;
        float camDist = MathF.Sqrt(Float3.Dot(toCamera, toCamera));
        float fovRad = camera.FieldOfView * (MathF.PI / 180f);
        float worldHandleLen = (DesiredScreenLength / vpH) * camDist * MathF.Tan(fovRad / 2f) * 2f;

        Vector4[] colors   = [XColorVec, YColorVec, ZColorVec];
        Vector4[] hiColors = [XColorHiVec, YColorHiVec, ZColorHiVec];

        // Project axis tips using WorldToViewport so axes foreshorten naturally
        // with perspective. Only fall back to the VP-direction heuristic when a
        // tip extends behind the camera near plane.
        float aspect = vpW / Math.Max(vpH, 1f);
        Float4x4 vpMat = camera.GetProjectionMatrix(aspect) * camera.GetViewMatrix();
        Float2 center = new(screenPos.X, screenPos.Y);

        Float2[] tipVp = new Float2[3];
        Float2[] tipVp2 = new Float2[3];
        Float2[] screenDirs = new Float2[3];
        float[] screenLens = new float[3];
        for (int i = 0; i < 3; i++)
        {
            Float3 tipWorld = worldPos + axisDirs[i] * worldHandleLen;
            Float3 tipScreen = camera.WorldToViewport(tipWorld, vpW, vpH);

            Float2 diff;
            if (tipScreen.Z >= 0f)
            {
                // Tip is in front of camera — use the real projected position
                diff = new Float2(tipScreen.X, tipScreen.Y) - center;
            }
            else
            {
                // Tip behind camera — axis points into the viewer. Use the VP
                // direction transform (w=0) to get a valid screen direction
                // and show a small stub (the axis is heavily foreshortened).
                Float4 clipDir = Float4x4.TransformPoint(new Float4(axisDirs[i], 0f), vpMat);
                Float2 sd = new(clipDir.X, -clipDir.Y);
                float dirLen = Float2.Length(sd);
                if (dirLen < 0.001f)
                    diff = Float2.Zero;
                else
                    diff = (sd / dirLen) * DesiredScreenLength * 0.15f;
            }

            float len = Float2.Length(diff);
            if (len < 1f)
            {
                screenDirs[i] = Float2.Zero;
                screenLens[i] = 0f;
                tipVp[i] = center;
                tipVp2[i] = center;
            }
            else
            {
                screenDirs[i] = diff / len;
                screenLens[i] = len;
                tipVp[i] = center + diff;
                tipVp2[i] = center + screenDirs[i] * len * 1.10f;
            }
        }

        // Sort axes by depth so back-facing axes draw first (behind)
        // and front-facing axes draw last (on top). Active axis always draws on top.
        int[] drawOrder = [0, 1, 2];
        Array.Sort(drawOrder, (a, b) =>
        {
            bool aActive = _activeAxis == a;
            bool bActive = _activeAxis == b;
            if (aActive != bActive) return aActive ? 1 : -1;
            return Float3.Dot(axisDirs[a], toCamera).CompareTo(Float3.Dot(axisDirs[b], toCamera));
        });

        Float2 mouseLocal = input.MousePosition - vpRect.Min;
        var drawList = ImGui.GetWindowDrawList();

        // Pre-compute rotation arc parameters
        float rotationRadius = DesiredScreenLength * 0.8f;

        // Pre-compute projected circle points for rotation hit testing
        Float2[][] circlePoints = new Float2[3][];
        if (Mode == GizmoMode.Rotate)
        {
            for (int i = 0; i < 3; i++)
                circlePoints[i] = ProjectCircleToScreen(worldPos, axisDirs[i], worldHandleLen * 0.8f,
                    camera, vpW, vpH, 48);
        }

        // Determine which axis is hovered — iterate front-to-back so the
        // visually topmost axis gets hover priority.
        int hoveredAxis = -1;
        for (int idx = 2; idx >= 0; idx--)
        {
            int i = drawOrder[idx];
            if (screenLens[i] < 1f) continue;
            bool hovered = Mode == GizmoMode.Rotate
                ? IsNearProjectedCircle(mouseLocal, circlePoints[i], HandleHitSize)
                : IsNearSegment(mouseLocal, center, tipVp[i], HandleHitSize);
            if (hovered) { hoveredAxis = i; break; }
        }

        // Check plane handle hovers (Translate mode only, when no single axis is hovered/active)
        // Plane handles are offset from center (Unity-style) to avoid occluding axis lines.
        float planeInner = 0.22f;
        float planeOuter = 0.48f;
        if (hoveredAxis == -1 && _activeAxis == -1 && Mode == GizmoMode.Translate)
        {
            int[,] planePairs = { {0,1}, {0,2}, {1,2} };
            for (int p = 0; p < 3; p++)
            {
                int pi = planePairs[p,0], pj = planePairs[p,1];
                if (screenLens[pi] < 1f || screenLens[pj] < 1f) continue;

                Float2 dI0 = screenDirs[pi] * screenLens[pi] * planeInner;
                Float2 dI1 = screenDirs[pi] * screenLens[pi] * planeOuter;
                Float2 dJ0 = screenDirs[pj] * screenLens[pj] * planeInner;
                Float2 dJ1 = screenDirs[pj] * screenLens[pj] * planeOuter;
                Float2 q0 = center + dI0 + dJ0;
                Float2 q1 = center + dI1 + dJ0;
                Float2 q2 = center + dI1 + dJ1;
                Float2 q3 = center + dI0 + dJ1;

                if (IsPointInQuad(mouseLocal, q0, q1, q2, q3))
                {
                    hoveredAxis = 3 + p;
                    break;
                }
            }
        }

        // Check uniform scale hover (Scale mode only)
        if (hoveredAxis == -1 && _activeAxis == -1 && Mode == GizmoMode.Scale)
        {
            float dist = Float2.Length(mouseLocal - center);
            if (dist < HandleHitSize * 1.5f)
                hoveredAxis = 6;
        }

        // Draw center circle (white dot)
        drawList.AddCircleFilled(new Vector2(cx, cy), 4f * Game.DpiScale,
            ImGui.GetColorU32(new Vector4(0.90f, 0.90f, 0.90f, 0.80f)));

        for (int idx = 0; idx < 3; idx++)
        {
            int i = drawOrder[idx];
            if (screenLens[i] < 1f) continue;

            float endX = ox + tipVp[i].X;
            float endY = oy + tipVp[i].Y;

            float tipX = ox + tipVp2[i].X;
            float tipY = oy + tipVp2[i].Y;

            // Hit test — use projected circle proximity for Rotate mode, segment for others
            bool hovered = Mode == GizmoMode.Rotate
                ? IsNearProjectedCircle(mouseLocal, circlePoints[i], HandleHitSize)
                : IsNearSegment(mouseLocal, center, tipVp[i], HandleHitSize);
            bool active = _activeAxis == i;

            // Check if this axis is part of a hovered/active plane or uniform group
            bool groupActive = _activeAxis >= 3 && IsAxisPartOfGroup(_activeAxis, i);
            bool groupHovered = hoveredAxis >= 3 && IsAxisPartOfGroup(hoveredAxis, i);

            // Color selection with transparency for non-hovered axes
            Vector4 colVec;
            if (hovered || active || groupActive || groupHovered)
            {
                colVec = hiColors[i];
            }
            else if (_activeAxis >= 0 || hoveredAxis >= 0)
            {
                // Dim non-active axes when one axis is being manipulated or hovered
                colVec = DimAlpha(colors[i], 0.35f);
            }
            else
            {
                colVec = colors[i];
            }

            uint col = ImGui.GetColorU32(colVec);
            bool isHighlighted = hovered || active || groupActive || groupHovered;
            float thick = isHighlighted ? HandleThick * 1.5f : HandleThick;

            if (Mode == GizmoMode.Rotate)
            {
                // Draw proper projected 3D rotation circle
                DrawProjectedCircle(drawList, circlePoints[i], ox, oy, col, thick);
            }
            else
            {
                // Draw handle line
                drawList.AddLine(new Vector2(cx, cy), new Vector2(endX, endY), col, thick);
            }

            // Draw tip
            float tipR = (hovered || active ? 6f : 5f) * Game.DpiScale;
            if (Mode == GizmoMode.Translate)
            {
                // Arrow head (triangle)
                DrawArrowHead(drawList, new Vector2(tipX, tipY), screenDirs[i], tipR * 2f, col);
            }
            else if (Mode == GizmoMode.Scale)
            {
                // Scale cube
                drawList.AddRectFilled(
                    new Vector2(endX - tipR, endY - tipR),
                    new Vector2(endX + tipR, endY + tipR), col);
            }

            // Handle interaction
            HandleDrag(input, selected, i, hovered, screenDirs[i], mouseLocal, camDist, axisDirs[i]);
        }

        // ── Plane handles (Translate mode only) ────────────────────
        // Offset from center (Unity-style) for clearer visual and interaction.
        if (Mode == GizmoMode.Translate)
        {
            int[,] planePairs = { {0,1}, {0,2}, {1,2} };
            for (int p = 0; p < 3; p++)
            {
                int pi = planePairs[p,0], pj = planePairs[p,1];
                if (screenLens[pi] < 1f || screenLens[pj] < 1f) continue;

                Float2 dI0 = screenDirs[pi] * screenLens[pi] * planeInner;
                Float2 dI1 = screenDirs[pi] * screenLens[pi] * planeOuter;
                Float2 dJ0 = screenDirs[pj] * screenLens[pj] * planeInner;
                Float2 dJ1 = screenDirs[pj] * screenLens[pj] * planeOuter;
                Float2 q0 = center + dI0 + dJ0;
                Float2 q1 = center + dI1 + dJ0;
                Float2 q2 = center + dI1 + dJ1;
                Float2 q3 = center + dI0 + dJ1;

                int handleIdx = 3 + p;
                bool planeHovered = hoveredAxis == handleIdx;
                bool planeActive = _activeAxis == handleIdx;

                // Blend colors of the two participating axes
                Vector4 blendNorm = new(
                    (colors[pi].X + colors[pj].X) * 0.5f,
                    (colors[pi].Y + colors[pj].Y) * 0.5f,
                    (colors[pi].Z + colors[pj].Z) * 0.5f, 1f);
                Vector4 blendHi = new(
                    (hiColors[pi].X + hiColors[pj].X) * 0.5f,
                    (hiColors[pi].Y + hiColors[pj].Y) * 0.5f,
                    (hiColors[pi].Z + hiColors[pj].Z) * 0.5f, 1f);

                Vector4 planeColor;
                if (planeHovered || planeActive)
                    planeColor = DimAlpha(blendHi, 0.55f);
                else if (_activeAxis >= 0 || hoveredAxis >= 0)
                    planeColor = DimAlpha(blendNorm, 0.08f);
                else
                    planeColor = DimAlpha(blendNorm, 0.28f);

                uint planeFill = ImGui.GetColorU32(planeColor);

                // Draw filled quad as two triangles
                Vector2 sp0 = new(ox + q0.X, oy + q0.Y);
                Vector2 sp1 = new(ox + q1.X, oy + q1.Y);
                Vector2 sp2 = new(ox + q2.X, oy + q2.Y);
                Vector2 sp3 = new(ox + q3.X, oy + q3.Y);
                drawList.AddTriangleFilled(sp0, sp1, sp2, planeFill);
                drawList.AddTriangleFilled(sp0, sp2, sp3, planeFill);

                // Draw outline
                if (planeHovered || planeActive)
                {
                    uint outlineCol = ImGui.GetColorU32(DimAlpha(blendHi, 0.85f));
                    drawList.AddLine(sp0, sp1, outlineCol, HandleThick);
                    drawList.AddLine(sp1, sp2, outlineCol, HandleThick);
                    drawList.AddLine(sp2, sp3, outlineCol, HandleThick);
                    drawList.AddLine(sp3, sp0, outlineCol, HandleThick);
                }

                // Handle plane drag interaction
                HandlePlaneDrag(input, selected, handleIdx, planeHovered,
                    screenDirs[pi], screenDirs[pj], mouseLocal, camDist,
                    axisDirs[pi], axisDirs[pj]);
            }
        }

        // ── Uniform scale handle (Scale mode only) ─────────────────
        if (Mode == GizmoMode.Scale)
        {
            float uniformRadius = 8f * Game.DpiScale;
            bool uniformHovered = hoveredAxis == 6;
            bool uniformActive = _activeAxis == 6;

            Vector4 uniformColor;
            if (uniformHovered || uniformActive)
                uniformColor = new Vector4(1f, 1f, 1f, 0.90f);
            else if (_activeAxis >= 0 || hoveredAxis >= 0)
                uniformColor = new Vector4(0.6f, 0.6f, 0.6f, 0.30f);
            else
                uniformColor = new Vector4(0.85f, 0.85f, 0.85f, 0.65f);

            drawList.AddCircleFilled(new Vector2(cx, cy), uniformRadius,
                ImGui.GetColorU32(uniformColor));

            if (uniformHovered || uniformActive)
                drawList.AddCircle(new Vector2(cx, cy), uniformRadius,
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.80f)), 0, HandleThick);

            HandleUniformScaleDrag(input, selected, uniformHovered, mouseLocal);
        }

        // Draw the axis indicator in the corner
        DrawAxisIndicator(camera, vpRect, input);
    }

    /// <summary>
    /// Draws the gizmo mode toolbar (Translate / Rotate / Scale buttons).
    /// </summary>
    public void DrawToolbar()
    {
        DrawModeButton("Translate", EditorIconType.Translate, GizmoMode.Translate);
        ImGui.SameLine();
        DrawModeButton("Rotate", EditorIconType.Rotate, GizmoMode.Rotate);
        ImGui.SameLine();
        DrawModeButton("Scale", EditorIconType.Scale, GizmoMode.Scale);
        ImGui.SameLine(0, 16 * Game.DpiScale);

        // World / Local orientation toggle
        bool isWorld = Orientation == GizmoOrientation.World;
        if (isWorld)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.45f, 0.65f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.30f, 0.50f, 0.72f, 1f));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.35f, 0.25f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.62f, 0.40f, 0.30f, 1f));
        }

        if (ImGui.Button(isWorld ? "World" : "Local", new Vector2(52 * Game.DpiScale, 23 * Game.DpiScale)))
            Orientation = isWorld ? GizmoOrientation.Local : GizmoOrientation.World;

        ImGui.PopStyleColor(2);

        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), $"  [{Mode}]");
    }

    private void DrawModeButton(string label, EditorIconType icon, GizmoMode mode)
    {
        bool active = Mode == mode;

        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.31f, 0.47f, 0.78f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.36f, 0.52f, 0.85f, 1f));
        }

        if (EditorIcons.ImageButtonWithLabel(label, icon, label))
            Mode = mode;

        if (active)
            ImGui.PopStyleColor(2);
    }

    /// <summary>
    /// Draws an arrow head (triangle) pointing in the given direction.
    /// </summary>
    private static void DrawArrowHead(ImDrawListPtr drawList, Vector2 tip, Float2 dir, float size, uint color)
    {
        Float2 perp = new(-dir.Y, dir.X);
        Vector2 p1 = tip;
        Vector2 p2 = new(tip.X - dir.X * size + perp.X * size * 0.35f,
                         tip.Y - dir.Y * size + perp.Y * size * 0.35f);
        Vector2 p3 = new(tip.X - dir.X * size - perp.X * size * 0.35f,
                         tip.Y - dir.Y * size - perp.Y * size * 0.35f);
        drawList.AddTriangleFilled(p1, p2, p3, color);
    }

    /// <summary>
    /// Projects a 3D circle (in the plane perpendicular to <paramref name="axis"/>) to viewport-local screen coordinates.
    /// Returns an array of screen-space points (not offset by viewport origin).
    /// </summary>
    private static Float2[] ProjectCircleToScreen(Float3 worldCenter, Float3 axis, float worldRadius,
        SceneCamera camera, float vpW, float vpH, int segments = 48)
    {
        // Compute two perpendicular vectors to the axis
        Float3 perp1;
        if (MathF.Abs(Float3.Dot(axis, Float3.UnitY)) < 0.99f)
            perp1 = Float3.Normalize(Float3.Cross(axis, Float3.UnitY));
        else
            perp1 = Float3.Normalize(Float3.Cross(axis, Float3.UnitX));
        Float3 perp2 = Float3.Normalize(Float3.Cross(axis, perp1));

        var points = new Float2[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float angle = (float)i / segments * MathF.PI * 2f;
            Float3 worldPt = worldCenter + (perp1 * MathF.Cos(angle) + perp2 * MathF.Sin(angle)) * worldRadius;
            Float3 sp = camera.WorldToViewport(worldPt, vpW, vpH);
            points[i] = sp.Z < 0f ? new Float2(float.NaN, float.NaN) : new Float2(sp.X, sp.Y);
        }
        return points;
    }

    /// <summary>
    /// Draws a projected 3D circle from pre-computed viewport-local screen points.
    /// </summary>
    private static void DrawProjectedCircle(ImDrawListPtr drawList, Float2[] points,
        float ox, float oy, uint color, float thickness)
    {
        for (int i = 0; i < points.Length - 1; i++)
        {
            if (float.IsNaN(points[i].X) || float.IsNaN(points[i + 1].X)) continue;
            drawList.AddLine(
                new Vector2(ox + points[i].X, oy + points[i].Y),
                new Vector2(ox + points[i + 1].X, oy + points[i + 1].Y),
                color, thickness);
        }
    }

    /// <summary>
    /// Hit-tests a point against a projected 3D circle (polyline of screen-space points).
    /// </summary>
    private static bool IsNearProjectedCircle(Float2 point, Float2[] circlePoints, float threshold)
    {
        for (int i = 0; i < circlePoints.Length - 1; i++)
        {
            if (float.IsNaN(circlePoints[i].X) || float.IsNaN(circlePoints[i + 1].X)) continue;
            if (IsNearSegment(point, circlePoints[i], circlePoints[i + 1], threshold))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Draws a Unity-style 3D orientation gizmo in the top-right corner of the viewport.
    /// Shows colored cone tips on positive axes and small dots on negative axes.
    /// Clicking an axis snaps the camera to look along that direction.
    /// </summary>
    private void DrawAxisIndicator(SceneCamera camera, Rect vpRect, IEditorInput input)
    {
        float size = 55f * Game.DpiScale;
        float margin = 16f * Game.DpiScale;
        float cx = vpRect.Max.X - size - margin;
        float cy = vpRect.Min.Y + size + margin;
        Float2 gizmoCenter = new(cx, cy);

        var drawList = ImGui.GetWindowDrawList();

        // Background circle
        drawList.AddCircleFilled(new Vector2(cx, cy), size,
            ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.10f, 0.65f)));
        drawList.AddCircle(new Vector2(cx, cy), size,
            ImGui.GetColorU32(new Vector4(0.25f, 0.25f, 0.30f, 0.50f)), 0, 1.5f * Game.DpiScale);

        // Derive camera basis from its rotation (position-independent)
        Prowl.Vector.Quaternion camRot = camera.GetRotation();
        Float3 camRight   = QuatMulVec(camRot, Float3.UnitX);
        Float3 camUp      = QuatMulVec(camRot, Float3.UnitY);
        Float3 camForward = QuatMulVec(camRot, new Float3(0, 0, -1));

        // All 6 axis directions: +X, +Y, +Z, -X, -Y, -Z
        Float3[] dirs =
        [
            Float3.UnitX, Float3.UnitY, Float3.UnitZ,
            new Float3(-1, 0, 0), new Float3(0, -1, 0), new Float3(0, 0, -1)
        ];
        string[] labels = ["X", "Y", "Z", "", "", ""];
        Vector4[] axisClrs =
        [
            XColorVec, YColorVec, ZColorVec,
            DimAlpha(XColorVec, 0.55f), DimAlpha(YColorVec, 0.55f), DimAlpha(ZColorVec, 0.55f)
        ];

        // Sort all 6 endpoints by depth (back-to-front draw order)
        int[] order = [0, 1, 2, 3, 4, 5];
        Array.Sort(order, (a, b) =>
            Float3.Dot(dirs[a], camForward).CompareTo(Float3.Dot(dirs[b], camForward)));

        float axisLen = size * 0.72f;
        float coneSize = 12f * Game.DpiScale;
        float dotSize = 5f * Game.DpiScale;

        // Pre-compute endpoints for hover detection.
        // Use the raw (un-normalized) screen projection so axes foreshorten
        // with the camera perspective — axes pointing toward/away from the
        // viewer appear shorter, giving a true 3D orientation cue.
        Float2[] endPoints = new Float2[6];
        Float2[] dir2ds = new Float2[6];
        for (int i = 0; i < 6; i++)
        {
            float dx = Float3.Dot(dirs[i], camRight);
            float dy = -Float3.Dot(dirs[i], camUp);
            Float2 d = new(dx, dy);
            float len = Float2.Length(d);
            if (len < 0.01f) { dir2ds[i] = Float2.Zero; endPoints[i] = gizmoCenter; continue; }
            dir2ds[i] = d / len;
            endPoints[i] = gizmoCenter + d * axisLen;
        }

        // Hover test (front-to-back priority)
        int hoveredIndex = -1;
        Float2 mousePos = input.MousePosition;
        for (int idx = 5; idx >= 0; idx--)
        {
            int i = order[idx];
            float hitR = i < 3 ? coneSize * 1.1f : dotSize * 2f;
            if (Float2.Length(mousePos - endPoints[i]) < hitR)
            {
                hoveredIndex = i;
                break;
            }
        }

        IsSceneGizmoHovered = hoveredIndex >= 0;

        // Click to snap camera
        if (hoveredIndex >= 0 && input.IsMouseButtonDown(0) && !IsActive)
            SnapCameraToAxis(camera, hoveredIndex);

        // Draw back-to-front
        for (int idx = 0; idx < 6; idx++)
        {
            int i = order[idx];
            Float2 d2 = dir2ds[i];
            if (Float2.Length(d2) < 0.01f) continue;

            Float2 ep = endPoints[i];
            bool hovered = hoveredIndex == i;

            Vector4 col = hovered
                ? new Vector4(MathF.Min(axisClrs[i].X * 1.4f, 1f),
                              MathF.Min(axisClrs[i].Y * 1.4f, 1f),
                              MathF.Min(axisClrs[i].Z * 1.4f, 1f), 1f)
                : axisClrs[i];
            uint colU = ImGui.GetColorU32(col);

            float thick = (hovered ? 2.5f : 1.8f) * Game.DpiScale;
            drawList.AddLine(new Vector2(cx, cy), new Vector2(ep.X, ep.Y), colU, thick);

            if (i < 3)
            {
                // Positive axis: filled cone tip
                float cs = hovered ? coneSize * 1.25f : coneSize;
                DrawCone(drawList, new Vector2(ep.X, ep.Y), d2, cs, colU);

                // Label on the cone
                string lbl = labels[i];
                var ls = ImGui.CalcTextSize(lbl);
                uint lblCol = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, hovered ? 1f : 0.90f));
                drawList.AddText(new Vector2(ep.X - ls.X * 0.5f, ep.Y - ls.Y * 0.5f), lblCol, lbl);
            }
            else
            {
                // Negative axis: small circle
                float ds = hovered ? dotSize * 1.4f : dotSize;
                drawList.AddCircleFilled(new Vector2(ep.X, ep.Y), ds, colU);
                if (hovered)
                    drawList.AddCircle(new Vector2(ep.X, ep.Y), ds + 1.5f * Game.DpiScale,
                        ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.65f)));
            }
        }

        // Center dot
        drawList.AddCircleFilled(new Vector2(cx, cy), 3.5f * Game.DpiScale,
            ImGui.GetColorU32(new Vector4(0.60f, 0.60f, 0.65f, 0.85f)));

        // "Persp" label beneath the gizmo
        string perspLabel = "Persp";
        var perspSz = ImGui.CalcTextSize(perspLabel);
        drawList.AddText(
            new Vector2(cx - perspSz.X * 0.5f, cy + size + 4f * Game.DpiScale),
            ImGui.GetColorU32(new Vector4(0.55f, 0.55f, 0.55f, 0.70f)), perspLabel);
    }

    /// <summary>
    /// Draws a filled cone (triangle) pointing in the given 2D direction.
    /// </summary>
    private static void DrawCone(ImDrawListPtr drawList, Vector2 center, Float2 dir, float size, uint color)
    {
        Float2 perp = new(-dir.Y, dir.X);
        float half = size * 0.42f;
        Vector2 tip = new(center.X + dir.X * size * 0.5f, center.Y + dir.Y * size * 0.5f);
        Vector2 bl = new(center.X - dir.X * size * 0.35f + perp.X * half,
                         center.Y - dir.Y * size * 0.35f + perp.Y * half);
        Vector2 br = new(center.X - dir.X * size * 0.35f - perp.X * half,
                         center.Y - dir.Y * size * 0.35f - perp.Y * half);
        drawList.AddTriangleFilled(tip, bl, br, color);
    }

    /// <summary>
    /// Snaps the camera to look along a specific axis direction.
    /// </summary>
    private static void SnapCameraToAxis(SceneCamera camera, int axisIndex)
    {
        (float yaw, float pitch) = axisIndex switch
        {
            0 => (90f, 0f),            // +X
            1 => (camera.Yaw, 89f),    // +Y (top-down, keep horizontal orientation)
            2 => (0f, 0f),             // +Z
            3 => (-90f, 0f),           // -X
            4 => (camera.Yaw, -89f),   // -Y (bottom-up)
            5 => (180f, 0f),           // -Z
            _ => (camera.Yaw, camera.Pitch)
        };

        camera.Yaw = yaw;
        camera.Pitch = pitch;
    }

    /// <summary> Rotates a vector by a quaternion (q * v * q⁻¹). </summary>
    private static Float3 QuatMulVec(Prowl.Vector.Quaternion q, Float3 v)
    {
        float x2 = q.X + q.X, y2 = q.Y + q.Y, z2 = q.Z + q.Z;
        float xx2 = q.X * x2, yy2 = q.Y * y2, zz2 = q.Z * z2;
        float xy2 = q.X * y2, xz2 = q.X * z2, yz2 = q.Y * z2;
        float wx2 = q.W * x2, wy2 = q.W * y2, wz2 = q.W * z2;
        return new Float3(
            v.X * (1f - yy2 - zz2) + v.Y * (xy2 - wz2) + v.Z * (xz2 + wy2),
            v.X * (xy2 + wz2) + v.Y * (1f - xx2 - zz2) + v.Z * (yz2 - wx2),
            v.X * (xz2 - wy2) + v.Y * (yz2 + wx2) + v.Z * (1f - xx2 - yy2));
    }

    private void HandleDrag(IEditorInput input, GameObject selected, int axis,
        bool hovered, Float2 screenDir, Float2 mouseLocal, float cameraDistance, Float3 axisWorldDir)
    {
        if (_activeAxis == -1 && hovered && input.IsMouseButtonDown(0))
        {
            _activeAxis = axis;
            _dragStart = mouseLocal;
            _dragStartValue = Mode switch
            {
                GizmoMode.Translate => selected.Transform.Position,
                GizmoMode.Rotate => selected.Transform.LocalEulerAngles,
                GizmoMode.Scale => selected.Transform.LocalScale,
                _ => Float3.Zero
            };
            _dragStartRotation = selected.Transform.Rotation;
            _dragStartAxisDir = axisWorldDir;

            // For rotation, capture the initial mouse angle around the gizmo center
            Float2 toMouse = mouseLocal - new Float2(screenDir.X, screenDir.Y); // dummy, we use center below
            _dragStartAngle = MathF.Atan2(mouseLocal.Y - _dragStart.Y, mouseLocal.X - _dragStart.X);

            // Capture pre-drag transform for undo
            _undoLocalPos   = selected.Transform.LocalPosition;
            _undoLocalEuler = selected.Transform.LocalEulerAngles;
            _undoLocalScale = selected.Transform.LocalScale;
            _dragCameraDistance = cameraDistance;
        }

        if (_activeAxis == axis && input.IsMouseButton(0))
        {
            Float2 diff = mouseLocal - _dragStart;
            float projection = Float2.Dot(diff, screenDir);

            // Scale translate sensitivity by camera distance so distant objects
            // are just as easy to move as nearby ones.
            float sensitivity = Mode switch
            {
                GizmoMode.Translate => 0.02f * Math.Max(_dragCameraDistance * 0.1f, 0.1f),
                GizmoMode.Rotate => 0.5f,
                GizmoMode.Scale => 0.01f,
                _ => 0.01f
            };

            float amount = projection * sensitivity;

            switch (Mode)
            {
                case GizmoMode.Translate:
                    // Move along the gizmo axis direction (respects world/local orientation)
                    selected.Transform.Position = _dragStartValue + axisWorldDir * amount;
                    break;
                case GizmoMode.Rotate:
                {
                    // Compute rotation angle from mouse movement perpendicular to the
                    // projected axis direction — gives intuitive drag-to-rotate.
                    Float2 perp = new(-screenDir.Y, screenDir.X);
                    float angle = Float2.Dot(diff, perp) * sensitivity;

                    // Build a delta quaternion around the CACHED axis direction
                    // (using the axis captured at drag start prevents drift in Local mode)
                    Prowl.Vector.Quaternion deltaRot = Prowl.Vector.Quaternion.AxisAngle(
                        _dragStartAxisDir, angle * (MathF.PI / 180f));

                    // Apply to the start rotation (captured as a quaternion)
                    Prowl.Vector.Quaternion startRot = _dragStartRotation;
                    selected.Transform.Rotation = deltaRot * startRot;
                    break;
                }
                case GizmoMode.Scale:
                {
                    Float3 delta = Float3.Zero;
                    switch (axis)
                    {
                        case 0: delta = new Float3(amount, 0, 0); break;
                        case 1: delta = new Float3(0, amount, 0); break;
                        case 2: delta = new Float3(0, 0, amount); break;
                    }
                    selected.Transform.LocalScale = _dragStartValue + delta;
                    break;
                }
            }
        }

        if (_activeAxis == axis && input.IsMouseButtonUp(0))
        {
            string modeName = Mode switch
            {
                GizmoMode.Translate => "Move",
                GizmoMode.Rotate => "Rotate",
                GizmoMode.Scale => "Scale",
                _ => "Transform"
            };
            CommitUndoIfChanged(selected, modeName);
            _activeAxis = -1;
        }
    }

    private void HandlePlaneDrag(IEditorInput input, GameObject selected, int planeIndex,
        bool hovered, Float2 screenDirI, Float2 screenDirJ,
        Float2 mouseLocal, float cameraDistance, Float3 axisDirI, Float3 axisDirJ)
    {
        if (_activeAxis == -1 && hovered && input.IsMouseButtonDown(0))
        {
            _activeAxis = planeIndex;
            _dragStart = mouseLocal;
            _dragStartValue = selected.Transform.Position;
            _dragStartAxisDir = axisDirI;
            _dragStartAxisDir2 = axisDirJ;
            _dragStartScreenDir = screenDirI;
            _dragStartScreenDir2 = screenDirJ;
            _undoLocalPos = selected.Transform.LocalPosition;
            _undoLocalEuler = selected.Transform.LocalEulerAngles;
            _undoLocalScale = selected.Transform.LocalScale;
            _dragCameraDistance = cameraDistance;
        }

        if (_activeAxis == planeIndex && input.IsMouseButton(0))
        {
            Float2 diff = mouseLocal - _dragStart;
            float sensitivity = 0.02f * Math.Max(_dragCameraDistance * 0.1f, 0.1f);
            float projI = Float2.Dot(diff, _dragStartScreenDir) * sensitivity;
            float projJ = Float2.Dot(diff, _dragStartScreenDir2) * sensitivity;
            selected.Transform.Position = _dragStartValue
                + _dragStartAxisDir * projI
                + _dragStartAxisDir2 * projJ;
        }

        if (_activeAxis == planeIndex && input.IsMouseButtonUp(0))
        {
            CommitUndoIfChanged(selected, "Move");
            _activeAxis = -1;
        }
    }

    private void HandleUniformScaleDrag(IEditorInput input, GameObject selected,
        bool hovered, Float2 mouseLocal)
    {
        if (_activeAxis == -1 && hovered && input.IsMouseButtonDown(0))
        {
            _activeAxis = 6;
            _dragStart = mouseLocal;
            _dragStartValue = selected.Transform.LocalScale;
            _undoLocalPos = selected.Transform.LocalPosition;
            _undoLocalEuler = selected.Transform.LocalEulerAngles;
            _undoLocalScale = selected.Transform.LocalScale;
        }

        if (_activeAxis == 6 && input.IsMouseButton(0))
        {
            Float2 diff = mouseLocal - _dragStart;
            float amount = (diff.X - diff.Y) * 0.005f;
            Float3 uniform = new(amount, amount, amount);
            selected.Transform.LocalScale = _dragStartValue + uniform;
        }

        if (_activeAxis == 6 && input.IsMouseButtonUp(0))
        {
            CommitUndoIfChanged(selected, "Scale");
            _activeAxis = -1;
        }
    }

    private void CommitUndoIfChanged(GameObject selected, string modeName)
    {
        Float3 newLocalPos   = selected.Transform.LocalPosition;
        Float3 newLocalEuler = selected.Transform.LocalEulerAngles;
        Float3 newLocalScale = selected.Transform.LocalScale;

        bool changed = _undoLocalPos != newLocalPos ||
                       _undoLocalEuler != newLocalEuler ||
                       _undoLocalScale != newLocalScale;

        if (changed && EditorServices.TryGet<UndoRedoService>(out var undoSvc))
        {
            var cmd = new TransformChangeCommand(
                selected.Transform,
                _undoLocalPos, _undoLocalEuler, _undoLocalScale,
                newLocalPos, newLocalEuler, newLocalScale,
                $"{modeName} {selected.Name}");
            undoSvc!.Push(cmd);
        }
    }

    /// <summary>
    /// Returns true if the given single axis (0=X, 1=Y, 2=Z) participates
    /// in the specified group handle.
    /// </summary>
    private static bool IsAxisPartOfGroup(int group, int axis)
    {
        return group switch
        {
            3 => axis == 0 || axis == 1, // XY
            4 => axis == 0 || axis == 2, // XZ
            5 => axis == 1 || axis == 2, // YZ
            6 => true,                   // Uniform (all axes)
            _ => false
        };
    }

    private static bool IsPointInTriangle(Float2 p, Float2 a, Float2 b, Float2 c)
    {
        Float2 v0 = c - a, v1 = b - a, v2 = p - a;
        float d00 = Float2.Dot(v0, v0);
        float d01 = Float2.Dot(v0, v1);
        float d02 = Float2.Dot(v0, v2);
        float d11 = Float2.Dot(v1, v1);
        float d12 = Float2.Dot(v1, v2);
        float inv = 1.0f / Math.Max(d00 * d11 - d01 * d01, 0.0001f);
        float u = (d11 * d02 - d01 * d12) * inv;
        float v = (d00 * d12 - d01 * d02) * inv;
        return u >= 0 && v >= 0 && (u + v) <= 1;
    }

    private static bool IsPointInQuad(Float2 p, Float2 a, Float2 b, Float2 c, Float2 d)
    {
        return IsPointInTriangle(p, a, b, c) || IsPointInTriangle(p, a, c, d);
    }

    private static bool IsNearSegment(Float2 point, Float2 a, Float2 b, float threshold)
    {
        Float2 ab = b - a;
        Float2 ap = point - a;
        float t = Float2.Dot(ap, ab) / Math.Max(Float2.Dot(ab, ab), 0.0001f);
        t = Math.Clamp(t, 0f, 1f);
        Float2 closest = a + ab * t;
        return Float2.Length(point - closest) <= threshold;
    }

    }
