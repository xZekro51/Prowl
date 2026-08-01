// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Tweening;

/// <summary>
/// Type-agnostic per-tween state. Lives inline inside the typed storage's dense array so the
/// update loop walks one contiguous block of memory.
/// </summary>
internal struct TweenCore
{
    /// <summary>Elapsed time inside the current loop cycle.</summary>
    public float Position;
    public float Duration;
    public float Delay;
    public float DelayElapsed;
    /// <summary>Per-tween speed multiplier.</summary>
    public float TimeScale;
    public float EaseOvershoot;
    public float EasePeriod;
    public int Loops;
    public int CompletedLoops;
    public TweenFlags Flags;
    public Ease Ease;
    public LoopType LoopType;
    public UpdateType UpdateType;

    /// <summary>Total playable length including delay, treating infinite loops as a single cycle.</summary>
    public readonly float FullDuration => Delay + Duration * (Loops < 0 ? 1 : Loops);
}

/// <summary>
/// Cold per-tween data: identifiers, callbacks and custom easing. The backing array is only
/// allocated the first time a tween in a storage actually uses one of these.
/// </summary>
internal struct TweenEvents
{
    public object? Id;
    public object? Target;

    /// <summary>
    /// Shared state passed to the allocation-free <c>On*(state, callback)</c> overloads.
    /// One state object per tween - assigning a second typed callback with a different state
    /// replaces the first one's state.
    /// </summary>
    public object? State;

    public Delegate? OnStart;
    public Delegate? OnPlay;
    public Delegate? OnPause;
    public Delegate? OnUpdate;
    public Delegate? OnStepComplete;
    public Delegate? OnComplete;
    public Delegate? OnKill;
    public Delegate? OnRewind;

    public EaseFunction? CustomEase;
}

/// <summary>Type-erased view over a typed <c>TweenStorage&lt;T, TAdapter&gt;</c>.</summary>
internal interface ITweenStorage
{
    int Id { get; set; }
    int ActiveCount { get; }

    bool IsAlive(int slot, int version);
    ref TweenCore CoreRef(int slot, int version);
    ref TweenEvents EventsRef(int slot, int version);
    object? GetState(int slot, int version);

    void Kill(int slot, int version, bool complete);
    void Goto(int slot, int version, float position, bool withCallbacks);
    void ApplyCurrent(int slot, int version);
    void MakeFrom(int slot, int version);
    void MakeRelative(int slot, int version);
    void Flip(int slot, int version);

    void Update(UpdateType type, float deltaTime, float unscaledDeltaTime);
    void Sweep();

    int KillWhere(object? idOrTarget, bool complete);
    int ControlWhere(object? idOrTarget, TweenAction action);
}
