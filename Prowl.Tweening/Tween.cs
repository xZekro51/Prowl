// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace Prowl.Tweening;

/// <summary>
/// A handle to a running tween. This is a 12-byte struct, not an object: copying it around,
/// storing it in a field or passing it to a method never allocates, and a handle to a tween that
/// has already died simply stops resolving instead of dangling.
/// </summary>
/// <remarks>
/// Every method is safe to call on a dead or default handle - it becomes a no-op.
/// </remarks>
public readonly partial struct Tween : IEquatable<Tween>
{
    internal readonly int StorageId;
    internal readonly int Slot;
    internal readonly int Version;

    /// <summary>An invalid handle. Every operation on it is a no-op.</summary>
    public static readonly Tween None = default;

    internal Tween(int storageId, int slot, int version)
    {
        StorageId = storageId;
        Slot = slot;
        Version = version;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ITweenStorage? Storage()
    {
        if (Version == 0)
            return null;

        ITweenStorage? storage = TweenManager.Resolve(StorageId);
        return storage != null && storage.IsAlive(Slot, Version) ? storage : null;
    }

    #region State

    /// <summary>True while the tween exists (it may still be paused).</summary>
    public bool IsActive => Storage() != null;

    /// <summary>True when the tween exists and is not paused.</summary>
    public bool IsPlaying
    {
        get
        {
            ITweenStorage? s = Storage();
            return s != null && (s.CoreRef(Slot, Version).Flags & TweenFlags.Paused) == 0;
        }
    }

    /// <summary>True once the tween has reached the end of its last loop.</summary>
    public bool IsComplete
    {
        get
        {
            ITweenStorage? s = Storage();
            return s != null && (s.CoreRef(Slot, Version).Flags & TweenFlags.Completed) != 0;
        }
    }

    /// <summary>Elapsed time including delay and completed loops.</summary>
    public float Elapsed
    {
        get
        {
            ITweenStorage? s = Storage();
            if (s == null) return 0f;
            ref TweenCore c = ref s.CoreRef(Slot, Version);
            return c.DelayElapsed + c.Duration * c.CompletedLoops + c.Position;
        }
    }

    /// <summary>Length of a single loop cycle, excluding delay.</summary>
    public float Duration
    {
        get
        {
            ITweenStorage? s = Storage();
            return s == null ? 0f : s.CoreRef(Slot, Version).Duration;
        }
    }

    /// <summary>Total length including delay and all loops. Infinite loops count as one.</summary>
    public float FullDuration
    {
        get
        {
            ITweenStorage? s = Storage();
            return s == null ? 0f : s.CoreRef(Slot, Version).FullDuration;
        }
    }

    /// <summary>Number of loop cycles finished so far.</summary>
    public int CompletedLoops
    {
        get
        {
            ITweenStorage? s = Storage();
            return s == null ? 0 : s.CoreRef(Slot, Version).CompletedLoops;
        }
    }

    /// <summary>Per-tween speed multiplier. 2 plays twice as fast, 0 freezes it.</summary>
    public float TimeScale
    {
        get
        {
            ITweenStorage? s = Storage();
            return s == null ? 1f : s.CoreRef(Slot, Version).TimeScale;
        }
        set
        {
            ITweenStorage? s = Storage();
            if (s != null) s.CoreRef(Slot, Version).TimeScale = value;
        }
    }

    #endregion

    #region Settings

    /// <summary>Sets the easing equation.</summary>
    public Tween SetEase(Ease ease)
    {
        ITweenStorage? s = Storage();
        if (s != null)
        {
            ref TweenCore c = ref s.CoreRef(Slot, Version);
            c.Ease = ease;
            c.Flags &= ~TweenFlags.CustomEase;
        }
        return this;
    }

    /// <summary>Sets the easing equation with a Back overshoot / Elastic amplitude.</summary>
    public Tween SetEase(Ease ease, float overshootOrAmplitude)
    {
        ITweenStorage? s = Storage();
        if (s != null)
        {
            ref TweenCore c = ref s.CoreRef(Slot, Version);
            c.Ease = ease;
            c.EaseOvershoot = overshootOrAmplitude;
            c.Flags &= ~TweenFlags.CustomEase;
        }
        return this;
    }

    /// <summary>Sets the easing equation with an Elastic amplitude and period.</summary>
    public Tween SetEase(Ease ease, float amplitude, float period)
    {
        ITweenStorage? s = Storage();
        if (s != null)
        {
            ref TweenCore c = ref s.CoreRef(Slot, Version);
            c.Ease = ease;
            c.EaseOvershoot = amplitude;
            c.EasePeriod = period;
            c.Flags &= ~TweenFlags.CustomEase;
        }
        return this;
    }

    /// <summary>Sets a custom easing function. Cache the delegate to keep this allocation-free.</summary>
    public Tween SetEase(EaseFunction customEase)
    {
        ITweenStorage? s = Storage();
        if (s != null)
        {
            s.EventsRef(Slot, Version).CustomEase = customEase;
            ref TweenCore c = ref s.CoreRef(Slot, Version);
            c.Ease = Ease.Custom;
            c.Flags |= TweenFlags.CustomEase;
        }
        return this;
    }

    /// <summary>Sets the loop count (-1 for infinite) and how each cycle restarts.</summary>
    public Tween SetLoops(int loops, LoopType loopType = LoopType.Restart)
    {
        ITweenStorage? s = Storage();
        if (s != null)
        {
            ref TweenCore c = ref s.CoreRef(Slot, Version);
            c.Loops = loops == 0 ? 1 : loops;
            c.LoopType = loopType;
        }
        return this;
    }

    /// <summary>Delays the start of the tween.</summary>
    public Tween SetDelay(float delay)
    {
        ITweenStorage? s = Storage();
        if (s != null)
        {
            ref TweenCore c = ref s.CoreRef(Slot, Version);
            c.Delay = delay;
            c.DelayElapsed = 0f;
        }
        return this;
    }

    /// <summary>When true (the default) the tween is destroyed as soon as it completes.</summary>
    public Tween SetAutoKill(bool autoKill = true)
    {
        ITweenStorage? s = Storage();
        if (s != null)
        {
            ref TweenCore c = ref s.CoreRef(Slot, Version);
            if (autoKill) c.Flags |= TweenFlags.AutoKill;
            else c.Flags &= ~TweenFlags.AutoKill;
        }
        return this;
    }

    /// <summary>Chooses which manager tick drives the tween, and whether it ignores time scale.</summary>
    public Tween SetUpdate(UpdateType updateType, bool isIndependentUpdate = false)
    {
        ITweenStorage? s = Storage();
        if (s != null)
        {
            ref TweenCore c = ref s.CoreRef(Slot, Version);
            c.UpdateType = updateType;
            if (isIndependentUpdate) c.Flags |= TweenFlags.Independent;
            else c.Flags &= ~TweenFlags.Independent;
            TweenManager.NotifyUpdateType(updateType);
        }
        return this;
    }

    /// <summary>Makes the tween ignore <see cref="TweenManager.TimeScale"/>.</summary>
    public Tween SetUpdate(bool isIndependentUpdate)
    {
        ITweenStorage? s = Storage();
        if (s != null)
        {
            ref TweenCore c = ref s.CoreRef(Slot, Version);
            if (isIndependentUpdate) c.Flags |= TweenFlags.Independent;
            else c.Flags &= ~TweenFlags.Independent;
        }
        return this;
    }

    /// <summary>Tags the tween so <see cref="KillAll(object, bool)"/> and friends can find it later.</summary>
    public Tween SetId(object id)
    {
        ITweenStorage? s = Storage();
        if (s != null) s.EventsRef(Slot, Version).Id = id;
        return this;
    }

    /// <summary>Associates the tween with an object, for target-based kills.</summary>
    public Tween SetTarget(object target)
    {
        ITweenStorage? s = Storage();
        if (s != null) s.EventsRef(Slot, Version).Target = target;
        return this;
    }

    /// <summary>Per-tween speed multiplier (same as assigning <see cref="TimeScale"/>).</summary>
    public Tween SetTimeScale(float timeScale)
    {
        TimeScale = timeScale;
        return this;
    }

    /// <summary>
    /// Treats the end value as an offset from the start value: <c>end = start + end</c>.
    /// Call it before the tween has run.
    /// </summary>
    public Tween SetRelative(bool isRelative = true)
    {
        if (isRelative)
            Storage()?.MakeRelative(Slot, Version);
        return this;
    }

    /// <summary>
    /// Swaps start and end so the tween runs <i>from</i> the given value <i>to</i> the current one,
    /// and immediately applies the new start value.
    /// </summary>
    public Tween From()
    {
        Storage()?.MakeFrom(Slot, Version);
        return this;
    }

    /// <summary>Applies <see cref="SetRelative"/> and then <see cref="From()"/> in one call.</summary>
    public Tween From(bool isRelative)
    {
        if (isRelative)
            Storage()?.MakeRelative(Slot, Version);
        return From();
    }

    #endregion

    #region Callbacks

    /// <summary>Fired the first time the tween runs, after any delay.</summary>
    public Tween OnStart(Action callback) => SetCallback(callback, CallbackKind.Start);

    /// <summary>Fired when the tween is played (or resumed).</summary>
    public Tween OnPlay(Action callback) => SetCallback(callback, CallbackKind.Play);

    /// <summary>Fired when the tween is paused.</summary>
    public Tween OnPause(Action callback) => SetCallback(callback, CallbackKind.Pause);

    /// <summary>Fired every time the tween's value is applied.</summary>
    public Tween OnUpdate(Action callback) => SetCallback(callback, CallbackKind.Update);

    /// <summary>Fired at the end of each loop cycle.</summary>
    public Tween OnStepComplete(Action callback) => SetCallback(callback, CallbackKind.StepComplete);

    /// <summary>Fired when the tween reaches the end of its final loop.</summary>
    public Tween OnComplete(Action callback) => SetCallback(callback, CallbackKind.Complete);

    /// <summary>Fired when the tween is destroyed, for any reason.</summary>
    public Tween OnKill(Action callback) => SetCallback(callback, CallbackKind.Kill);

    /// <summary>Fired when the tween is rewound.</summary>
    public Tween OnRewind(Action callback) => SetCallback(callback, CallbackKind.Rewind);

    /// <summary>
    /// Allocation-free callback: pass the object you need instead of capturing it, and use a
    /// <c>static</c> lambda. All <c>On*</c> state overloads on one tween share a single state object.
    /// </summary>
    public Tween OnStart<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.Start);

    /// <inheritdoc cref="OnStart{TState}(TState, Action{TState})"/>
    public Tween OnPlay<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.Play);

    /// <inheritdoc cref="OnStart{TState}(TState, Action{TState})"/>
    public Tween OnPause<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.Pause);

    /// <inheritdoc cref="OnStart{TState}(TState, Action{TState})"/>
    public Tween OnUpdate<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.Update);

    /// <inheritdoc cref="OnStart{TState}(TState, Action{TState})"/>
    public Tween OnStepComplete<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.StepComplete);

    /// <inheritdoc cref="OnStart{TState}(TState, Action{TState})"/>
    public Tween OnComplete<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.Complete);

    /// <inheritdoc cref="OnStart{TState}(TState, Action{TState})"/>
    public Tween OnKill<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.Kill);

    /// <inheritdoc cref="OnStart{TState}(TState, Action{TState})"/>
    public Tween OnRewind<TState>(TState state, Action<TState> callback) where TState : class => SetCallback(state, callback, CallbackKind.Rewind);

    private enum CallbackKind : byte { Start, Play, Pause, Update, StepComplete, Complete, Kill, Rewind }

    private Tween SetCallback(Delegate? callback, CallbackKind kind)
    {
        ITweenStorage? s = Storage();
        if (s == null)
            return this;

        ref TweenEvents ev = ref s.EventsRef(Slot, Version);
        switch (kind)
        {
            case CallbackKind.Start: ev.OnStart = callback; break;
            case CallbackKind.Play: ev.OnPlay = callback; break;
            case CallbackKind.Pause: ev.OnPause = callback; break;
            case CallbackKind.Update: ev.OnUpdate = callback; break;
            case CallbackKind.StepComplete: ev.OnStepComplete = callback; break;
            case CallbackKind.Complete: ev.OnComplete = callback; break;
            case CallbackKind.Kill: ev.OnKill = callback; break;
            case CallbackKind.Rewind: ev.OnRewind = callback; break;
        }
        return this;
    }

    private Tween SetCallback<TState>(TState state, Action<TState> callback, CallbackKind kind) where TState : class
    {
        ITweenStorage? s = Storage();
        if (s == null)
            return this;

        // A delegate over a reference type has the same calling convention as one over object,
        // so the reinterpret is free and avoids wrapping the user's callback in a closure.
        Action<object?> erased = Unsafe.As<Action<TState>, Action<object?>>(ref callback);
        s.EventsRef(Slot, Version).State = state;
        return SetCallback(erased, kind);
    }

    #endregion

    #region Playback control

    /// <summary>Resumes a paused tween.</summary>
    public Tween Play()
    {
        ITweenStorage? s = Storage();
        if (s != null)
        {
            ref TweenCore c = ref s.CoreRef(Slot, Version);
            if ((c.Flags & TweenFlags.Paused) != 0)
            {
                c.Flags &= ~TweenFlags.Paused;
                if ((c.Flags & TweenFlags.HasEvents) != 0)
                    Invoke(ref s.EventsRef(Slot, Version), CallbackKind.Play);
            }
        }
        return this;
    }

    /// <summary>Pauses the tween where it is.</summary>
    public Tween Pause()
    {
        ITweenStorage? s = Storage();
        if (s != null)
        {
            ref TweenCore c = ref s.CoreRef(Slot, Version);
            if ((c.Flags & TweenFlags.Paused) == 0)
            {
                c.Flags |= TweenFlags.Paused;
                if ((c.Flags & TweenFlags.HasEvents) != 0)
                    Invoke(ref s.EventsRef(Slot, Version), CallbackKind.Pause);
            }
        }
        return this;
    }

    /// <summary>Pauses if playing, plays if paused.</summary>
    public Tween TogglePause() => IsPlaying ? Pause() : Play();

    /// <summary>Rewinds to the start and plays from there.</summary>
    public Tween Restart(bool includeDelay = true)
    {
        ITweenStorage? s = Storage();
        if (s == null)
            return this;

        ref TweenCore c = ref s.CoreRef(Slot, Version);
        c.Position = 0f;
        c.CompletedLoops = 0;
        c.DelayElapsed = includeDelay ? 0f : c.Delay;
        c.Flags &= ~(TweenFlags.Paused | TweenFlags.Completed | TweenFlags.Started);
        s.ApplyCurrent(Slot, Version);
        return this;
    }

    /// <summary>Rewinds to the start and pauses.</summary>
    public Tween Rewind(bool includeDelay = true)
    {
        ITweenStorage? s = Storage();
        if (s == null)
            return this;

        ref TweenCore c = ref s.CoreRef(Slot, Version);
        c.Position = 0f;
        c.CompletedLoops = 0;
        c.DelayElapsed = includeDelay ? 0f : c.Delay;
        c.Flags &= ~(TweenFlags.Completed | TweenFlags.Started);
        c.Flags |= TweenFlags.Paused;

        bool hasEvents = (c.Flags & TweenFlags.HasEvents) != 0;
        s.ApplyCurrent(Slot, Version);

        if (hasEvents && s.IsAlive(Slot, Version))
            Invoke(ref s.EventsRef(Slot, Version), CallbackKind.Rewind);
        return this;
    }

    /// <summary>Jumps to the end. Also kills the tween when auto-kill is on.</summary>
    public Tween Complete(bool withCallbacks = false)
    {
        ITweenStorage? s = Storage();
        if (s == null)
            return this;

        bool autoKill = (s.CoreRef(Slot, Version).Flags & TweenFlags.AutoKill) != 0;
        s.Goto(Slot, Version, s.CoreRef(Slot, Version).FullDuration, withCallbacks);

        if (autoKill)
            s.Kill(Slot, Version, false);
        else if (s.IsAlive(Slot, Version))
            s.CoreRef(Slot, Version).Flags |= TweenFlags.Paused;

        return this;
    }

    /// <summary>Jumps to an absolute time (delay included), optionally resuming playback.</summary>
    public Tween Goto(float to, bool andPlay = false)
    {
        ITweenStorage? s = Storage();
        if (s == null)
            return this;

        s.Goto(Slot, Version, to < 0f ? 0f : to, false);
        if (!s.IsAlive(Slot, Version))
            return this;

        ref TweenCore c = ref s.CoreRef(Slot, Version);
        if (andPlay) c.Flags &= ~TweenFlags.Paused;
        else c.Flags |= TweenFlags.Paused;
        return this;
    }

    /// <summary>
    /// Reverses the tween: start and end are swapped and the playhead is mirrored inside the
    /// current cycle, so it plays back the way it came.
    /// </summary>
    public Tween Flip()
    {
        Storage()?.Flip(Slot, Version);
        return this;
    }

    /// <summary>Destroys the tween. Optionally jumps to the end value first.</summary>
    public void Kill(bool complete = false) => Storage()?.Kill(Slot, Version, complete);

    internal Tween Apply(TweenAction action) => action switch
    {
        TweenAction.Play => Play(),
        TweenAction.Pause => Pause(),
        TweenAction.Restart => Restart(),
        TweenAction.Rewind => Rewind(),
        TweenAction.Complete => Complete(),
        TweenAction.Flip => Flip(),
        _ => this
    };

    private static void Invoke(ref TweenEvents ev, CallbackKind kind)
    {
        Delegate? d = kind switch
        {
            CallbackKind.Play => ev.OnPlay,
            CallbackKind.Pause => ev.OnPause,
            CallbackKind.Rewind => ev.OnRewind,
            _ => null
        };

        if (d is null)
            return;

        if (d is Action plain)
            plain();
        else
            Unsafe.As<Action<object?>>(d)(ev.State);
    }

    #endregion

    #region Equality

    public bool Equals(Tween other) => StorageId == other.StorageId && Slot == other.Slot && Version == other.Version;
    public override bool Equals(object? obj) => obj is Tween t && Equals(t);
    public override int GetHashCode() => HashCode.Combine(StorageId, Slot, Version);
    public static bool operator ==(Tween a, Tween b) => a.Equals(b);
    public static bool operator !=(Tween a, Tween b) => !a.Equals(b);

    #endregion
}
