// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Tweening;

using Xunit;

namespace Prowl.Runtime.Test;

/// <summary>
/// Tests for awaiting tweens. Every test drives the library by hand with
/// <see cref="Tween.Update(float, float)"/>, which is also what resumes the continuations, so a
/// suspended <c>await</c> can only make progress where a test says it can.
/// </summary>
public class TweenAsyncTests : IDisposable
{
    public TweenAsyncTests()
    {
        Tween.KillAll();
        Tween.GlobalTimeScale = 1f;
    }

    public void Dispose() => Tween.KillAll();

    private static Tween Simple(float duration = 1f) => Tween.To(0f, 1f, duration).BindNothing();

    [Fact]
    public async Task Await_ResumesWhenTheTweenCompletes()
    {
        Tween tween = Simple(1f);
        bool done = false;

        Task task = Run();
        async Task Run()
        {
            await tween.AsyncWaitForCompletion();
            done = true;
        }

        Tween.Update(0.5f);
        Assert.False(done);
        Assert.Equal(1, Tween.PendingAwaits);

        Tween.Update(0.6f);
        Assert.True(done);
        Assert.Equal(0, Tween.PendingAwaits);
        await task;
    }

    // The continuation runs inline on the ticking thread, not on a pool thread later.
    [Fact]
    public async Task Await_ResumesOnTheTickingThread()
    {
        Tween tween = Simple(1f);
        int tickThread = Environment.CurrentManagedThreadId;
        int resumeThread = 0;

        Task task = Run();
        async Task Run()
        {
            await tween.AsyncWaitForCompletion();
            resumeThread = Environment.CurrentManagedThreadId;
        }

        Tween.Update(1.5f);

        Assert.Equal(tickThread, resumeThread);
        await task;
    }

    // Awaiting something that can never change again must not suspend at all.
    [Fact]
    public async Task Await_OnDeadOrDefaultHandle_CompletesSynchronously()
    {
        Tween killed = Simple();
        killed.Kill();

        await killed.AsyncWaitForCompletion();
        await killed.AsyncWaitForKill();
        await Tween.None.AsyncWaitForCompletion();
        await Tween.None.AsyncWaitForElapsedLoops(3);

        Assert.Equal(0, Tween.PendingAwaits);
    }

    // A tween killed before it finishes resumes its waiters instead of hanging them forever.
    [Fact]
    public async Task AsyncWaitForCompletion_ResumesWhenTheTweenIsKilledEarly()
    {
        Tween tween = Simple(10f);
        bool done = false;

        Task task = Run();
        async Task Run()
        {
            await tween.AsyncWaitForCompletion();
            done = true;
        }

        Tween.Update(1f);
        Assert.False(done);

        tween.Kill();
        Tween.Update(0f);

        Assert.True(done);
        await task;
    }

    [Fact]
    public async Task AsyncWaitForKill_IgnoresCompletionOfATweenThatSurvivesIt()
    {
        Tween tween = Simple(1f).SetAutoKill(false);
        bool done = false;

        Task task = Run();
        async Task Run()
        {
            await tween.AsyncWaitForKill();
            done = true;
        }

        Tween.Update(1.5f);
        Assert.True(tween.IsComplete);
        Assert.False(done);

        tween.Kill();
        Tween.Update(0f);

        Assert.True(done);
        await task;
    }

    [Fact]
    public async Task AsyncWaitForStart_ResumesOnceTheDelayHasPassed()
    {
        Tween tween = Simple(1f).SetDelay(1f);
        bool started = false;

        Task task = Run();
        async Task Run()
        {
            await tween.AsyncWaitForStart();
            started = true;
        }

        Tween.Update(0.5f);
        Assert.False(started);

        Tween.Update(0.6f);
        Assert.True(started);
        Assert.False(tween.IsComplete);
        await task;
    }

    [Fact]
    public async Task AsyncWaitForElapsedLoops_ResumesMidwayThroughAnInfiniteTween()
    {
        Tween tween = Simple(1f).SetLoops(-1);
        bool done = false;

        Task task = Run();
        async Task Run()
        {
            await tween.AsyncWaitForElapsedLoops(2);
            done = true;
        }

        Tween.Update(1.5f);
        Assert.False(done);

        Tween.Update(1f);
        Assert.True(done);
        Assert.True(tween.IsActive);
        await task;
    }

    [Fact]
    public async Task AsyncWaitForPosition_CountsTheDelayIn()
    {
        Tween tween = Simple(2f).SetDelay(1f);
        bool done = false;

        Task task = Run();
        async Task Run()
        {
            await tween.AsyncWaitForPosition(1.5f);
            done = true;
        }

        Tween.Update(1.2f);   // still inside the delay's shadow, elapsed = 1.2
        Assert.False(done);

        Tween.Update(0.5f);   // elapsed = 1.7
        Assert.True(done);
        await task;
    }

    [Fact]
    public async Task AsyncWaitForSeconds_ResumesAfterTheGivenTime()
    {
        bool done = false;

        Task task = Run();
        async Task Run()
        {
            await Tween.AsyncWaitForSeconds(0.5f);
            done = true;
        }

        Tween.Update(0.2f);
        Assert.False(done);

        Tween.Update(0.4f);
        Assert.True(done);
        await task;
    }

    // Unscaled by default, so a frozen game clock does not hold the wait back.
    [Fact]
    public async Task AsyncWaitForSeconds_IgnoresTimeScaleByDefault()
    {
        Tween.GlobalTimeScale = 0f;
        bool done = false;

        Task task = Run();
        async Task Run()
        {
            await Tween.AsyncWaitForSeconds(0.5f);
            done = true;
        }

        Tween.Update(1f, 1f);

        Assert.True(done);
        await task;
    }

    [Fact]
    public async Task Cancellation_ThrowsAndDropsTheWaitWithoutTouchingTheTween()
    {
        using var cts = new CancellationTokenSource();
        Tween tween = Simple(10f);

        Task task = Run();
        async Task Run() => await tween.AsyncWaitForCompletion(cts.Token);

        Tween.Update(1f);
        Assert.False(task.IsCompleted);

        cts.Cancel();
        Tween.Update(0f);   // cancellation is observed on the tick

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, Tween.PendingAwaits);
        Assert.True(tween.IsActive, "cancelling a wait must not kill the tween");
    }

    [Fact]
    public async Task Cancellation_BeforeAwaiting_ThrowsWithoutSuspending()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Tween tween = Simple(10f);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await tween.AsyncWaitForCompletion(cts.Token));
        Assert.Equal(0, Tween.PendingAwaits);
    }

    [Fact]
    public async Task ManyWaitersOnOneTween_AllResume()
    {
        Tween tween = Simple(1f);
        int resumed = 0;

        Task[] tasks = new Task[8];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = Run();

        async Task Run()
        {
            await tween.AsyncWaitForCompletion();
            resumed++;
        }

        Assert.Equal(8, Tween.PendingAwaits);
        Tween.Update(1.5f);

        Assert.Equal(8, resumed);
        Assert.Equal(0, Tween.PendingAwaits);
        await Task.WhenAll(tasks);
    }

    // A continuation that awaits again must not disturb the waiters still being pumped.
    [Fact]
    public async Task ChainedAwaits_RunOneTweenAfterAnother()
    {
        int stage = 0;

        Task task = Run();
        async Task Run()
        {
            await Simple(1f).AsyncWaitForCompletion();
            stage = 1;
            await Simple(1f).AsyncWaitForCompletion();
            stage = 2;
        }

        Tween.Update(1.5f);
        Assert.Equal(1, stage);

        // The second tween was created from inside the pump, so it starts on the next tick.
        Tween.Update(1.5f);
        Assert.Equal(2, stage);
        await task;
    }

    [Fact]
    public async Task Sequence_IsAwaitable()
    {
        Sequence seq = Tween.Sequence();
        seq.Append(Simple(1f));
        seq.Append(Simple(1f));

        bool done = false;
        Task task = Run();
        async Task Run()
        {
            await seq.AsyncWaitForCompletion();
            done = true;
        }

        Tween.Update(1.5f);
        Assert.False(done);

        Tween.Update(1f);
        Assert.True(done);
        await task;
    }

    // A child of a sequence is driven by the sequence, but still tracks its own completion.
    [Fact]
    public async Task Await_OnASequenceChild_ResumesWhenThatChildEnds()
    {
        Tween first = Simple(1f);
        Sequence seq = Tween.Sequence();
        seq.Append(first);
        seq.Append(Simple(1f));

        bool done = false;
        Task task = Run();
        async Task Run()
        {
            await first.AsyncWaitForCompletion();
            done = true;
        }

        Tween.Update(0.5f);
        Assert.False(done);

        Tween.Update(0.6f);
        Assert.True(done);
        Assert.True(seq.IsActive);
        await task;
    }

    [Fact]
    public async Task AsTask_CompletesAlongsideTheTween()
    {
        Tween tween = Simple(1f);
        Task task = tween.AsyncWaitForCompletion().AsTask();

        Tween.Update(0.5f);
        Assert.False(task.IsCompleted);

        Tween.Update(0.6f);
        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact]
    public async Task AsTask_CombinesWithWhenAll()
    {
        Tween a = Simple(1f);
        Tween b = Simple(2f);

        Task all = Task.WhenAll(a.AsyncWaitForCompletion().AsTask(), b.AsyncWaitForCompletion().AsTask());

        Tween.Update(1.5f);
        Assert.False(all.IsCompleted);

        Tween.Update(1f);
        Assert.True(all.IsCompletedSuccessfully);
        await all;
    }

    [Fact]
    public void AsTask_OnAFinishedWait_DoesNotSuspend()
    {
        Tween tween = Simple();
        tween.Kill();

        Task task = tween.AsyncWaitForCompletion().AsTask();

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(0, Tween.PendingAwaits);
    }

    // Late/Fixed/Manual ticks pump too, so a tween on those does not wait for the Normal update.
    [Fact]
    public async Task Await_WorksOnEveryUpdateType()
    {
        Tween late = Simple(1f).SetUpdate(UpdateType.Late);
        Tween fixedTween = Simple(1f).SetUpdate(UpdateType.Fixed);
        Tween manual = Simple(1f).SetUpdate(UpdateType.Manual);

        int done = 0;
        Task lateTask = Run(late);
        Task fixedTask = Run(fixedTween);
        Task manualTask = Run(manual);

        async Task Run(Tween t)
        {
            await t.AsyncWaitForCompletion();
            done++;
        }

        Tween.LateUpdate(1.5f);
        Assert.Equal(1, done);

        Tween.FixedUpdate(1.5f);
        Assert.Equal(2, done);

        Tween.ManualUpdate(1.5f);
        Assert.Equal(3, done);

        await Task.WhenAll(lateTask, fixedTask, manualTask);
    }

    // Killing everything is the escape hatch for waits left over from a scene that went away.
    [Fact]
    public async Task KillAll_ReleasesEveryPendingWait()
    {
        Task first = Run(Simple(10f));
        Task second = Run(Simple(10f));
        static async Task Run(Tween t) => await t.AsyncWaitForCompletion();

        Assert.Equal(2, Tween.PendingAwaits);

        Tween.KillAll();
        Tween.Update(0f);

        Assert.Equal(0, Tween.PendingAwaits);
        await Task.WhenAll(first, second);
    }
}
