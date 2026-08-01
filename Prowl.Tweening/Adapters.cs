// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Prowl.Tweening;

/// <summary>
/// Describes how to interpolate a value type. Implement this on a <c>readonly struct</c>:
/// the storage is generic over the adapter, so the JIT inlines <see cref="Evaluate"/> straight
/// into the update loop with no virtual dispatch.
/// </summary>
/// <remarks>
/// <see cref="Evaluate"/> must be unclamped - <see cref="LoopType.Incremental"/> feeds it
/// progress values above 1.
/// </remarks>
public interface ITweenAdapter<T> where T : unmanaged
{
    T Evaluate(in T start, in T end, float progress);

    /// <summary>Used by <c>SetRelative()</c> to turn an absolute end value into an offset.</summary>
    T Add(in T a, in T b);
}

public readonly struct FloatAdapter : ITweenAdapter<float>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Evaluate(in float start, in float end, float progress) => start + (end - start) * progress;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Add(in float a, in float b) => a + b;
}

public readonly struct DoubleAdapter : ITweenAdapter<double>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Evaluate(in double start, in double end, float progress) => start + (end - start) * progress;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Add(in double a, in double b) => a + b;
}

/// <summary>Interpolates and rounds to the nearest integer, so the value never lands between steps.</summary>
public readonly struct IntAdapter : ITweenAdapter<int>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Evaluate(in int start, in int end, float progress)
        => (int)System.MathF.Round(start + (end - start) * progress);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Add(in int a, in int b) => a + b;
}

public readonly struct LongAdapter : ITweenAdapter<long>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Evaluate(in long start, in long end, float progress)
        => start + (long)System.Math.Round((end - start) * (double)progress);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Add(in long a, in long b) => a + b;
}

public readonly struct Vector2Adapter : ITweenAdapter<Vector2>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 Evaluate(in Vector2 start, in Vector2 end, float progress) => start + (end - start) * progress;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 Add(in Vector2 a, in Vector2 b) => a + b;
}

public readonly struct Vector3Adapter : ITweenAdapter<Vector3>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 Evaluate(in Vector3 start, in Vector3 end, float progress) => start + (end - start) * progress;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 Add(in Vector3 a, in Vector3 b) => a + b;
}

public readonly struct Vector4Adapter : ITweenAdapter<Vector4>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector4 Evaluate(in Vector4 start, in Vector4 end, float progress) => start + (end - start) * progress;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector4 Add(in Vector4 a, in Vector4 b) => a + b;
}

/// <summary>Normalized lerp - cheaper than slerp and stable for the small steps a tween takes.</summary>
public readonly struct QuaternionAdapter : ITweenAdapter<Quaternion>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Quaternion Evaluate(in Quaternion start, in Quaternion end, float progress)
    {
        Quaternion b = end;
        // Take the shortest arc.
        if (Quaternion.Dot(start, end) < 0f)
            b = new Quaternion(-end.X, -end.Y, -end.Z, -end.W);

        Quaternion r = new(
            start.X + (b.X - start.X) * progress,
            start.Y + (b.Y - start.Y) * progress,
            start.Z + (b.Z - start.Z) * progress,
            start.W + (b.W - start.W) * progress);

        return Quaternion.Normalize(r);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Quaternion Add(in Quaternion a, in Quaternion b) => Quaternion.Normalize(b * a);
}

/// <summary>
/// A value that carries no data. Used by tweens that only exist to drive callbacks
/// (delayed calls) or a <see cref="Sequence"/>.
/// </summary>
public readonly struct Unit { }

public readonly struct UnitAdapter : ITweenAdapter<Unit>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Unit Evaluate(in Unit start, in Unit end, float progress) => default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Unit Add(in Unit a, in Unit b) => default;
}
