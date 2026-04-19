// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Echo;
using Prowl.Vector;

namespace Prowl.Runtime.Frame;

/// <summary>
/// Maintains a fixed offset from the Follow target.
/// Equivalent to Cinemachine Transposer.
/// </summary>
public class FrameTransposer : FrameBodyComponent
{
    public enum BindingMode
    {
        LockToTarget,
        LockToTargetWithWorldUp,
        WorldSpace,
    }

    public Float3 FollowOffset = new(0, 2, -10);
    public BindingMode Binding = BindingMode.LockToTargetWithWorldUp;

    [Range(0f, 20f)] public float XDamping = 1f;
    [Range(0f, 20f)] public float YDamping = 1f;
    [Range(0f, 20f)] public float ZDamping = 1f;

    private Float3 _previousPosition;
    private bool _initialized;

    public override void MutateState(ref CameraState state, Transform? follow, Transform? lookAt, float deltaTime)
    {
        if (follow == null) return;

        Float3 targetPos = follow.Position;
        Float3 desiredPos = Binding switch
        {
            BindingMode.WorldSpace => targetPos + FollowOffset,
            BindingMode.LockToTarget => targetPos + (follow.Rotation * FollowOffset),
            BindingMode.LockToTargetWithWorldUp => targetPos + ComputeWorldUpOffset(follow),
            _ => targetPos + FollowOffset,
        };

        if (!_initialized) { _previousPosition = desiredPos; _initialized = true; }

        Float3 delta = desiredPos - _previousPosition;
        float dampX = Damp(XDamping, deltaTime);
        float dampY = Damp(YDamping, deltaTime);
        float dampZ = Damp(ZDamping, deltaTime);
        Float3 dampedPos = _previousPosition + new Float3(delta.X * dampX, delta.Y * dampY, delta.Z * dampZ);
        _previousPosition = dampedPos;
        state.Position = dampedPos;
    }

    private Float3 ComputeWorldUpOffset(Transform follow)
    {
        Float3 fwd = follow.Forward;
        float yaw = Maths.Atan2(fwd.X, fwd.Z);
        Quaternion yawRot = Quaternion.AxisAngle(Float3.UnitY, yaw);
        return yawRot * FollowOffset;
    }

    private static float Damp(float damping, float dt)
    {
        if (damping <= 0f || dt <= 0f) return 1f;
        return 1f - Maths.Exp(-dt / (damping * 0.1f));
    }
}

/// <summary>
/// Hard-locks the camera position to the Follow target.
/// </summary>
public class FrameHardLockToTarget : FrameBodyComponent
{
    public Float3 Offset = Float3.Zero;

    public override void MutateState(ref CameraState state, Transform? follow, Transform? lookAt, float deltaTime)
    {
        if (follow == null) return;
        state.Position = follow.Position + Offset;
    }
}

/// <summary>
/// Orbital transposer — follows target while allowing player-controlled horizontal orbit.
/// Equivalent to Cinemachine OrbitalTransposer.
/// </summary>
public class FrameOrbitalTransposer : FrameBodyComponent
{
    public Float3 FollowOffset = new(0, 2, -10);
    public float Heading = 0f;
    public float HeadingSpeed = 120f;

    [Range(0f, 20f)] public float XDamping = 1f;
    [Range(0f, 20f)] public float YDamping = 1f;
    [Range(0f, 20f)] public float ZDamping = 1f;

    private Float3 _previousPosition;
    private bool _initialized;

    public void AddHeadingDelta(float degrees) => Heading += degrees;

    public override void MutateState(ref CameraState state, Transform? follow, Transform? lookAt, float deltaTime)
    {
        if (follow == null) return;

        Float3 targetPos = follow.Position;
        float headingRad = Maths.ToRadians(Heading);
        Quaternion headingRot = Quaternion.AxisAngle(Float3.UnitY, headingRad);
        Float3 desiredPos = targetPos + (headingRot * FollowOffset);

        if (!_initialized) { _previousPosition = desiredPos; _initialized = true; }

        Float3 delta = desiredPos - _previousPosition;
        float dampX = Damp(XDamping, deltaTime);
        float dampY = Damp(YDamping, deltaTime);
        float dampZ = Damp(ZDamping, deltaTime);
        Float3 dampedPos = _previousPosition + new Float3(delta.X * dampX, delta.Y * dampY, delta.Z * dampZ);
        _previousPosition = dampedPos;
        state.Position = dampedPos;
    }

    private static float Damp(float damping, float dt)
    {
        if (damping <= 0f || dt <= 0f) return 1f;
        return 1f - Maths.Exp(-dt / (damping * 0.1f));
    }
}

/// <summary>
/// Framing transposer — positions camera to frame the follow target within screen-space regions.
/// Equivalent to Cinemachine FramingTransposer.
/// </summary>
public class FrameFramingTransposer : FrameBodyComponent
{
    [Range(0.1f, 500f)] public float CameraDistance = 10f;
    [Range(0f, 1f)] public float ScreenX = 0.5f;
    [Range(0f, 1f)] public float ScreenY = 0.5f;
    [Range(0f, 1f)] public float DeadZoneWidth = 0.1f;
    [Range(0f, 1f)] public float DeadZoneHeight = 0.1f;
    [Range(0f, 2f)] public float SoftZoneWidth = 0.8f;
    [Range(0f, 2f)] public float SoftZoneHeight = 0.8f;
    [Range(0f, 20f)] public float XDamping = 1f;
    [Range(0f, 20f)] public float YDamping = 1f;
    [Range(0f, 20f)] public float ZDamping = 1f;

    private Float3 _previousPosition;
    private bool _initialized;

    public override void MutateState(ref CameraState state, Transform? follow, Transform? lookAt, float deltaTime)
    {
        if (follow == null) return;

        Float3 targetPos = follow.Position;
        Float3 camFwd = state.Forward;
        Float3 desiredPos = targetPos - camFwd * CameraDistance;

        Float3 right = state.Right;
        Float3 up = state.Up;
        float hFov = state.FieldOfView * 0.5f;
        float extent = Maths.Tan(Maths.ToRadians(hFov)) * CameraDistance;

        desiredPos += right * ((ScreenX - 0.5f) * 2f * extent);
        desiredPos += up * ((ScreenY - 0.5f) * 2f * extent);

        if (!_initialized) { _previousPosition = desiredPos; _initialized = true; }

        Float3 delta = desiredPos - _previousPosition;
        float dampX = Damp(XDamping, deltaTime);
        float dampY = Damp(YDamping, deltaTime);
        float dampZ = Damp(ZDamping, deltaTime);
        Float3 dampedPos = _previousPosition + new Float3(delta.X * dampX, delta.Y * dampY, delta.Z * dampZ);
        _previousPosition = dampedPos;
        state.Position = dampedPos;
    }

    private static float Damp(float damping, float dt)
    {
        if (damping <= 0f || dt <= 0f) return 1f;
        return 1f - Maths.Exp(-dt / (damping * 0.1f));
    }
}

/// <summary>
/// Moves the camera along a <see cref="FramePath"/> based on the closest point to Follow target.
/// Equivalent to Cinemachine TrackedDolly.
/// </summary>
public class FrameTrackedDolly : FrameBodyComponent
{
    [SerializeIgnore] public FramePath? Path;

    public Float3 PathOffset = Float3.Zero;
    public float PathPosition = -1f;
    [Range(0f, 20f)] public float Damping = 1f;

    private float _currentPathPosition;
    private bool _initialized;

    public override void MutateState(ref CameraState state, Transform? follow, Transform? lookAt, float deltaTime)
    {
        if (Path == null) return;

        float targetT;
        if (PathPosition >= 0f)
            targetT = PathPosition;
        else if (follow != null)
            targetT = Path.FindClosestPoint(follow.Position);
        else
            targetT = 0f;

        if (!_initialized) { _currentPathPosition = targetT; _initialized = true; }

        float damp = Damping <= 0f ? 1f : 1f - Maths.Exp(-deltaTime / (Damping * 0.1f));
        _currentPathPosition += (targetT - _currentPathPosition) * damp;

        Float3 pos = Path.EvaluatePosition(_currentPathPosition);
        Quaternion rot = Path.EvaluateOrientation(_currentPathPosition);
        state.Position = pos + (rot * PathOffset);
    }
}
