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

    // Drag state
    private int _activeAxis = -1;        // -1 = none, 0=X, 1=Y, 2=Z
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

        // Pre-compute projected axis tips
        Float2[] tipVp = new Float2[3];
        Float2[] tipVp2 = new Float2[3];
        Float2[] screenDirs = new Float2[3];
        float[] screenLens = new float[3];
        for (int i = 0; i < 3; i++)
        {
            Float3 tipWorld = worldPos + axisDirs[i] * worldHandleLen;
            Float3 tipScreen = camera.WorldToViewport(tipWorld, vpW, vpH);
            Float3 tipWorld2 = worldPos + axisDirs[i] * worldHandleLen * 1.07f;
            Float3 tipScreen2 = camera.WorldToViewport(tipWorld2, vpW, vpH);
            Float2 d = new(tipScreen.X - screenPos.X, tipScreen.Y - screenPos.Y);
            float l = Float2.Length(d);
            tipVp[i] = new Float2(tipScreen.X, tipScreen.Y);
            tipVp2[i] = new Float2(tipScreen2.X, tipScreen2.Y);
            screenLens[i] = l;
            screenDirs[i] = l < 1f ? Float2.Zero : d / l;
        }

        Float2 mouseLocal = input.MousePosition - vpRect.Min;
        var drawList = ImGui.GetWindowDrawList();

        // Pre-compute rotation arc parameters (center in viewport-local coords, radius)
        Float2 center = new(screenPos.X, screenPos.Y);
        float rotationRadius = DesiredScreenLength * 0.8f;

        // Pre-compute projected circle points for rotation hit testing
        Float2[][] circlePoints = new Float2[3][];
        if (Mode == GizmoMode.Rotate)
        {
            for (int i = 0; i < 3; i++)
                circlePoints[i] = ProjectCircleToScreen(worldPos, axisDirs[i], worldHandleLen * 0.8f,
                    camera, vpW, vpH, 48);
        }

        // Determine which axis is hovered (for dimming others)
        int hoveredAxis = -1;
        for (int i = 0; i < 3; i++)
        {
            if (screenLens[i] < 1f) continue;
            bool hovered = Mode == GizmoMode.Rotate
                ? IsNearProjectedCircle(mouseLocal, circlePoints[i], HandleHitSize)
                : IsNearSegment(mouseLocal, center, tipVp[i], HandleHitSize);
            if (hovered) { hoveredAxis = i; break; }
        }

        // Draw center circle (white dot)
        drawList.AddCircleFilled(new Vector2(cx, cy), 4f * Game.DpiScale,
            ImGui.GetColorU32(new Vector4(0.90f, 0.90f, 0.90f, 0.80f)));

        for (int i = 0; i < 3; i++)
        {
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

        // Draw the axis indicator in the corner
        DrawAxisIndicator(camera, vpRect);
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

        if (ImGui.Button(isWorld ? "World" : "Local", new Vector2(52 * Game.DpiScale, 22 * Game.DpiScale)))
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
            points[i] = new Float2(sp.X, sp.Y);
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
            if (IsNearSegment(point, circlePoints[i], circlePoints[i + 1], threshold))
                return true;
        }
        return false;
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
            // Push an undo command if the transform actually changed
            Float3 newLocalPos   = selected.Transform.LocalPosition;
            Float3 newLocalEuler = selected.Transform.LocalEulerAngles;
            Float3 newLocalScale = selected.Transform.LocalScale;

            bool changed = _undoLocalPos != newLocalPos ||
                           _undoLocalEuler != newLocalEuler ||
                           _undoLocalScale != newLocalScale;

            if (changed && EditorServices.TryGet<UndoRedoService>(out var undoSvc))
            {
                string desc = Mode switch
                {
                    GizmoMode.Translate => "Move",
                    GizmoMode.Rotate => "Rotate",
                    GizmoMode.Scale => "Scale",
                    _ => "Transform"
                };

                // Push without Execute — the transform is already at the new value.
                var cmd = new TransformChangeCommand(
                    selected.Transform,
                    _undoLocalPos, _undoLocalEuler, _undoLocalScale,
                    newLocalPos, newLocalEuler, newLocalScale,
                    $"{desc} {selected.Name}");

                undoSvc!.Push(cmd);
            }

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
