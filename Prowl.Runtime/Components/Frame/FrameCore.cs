// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Vector;

namespace Prowl.Runtime.Frame;

/// <summary>
/// Immutable snapshot of a virtual camera's output state for a single frame.
/// Kept as a struct for zero-allocation blending.
/// </summary>
public struct CameraState
{
    public Float3 Position;
    public Quaternion Orientation;
    public float FieldOfView;
    public float NearClip;
    public float FarClip;
    public float OrthographicSize;
    public bool IsOrthographic;
    public float Dutch;
    public Float2 LensShift;

    public Float3 Forward => Orientation * new Float3(0, 0, 1);
    public Float3 Up => Orientation * Float3.UnitY;
    public Float3 Right => Orientation * Float3.UnitX;

    public static CameraState Default => new()
    {
        Position = Float3.Zero,
        Orientation = Quaternion.Identity,
        FieldOfView = 60f,
        NearClip = 0.1f,
        FarClip = 100f,
        OrthographicSize = 0.5f,
        IsOrthographic = false,
        Dutch = 0f,
        LensShift = Float2.Zero,
    };

    public static CameraState Lerp(in CameraState a, in CameraState b, float t)
    {
        return new CameraState
        {
            Position = Maths.Lerp(a.Position, b.Position, t),
            Orientation = Quaternion.Slerp(a.Orientation, b.Orientation, t),
            FieldOfView = Maths.Lerp(a.FieldOfView, b.FieldOfView, t),
            NearClip = Maths.Lerp(a.NearClip, b.NearClip, t),
            FarClip = Maths.Lerp(a.FarClip, b.FarClip, t),
            OrthographicSize = Maths.Lerp(a.OrthographicSize, b.OrthographicSize, t),
            IsOrthographic = t < 0.5f ? a.IsOrthographic : b.IsOrthographic,
            Dutch = Maths.Lerp(a.Dutch, b.Dutch, t),
            LensShift = new Float2(
                Maths.Lerp(a.LensShift.X, b.LensShift.X, t),
                Maths.Lerp(a.LensShift.Y, b.LensShift.Y, t)),
        };
    }
}

/// <summary>
/// Defines a blend curve between two virtual cameras.
/// </summary>
public enum BlendStyle
{
    Cut,
    EaseInOut,
    EaseIn,
    EaseOut,
    Linear,
    HardIn,
    HardOut,
}

/// <summary>
/// Describes an in-progress blend between two virtual cameras.
/// </summary>
public struct FrameBlend
{
    public IFrameCamera? CameraA;
    public IFrameCamera? CameraB;
    public BlendStyle Style;
    public float Duration;
    public float TimeInBlend;

    public bool IsComplete => TimeInBlend >= Duration;

    public float BlendWeight
    {
        get
        {
            float t = Duration <= 0f ? 1f : Maths.Clamp(TimeInBlend / Duration, 0f, 1f);
            return EvaluateCurve(t);
        }
    }

    public CameraState State
    {
        get
        {
            CameraState a = CameraA?.State ?? CameraState.Default;
            CameraState b = CameraB?.State ?? CameraState.Default;
            return CameraState.Lerp(a, b, BlendWeight);
        }
    }

    private float EvaluateCurve(float t)
    {
        return Style switch
        {
            BlendStyle.Cut => 1f,
            BlendStyle.Linear => t,
            BlendStyle.EaseIn => t * t,
            BlendStyle.EaseOut => 1f - (1f - t) * (1f - t),
            BlendStyle.EaseInOut => t * t * (3f - 2f * t),
            BlendStyle.HardIn => t < 0.5f ? 0f : (t - 0.5f) * 2f,
            BlendStyle.HardOut => t < 0.5f ? t * 2f : 1f,
            _ => t,
        };
    }
}

/// <summary>
/// Central registry and evaluation hub for the virtual camera system.
/// Manages priorities, blending, and the global virtual camera stack.
/// </summary>
public static class FrameCore
{
    private static readonly List<IFrameCamera> s_cameras = [];
    private static readonly List<FrameDriver> s_brains = [];

    public static IReadOnlyList<IFrameCamera> VirtualCameras => s_cameras;

    public static void ClearAll()
    {
        s_cameras.Clear();
        s_brains.Clear();
    }


    public static void RegisterCamera(IFrameCamera cam)
    {
        if (!s_cameras.Contains(cam))
        {
            s_cameras.Add(cam);
            SortCamerasByPriority();
        }
    }

    public static void UnregisterCamera(IFrameCamera cam)
    {
        s_cameras.Remove(cam);
    }

    public static void RegisterBrain(FrameDriver brain)
    {
        if (!s_brains.Contains(brain))
            s_brains.Add(brain);
    }

    public static void UnregisterBrain(FrameDriver brain)
    {
        s_brains.Remove(brain);
    }

    public static IFrameCamera? GetTopCamera()
    {
        for (int i = 0; i < s_cameras.Count; i++)
        {
            if (s_cameras[i].IsLive)
                return s_cameras[i];
        }
        return null;
    }

    public static void SortCamerasByPriority()
    {
        s_cameras.Sort((a, b) => b.Priority.CompareTo(a.Priority));
    }
}
