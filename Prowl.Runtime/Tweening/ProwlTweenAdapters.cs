// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.CompilerServices;

using Prowl.Tweening;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// Interpolators for Prowl's own math types, so <see cref="Tween.To{T, TAdapter}"/> works with
/// <see cref="Float2"/>, <see cref="Float3"/>, <see cref="Float4"/>, <see cref="Quaternion"/>
/// and <see cref="Color"/> the same way it does with the built-in ones.
/// </summary>
/// <remarks>
/// Each adapter is a <c>readonly struct</c> used as a generic argument, so the JIT inlines the
/// interpolation into the update loop instead of dispatching through an interface.
/// </remarks>
public readonly struct Float2Adapter : ITweenAdapter<Float2>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Float2 Evaluate(in Float2 start, in Float2 end, float progress) => start + (end - start) * progress;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Float2 Add(in Float2 a, in Float2 b) => a + b;
}

/// <inheritdoc cref="Float2Adapter"/>
public readonly struct Float3Adapter : ITweenAdapter<Float3>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Float3 Evaluate(in Float3 start, in Float3 end, float progress) => start + (end - start) * progress;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Float3 Add(in Float3 a, in Float3 b) => a + b;
}

/// <inheritdoc cref="Float2Adapter"/>
public readonly struct Float4Adapter : ITweenAdapter<Float4>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Float4 Evaluate(in Float4 start, in Float4 end, float progress) => start + (end - start) * progress;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Float4 Add(in Float4 a, in Float4 b) => a + b;
}

/// <summary>
/// Interpolates rotations along the shortest arc using a normalized lerp, then renormalizes.
/// </summary>
/// <inheritdoc cref="Float2Adapter"/>
public readonly struct RotationAdapter : ITweenAdapter<Quaternion>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Quaternion Evaluate(in Quaternion start, in Quaternion end, float progress)
    {
        Quaternion b = end;
        if (Quaternion.Dot(start, end) < 0f)
            b = new Quaternion(-end.X, -end.Y, -end.Z, -end.W);

        Quaternion r = new(
            start.X + (b.X - start.X) * progress,
            start.Y + (b.Y - start.Y) * progress,
            start.Z + (b.Z - start.Z) * progress,
            start.W + (b.W - start.W) * progress);

        return Quaternion.NormalizeSafe(r);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Quaternion Add(in Quaternion a, in Quaternion b) => Quaternion.NormalizeSafe(b * a);
}

/// <summary>Interpolates all four channels linearly.</summary>
/// <inheritdoc cref="Float2Adapter"/>
public readonly struct ColorAdapter : ITweenAdapter<Color>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Color Evaluate(in Color start, in Color end, float progress) => new(
        start.R + (end.R - start.R) * progress,
        start.G + (end.G - start.G) * progress,
        start.B + (end.B - start.B) * progress,
        start.A + (end.A - start.A) * progress);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Color Add(in Color a, in Color b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);
}
