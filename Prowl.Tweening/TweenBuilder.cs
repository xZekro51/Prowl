// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace Prowl.Tweening;

/// <summary>
/// The zero-allocation creation path: describes a tween that has not been scheduled yet.
/// Nothing exists until you call one of the <c>Bind</c> methods.
/// </summary>
/// <example>
/// <code>
/// Tween.To(0f, 10f, 1f)
///      .Bind(this, static (v, self) =&gt; self.Health = v)
///      .SetEase(Ease.OutQuad);
/// </code>
/// </example>
public readonly struct TweenBuilder<T, TAdapter>
    where T : unmanaged
    where TAdapter : unmanaged, ITweenAdapter<T>
{
    private readonly T _from;
    private readonly T _to;
    private readonly float _duration;

    // Cached once per closed generic type: unwraps a plain Action<T> stored as the tween's state.
    private static readonly Action<T, object?> s_invokePlain = static (value, state) => Unsafe.As<Action<T>>(state!)(value);

    internal TweenBuilder(in T from, in T to, float duration)
    {
        _from = from;
        _to = to;
        _duration = duration;
    }

    /// <summary>
    /// Schedules the tween, pushing each value into <paramref name="setter"/>.
    /// The delegate itself is the only allocation - use a <c>static</c> lambda and the
    /// <see cref="Bind{TState}(TState, Action{T, TState})"/> overload to avoid even that.
    /// </summary>
    public Tween Bind(Action<T> setter)
    {
        ArgumentNullException.ThrowIfNull(setter);
        return TweenManager.Of<T, TAdapter>.Storage.Create(in _from, in _to, _duration, s_invokePlain, setter);
    }

    /// <summary>
    /// Schedules the tween with an explicit state object, so the setter can stay a cached
    /// <c>static</c> lambda and never captures anything.
    /// </summary>
    public Tween Bind<TState>(TState state, Action<T, TState> setter) where TState : class
    {
        ArgumentNullException.ThrowIfNull(setter);

        // Delegates over reference types share their calling convention with Action<T, object>,
        // so this reinterpret avoids allocating a wrapper closure per tween.
        Action<T, object?> erased = Unsafe.As<Action<T, TState>, Action<T, object?>>(ref setter);
        return TweenManager.Of<T, TAdapter>.Storage.Create(in _from, in _to, _duration, erased, state);
    }

    /// <summary>Schedules the tween without a setter - useful when you only care about the callbacks.</summary>
    public Tween BindNothing()
        => TweenManager.Of<T, TAdapter>.Storage.Create(in _from, in _to, _duration, null, null);
}
