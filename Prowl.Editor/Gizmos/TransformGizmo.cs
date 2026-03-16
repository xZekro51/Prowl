// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using ImGuiNET;
using Prowl.Editor.Rendering;
using Prowl.Editor.Services;
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
    private static float HandleLength => 70f * Game.DpiScale;
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

    // Drag state
    private int _activeAxis = -1;        // -1 = none, 0=X, 1=Y, 2=Z
    private Float2 _dragStart;
    private Float3 _dragStartValue;

    /// <summary>
    /// Processes keyboard shortcuts for mode switching.
    /// </summary>
    public void ProcessShortcuts(IEditorInput input)
    {
        if (input.IsKeyDown(KeyCode.Number1)) Mode = GizmoMode.Translate;
        if (input.IsKeyDown(KeyCode.Number2)) Mode = GizmoMode.Rotate;
        if (input.IsKeyDown(KeyCode.Number3)) Mode = GizmoMode.Scale;
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
            DrawAxisIndicator(camera, vpRect);
            return;
        }

        Float3 worldPos = selected.Transform.Position;
        float vpW = vpRect.Size.X;
        float vpH = vpRect.Size.Y;
        Float3 screenPos = camera.WorldToViewport(worldPos, vpW, vpH);

        // Behind camera — don't draw
        if (screenPos.Z < 0f) return;

        // Convert viewport-local coords to screen coords for DrawList
        float ox = vpRect.Min.X;
        float oy = vpRect.Min.Y;
        float cx = ox + screenPos.X;
        float cy = oy + screenPos.Y;

        Float3[] worldDirs = [Float3.UnitX, Float3.UnitY, Float3.UnitZ];
        Vector4[] colors   = [XColorVec, YColorVec, ZColorVec];
        Vector4[] hiColors = [XColorHiVec, YColorHiVec, ZColorHiVec];

        Float2 mouseLocal = input.MousePosition - vpRect.Min;

        var drawList = ImGui.GetWindowDrawList();

        // Determine which axis is hovered (for dimming others)
        int hoveredAxis = -1;
        for (int i = 0; i < 3; i++)
        {
            Float3 tipWorld = worldPos + worldDirs[i] * 1.0f;
            Float3 tipScreen = camera.WorldToViewport(tipWorld, vpW, vpH);
            Float2 dir2d = new Float2(tipScreen.X - screenPos.X, tipScreen.Y - screenPos.Y);
            float len = Float2.Length(dir2d);
            if (len < 1f) continue;
            dir2d = dir2d / len;

            bool hovered = IsNearSegment(mouseLocal,
                new Float2(screenPos.X, screenPos.Y),
                new Float2(screenPos.X + dir2d.X * HandleLength, screenPos.Y + dir2d.Y * HandleLength),
                HandleHitSize);
            if (hovered) { hoveredAxis = i; break; }
        }

        // Draw center circle (white dot)
        drawList.AddCircleFilled(new Vector2(cx, cy), 4f * Game.DpiScale,
            ImGui.GetColorU32(new Vector4(0.90f, 0.90f, 0.90f, 0.80f)));

        for (int i = 0; i < 3; i++)
        {
            Float3 tipWorld = worldPos + worldDirs[i] * 1.0f;
            Float3 tipScreen = camera.WorldToViewport(tipWorld, vpW, vpH);

            Float2 dir2d = new Float2(tipScreen.X - screenPos.X, tipScreen.Y - screenPos.Y);
            float len = Float2.Length(dir2d);
            if (len < 1f) continue;
            dir2d = dir2d / len;

            float endX = cx + dir2d.X * HandleLength;
            float endY = cy + dir2d.Y * HandleLength;

            // Hit test
            bool hovered = IsNearSegment(mouseLocal,
                new Float2(screenPos.X, screenPos.Y),
                new Float2(screenPos.X + dir2d.X * HandleLength, screenPos.Y + dir2d.Y * HandleLength),
                HandleHitSize);
            bool active = _activeAxis == i;

            // Color selection with transparency for non-hovered axes
            Vector4 colVec;
            if (hovered || active)
            {
                colVec = hiColors[i];
            }
            else if (_activeAxis >= 0 || (hoveredAxis >= 0 && hoveredAxis != i))
            {
                // Dim non-active axes when one axis is being manipulated or hovered
                colVec = DimAlpha(colors[i], 0.35f);
            }
            else
            {
                colVec = colors[i];
            }

            uint col = ImGui.GetColorU32(colVec);
            float thick = (hovered || active) ? HandleThick * 1.5f : HandleThick;

            if (Mode == GizmoMode.Rotate)
            {
                // Draw rotation arcs instead of straight lines
                DrawRotationArc(drawList, cx, cy, dir2d, HandleLength * 0.8f, col, thick);
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
                DrawArrowHead(drawList, new Vector2(endX, endY), dir2d, tipR * 2f, col);
            }
            else if (Mode == GizmoMode.Scale)
            {
                // Scale cube
                drawList.AddRectFilled(
                    new Vector2(endX - tipR, endY - tipR),
                    new Vector2(endX + tipR, endY + tipR), col);
            }
            else // Rotate
            {
                // Small circle at arc end
                float arcEndAngle = MathF.Atan2(dir2d.Y, dir2d.X) + MathF.PI * 0.3f;
                float radius = HandleLength * 0.8f;
                float arcEndX = cx + MathF.Cos(arcEndAngle) * radius;
                float arcEndY = cy + MathF.Sin(arcEndAngle) * radius;
                drawList.AddCircleFilled(new Vector2(arcEndX, arcEndY), tipR * 0.8f, col);
            }

            // Handle interaction
            HandleDrag(input, selected, i, hovered, dir2d, mouseLocal);
        }

        // Draw the axis indicator in the corner
        DrawAxisIndicator(camera, vpRect);
    }

    /// <summary>
    /// Draws the gizmo mode toolbar (Translate / Rotate / Scale buttons).
    /// </summary>
    public void DrawToolbar()
    {
        DrawModeButton("\uf0b2 T", GizmoMode.Translate);
        ImGui.SameLine();
        DrawModeButton("\uf2f1 R", GizmoMode.Rotate);
        ImGui.SameLine();
        DrawModeButton("\uf065 S", GizmoMode.Scale);
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), $"  [{Mode}]");
    }

    private void DrawModeButton(string label, GizmoMode mode)
    {
        bool active = Mode == mode;

        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.31f, 0.47f, 0.78f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.36f, 0.52f, 0.85f, 1f));
        }

        if (ImGui.Button(label, new Vector2(42 * Game.DpiScale, 22 * Game.DpiScale)))
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
    /// Draws a rotation arc (partial circle) for the Rotate mode.
    /// </summary>
    private static void DrawRotationArc(ImDrawListPtr drawList, float cx, float cy,
        Float2 dir, float radius, uint color, float thickness)
    {
        float baseAngle = MathF.Atan2(dir.Y, dir.X);
        float arcSpan = MathF.PI * 0.6f; // 108° arc
        int segments = 24;

        float startAngle = baseAngle - arcSpan * 0.5f;

        for (int s = 0; s < segments; s++)
        {
            float a0 = startAngle + arcSpan * s / segments;
            float a1 = startAngle + arcSpan * (s + 1) / segments;

            drawList.AddLine(
                new Vector2(cx + MathF.Cos(a0) * radius, cy + MathF.Sin(a0) * radius),
                new Vector2(cx + MathF.Cos(a1) * radius, cy + MathF.Sin(a1) * radius),
                color, thickness);
        }
    }

    /// <summary>
    /// Draws a small 3D axis indicator in the top-right corner of the viewport.
    /// Uses only the camera's rotation so it is independent of camera position.
    /// </summary>
    private static void DrawAxisIndicator(SceneCamera camera, Rect vpRect)
    {
        float size = 40f * Game.DpiScale;
        float margin = 12f * Game.DpiScale;
        float cx = vpRect.Max.X - size - margin;
        float cy = vpRect.Min.Y + size + margin;

        var drawList = ImGui.GetWindowDrawList();

        // Background circle
        drawList.AddCircleFilled(new Vector2(cx, cy), size * 0.85f,
            ImGui.GetColorU32(new Vector4(0.10f, 0.10f, 0.10f, 0.60f)));
        drawList.AddCircle(new Vector2(cx, cy), size * 0.85f,
            ImGui.GetColorU32(new Vector4(0.30f, 0.30f, 0.30f, 0.40f)));

        // Derive camera basis from its rotation (position-independent)
        Prowl.Vector.Quaternion camRot = camera.GetRotation();
        Float3 camRight   = QuatMulVec(camRot, Float3.UnitX);
        Float3 camUp      = QuatMulVec(camRot, Float3.UnitY);

        Float3[] dirs = [Float3.UnitX, Float3.UnitY, Float3.UnitZ];
        string[] labels = ["X", "Y", "Z"];
        Vector4[] axisColors = [XColorVec, YColorVec, ZColorVec];

        // Sort axes by depth so the nearest axis draws on top
        Float3 camForward = QuatMulVec(camRot, new Float3(0, 0, -1));
        int[] order = [0, 1, 2];
        Array.Sort(order, (a, b) =>
            Float3.Dot(dirs[a], camForward).CompareTo(Float3.Dot(dirs[b], camForward)));

        float axisLen = size * 0.70f;

        for (int idx = 0; idx < 3; idx++)
        {
            int i = order[idx];
            // Project world axis onto camera's screen-space right / up
            float dx =  Float3.Dot(dirs[i], camRight);
            float dy = -Float3.Dot(dirs[i], camUp); // negate: screen Y is down

            Float2 dir2d = new(dx, dy);
            float len = Float2.Length(dir2d);
            if (len < 0.01f) continue;
            dir2d = dir2d / len;

            float endX = cx + dir2d.X * axisLen;
            float endY = cy + dir2d.Y * axisLen;

            uint col = ImGui.GetColorU32(axisColors[i]);

            drawList.AddLine(new Vector2(cx, cy), new Vector2(endX, endY), col, 2f * Game.DpiScale);
            drawList.AddCircleFilled(new Vector2(endX, endY), 4f * Game.DpiScale, col);

            // Label
            var labelSize = ImGui.CalcTextSize(labels[i]);
            float lx = endX + dir2d.X * 6f * Game.DpiScale - labelSize.X * 0.5f;
            float ly = endY + dir2d.Y * 6f * Game.DpiScale - labelSize.Y * 0.5f;
            drawList.AddText(new Vector2(lx, ly), col, labels[i]);
        }
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
        bool hovered, Float2 screenDir, Float2 mouseLocal)
    {
        if (_activeAxis == -1 && hovered && input.IsMouseButtonDown(0))
        {
            _activeAxis = axis;
            _dragStart = mouseLocal;
            _dragStartValue = Mode switch
            {
                GizmoMode.Translate => selected.Transform.LocalPosition,
                GizmoMode.Rotate => selected.Transform.LocalEulerAngles,
                GizmoMode.Scale => selected.Transform.LocalScale,
                _ => Float3.Zero
            };
        }

        if (_activeAxis == axis && input.IsMouseButton(0))
        {
            Float2 diff = mouseLocal - _dragStart;
            float projection = Float2.Dot(diff, screenDir);

            float sensitivity = Mode switch
            {
                GizmoMode.Translate => 0.02f,
                GizmoMode.Rotate => 0.5f,
                GizmoMode.Scale => 0.01f,
                _ => 0.01f
            };

            Float3 delta = Float3.Zero;
            switch (axis)
            {
                case 0: delta = new Float3(projection * sensitivity, 0, 0); break;
                case 1: delta = new Float3(0, projection * sensitivity, 0); break;
                case 2: delta = new Float3(0, 0, projection * sensitivity); break;
            }

            switch (Mode)
            {
                case GizmoMode.Translate:
                    selected.Transform.LocalPosition = _dragStartValue + delta;
                    break;
                case GizmoMode.Rotate:
                    selected.Transform.LocalEulerAngles = _dragStartValue + delta;
                    break;
                case GizmoMode.Scale:
                    selected.Transform.LocalScale = _dragStartValue + delta;
                    break;
            }
        }

        if (_activeAxis == axis && input.IsMouseButtonUp(0))
        {
            _activeAxis = -1;
        }
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
