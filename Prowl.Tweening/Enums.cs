// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Tweening;

/// <summary>
/// The available easing equations, named <c>In</c>/<c>Out</c>/<c>InOut</c> by where the
/// acceleration happens: <c>In</c> starts slow, <c>Out</c> ends slow, <c>InOut</c> does both.
/// </summary>
public enum Ease : byte
{
    Linear = 0,
    InSine, OutSine, InOutSine,
    InQuad, OutQuad, InOutQuad,
    InCubic, OutCubic, InOutCubic,
    InQuart, OutQuart, InOutQuart,
    InQuint, OutQuint, InOutQuint,
    InExpo, OutExpo, InOutExpo,
    InCirc, OutCirc, InOutCirc,
    InElastic, OutElastic, InOutElastic,
    InBack, OutBack, InOutBack,
    InBounce, OutBounce, InOutBounce,

    /// <summary>Set automatically when a custom <see cref="EaseFunction"/> is assigned.</summary>
    Custom = 254,
    /// <summary>"No ease chosen"; resolves to <see cref="Tween.DefaultEase"/> at creation time.</summary>
    Unset = 255
}

/// <summary>How a tween behaves once a loop cycle ends.</summary>
public enum LoopType : byte
{
    /// <summary>Restart from the start value.</summary>
    Restart = 0,
    /// <summary>Play backwards, then forwards, alternating each cycle.</summary>
    Yoyo = 1,
    /// <summary>Keep adding the tween's delta to the end value each cycle.</summary>
    Incremental = 2
}

/// <summary>Which manager tick drives the tween.</summary>
public enum UpdateType : byte
{
    Normal = 0,
    Late = 1,
    Fixed = 2,
    Manual = 3
}

/// <summary>
/// A user-supplied easing curve. <paramref name="time"/> is the elapsed time inside the cycle and
/// <paramref name="duration"/> is the cycle length; tweens always call this normalized, with
/// <c>duration == 1</c>, so <paramref name="time"/> runs 0..1. Return 0 at the start and 1 at the
/// end - values outside that range simply overshoot.
/// </summary>
public delegate float EaseFunction(float time, float duration, float overshootOrAmplitude, float period);

/// <summary>Reads the value a tween should start from. Used by the <c>Tween.To(getter, setter, ...)</c> overloads.</summary>
public delegate T TweenGetter<out T>();

/// <summary>Writes each value a tween produces. Used by the <c>Tween.To(getter, setter, ...)</c> overloads.</summary>
public delegate void TweenSetter<in T>(T value);

[Flags]
internal enum TweenFlags : uint
{
    None = 0,
    Paused = 1u << 0,
    Dead = 1u << 1,
    Started = 1u << 2,
    Completed = 1u << 3,
    /// <summary>Advances on unscaled time, ignoring <see cref="Tween.GlobalTimeScale"/>.</summary>
    Independent = 1u << 4,
    AutoKill = 1u << 5,
    /// <summary>Owned by a <see cref="Sequence"/>; skipped by the normal update loop.</summary>
    Sequenced = 1u << 6,
    IsSequence = 1u << 7,
    HasEvents = 1u << 8,
    CustomEase = 1u << 9
}

internal enum TweenAction : byte
{
    Play,
    Pause,
    Restart,
    Rewind,
    Complete,
    Flip
}
