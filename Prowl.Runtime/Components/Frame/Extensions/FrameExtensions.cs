// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Echo;
using Prowl.Vector;

namespace Prowl.Runtime.Frame;

/// <summary>
/// Confines the camera position within an AABB.
/// Equivalent to Cinemachine Confiner / Confiner2D.
/// </summary>
[AddComponentMenu("Frame/Confiner")]
public class FrameConfiner : FrameExtension
{
    public bool Mode2D = false;
    public Float3 BoundsMin = new(-50, -50, -50);
    public Float3 BoundsMax = new(50, 50, 50);
    [Range(0f, 5f)] public float Damping = 0.2f;

    private Float3 _previousCorrection;
    private bool _initialized;

    public override void PostPipelineStage(ref CameraState state, float deltaTime)
    {
        Float3 pos = state.Position;
        Float3 confined = new(
            Maths.Clamp(pos.X, BoundsMin.X, BoundsMax.X),
            Maths.Clamp(pos.Y, BoundsMin.Y, BoundsMax.Y),
            Mode2D ? pos.Z : Maths.Clamp(pos.Z, BoundsMin.Z, BoundsMax.Z)
        );

        Float3 correction = confined - pos;

        if (!_initialized) { _previousCorrection = correction; _initialized = true; }

        if (Damping > 0f && deltaTime > 0f)
        {
            float damp = 1f - Maths.Exp(-deltaTime / (Damping * 0.1f));
            correction = Maths.Lerp(_previousCorrection, correction, damp);
        }
        _previousCorrection = correction;
        state.Position = pos + correction;
    }
}

/// <summary>
/// Prevents the camera from clipping through geometry by pushing it forward.
/// Equivalent to Cinemachine Collider extension.
/// </summary>
[AddComponentMenu("Frame/Collider")]
public class FrameColliderExtension : FrameExtension
{
    [Range(0.01f, 5f)] public float MinDistanceFromTarget = 0.5f;
    [Range(0f, 5f)] public float Damping = 0.1f;

    /// <summary>
    /// User-provided raycast callback: (origin, direction, maxDistance) => hitDistance or -1.
    /// </summary>
    [SerializeIgnore]
    public Func<Float3, Float3, float, float>? RaycastCallback;

    private float _previousPushBack;
    private bool _initialized;

    public override void PostPipelineStage(ref CameraState state, float deltaTime)
    {
        if (RaycastCallback == null) return;

        Float3 camPos = state.Position;
        Float3 fwd = state.Forward;
        Float3 origin = camPos + fwd * MinDistanceFromTarget;
        Float3 dir = -fwd;
        float maxDist = 100f;

        float hitDist = RaycastCallback(origin, dir, maxDist);
        float pushBack = 0f;

        if (hitDist >= 0f && hitDist < maxDist)
            pushBack = Maths.Max(0f, MinDistanceFromTarget - hitDist);

        if (!_initialized) { _previousPushBack = pushBack; _initialized = true; }

        if (Damping > 0f && deltaTime > 0f)
        {
            float damp = 1f - Maths.Exp(-deltaTime / (Damping * 0.1f));
            pushBack = Maths.Lerp(_previousPushBack, pushBack, damp);
        }
        _previousPushBack = pushBack;

        if (pushBack > 0.001f)
            state.Position = camPos + fwd * pushBack;
    }
}
