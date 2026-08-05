// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Prowl.Tweening;

/// <summary>What a pending <c>await</c> is waiting for.</summary>
internal enum TweenWaitKind : byte
{
    /// <summary>The tween reached the end of its final loop.</summary>
    Completion,
    /// <summary>The tween was destroyed.</summary>
    Kill,
    /// <summary>The tween ran for the first time, after any delay.</summary>
    Start,
    /// <summary>A given number of loop cycles finished.</summary>
    ElapsedLoops,
    /// <summary>The playhead passed a given absolute time.</summary>
    Position
}

/// <summary>
/// An awaitable view of a tween, produced by the <c>AsyncWaitFor*</c> methods. It is a struct and
/// describes the wait rather than performing it, so building one never allocates and never
/// schedules anything by itself.
/// </summary>
/// <remarks>
/// <para>
/// Every wait also finishes when the tween is destroyed, so awaiting a tween that gets killed
/// early resumes instead of hanging forever. Use the tween's own callbacks if you need to tell
/// the two apart.
/// </para>
/// <para>
/// Continuations run inline on the thread that ticks the library, from inside
/// <see cref="TweenManager.Update(float, float)"/> (or whichever tick advanced the tween). The
/// captured synchronization context is deliberately ignored: game code awaiting a tween wants to
/// come back on the game thread, which is where the tick already is.
/// </para>
/// </remarks>
public readonly struct TweenAwaitable
{
    private readonly Tween _tween;
    private readonly CancellationToken _token;
    private readonly float _threshold;
    private readonly TweenWaitKind _kind;

    internal TweenAwaitable(Tween tween, TweenWaitKind kind, float threshold, CancellationToken cancellationToken)
    {
        _tween = tween;
        _kind = kind;
        _threshold = threshold;
        _token = cancellationToken;
    }

    /// <summary>Makes this type awaitable. You normally never call it yourself.</summary>
    public TweenAwaiter GetAwaiter() => new(_tween, _kind, _threshold, _token);

    /// <summary>Returns the same wait, cancellable through <paramref name="cancellationToken"/>.</summary>
    public TweenAwaitable WithCancellation(CancellationToken cancellationToken)
        => new(_tween, _kind, _threshold, cancellationToken);

    /// <summary>
    /// Wraps the wait in a <see cref="Task"/> for interop - <see cref="Task.WhenAll(Task[])"/> and
    /// friends. Awaiting this awaitable directly is cheaper; the task is an extra allocation.
    /// </summary>
    public Task AsTask()
    {
        if (!GetAwaiter().IsCompleted)
            return Run(this);

        return _token.IsCancellationRequested ? Task.FromCanceled(_token) : Task.CompletedTask;
    }

    private static async Task Run(TweenAwaitable awaitable) => await awaitable;
}

/// <summary>
/// The awaiter behind a <see cref="TweenAwaitable"/>. Resuming is driven by the library's tick, so
/// a tween that is never ticked keeps its awaiter suspended.
/// </summary>
public readonly struct TweenAwaiter : ICriticalNotifyCompletion
{
    private readonly Tween _tween;
    private readonly CancellationToken _token;
    private readonly float _threshold;
    private readonly TweenWaitKind _kind;

    internal TweenAwaiter(Tween tween, TweenWaitKind kind, float threshold, CancellationToken cancellationToken)
    {
        _tween = tween;
        _kind = kind;
        _threshold = threshold;
        _token = cancellationToken;
    }

    /// <summary>
    /// True when there is nothing to wait for, which lets the compiler skip suspending entirely -
    /// awaiting an already finished, already dead or <see cref="Tween.None"/> handle costs nothing.
    /// </summary>
    public bool IsCompleted => _token.IsCancellationRequested || TweenAwaiterRegistry.IsSatisfied(_tween, _kind, _threshold);

    /// <summary>Throws <see cref="OperationCanceledException"/> if the wait was cancelled, otherwise returns.</summary>
    public void GetResult() => _token.ThrowIfCancellationRequested();

    /// <inheritdoc/>
    public void OnCompleted(Action continuation) => TweenAwaiterRegistry.Register(_tween, _kind, _threshold, _token, continuation);

    /// <inheritdoc/>
    public void UnsafeOnCompleted(Action continuation) => TweenAwaiterRegistry.Register(_tween, _kind, _threshold, _token, continuation);
}

/// <summary>
/// Holds the continuations of every suspended <c>await</c> and resumes them from the tick.
/// <para>
/// Waits are polled rather than hung off the tween's callbacks: a tween owns one delegate per
/// callback kind, so hooking <c>OnComplete</c> would silently steal the user's own. Polling also
/// keeps the update loop and the per-tween data untouched - a program that never awaits pays a
/// single <c>if</c> per tick.
/// </para>
/// </summary>
internal static class TweenAwaiterRegistry
{
    private struct Waiter
    {
        public Tween Tween;
        public Action? Continuation;
        public CancellationToken Token;
        public float Threshold;
        public TweenWaitKind Kind;
    }

    // Registration can come from any thread (an async method that resumed on the pool), while the
    // pump only ever runs on the ticking thread. The gate covers the list, never a continuation.
    private static readonly object s_gate = new();
    private static Waiter[] s_waiters = new Waiter[8];
    private static int s_count;

    /// <summary>Guards against a continuation ticking the library again from inside the pump.</summary>
    private static bool s_pumping;

    internal static int PendingCount
    {
        get { lock (s_gate) return s_count; }
    }

    internal static void Register(Tween tween, TweenWaitKind kind, float threshold, CancellationToken token, Action continuation)
    {
        if (continuation is null)
            return;

        lock (s_gate)
        {
            if (s_count == s_waiters.Length)
                Array.Resize(ref s_waiters, s_waiters.Length * 2);

            ref Waiter w = ref s_waiters[s_count++];
            w.Tween = tween;
            w.Kind = kind;
            w.Threshold = threshold;
            w.Token = token;
            w.Continuation = continuation;
        }
    }

    /// <summary>Resumes every wait whose condition now holds. Called at the end of each tick.</summary>
    internal static void Pump()
    {
        if (s_pumping)
            return;

        int i;
        lock (s_gate)
            i = s_count - 1;

        if (i < 0)
            return;

        s_pumping = true;
        try
        {
            // Backwards, with swap-removal from the tail: a continuation may register new waits,
            // and those land past the point this pass will ever look at.
            for (; i >= 0; i--)
            {
                Action? continuation;
                lock (s_gate)
                {
                    if (i >= s_count)
                        continue;

                    ref Waiter w = ref s_waiters[i];
                    if (!w.Token.IsCancellationRequested && !IsSatisfied(w.Tween, w.Kind, w.Threshold))
                        continue;

                    continuation = w.Continuation;
                    RemoveAt(i);
                }

                // ---- user code below: the list is considered invalid from here ----
                continuation?.Invoke();
            }
        }
        finally
        {
            s_pumping = false;
        }
    }

    private static void RemoveAt(int index)
    {
        int last = --s_count;
        s_waiters[index] = s_waiters[last];
        s_waiters[last] = default;
    }

    /// <summary>
    /// True when the wait is over. A handle that no longer resolves counts as satisfied for every
    /// kind: the tween is gone, so nothing about it can change again.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsSatisfied(Tween tween, TweenWaitKind kind, float threshold)
    {
        ITweenStorage? storage = tween.Version == 0 ? null : TweenManager.Resolve(tween.StorageId);
        if (storage == null || !storage.IsAlive(tween.Slot, tween.Version))
            return true;

        ref TweenCore c = ref storage.CoreRef(tween.Slot, tween.Version);
        return kind switch
        {
            TweenWaitKind.Completion => (c.Flags & TweenFlags.Completed) != 0,
            TweenWaitKind.Start => (c.Flags & TweenFlags.Started) != 0,
            TweenWaitKind.ElapsedLoops => c.CompletedLoops >= (int)threshold,
            TweenWaitKind.Position => c.DelayElapsed + c.Duration * c.CompletedLoops + c.Position >= threshold,
            TweenWaitKind.Kill => false, // only the death of the tween ends this one
            _ => true
        };
    }
}
