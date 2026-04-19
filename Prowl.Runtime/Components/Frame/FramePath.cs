// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Echo;
using Prowl.Vector;

namespace Prowl.Runtime.Frame;

/// <summary>
/// A spline/path for dolly tracks with Catmull-Rom interpolation.
/// Equivalent to Cinemachine SmoothPath / CinemachinePath.
/// </summary>
[AddComponentMenu("Frame/Path")]
public class FramePath : MonoBehaviour
{
    [Serializable]
    public struct Waypoint
    {
        public Float3 Position;
        public Float3 Tangent;
    }

    public List<Waypoint> Waypoints = [];
    public bool Looped = false;
    [Range(4, 64)] public int Resolution = 20;

    public int SpanCount => Looped ? Waypoints.Count : Maths.Max(0, Waypoints.Count - 1);
    public float MaxPos => SpanCount;

    public Float3 EvaluatePosition(float t)
    {
        if (Waypoints.Count == 0) return Transform.Position;
        if (Waypoints.Count == 1) return LocalToWorld(Waypoints[0].Position);

        t = ClampPathPos(t);
        int spanIdx = (int)Maths.Floor(t);
        if (spanIdx >= SpanCount) spanIdx = SpanCount - 1;
        float frac = t - spanIdx;

        GetSpanPoints(spanIdx, out Float3 p0, out Float3 p1, out Float3 t0, out Float3 t1);
        return LocalToWorld(HermiteInterpolate(p0, p1, t0, t1, frac));
    }

    public Quaternion EvaluateOrientation(float t)
    {
        Float3 fwd = EvaluateTangent(t);
        if (Float3.LengthSquared(fwd) < 0.0001f) return Quaternion.Identity;
        return Quaternion.LookRotation(Float3.Normalize(fwd), Float3.UnitY);
    }

    public Float3 EvaluateTangent(float t)
    {
        if (Waypoints.Count < 2) return new Float3(0, 0, 1);

        t = ClampPathPos(t);
        int spanIdx = (int)Maths.Floor(t);
        if (spanIdx >= SpanCount) spanIdx = SpanCount - 1;
        float frac = t - spanIdx;

        GetSpanPoints(spanIdx, out Float3 p0, out Float3 p1, out Float3 t0, out Float3 t1);
        Float3 localTangent = HermiteTangent(p0, p1, t0, t1, frac);
        return Transform.Rotation * localTangent;
    }

    public float FindClosestPoint(Float3 worldPos)
    {
        if (SpanCount == 0) return 0f;

        float bestT = 0f;
        float bestDist = float.MaxValue;
        int totalSteps = SpanCount * Resolution;
        float step = MaxPos / totalSteps;

        for (int i = 0; i <= totalSteps; i++)
        {
            float tp = i * step;
            Float3 p = EvaluatePosition(tp);
            float d = Float3.LengthSquared(p - worldPos);
            if (d < bestDist) { bestDist = d; bestT = tp; }
        }
        return bestT;
    }

    private float ClampPathPos(float t)
    {
        if (Looped)
        {
            float max = MaxPos;
            if (max <= 0f) return 0f;
            t %= max;
            if (t < 0f) t += max;
            return t;
        }
        return Maths.Clamp(t, 0f, Maths.Max(0f, MaxPos));
    }

    private void GetSpanPoints(int idx, out Float3 p0, out Float3 p1, out Float3 t0, out Float3 t1)
    {
        int count = Waypoints.Count;
        int i0 = idx % count;
        int i1 = (idx + 1) % count;

        p0 = Waypoints[i0].Position;
        p1 = Waypoints[i1].Position;

        t0 = Waypoints[i0].Tangent;
        if (Float3.LengthSquared(t0) < 0.0001f)
        {
            int iPrev = Looped ? (i0 - 1 + count) % count : Maths.Max(0, i0 - 1);
            t0 = (p1 - Waypoints[iPrev].Position) * 0.5f;
        }

        t1 = Waypoints[i1].Tangent;
        if (Float3.LengthSquared(t1) < 0.0001f)
        {
            int iNext = Looped ? (i1 + 1) % count : Maths.Min(count - 1, i1 + 1);
            t1 = (Waypoints[iNext].Position - p0) * 0.5f;
        }
    }

    private Float3 LocalToWorld(Float3 local)
    {
        return Transform.Position + (Transform.Rotation * (local * Transform.LocalScale));
    }

    private static Float3 HermiteInterpolate(Float3 p0, Float3 p1, Float3 t0, Float3 t1, float s)
    {
        float s2 = s * s;
        float s3 = s2 * s;
        float h00 = 2f * s3 - 3f * s2 + 1f;
        float h10 = s3 - 2f * s2 + s;
        float h01 = -2f * s3 + 3f * s2;
        float h11 = s3 - s2;
        return p0 * h00 + t0 * h10 + p1 * h01 + t1 * h11;
    }

    private static Float3 HermiteTangent(Float3 p0, Float3 p1, Float3 t0, Float3 t1, float s)
    {
        float s2 = s * s;
        float h00 = 6f * s2 - 6f * s;
        float h10 = 3f * s2 - 4f * s + 1f;
        float h01 = -6f * s2 + 6f * s;
        float h11 = 3f * s2 - 2f * s;
        return p0 * h00 + t0 * h10 + p1 * h01 + t1 * h11;
    }
}
