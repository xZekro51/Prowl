// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Prowl.Tweening;

/// <summary>
/// Timeline of child tweens and callbacks. A sequence is itself a tween - it can be eased,
/// looped, killed, nested inside another sequence and given callbacks like any other.
/// </summary>
public readonly struct Sequence : IEquatable<Sequence>
{
    /// <summary>The underlying tween handle. Everything on <see cref="Tween"/> works on a sequence too.</summary>
    public readonly Tween Tween;

    internal Sequence(Tween tween) => Tween = tween;

    public static implicit operator Tween(Sequence sequence) => sequence.Tween;

    private SequenceData? Data
    {
        get
        {
            ITweenStorage? storage = TweenManager.Resolve(Tween.StorageId);
            return storage?.GetState(Tween.Slot, Tween.Version) as SequenceData;
        }
    }

    internal static Sequence Create()
    {
        SequenceData data = SequenceData.Rent();
        Tween tween = TweenManager.Of<float, FloatAdapter>.Storage
            .Create(0f, 1f, SequenceData.MinDuration, SequenceData.Driver, data, TweenFlags.IsSequence);

        tween.SetEase(Ease.Linear);
        data.Owner = tween;
        return new Sequence(tween);
    }

    #region Composition

    /// <summary>Adds a tween at the end of the sequence.</summary>
    public Sequence Append(Tween tween)
    {
        SequenceData? data = Data;
        if (data == null || !tween.IsActive)
            return this;

        float full = SequenceData.Adopt(tween);
        data.LastAppendPoint = data.Duration;
        data.Add(tween, data.Duration, full);
        data.Duration += full;
        SyncDuration(data);
        return this;
    }

    /// <summary>Adds a tween in parallel with the last appended element.</summary>
    public Sequence Join(Tween tween)
    {
        SequenceData? data = Data;
        if (data == null || !tween.IsActive)
            return this;

        float full = SequenceData.Adopt(tween);
        data.Add(tween, data.LastAppendPoint, full);
        data.Duration = MathF.Max(data.Duration, data.LastAppendPoint + full);
        SyncDuration(data);
        return this;
    }

    /// <summary>Adds a tween at an exact position on the timeline.</summary>
    public Sequence Insert(float atPosition, Tween tween)
    {
        SequenceData? data = Data;
        if (data == null || !tween.IsActive)
            return this;

        float full = SequenceData.Adopt(tween);
        data.Add(tween, atPosition, full);
        data.Duration = MathF.Max(data.Duration, atPosition + full);
        SyncDuration(data);
        return this;
    }

    /// <summary>Adds a tween at the start, pushing everything else back.</summary>
    public Sequence Prepend(Tween tween)
    {
        SequenceData? data = Data;
        if (data == null || !tween.IsActive)
            return this;

        float full = SequenceData.Adopt(tween);
        data.Shift(full);
        data.Add(tween, 0f, full);
        data.Duration += full;
        data.LastAppendPoint += full;
        SyncDuration(data);
        return this;
    }

    /// <summary>Adds empty time at the end of the sequence.</summary>
    public Sequence AppendInterval(float interval)
    {
        SequenceData? data = Data;
        if (data == null)
            return this;

        data.LastAppendPoint = data.Duration;
        data.Duration += interval;
        SyncDuration(data);
        return this;
    }

    /// <summary>Adds empty time at the start of the sequence.</summary>
    public Sequence PrependInterval(float interval)
    {
        SequenceData? data = Data;
        if (data == null)
            return this;

        data.Shift(interval);
        data.Duration += interval;
        data.LastAppendPoint += interval;
        SyncDuration(data);
        return this;
    }

    /// <summary>Fires a callback when the playhead reaches the end of the sequence so far.</summary>
    public Sequence AppendCallback(Action callback)
    {
        SequenceData? data = Data;
        data?.AddCallback(callback, data.Duration);
        return this;
    }

    /// <summary>Fires a callback at an exact position on the timeline.</summary>
    public Sequence InsertCallback(float atPosition, Action callback)
    {
        SequenceData? data = Data;
        if (data == null)
            return this;

        data.AddCallback(callback, atPosition);
        data.Duration = MathF.Max(data.Duration, atPosition);
        SyncDuration(data);
        return this;
    }

    /// <summary>Allocation-free variant of <see cref="AppendCallback(Action)"/>.</summary>
    public Sequence AppendCallback<TState>(TState state, Action<TState> callback) where TState : class
    {
        SequenceData? data = Data;
        if (data == null)
            return this;

        data.AddCallback(Unsafe.As<Action<TState>, Action<object?>>(ref callback), data.Duration, state);
        return this;
    }

    private void SyncDuration(SequenceData data)
    {
        ITweenStorage? storage = TweenManager.Resolve(Tween.StorageId);
        if (storage != null && storage.IsAlive(Tween.Slot, Tween.Version))
            storage.CoreRef(Tween.Slot, Tween.Version).Duration = MathF.Max(data.Duration, SequenceData.MinDuration);
    }

    #endregion

    #region Tween passthrough

    public Sequence SetEase(Ease ease) { Tween.SetEase(ease); return this; }
    public Sequence SetEase(EaseFunction customEase) { Tween.SetEase(customEase); return this; }
    public Sequence SetLoops(int loops, LoopType loopType = LoopType.Restart) { Tween.SetLoops(loops, loopType); return this; }
    public Sequence SetDelay(float delay) { Tween.SetDelay(delay); return this; }
    public Sequence SetAutoKill(bool autoKill = true) { Tween.SetAutoKill(autoKill); return this; }
    public Sequence SetUpdate(UpdateType updateType, bool isIndependentUpdate = false) { Tween.SetUpdate(updateType, isIndependentUpdate); return this; }
    public Sequence SetId(object id) { Tween.SetId(id); return this; }
    public Sequence SetTarget(object target) { Tween.SetTarget(target); return this; }
    public Sequence SetTimeScale(float timeScale) { Tween.SetTimeScale(timeScale); return this; }

    public Sequence OnStart(Action callback) { Tween.OnStart(callback); return this; }
    public Sequence OnUpdate(Action callback) { Tween.OnUpdate(callback); return this; }
    public Sequence OnStepComplete(Action callback) { Tween.OnStepComplete(callback); return this; }
    public Sequence OnComplete(Action callback) { Tween.OnComplete(callback); return this; }
    public Sequence OnKill(Action callback) { Tween.OnKill(callback); return this; }

    public Sequence Play() { Tween.Play(); return this; }
    public Sequence Pause() { Tween.Pause(); return this; }
    public Sequence Restart(bool includeDelay = true) { Tween.Restart(includeDelay); return this; }
    public Sequence Rewind(bool includeDelay = true) { Tween.Rewind(includeDelay); return this; }
    public Sequence Complete(bool withCallbacks = false) { Tween.Complete(withCallbacks); return this; }
    public void Kill(bool complete = false) => Tween.Kill(complete);

    public bool IsActive => Tween.IsActive;
    public bool IsPlaying => Tween.IsPlaying;
    public bool IsComplete => Tween.IsComplete;
    public float Duration => Data?.Duration ?? 0f;

    #endregion

    public bool Equals(Sequence other) => Tween.Equals(other.Tween);
    public override bool Equals(object? obj) => obj is Sequence s && Equals(s);
    public override int GetHashCode() => Tween.GetHashCode();
    public static bool operator ==(Sequence a, Sequence b) => a.Equals(b);
    public static bool operator !=(Sequence a, Sequence b) => !a.Equals(b);
}

/// <summary>
/// Timeline backing a <see cref="Sequence"/>. Instances are pooled, so building and destroying
/// sequences repeatedly settles at zero allocations.
/// </summary>
internal sealed class SequenceData
{
    internal const float MinDuration = 1e-6f;

    private struct Element
    {
        public Tween Tween;
        public float Start;
        public float Duration;
        public float LastLocal;
        public Delegate? Callback;
        public object? CallbackState;
        public bool IsCallback;
    }

    /// <summary>Cached driver: the sequence tween's "value" is its normalized playhead.</summary>
    internal static readonly Action<float, object?> Driver =
        static (progress, state) => ((SequenceData)state!).Evaluate(progress);

    private static readonly Stack<SequenceData> s_pool = new();

    private Element[] _elements = new Element[8];
    private int _count;
    private float _lastPosition = -1f;
    private int _lastLoop;

    internal float Duration;
    internal float LastAppendPoint;
    internal Tween Owner;

    internal static SequenceData Rent()
    {
        if (s_pool.Count > 0)
        {
            SequenceData data = s_pool.Pop();
            data.Reset();
            return data;
        }

        return new SequenceData();
    }

    private void Reset()
    {
        Array.Clear(_elements, 0, _count);
        _count = 0;
        Duration = 0f;
        LastAppendPoint = 0f;
        _lastPosition = -1f;
        _lastLoop = 0;
        Owner = Tween.None;
    }

    /// <summary>Detaches a tween from the normal update loop and hands it to the sequence.</summary>
    internal static float Adopt(Tween tween)
    {
        ITweenStorage? storage = TweenManager.Resolve(tween.StorageId);
        if (storage == null || !storage.IsAlive(tween.Slot, tween.Version))
            return 0f;

        ref TweenCore core = ref storage.CoreRef(tween.Slot, tween.Version);
        core.Flags |= TweenFlags.Sequenced;
        core.Flags &= ~(TweenFlags.AutoKill | TweenFlags.Paused);
        return core.FullDuration;
    }

    internal void Add(Tween tween, float start, float duration)
    {
        EnsureCapacity();
        ref Element e = ref _elements[_count++];
        e.Tween = tween;
        e.Start = start;
        e.Duration = duration;
        e.LastLocal = float.NaN;
        e.Callback = null;
        e.CallbackState = null;
        e.IsCallback = false;
    }

    internal void AddCallback(Delegate callback, float position, object? state = null)
    {
        EnsureCapacity();
        ref Element e = ref _elements[_count++];
        e.Tween = Tween.None;
        e.Start = position;
        e.Duration = 0f;
        e.LastLocal = float.NaN;
        e.Callback = callback;
        e.CallbackState = state;
        e.IsCallback = true;
    }

    internal void Shift(float amount)
    {
        for (int i = 0; i < _count; i++)
            _elements[i].Start += amount;
    }

    private void EnsureCapacity()
    {
        if (_count == _elements.Length)
            Array.Resize(ref _elements, _elements.Length * 2);
    }

    /// <summary>Drives every child from the sequence's normalized playhead.</summary>
    internal void Evaluate(float progress)
    {
        float position = progress * Duration;

        int loops = Owner.CompletedLoops;
        if (loops != _lastLoop)
        {
            _lastLoop = loops;
            _lastPosition = -1f;
        }

        bool forward = position >= _lastPosition;

        // Index-based on purpose: a child's callback may append to this sequence and resize the array.
        for (int i = 0; i < _count; i++)
        {
            if (_elements[i].IsCallback)
            {
                float at = _elements[i].Start;
                if (forward && _lastPosition < at && position >= at)
                {
                    Delegate? callback = _elements[i].Callback;
                    object? state = _elements[i].CallbackState;
                    if (callback is Action plain) plain();
                    else if (callback != null) Unsafe.As<Action<object?>>(callback)(state);
                }
                continue;
            }

            float local = position - _elements[i].Start;
            if (local < 0f) local = 0f;
            else if (local > _elements[i].Duration) local = _elements[i].Duration;

            if (local == _elements[i].LastLocal)
                continue;

            _elements[i].LastLocal = local;

            Tween child = _elements[i].Tween;
            ITweenStorage? storage = TweenManager.Resolve(child.StorageId);
            if (storage != null && storage.IsAlive(child.Slot, child.Version))
                storage.Goto(child.Slot, child.Version, local, true);
        }

        _lastPosition = position;
    }

    /// <summary>Kills every child, then recycles this instance.</summary>
    internal void OnOwnerKilled()
    {
        for (int i = 0; i < _count; i++)
        {
            Tween child = _elements[i].Tween;
            if (child.IsActive)
                child.Kill();
        }

        Reset();
        if (s_pool.Count < 64)
            s_pool.Push(this);
    }
}
