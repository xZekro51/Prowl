// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Services;
using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Editor.Rendering;

/// <summary>
/// Virtual orbit camera for the Scene View.
/// Not attached to a GameObject — purely an editor construct.
/// Supports orbit (Alt+LMB), pan (MMB), and zoom (scroll).
/// </summary>
public sealed class SceneCamera
{
    // Orbit state
    public Float3 Pivot { get; set; } = Float3.Zero;
    public float Distance { get; set; } = 10f;
    public float Yaw { get; set; } = 45f;
    public float Pitch { get; set; } = 30f;

    // Projection
    public float FieldOfView { get; set; } = 60f;
    public float NearClip { get; set; } = 0.1f;
    public float FarClip { get; set; } = 1000f;

    // Sensitivity
    public float OrbitSpeed { get; set; } = 0.3f;
    public float PanSpeed { get; set; } = 0.01f;
    public float ZoomSpeed { get; set; } = 1.0f;
    public float MinDistance { get; set; } = 0.5f;
    public float MaxDistance { get; set; } = 500f;

    // Fly-camera settings
    public float FlySpeed { get; set; } = 10f;
    public float SprintMultiplier { get; set; } = 2.5f;
    public float LookSpeed { get; set; } = 0.2f;

    /// <summary>
    /// Computes the camera world position from orbit parameters.
    /// </summary>
    public Float3 GetPosition()
    {
        float yawRad = Yaw * (MathF.PI / 180f);
        float pitchRad = Pitch * (MathF.PI / 180f);

        Float3 offset = new(
            MathF.Cos(pitchRad) * MathF.Sin(yawRad),
            MathF.Sin(pitchRad),
            MathF.Cos(pitchRad) * MathF.Cos(yawRad)
        );

        return Pivot + offset * Distance;
    }

    /// <summary>
    /// Computes the camera rotation looking from position toward pivot.
    /// </summary>
    public Quaternion GetRotation()
    {
        Float3 pos = GetPosition();
        Float3 forward = Float3.Normalize(Pivot - pos);
        return LookRotation(forward, Float3.UnitY);
    }

    public Float4x4 GetViewMatrix()
    {
        Float3 pos = GetPosition();
        return Float4x4.CreateLookAt(pos, Pivot, Float3.UnitY);
    }

    public Float4x4 GetProjectionMatrix(float aspect)
    {
        return Float4x4.CreatePerspectiveFov(FieldOfView * (MathF.PI / 180f), aspect, NearClip, FarClip);
    }

    /// <summary>
    /// Processes mouse/keyboard input for orbit, pan, zoom, and fly-camera.
    /// <para>
    /// <b>Orbit mode:</b> Alt + LMB drag to orbit, MMB drag to pan, scroll to zoom.<br/>
    /// <b>Fly mode (RMB held):</b> WASD to move, Q/E to descend/ascend,
    /// mouse to look, Shift to sprint.
    /// </para>
    /// </summary>
    public void ProcessInput(IEditorInput input, bool isHovered)
    {
        if (!isHovered) return;

        float dt = Runtime.Time.DeltaTime;
        Float2 delta = input.MouseDelta;
        bool rmb = input.IsMouseButton(1);

        // ── Fly-camera mode (RMB held) ─────────────────────────
        if (rmb)
        {
            // Mouse look
            Yaw += delta.X * LookSpeed;
            Pitch += delta.Y * LookSpeed;
            Pitch = Math.Clamp(Pitch, -89f, 89f);

            // Compute camera axes
            Float3 pos = GetPosition();
            Float3 forward = Float3.Normalize(Pivot - pos);
            Float3 right = Float3.Normalize(Float3.Cross(forward, Float3.UnitY));
            Float3 up = Float3.UnitY;

            // Speed
            bool sprint = input.IsKey(KeyCode.ShiftLeft) || input.IsKey(KeyCode.ShiftRight);
            float speed = FlySpeed * (sprint ? SprintMultiplier : 1f) * dt;

            // WASD movement
            Float3 move = Float3.Zero;
            if (input.IsKey(KeyCode.W)) move += forward;
            if (input.IsKey(KeyCode.S)) move -= forward;
            if (input.IsKey(KeyCode.D)) move -= right;
            if (input.IsKey(KeyCode.A)) move += right;
            if (input.IsKey(KeyCode.E)) move += up;
            if (input.IsKey(KeyCode.Q)) move -= up;

            if (move != Float3.Zero)
            {
                move = Float3.Normalize(move) * speed;
                Pivot += move;
            }

            return; // skip orbit/pan/zoom while in fly mode
        }

        // ── Orbit mode ─────────────────────────────────────────
        // Alt + Left-drag → orbit
        bool alt = input.IsKey(KeyCode.AltLeft) || input.IsKey(KeyCode.AltRight);
        if (alt && input.IsMouseButton(0))
        {
            Yaw -= delta.X * OrbitSpeed;
            Pitch += delta.Y * OrbitSpeed;
            Pitch = Math.Clamp(Pitch, -89f, 89f);
        }

        // Middle-drag → pan
        if (input.IsMouseButton(2))
        {
            Float3 pos = GetPosition();
            Float3 forward = Float3.Normalize(Pivot - pos);
            Float3 right = Float3.Normalize(Float3.Cross(forward, Float3.UnitY));
            Float3 upVec = Float3.Cross(right, forward);

            Pivot += right * delta.X * PanSpeed * Distance;
            Pivot += upVec * delta.Y * PanSpeed * Distance;
        }

        // Scroll → zoom
        float scroll = input.ScrollDelta;
        if (scroll != 0)
        {
            Distance -= scroll * ZoomSpeed;
            Distance = Math.Clamp(Distance, MinDistance, MaxDistance);
        }
    }

    /// <summary>
    /// Snap the camera to look at a world position.
    /// </summary>
    public void FocusOn(Float3 target, float distance = -1f)
    {
        Pivot = target;
        if (distance > 0f)
            Distance = distance;
    }

    /// <summary>
    /// Projects a world position to viewport-local screen coordinates.
    /// Returns (x, y) in pixels relative to the viewport's top-left,
    /// and z as the NDC depth (0..1 range; negative means behind camera).
    /// </summary>
    public Float3 WorldToViewport(Float3 worldPos, float vpWidth, float vpHeight)
    {
        float aspect = vpWidth / Math.Max(vpHeight, 1f);
        Float4x4 vp = GetProjectionMatrix(aspect) * GetViewMatrix();
        Float4 clip = Float4x4.TransformPoint(new Float4(worldPos, 1f), vp);

        if (clip.W == 0f) return new Float3(-1, -1, -1);

        Float3 ndc = new(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);

        float screenX = (ndc.X * 0.5f + 0.5f) * vpWidth;
        float screenY = (1f - (ndc.Y * 0.5f + 0.5f)) * vpHeight; // flip Y for top-left origin

        return new Float3(screenX, screenY, ndc.Z);
    }

    /// <summary>
    /// Converts a viewport-local pixel position to a world-space ray
    /// (origin + direction). Used for mouse picking in the scene view.
    /// </summary>
    public (Float3 origin, Float3 direction) ViewportToRay(Float2 viewportPos, float vpWidth, float vpHeight)
    {
        float aspect = vpWidth / Math.Max(vpHeight, 1f);

        // Normalise to [-1, 1] NDC
        float ndcX = (viewportPos.X / vpWidth) * 2f - 1f;
        float ndcY = 1f - (viewportPos.Y / vpHeight) * 2f; // flip Y

        Float4 nearNDC = new(ndcX, ndcY, 0f, 1f);
        Float4 farNDC  = new(ndcX, ndcY, 1f, 1f);

        Float4x4 vpMat = GetProjectionMatrix(aspect) * GetViewMatrix();
        Float4x4 inv   = vpMat.Invert();

        Float4 nearW = Float4x4.TransformPoint(nearNDC, inv);
        Float4 farW  = Float4x4.TransformPoint(farNDC, inv);

        if (nearW.W != 0f) nearW /= nearW.W;
        if (farW.W != 0f) farW /= farW.W;

        Float3 origin = new(nearW.X, nearW.Y, nearW.Z);
        Float3 far3   = new(farW.X, farW.Y, farW.Z);
        Float3 dir    = Float3.Normalize(far3 - origin);

        return (origin, dir);
    }

    // Builds a quaternion that looks along 'forward' with the given 'up' hint.
    private static Quaternion LookRotation(Float3 forward, Float3 up)
    {
        Float3 f = Float3.Normalize(forward);
        Float3 r = Float3.Normalize(Float3.Cross(up, f));
        Float3 u = Float3.Cross(f, r);

        float m00 = r.X, m01 = u.X, m02 = f.X;
        float m10 = r.Y, m11 = u.Y, m12 = f.Y;
        float m20 = r.Z, m21 = u.Z, m22 = f.Z;

        float trace = m00 + m11 + m22;
        Quaternion q;

        if (trace > 0f)
        {
            float s = 0.5f / MathF.Sqrt(trace + 1f);
            q = new Quaternion((m21 - m12) * s, (m02 - m20) * s, (m10 - m01) * s, 0.25f / s);
        }
        else if (m00 > m11 && m00 > m22)
        {
            float s = 2f * MathF.Sqrt(1f + m00 - m11 - m22);
            q = new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
        }
        else if (m11 > m22)
        {
            float s = 2f * MathF.Sqrt(1f + m11 - m00 - m22);
            q = new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
        }
        else
        {
            float s = 2f * MathF.Sqrt(1f + m22 - m00 - m11);
            q = new Quaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
        }

        return Quaternion.NormalizeSafe(q);
    }
}
