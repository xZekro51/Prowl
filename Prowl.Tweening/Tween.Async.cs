// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Threading;

namespace Prowl.Tweening;

/// <summary>
/// Async half of <see cref="Tween"/>: waiting on a tween from an <c>async</c> method instead of
/// through callbacks.
/// </summary>
/// <example>
/// <code>
/// async Task PlayIntro()
/// {
///     await transform.MoveY(5f, 1f).AsyncWaitForCompletion();
///     await transform.Scale(2f, 0.3f).AsyncWaitForCompletion(token);
/// }
/// </code>
/// </example>
/// <remarks>
/// <see cref="Tween"/> deliberately has no <c>GetAwaiter</c> of its own, so you always name the
/// wait. An awaitable <see cref="Tween"/> would make the compiler flag every fire-and-forget
/// <c>transform.Move(...)</c> statement inside an <c>async</c> method as a forgotten <c>await</c>.
/// </remarks>
public readonly partial struct Tween
{
    #region Awaiting

    /// <summary>
    /// Waits until the tween reaches the end of its final loop, or is destroyed before it gets
    /// there. An infinite tween (<c>SetLoops(-1)</c>) therefore only resumes when something kills it.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the wait, throwing <see cref="System.OperationCanceledException"/>. Cancellation is
    /// observed on the next tick, and never affects the tween itself - kill it separately if that
    /// is what you want.
    /// </param>
    public TweenAwaitable AsyncWaitForCompletion(CancellationToken cancellationToken = default)
        => new(this, TweenWaitKind.Completion, 0f, cancellationToken);

    /// <summary>Waits until the tween is destroyed, which for a completed tween with auto-kill is the same moment.</summary>
    /// <inheritdoc cref="AsyncWaitForCompletion(CancellationToken)" path="/param"/>
    public TweenAwaitable AsyncWaitForKill(CancellationToken cancellationToken = default)
        => new(this, TweenWaitKind.Kill, 0f, cancellationToken);

    /// <summary>Waits until the tween runs for the first time, that is until any delay has passed.</summary>
    /// <inheritdoc cref="AsyncWaitForCompletion(CancellationToken)" path="/param"/>
    public TweenAwaitable AsyncWaitForStart(CancellationToken cancellationToken = default)
        => new(this, TweenWaitKind.Start, 0f, cancellationToken);

    /// <summary>Waits until <paramref name="loops"/> loop cycles have finished.</summary>
    /// <param name="loops">Cycle count to wait for, compared against <see cref="CompletedLoops"/>.</param>
    /// <param name="cancellationToken">Cancels the wait, throwing <see cref="System.OperationCanceledException"/>.</param>
    public TweenAwaitable AsyncWaitForElapsedLoops(int loops, CancellationToken cancellationToken = default)
        => new(this, TweenWaitKind.ElapsedLoops, loops, cancellationToken);

    /// <summary>Waits until the playhead passes an absolute time on the tween.</summary>
    /// <param name="position">Time to wait for, measured like <see cref="Elapsed"/>: delay and completed loops included.</param>
    /// <param name="cancellationToken">Cancels the wait, throwing <see cref="System.OperationCanceledException"/>.</param>
    public TweenAwaitable AsyncWaitForPosition(float position, CancellationToken cancellationToken = default)
        => new(this, TweenWaitKind.Position, position, cancellationToken);

    #endregion

    #region Static helpers

    /// <summary>
    /// Waits <paramref name="seconds"/> without a callback: the async counterpart of
    /// <see cref="DelayedCall(float, System.Action, bool)"/>.
    /// </summary>
    /// <param name="seconds">How long to wait.</param>
    /// <param name="ignoreTimeScale">
    /// When true (the default) the countdown uses unscaled time, so slowing the game down or
    /// pausing it won't hold it back.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait, throwing <see cref="System.OperationCanceledException"/>.</param>
    public static TweenAwaitable AsyncWaitForSeconds(float seconds, bool ignoreTimeScale = true, CancellationToken cancellationToken = default)
        => To<Unit, UnitAdapter>(default, default, seconds)
            .BindNothing()
            .SetEase(Ease.Linear)
            .SetUpdate(ignoreTimeScale)
            .AsyncWaitForCompletion(cancellationToken);

    /// <summary>How many <c>await</c>s are currently suspended on a tween. Diagnostics only.</summary>
    public static int PendingAwaits => TweenAwaiterRegistry.PendingCount;

    #endregion
}
