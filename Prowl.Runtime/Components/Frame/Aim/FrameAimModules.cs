// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Echo;
using Prowl.Vector;

namespace Prowl.Runtime.Frame;

/// <summary>
/// Rotates the camera to look at the LookAt target with dead-zone/soft-zone composition.
/// Equivalent to Cinemachine Composer.
/// </summary>
public class FrameComposer : FrameAimComponent
{
    [Range(0f, 1f)] public float ScreenX = 0.5f;
    [Range(0f, 1f)] public float ScreenY = 0.5f;
    [Range(0f, 1f)] public float DeadZoneWidth = 0.1f;
    [Range(0f, 1f)] public float DeadZoneHeight = 0.1f;
    [Range(0f, 2f)] public float SoftZoneWidth = 0.8f;
    [Range(0f, 2f)] public float SoftZoneHeight = 0.8f;
    [Range(0f, 20f)] public float HorizontalDamping = 0.5f;
    [Range(0f, 20f)] public float VerticalDamping = 0.5f;

    private Float2 _previousScreenOffset;
    private bool _initialized;

    public override void MutateState(ref CameraState state, Transform? follow, Transform? lookAt, float deltaTime)
    {
        if (lookAt == null) return;

        Float3 targetPos = lookAt.Position;
        Float3 dir = Float3.Normalize(targetPos - state.Position);

        if (Float3.LengthSquared(dir) < 0.0001f) return;

        Float3 localDir = Quaternion.Inverse(state.Orientation) * dir;
        float halfFovRad = Maths.ToRadians(state.FieldOfView * 0.5f);
        Float2 screenOffset = new(
            Maths.Atan2(localDir.X, localDir.Z) / halfFovRad * 0.5f,
            Maths.Atan2(localDir.Y, localDir.Z) / halfFovRad * 0.5f
        );

        Float2 desiredScreen = new(ScreenX - 0.5f, ScreenY - 0.5f);
        Float2 error = screenOffset - desiredScreen;

        float dzHalfX = DeadZoneWidth * 0.5f;
        float dzHalfY = DeadZoneHeight * 0.5f;
        if (Maths.Abs(error.X) < dzHalfX) error = new Float2(0, error.Y);
        if (Maths.Abs(error.Y) < dzHalfY) error = new Float2(error.X, 0);

        if (!_initialized) { _previousScreenOffset = error; _initialized = true; }

        float dampH = Damp(HorizontalDamping, deltaTime);
        float dampV = Damp(VerticalDamping, deltaTime);
        Float2 dampedError = new(
            _previousScreenOffset.X + (error.X - _previousScreenOffset.X) * dampH,
            _previousScreenOffset.Y + (error.Y - _previousScreenOffset.Y) * dampV
        );
        _previousScreenOffset = dampedError;

        float yawCorrection = dampedError.X * state.FieldOfView;
        float pitchCorrection = -dampedError.Y * state.FieldOfView;

        Quaternion yawRot = Quaternion.AxisAngle(Float3.UnitY, Maths.ToRadians(yawCorrection));
        Quaternion pitchRot = Quaternion.AxisAngle(Float3.UnitX, Maths.ToRadians(pitchCorrection));
        state.Orientation = yawRot * state.Orientation * pitchRot;
    }

    private static float Damp(float damping, float dt)
    {
        if (damping <= 0f || dt <= 0f) return 1f;
        return 1f - Maths.Exp(-dt / (damping * 0.1f));
    }
}

/// <summary>
/// Instantly aims at the LookAt target. No damping, no dead zone.
/// Equivalent to Cinemachine HardLookAt.
/// </summary>
public class FrameHardLookAt : FrameAimComponent
{
    public override void MutateState(ref CameraState state, Transform? follow, Transform? lookAt, float deltaTime)
    {
        if (lookAt == null) return;
        Float3 dir = lookAt.Position - state.Position;
        if (Float3.LengthSquared(dir) < 0.0001f) return;
        state.Orientation = Quaternion.LookRotation(Float3.Normalize(dir), Float3.UnitY);
    }
}

/// <summary>
/// Player-controlled aim via input axes (yaw + pitch).
/// Equivalent to Cinemachine POV.
/// </summary>
public class FramePOV : FrameAimComponent
{
    public float HorizontalAngle = 0f;
    public float VerticalAngle = 0f;
    [Range(-89f, 89f)] public float VerticalMin = -70f;
    [Range(-89f, 89f)] public float VerticalMax = 70f;
    public float HorizontalSpeed = 180f;
    public float VerticalSpeed = 180f;

    public void AddInput(float horizontal, float vertical)
    {
        HorizontalAngle += horizontal * HorizontalSpeed;
        VerticalAngle = Maths.Clamp(VerticalAngle + vertical * VerticalSpeed, VerticalMin, VerticalMax);
    }

    public override void MutateState(ref CameraState state, Transform? follow, Transform? lookAt, float deltaTime)
    {
        Quaternion yaw = Quaternion.AxisAngle(Float3.UnitY, Maths.ToRadians(HorizontalAngle));
        Quaternion pitch = Quaternion.AxisAngle(Float3.UnitX, Maths.ToRadians(VerticalAngle));
        state.Orientation = yaw * pitch;
    }
}

/// <summary>
/// Adjusts aim and optionally FOV to frame a <see cref="FrameTargetGroup"/>.
/// Equivalent to Cinemachine GroupComposer.
/// </summary>
public class FrameGroupComposer : FrameAimComponent
{
    public bool AdjustFOV = true;
    [Range(1f, 179f)] public float MinFOV = 20f;
    [Range(1f, 179f)] public float MaxFOV = 80f;
    [Range(0f, 1f)] public float FramingMargin = 0.1f;
    [Range(0f, 20f)] public float Damping = 0.5f;

    private float _previousFov;
    private bool _initialized;

    [SerializeIgnore] public FrameTargetGroup? TargetGroup;

    public override void MutateState(ref CameraState state, Transform? follow, Transform? lookAt, float deltaTime)
    {
        if (TargetGroup == null || TargetGroup.IsEmpty) return;

        Float3 groupCenter = TargetGroup.GroupCenter;
        float groupRadius = TargetGroup.GroupRadius;

        Float3 dir = groupCenter - state.Position;
        if (Float3.LengthSquared(dir) < 0.0001f) return;

        state.Orientation = Quaternion.LookRotation(Float3.Normalize(dir), Float3.UnitY);

        if (AdjustFOV)
        {
            float dist = Float3.Length(dir);
            float requiredHalfAngle = Maths.Atan2(groupRadius * (1f + FramingMargin), dist);
            float requiredFov = Maths.ToDegrees(requiredHalfAngle * 2f);
            float clampedFov = Maths.Clamp(requiredFov, MinFOV, MaxFOV);

            if (!_initialized) { _previousFov = clampedFov; _initialized = true; }

            float damp = Damping <= 0f ? 1f : 1f - Maths.Exp(-deltaTime / (Damping * 0.1f));
            _previousFov += (clampedFov - _previousFov) * damp;
            state.FieldOfView = _previousFov;
        }
    }
}
