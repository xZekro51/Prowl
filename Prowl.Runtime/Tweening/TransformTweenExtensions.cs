// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Tweening;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// Tween shortcuts for <see cref="Transform"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every shortcut captures the transform's current value as the start, tags the tween with the
/// transform as its target - so <c>Tween.KillAll(transform)</c> stops everything animating it -
/// and returns the handle for chaining:
/// </para>
/// <code>
/// transform.Move(new Float3(0, 5, 0), 1f)
///          .SetEase(Ease.OutBack)
///          .SetLoops(-1, LoopType.Yoyo);
/// </code>
/// <para>
/// All of them are allocation-free: the setters are cached static lambdas and the transform is
/// passed as state rather than captured.
/// </para>
/// </remarks>
public static class TransformTweenExtensions
{
    #region Position

    /// <summary>Moves the transform to a world-space position.</summary>
    public static Tween Move(this Transform transform, Float3 to, float duration)
        => Tween.To<Float3, Float3Adapter>(transform.Position, to, duration)
            .Bind(transform, static (v, t) => t.Position = v)
            .SetTarget(transform);

    /// <summary>Moves the transform along the world X axis, leaving Y and Z alone.</summary>
    public static Tween MoveX(this Transform transform, float to, float duration)
        => Tween.To(transform.Position.X, to, duration)
            .Bind(transform, static (v, t) => { Float3 p = t.Position; p.X = v; t.Position = p; })
            .SetTarget(transform);

    /// <summary>Moves the transform along the world Y axis, leaving X and Z alone.</summary>
    public static Tween MoveY(this Transform transform, float to, float duration)
        => Tween.To(transform.Position.Y, to, duration)
            .Bind(transform, static (v, t) => { Float3 p = t.Position; p.Y = v; t.Position = p; })
            .SetTarget(transform);

    /// <summary>Moves the transform along the world Z axis, leaving X and Y alone.</summary>
    public static Tween MoveZ(this Transform transform, float to, float duration)
        => Tween.To(transform.Position.Z, to, duration)
            .Bind(transform, static (v, t) => { Float3 p = t.Position; p.Z = v; t.Position = p; })
            .SetTarget(transform);

    /// <summary>Moves the transform to a position relative to its parent.</summary>
    public static Tween LocalMove(this Transform transform, Float3 to, float duration)
        => Tween.To<Float3, Float3Adapter>(transform.LocalPosition, to, duration)
            .Bind(transform, static (v, t) => t.LocalPosition = v)
            .SetTarget(transform);

    /// <summary>Moves the transform along its parent's X axis.</summary>
    public static Tween LocalMoveX(this Transform transform, float to, float duration)
        => Tween.To(transform.LocalPosition.X, to, duration)
            .Bind(transform, static (v, t) => { Float3 p = t.LocalPosition; p.X = v; t.LocalPosition = p; })
            .SetTarget(transform);

    /// <summary>Moves the transform along its parent's Y axis.</summary>
    public static Tween LocalMoveY(this Transform transform, float to, float duration)
        => Tween.To(transform.LocalPosition.Y, to, duration)
            .Bind(transform, static (v, t) => { Float3 p = t.LocalPosition; p.Y = v; t.LocalPosition = p; })
            .SetTarget(transform);

    /// <summary>Moves the transform along its parent's Z axis.</summary>
    public static Tween LocalMoveZ(this Transform transform, float to, float duration)
        => Tween.To(transform.LocalPosition.Z, to, duration)
            .Bind(transform, static (v, t) => { Float3 p = t.LocalPosition; p.Z = v; t.LocalPosition = p; })
            .SetTarget(transform);

    #endregion

    #region Rotation

    // Named RotateTo rather than Rotate on purpose: Transform already has an instance method
    // Rotate(Float3 axis, float angle), and an instance method always wins over an extension - so
    // transform.Rotate(euler, 1f) would silently snap-rotate instead of starting a tween.

    /// <summary>
    /// Rotates to the given world-space euler angles, in degrees. The angles themselves are
    /// interpolated, so the transform passes through every intermediate value - pass a
    /// <see cref="Quaternion"/> instead to take the shortest arc.
    /// </summary>
    public static Tween RotateTo(this Transform transform, Float3 toEulerAngles, float duration)
        => Tween.To<Float3, Float3Adapter>(transform.EulerAngles, toEulerAngles, duration)
            .Bind(transform, static (v, t) => t.EulerAngles = v)
            .SetTarget(transform);

    /// <summary>Rotates to a world-space orientation along the shortest arc.</summary>
    public static Tween RotateTo(this Transform transform, Quaternion to, float duration)
        => Tween.To<Quaternion, RotationAdapter>(transform.Rotation, to, duration)
            .Bind(transform, static (v, t) => t.Rotation = v)
            .SetTarget(transform);

    /// <summary>Rotates to euler angles relative to the parent, in degrees.</summary>
    public static Tween LocalRotateTo(this Transform transform, Float3 toEulerAngles, float duration)
        => Tween.To<Float3, Float3Adapter>(transform.LocalEulerAngles, toEulerAngles, duration)
            .Bind(transform, static (v, t) => t.LocalEulerAngles = v)
            .SetTarget(transform);

    /// <summary>Rotates to an orientation relative to the parent, along the shortest arc.</summary>
    public static Tween LocalRotateTo(this Transform transform, Quaternion to, float duration)
        => Tween.To<Quaternion, RotationAdapter>(transform.LocalRotation, to, duration)
            .Bind(transform, static (v, t) => t.LocalRotation = v)
            .SetTarget(transform);

    /// <summary>Turns the transform to face a world-space point, keeping world up.</summary>
    public static Tween LookAt(this Transform transform, Float3 target, float duration)
        => LookAt(transform, target, Float3.UnitY, duration);

    /// <summary>Turns the transform to face a world-space point, using an explicit up vector.</summary>
    public static Tween LookAt(this Transform transform, Float3 target, Float3 worldUp, float duration)
    {
        Float3 direction = target - transform.Position;
        Quaternion to = Float3.LengthSquared(direction) > 0f
            ? Quaternion.LookRotation(Float3.Normalize(direction), worldUp)
            : transform.Rotation;

        return RotateTo(transform, to, duration);
    }

    #endregion

    #region Scale

    /// <summary>Scales the transform relative to its parent.</summary>
    public static Tween Scale(this Transform transform, Float3 to, float duration)
        => Tween.To<Float3, Float3Adapter>(transform.LocalScale, to, duration)
            .Bind(transform, static (v, t) => t.LocalScale = v)
            .SetTarget(transform);

    /// <summary>Scales the transform uniformly on all three axes.</summary>
    public static Tween Scale(this Transform transform, float to, float duration)
        => Scale(transform, new Float3(to, to, to), duration);

    /// <summary>Scales the transform on its X axis only.</summary>
    public static Tween ScaleX(this Transform transform, float to, float duration)
        => Tween.To(transform.LocalScale.X, to, duration)
            .Bind(transform, static (v, t) => { Float3 s = t.LocalScale; s.X = v; t.LocalScale = s; })
            .SetTarget(transform);

    /// <summary>Scales the transform on its Y axis only.</summary>
    public static Tween ScaleY(this Transform transform, float to, float duration)
        => Tween.To(transform.LocalScale.Y, to, duration)
            .Bind(transform, static (v, t) => { Float3 s = t.LocalScale; s.Y = v; t.LocalScale = s; })
            .SetTarget(transform);

    /// <summary>Scales the transform on its Z axis only.</summary>
    public static Tween ScaleZ(this Transform transform, float to, float duration)
        => Tween.To(transform.LocalScale.Z, to, duration)
            .Bind(transform, static (v, t) => { Float3 s = t.LocalScale; s.Z = v; t.LocalScale = s; })
            .SetTarget(transform);

    #endregion

    /// <summary>Kills every tween currently animating this transform, returning how many.</summary>
    public static int KillTweens(this Transform transform, bool complete = false)
        => Tween.KillAll(transform, complete);
}
