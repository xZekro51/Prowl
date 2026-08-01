// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Prowl.Tweening;

/// <summary>
/// Static half of <see cref="Tween"/>: creating tweens, global defaults, bulk control and the
/// per-frame tick.
/// </summary>
public readonly partial struct Tween
{
    #region Global defaults

    /// <summary>Easing applied to tweens that don't choose one themselves.</summary>
    public static Ease DefaultEase = Ease.OutQuad;

    /// <summary>Back overshoot / Elastic amplitude used when none is given.</summary>
    public static float DefaultEaseOvershootOrAmplitude = 1.70158f;

    /// <summary>Elastic period used when none is given. 0 means "derive it from the duration".</summary>
    public static float DefaultEasePeriod = 0f;

    /// <summary>Whether new tweens destroy themselves once complete.</summary>
    public static bool DefaultAutoKill = true;

    /// <summary>
    /// Speed multiplier applied to every tween that isn't marked independent.
    /// Separate from a single tween's own <see cref="TimeScale"/>.
    /// </summary>
    public static float GlobalTimeScale
    {
        get => TweenManager.TimeScale;
        set => TweenManager.TimeScale = value;
    }

    /// <summary>How many tweens are currently alive, across every value type.</summary>
    public static int TotalActive => TweenManager.ActiveTweenCount;

    #endregion

    #region Creation - allocation free

    /// <summary>
    /// Describes a float tween. Nothing is scheduled until you call <c>Bind</c> on the result.
    /// </summary>
    public static TweenBuilder<float, FloatAdapter> To(float from, float to, float duration) => new(in from, in to, duration);

    /// <inheritdoc cref="To(float, float, float)"/>
    public static TweenBuilder<double, DoubleAdapter> To(double from, double to, float duration) => new(in from, in to, duration);

    /// <inheritdoc cref="To(float, float, float)"/>
    public static TweenBuilder<int, IntAdapter> To(int from, int to, float duration) => new(in from, in to, duration);

    /// <inheritdoc cref="To(float, float, float)"/>
    public static TweenBuilder<long, LongAdapter> To(long from, long to, float duration) => new(in from, in to, duration);

    /// <inheritdoc cref="To(float, float, float)"/>
    public static TweenBuilder<Vector2, Vector2Adapter> To(Vector2 from, Vector2 to, float duration) => new(in from, in to, duration);

    /// <inheritdoc cref="To(float, float, float)"/>
    public static TweenBuilder<Vector3, Vector3Adapter> To(Vector3 from, Vector3 to, float duration) => new(in from, in to, duration);

    /// <inheritdoc cref="To(float, float, float)"/>
    public static TweenBuilder<Vector4, Vector4Adapter> To(Vector4 from, Vector4 to, float duration) => new(in from, in to, duration);

    /// <inheritdoc cref="To(float, float, float)"/>
    public static TweenBuilder<Quaternion, QuaternionAdapter> To(Quaternion from, Quaternion to, float duration) => new(in from, in to, duration);

    /// <summary>
    /// Describes a tween of any value type through a custom <see cref="ITweenAdapter{T}"/>.
    /// Each distinct pair gets its own dense storage automatically.
    /// </summary>
    public static TweenBuilder<T, TAdapter> To<T, TAdapter>(in T from, in T to, float duration)
        where T : unmanaged
        where TAdapter : unmanaged, ITweenAdapter<T>
        => new(in from, in to, duration);

    #endregion

    #region Creation - getter/setter

    /// <summary>
    /// Tweens whatever <paramref name="getter"/> reads towards <paramref name="endValue"/>.
    /// Convenient, but the two delegates you pass in are heap allocations - prefer
    /// <see cref="To(float, float, float)"/> plus <c>Bind</c> on hot paths.
    /// </summary>
    public static Tween To(TweenGetter<float> getter, TweenSetter<float> setter, float endValue, float duration)
        => Capture<float, FloatAdapter>(getter, setter, endValue, duration);

    /// <inheritdoc cref="To(TweenGetter{float}, TweenSetter{float}, float, float)"/>
    public static Tween To(TweenGetter<double> getter, TweenSetter<double> setter, double endValue, float duration)
        => Capture<double, DoubleAdapter>(getter, setter, endValue, duration);

    /// <inheritdoc cref="To(TweenGetter{float}, TweenSetter{float}, float, float)"/>
    public static Tween To(TweenGetter<int> getter, TweenSetter<int> setter, int endValue, float duration)
        => Capture<int, IntAdapter>(getter, setter, endValue, duration);

    /// <inheritdoc cref="To(TweenGetter{float}, TweenSetter{float}, float, float)"/>
    public static Tween To(TweenGetter<long> getter, TweenSetter<long> setter, long endValue, float duration)
        => Capture<long, LongAdapter>(getter, setter, endValue, duration);

    /// <inheritdoc cref="To(TweenGetter{float}, TweenSetter{float}, float, float)"/>
    public static Tween To(TweenGetter<Vector2> getter, TweenSetter<Vector2> setter, Vector2 endValue, float duration)
        => Capture<Vector2, Vector2Adapter>(getter, setter, endValue, duration);

    /// <inheritdoc cref="To(TweenGetter{float}, TweenSetter{float}, float, float)"/>
    public static Tween To(TweenGetter<Vector3> getter, TweenSetter<Vector3> setter, Vector3 endValue, float duration)
        => Capture<Vector3, Vector3Adapter>(getter, setter, endValue, duration);

    /// <inheritdoc cref="To(TweenGetter{float}, TweenSetter{float}, float, float)"/>
    public static Tween To(TweenGetter<Vector4> getter, TweenSetter<Vector4> setter, Vector4 endValue, float duration)
        => Capture<Vector4, Vector4Adapter>(getter, setter, endValue, duration);

    /// <inheritdoc cref="To(TweenGetter{float}, TweenSetter{float}, float, float)"/>
    public static Tween To(TweenGetter<Quaternion> getter, TweenSetter<Quaternion> setter, Quaternion endValue, float duration)
        => Capture<Quaternion, QuaternionAdapter>(getter, setter, endValue, duration);

    private static Tween Capture<T, TAdapter>(TweenGetter<T> getter, TweenSetter<T> setter, in T endValue, float duration)
        where T : unmanaged
        where TAdapter : unmanaged, ITweenAdapter<T>
    {
        ArgumentNullException.ThrowIfNull(getter);
        ArgumentNullException.ThrowIfNull(setter);

        // The start value is read now, when the tween is created, not when it first plays.
        T start = getter();
        return TweenManager.Of<T, TAdapter>.Storage.Create(in start, in endValue, duration, Invoker<T>.Instance, setter);
    }

    private static class Invoker<T> where T : unmanaged
    {
        internal static readonly Action<T, object?> Instance = static (value, state) => Unsafe.As<TweenSetter<T>>(state!)(value);
    }

    #endregion

    #region Sequences and helpers

    /// <summary>Creates an empty <see cref="Sequence"/>, ready for <c>Append</c> / <c>Join</c> / <c>Insert</c>.</summary>
    public static Sequence Sequence() => Tweening.Sequence.Create();

    /// <summary>
    /// Runs <paramref name="callback"/> after <paramref name="delay"/> seconds. When
    /// <paramref name="ignoreTimeScale"/> is true the countdown uses unscaled time, so slowing the
    /// game down or pausing it won't hold the callback back.
    /// </summary>
    public static Tween DelayedCall(float delay, Action callback, bool ignoreTimeScale = true)
        => To<Unit, UnitAdapter>(default, default, delay)
            .BindNothing()
            .SetEase(Ease.Linear)
            .SetUpdate(ignoreTimeScale)
            .OnComplete(callback);

    /// <summary>Allocation-free <see cref="DelayedCall(float, Action, bool)"/>: pass state instead of capturing it.</summary>
    public static Tween DelayedCall<TState>(float delay, TState state, Action<TState> callback, bool ignoreTimeScale = true) where TState : class
        => To<Unit, UnitAdapter>(default, default, delay)
            .BindNothing()
            .SetEase(Ease.Linear)
            .SetUpdate(ignoreTimeScale)
            .OnComplete(state, callback);

    /// <summary>Samples an eased value directly, without creating a tween.</summary>
    public static float EasedValue(float from, float to, float progress, Ease ease)
        => from + (to - from) * EaseUtility.Evaluate(ease, progress);

    #endregion

    #region Bulk control

    /// <summary>Destroys every live tween. Returns how many were affected.</summary>
    public static int KillAll(bool complete = false) => TweenManager.Kill(null, complete);

    /// <summary>Destroys every tween tagged with this id or target. Returns how many were affected.</summary>
    public static int KillAll(object idOrTarget, bool complete = false) => TweenManager.Kill(idOrTarget, complete);

    /// <summary>Resumes every live tween.</summary>
    public static int PlayAll() => TweenManager.Control(null, TweenAction.Play);

    /// <summary>Resumes every tween tagged with this id or target.</summary>
    public static int PlayAll(object idOrTarget) => TweenManager.Control(idOrTarget, TweenAction.Play);

    /// <summary>Pauses every live tween.</summary>
    public static int PauseAll() => TweenManager.Control(null, TweenAction.Pause);

    /// <summary>Pauses every tween tagged with this id or target.</summary>
    public static int PauseAll(object idOrTarget) => TweenManager.Control(idOrTarget, TweenAction.Pause);

    /// <summary>Restarts every live tween.</summary>
    public static int RestartAll() => TweenManager.Control(null, TweenAction.Restart);

    /// <summary>Restarts every tween tagged with this id or target.</summary>
    public static int RestartAll(object idOrTarget) => TweenManager.Control(idOrTarget, TweenAction.Restart);

    /// <summary>Rewinds and pauses every live tween.</summary>
    public static int RewindAll() => TweenManager.Control(null, TweenAction.Rewind);

    /// <summary>Rewinds and pauses every tween tagged with this id or target.</summary>
    public static int RewindAll(object idOrTarget) => TweenManager.Control(idOrTarget, TweenAction.Rewind);

    /// <summary>Jumps every live tween to its end.</summary>
    public static int CompleteAll() => TweenManager.Control(null, TweenAction.Complete);

    /// <summary>Jumps every tween tagged with this id or target to its end.</summary>
    public static int CompleteAll(object idOrTarget) => TweenManager.Control(idOrTarget, TweenAction.Complete);

    #endregion

    #region Ticking

    /// <inheritdoc cref="TweenManager.Update(float, float)"/>
    public static void Update(float deltaTime, float unscaledDeltaTime = -1f) => TweenManager.Update(deltaTime, unscaledDeltaTime);

    /// <inheritdoc cref="TweenManager.LateUpdate(float, float)"/>
    public static void LateUpdate(float deltaTime, float unscaledDeltaTime = -1f) => TweenManager.LateUpdate(deltaTime, unscaledDeltaTime);

    /// <inheritdoc cref="TweenManager.FixedUpdate(float, float)"/>
    public static void FixedUpdate(float fixedDeltaTime, float unscaledFixedDeltaTime = -1f) => TweenManager.FixedUpdate(fixedDeltaTime, unscaledFixedDeltaTime);

    /// <inheritdoc cref="TweenManager.ManualUpdate(float, float)"/>
    public static void ManualUpdate(float deltaTime, float unscaledDeltaTime = -1f) => TweenManager.ManualUpdate(deltaTime, unscaledDeltaTime);

    #endregion
}
