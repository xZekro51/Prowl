// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace Prowl.Tweening;

/// <summary>
/// Dense, cache-friendly storage for every tween of one (value type, adapter) pair.
/// <para>
/// Tweens live packed at the head of <c>_entries</c> with no holes, so the update loop is a
/// straight linear walk. Public handles point at a sparse slot table instead of the dense array,
/// which lets entries be swapped around during compaction without invalidating them.
/// </para>
/// <para>
/// Nothing here allocates after the arrays have grown to their high-water mark: killing a tween
/// only flips a flag, and structural removal is batched into <see cref="Sweep"/>.
/// </para>
/// </summary>
internal sealed class TweenStorage<T, TAdapter> : ITweenStorage
    where T : unmanaged
    where TAdapter : unmanaged, ITweenAdapter<T>
{
    private const float MinDuration = 1e-6f;
    private const int InitialCapacity = 32;

    private struct Entry
    {
        public TweenCore Core;
        public T Start;
        public T End;
        /// <summary>Back-reference into <c>_slots</c>, kept in sync when entries are swapped.</summary>
        public int Slot;
    }

    private struct Slot
    {
        public int Dense;     // index into _entries, -1 when free
        public int Version;   // bumped on kill so stale handles stop resolving
        public int NextFree;
    }

    private struct Binding
    {
        public Action<T, object?>? Setter;
        public object? State;
    }

    private Entry[] _entries = new Entry[InitialCapacity];
    private Binding[] _bindings = new Binding[InitialCapacity];
    private TweenEvents[]? _events;
    private int _count;
    private int _deadCount;

    private Slot[] _slots = new Slot[InitialCapacity];
    private int _slotCount;
    private int _freeSlot = -1;

    // Scratch sinks so ref-returning lookups never have to fail loudly on a stale handle.
    private static TweenCore s_noCore;
    private static TweenEvents s_noEvents;

    public int Id { get; set; }
    public int ActiveCount => _count;

    #region Creation

    public Tween Create(in T from, in T to, float duration, Action<T, object?>? setter, object? state, TweenFlags extraFlags = TweenFlags.None)
    {
        if (_count == _entries.Length)
            Grow();

        int dense = _count++;
        int slot = AllocateSlot(dense);

        ref Entry e = ref _entries[dense];
        e.Start = from;
        e.End = to;
        e.Slot = slot;

        ref TweenCore c = ref e.Core;
        c.Position = 0f;
        c.Duration = duration < 0f ? 0f : duration;
        c.Delay = 0f;
        c.DelayElapsed = 0f;
        c.TimeScale = 1f;
        c.Loops = 1;
        c.CompletedLoops = 0;
        c.Ease = Tween.DefaultEase;
        c.EaseOvershoot = Tween.DefaultEaseOvershootOrAmplitude;
        c.EasePeriod = Tween.DefaultEasePeriod;
        c.LoopType = LoopType.Restart;
        c.UpdateType = UpdateType.Normal;
        c.Flags = extraFlags | (Tween.DefaultAutoKill ? TweenFlags.AutoKill : TweenFlags.None);

        _bindings[dense].Setter = setter;
        _bindings[dense].State = state;
        if (_events != null)
            _events[dense] = default;

        return new Tween(Id, slot, _slots[slot].Version);
    }

    private void Grow()
    {
        int capacity = _entries.Length * 2;
        Array.Resize(ref _entries, capacity);
        Array.Resize(ref _bindings, capacity);
        if (_events != null)
            Array.Resize(ref _events, capacity);
    }

    private int AllocateSlot(int dense)
    {
        int slot;
        if (_freeSlot >= 0)
        {
            slot = _freeSlot;
            _freeSlot = _slots[slot].NextFree;
        }
        else
        {
            if (_slotCount == _slots.Length)
                Array.Resize(ref _slots, _slotCount * 2);
            slot = _slotCount++;
            _slots[slot].Version = 1; // version 0 is reserved for the default (invalid) handle
        }

        _slots[slot].Dense = dense;
        return slot;
    }

    #endregion

    #region Handle resolution

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryDense(int slot, int version, out int dense)
    {
        if ((uint)slot < (uint)_slotCount)
        {
            ref Slot s = ref _slots[slot];
            if (s.Version == version && s.Dense >= 0)
            {
                dense = s.Dense;
                return true;
            }
        }

        dense = -1;
        return false;
    }

    public bool IsAlive(int slot, int version) => TryDense(slot, version, out _);

    public ref TweenCore CoreRef(int slot, int version)
    {
        if (TryDense(slot, version, out int dense))
            return ref _entries[dense].Core;

        s_noCore = default;
        return ref s_noCore;
    }

    public ref TweenEvents EventsRef(int slot, int version)
    {
        if (!TryDense(slot, version, out int dense))
        {
            s_noEvents = default;
            return ref s_noEvents;
        }

        _events ??= new TweenEvents[_entries.Length];
        _entries[dense].Core.Flags |= TweenFlags.HasEvents;
        return ref _events[dense];
    }

    public object? GetState(int slot, int version)
        => TryDense(slot, version, out int dense) ? _bindings[dense].State : null;

    #endregion

    #region Value edits

    public void MakeFrom(int slot, int version)
    {
        if (!TryDense(slot, version, out int dense)) return;

        ref Entry e = ref _entries[dense];
        (e.Start, e.End) = (e.End, e.Start);
        ApplyAt(dense, EasedProgress(dense, false));
    }

    public void MakeRelative(int slot, int version)
    {
        if (!TryDense(slot, version, out int dense)) return;

        ref Entry e = ref _entries[dense];
        e.End = default(TAdapter).Add(in e.Start, in e.End);
    }

    public void Flip(int slot, int version)
    {
        if (!TryDense(slot, version, out int dense)) return;

        ref Entry e = ref _entries[dense];
        (e.Start, e.End) = (e.End, e.Start);
        e.Core.Position = Math.Max(0f, e.Core.Duration - e.Core.Position);
        e.Core.Flags &= ~TweenFlags.Completed;
    }

    public void ApplyCurrent(int slot, int version)
    {
        if (!TryDense(slot, version, out int dense)) return;
        ApplyAt(dense, EasedProgress(dense, false));
    }

    #endregion

    #region Update

    public void Update(UpdateType type, float deltaTime, float unscaledDeltaTime)
    {
        // Snapshot the count: tweens created from a callback start on the next tick, which is
        // also what keeps index-based iteration valid while user code runs.
        int n = _count;
        for (int i = 0; i < n; i++)
        {
            // Re-read the array every iteration - a callback may have grown (reallocated) it.
            ref TweenCore c = ref _entries[i].Core;
            if ((c.Flags & (TweenFlags.Dead | TweenFlags.Paused | TweenFlags.Sequenced)) != 0)
                continue;
            if (c.UpdateType != type)
                continue;

            float delta = ((c.Flags & TweenFlags.Independent) != 0 ? unscaledDeltaTime : deltaTime) * c.TimeScale;
            if (delta == 0f)
                continue;

            Step(i, delta);
        }
    }

    /// <summary>Advances one tween and applies the result. Never touches a <c>ref</c> after user code runs.</summary>
    private void Step(int dense, float delta)
    {
        ref Entry e = ref _entries[dense];
        ref TweenCore c = ref e.Core;

        if (c.DelayElapsed < c.Delay)
        {
            c.DelayElapsed += delta;
            if (c.DelayElapsed < c.Delay)
                return;

            delta = c.DelayElapsed - c.Delay;
            c.DelayElapsed = c.Delay;
        }

        bool started = (c.Flags & TweenFlags.Started) == 0;
        c.Flags |= TweenFlags.Started;

        float duration = c.Duration > MinDuration ? c.Duration : MinDuration;
        c.Position += delta;

        int steps = 0;
        bool complete = false;
        if (c.Position >= duration)
        {
            steps = (int)(c.Position / duration);
            c.CompletedLoops += steps;
            c.Position -= steps * duration;

            if (c.Loops >= 0 && c.CompletedLoops >= c.Loops)
            {
                c.CompletedLoops = c.Loops;
                c.Position = 0f;
                complete = true;
            }
        }

        float eased = EasedProgress(dense, complete);
        T value = default(TAdapter).Evaluate(in e.Start, in e.End, eased);

        bool hasEvents = (c.Flags & TweenFlags.HasEvents) != 0;
        bool autoKill = false;
        if (complete)
        {
            c.Flags |= TweenFlags.Completed;
            if ((c.Flags & TweenFlags.AutoKill) != 0)
                autoKill = true;
            else
                c.Flags |= TweenFlags.Paused;
        }

        // ---- user code below: all refs into the arrays are considered invalid from here ----

        if (hasEvents && started)
            Fire(_events![dense].OnStart, _events[dense].State);

        Action<T, object?>? setter = _bindings[dense].Setter;
        setter?.Invoke(value, _bindings[dense].State);

        if (hasEvents)
        {
            object? state = _events![dense].State;
            Fire(_events[dense].OnUpdate, state);
            if (steps > 0)
                Fire(_events[dense].OnStepComplete, state);
            if (complete)
                Fire(_events[dense].OnComplete, state);
        }

        if (autoKill)
            KillAt(dense);
    }

    /// <summary>Cycle progress with loop mode and easing applied. Can exceed 0..1 for <see cref="LoopType.Incremental"/>.</summary>
    private float EasedProgress(int dense, bool complete)
    {
        ref TweenCore c = ref _entries[dense].Core;

        float p;
        int loopIndex;
        if (complete)
        {
            loopIndex = c.CompletedLoops > 0 ? c.CompletedLoops - 1 : 0;
            p = c.LoopType == LoopType.Yoyo && (loopIndex & 1) == 1 ? 0f : 1f;
        }
        else
        {
            loopIndex = c.CompletedLoops;
            p = c.Duration > MinDuration ? c.Position / c.Duration : 1f;
            if (c.LoopType == LoopType.Yoyo && (loopIndex & 1) == 1)
                p = 1f - p;
        }

        float eased;
        if ((c.Flags & TweenFlags.CustomEase) != 0 && _events != null && _events[dense].CustomEase != null)
            eased = _events[dense].CustomEase!(p, 1f, c.EaseOvershoot, c.EasePeriod);
        else
            eased = EaseUtility.Evaluate(c.Ease, p, c.EaseOvershoot, c.EasePeriod);

        if (c.LoopType == LoopType.Incremental)
            eased += loopIndex;

        return eased;
    }

    private void ApplyAt(int dense, float easedProgress)
    {
        T value = default(TAdapter).Evaluate(in _entries[dense].Start, in _entries[dense].End, easedProgress);
        Action<T, object?>? setter = _bindings[dense].Setter;
        setter?.Invoke(value, _bindings[dense].State);
    }

    /// <summary>Jumps to an absolute time (delay included) and applies the value there.</summary>
    public void Goto(int slot, int version, float position, bool withCallbacks)
    {
        if (!TryDense(slot, version, out int dense)) return;

        ref TweenCore c = ref _entries[dense].Core;

        if (position < c.Delay)
        {
            c.DelayElapsed = position < 0f ? 0f : position;
            c.Position = 0f;
            c.CompletedLoops = 0;
            c.Flags &= ~TweenFlags.Completed;
            return;
        }

        c.DelayElapsed = c.Delay;
        float local = position - c.Delay;
        float duration = c.Duration > MinDuration ? c.Duration : MinDuration;
        float full = c.Loops < 0 ? float.PositiveInfinity : duration * c.Loops;

        bool complete;
        if (local >= full)
        {
            c.CompletedLoops = c.Loops;
            c.Position = 0f;
            complete = true;
        }
        else
        {
            int done = (int)(local / duration);
            c.CompletedLoops = done;
            c.Position = local - done * duration;
            complete = false;
        }

        bool started = (c.Flags & TweenFlags.Started) == 0;
        c.Flags |= TweenFlags.Started;

        bool wasComplete = (c.Flags & TweenFlags.Completed) != 0;
        if (complete)
            c.Flags |= TweenFlags.Completed;
        else
            c.Flags &= ~TweenFlags.Completed;

        bool hasEvents = withCallbacks && (c.Flags & TweenFlags.HasEvents) != 0;
        float eased = EasedProgress(dense, complete);

        // ---- user code below ----

        if (hasEvents && started)
            Fire(_events![dense].OnStart, _events[dense].State);

        ApplyAt(dense, eased);

        if (hasEvents)
        {
            object? state = _events![dense].State;
            Fire(_events[dense].OnUpdate, state);
            if (complete && !wasComplete)
            {
                Fire(_events[dense].OnStepComplete, state);
                Fire(_events[dense].OnComplete, state);
            }
        }
    }

    #endregion

    #region Kill / sweep

    public void Kill(int slot, int version, bool complete)
    {
        if (!TryDense(slot, version, out int dense)) return;

        if (complete)
        {
            Goto(slot, version, _entries[dense].Core.FullDuration, true);
            // The completion pass may have auto-killed it already.
            if (!TryDense(slot, version, out dense)) return;
        }

        KillAt(dense);
    }

    /// <summary>
    /// Marks a tween dead: no array is restructured here, so this is safe to call from inside
    /// callbacks and from the update loop. <see cref="Sweep"/> reclaims the entry later.
    /// </summary>
    private void KillAt(int dense)
    {
        ref TweenCore c = ref _entries[dense].Core;
        if ((c.Flags & TweenFlags.Dead) != 0)
            return;

        c.Flags |= TweenFlags.Dead;
        _deadCount++;

        int slot = _entries[dense].Slot;
        ref Slot s = ref _slots[slot];
        s.Version++;
        if (s.Version == 0)
            s.Version = 1;

        bool isSequence = (c.Flags & TweenFlags.IsSequence) != 0;
        bool hasEvents = (c.Flags & TweenFlags.HasEvents) != 0;

        // ---- user code below ----

        if (isSequence && _bindings[dense].State is SequenceData data)
            data.OnOwnerKilled();

        if (hasEvents)
            Fire(_events![dense].OnKill, _events[dense].State);
    }

    /// <summary>Compacts dead entries out of the dense array. Called once per frame by the manager.</summary>
    public void Sweep()
    {
        if (_deadCount == 0)
            return;

        for (int i = _count - 1; i >= 0; i--)
        {
            if ((_entries[i].Core.Flags & TweenFlags.Dead) == 0)
                continue;

            int slot = _entries[i].Slot;
            _slots[slot].Dense = -1;
            _slots[slot].NextFree = _freeSlot;
            _freeSlot = slot;

            int last = --_count;
            if (i != last)
            {
                _entries[i] = _entries[last];
                _bindings[i] = _bindings[last];
                if (_events != null)
                    _events[i] = _events[last];
                _slots[_entries[i].Slot].Dense = i;
            }

            _entries[last] = default;
            _bindings[last] = default;
            if (_events != null)
                _events[last] = default;
        }

        _deadCount = 0;
    }

    #endregion

    #region Bulk operations

    public int KillWhere(object? idOrTarget, bool complete)
    {
        int killed = 0;
        int n = _count;
        for (int i = 0; i < n; i++)
        {
            if ((_entries[i].Core.Flags & (TweenFlags.Dead | TweenFlags.Sequenced)) != 0)
                continue;
            if (!Matches(i, idOrTarget))
                continue;

            int slot = _entries[i].Slot;
            Kill(slot, _slots[slot].Version, complete);
            killed++;
        }

        return killed;
    }

    public int ControlWhere(object? idOrTarget, TweenAction action)
    {
        int affected = 0;
        int n = _count;
        for (int i = 0; i < n; i++)
        {
            if ((_entries[i].Core.Flags & (TweenFlags.Dead | TweenFlags.Sequenced)) != 0)
                continue;
            if (!Matches(i, idOrTarget))
                continue;

            int slot = _entries[i].Slot;
            new Tween(Id, slot, _slots[slot].Version).Apply(action);
            affected++;
        }

        return affected;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Matches(int dense, object? idOrTarget)
    {
        if (idOrTarget == null)
            return true;
        if (_events == null || (_entries[dense].Core.Flags & TweenFlags.HasEvents) == 0)
            return false;

        ref TweenEvents ev = ref _events[dense];
        return Equals(ev.Id, idOrTarget) || ReferenceEquals(ev.Target, idOrTarget);
    }

    #endregion

    private static void Fire(Delegate? callback, object? state)
    {
        if (callback is null)
            return;

        if (callback is Action plain)
            plain();
        else
            Unsafe.As<Action<object?>>(callback)(state);
    }
}
