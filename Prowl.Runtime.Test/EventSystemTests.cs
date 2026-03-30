// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.EventSystem;

using Xunit;

namespace Prowl.Runtime.Test;

/// <summary>
/// Tests for the EventSystem: EventManager, Event, and EventDelegateContainer.
/// Covers priority ordering, enable/disable, global invoke, and thread safety.
/// </summary>
public class EventSystemTests : IDisposable
{
    private enum TestEvents
    {
        EventA,
        EventB,
        EventC,
    }

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
        _disposables.Clear();
    }

    private EventManager<TestEvents> CreateManager(bool global = false)
    {
        var mgr = new EventManager<TestEvents>(global);
        _disposables.Add(mgr);
        return mgr;
    }

    #region Basic Invoke

    [Fact]
    public void InvokeEvent_CallsRegisteredDelegate()
    {
        var manager = CreateManager();
        bool called = false;
        manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        manager.InvokeEvent(TestEvents.EventA);

        Assert.True(called);
    }

    [Fact]
    public void InvokeEvent_DoesNotCallOtherEventDelegates()
    {
        var manager = CreateManager();
        bool calledA = false;
        bool calledB = false;
        manager.AddNewDelegate(TestEvents.EventA, () => calledA = true);
        manager.AddNewDelegate(TestEvents.EventB, () => calledB = true);

        manager.InvokeEvent(TestEvents.EventA);

        Assert.True(calledA);
        Assert.False(calledB);
    }

    [Fact]
    public void InvokeEvent_CallsMultipleDelegates()
    {
        var manager = CreateManager();
        int callCount = 0;
        manager.AddNewDelegate(TestEvents.EventA, () => callCount++);
        manager.AddNewDelegate(TestEvents.EventA, () => callCount++);
        manager.AddNewDelegate(TestEvents.EventA, () => callCount++);

        manager.InvokeEvent(TestEvents.EventA);

        Assert.Equal(3, callCount);
    }

    [Fact]
    public void InvokeEvent_PassesArguments()
    {
        var manager = CreateManager();
        TestParam? received = null;
        manager.AddNewDelegate<TestParam>(TestEvents.EventA, args => received = args);

        var param = new TestParam { Data = 42 };
        manager.InvokeEvent(TestEvents.EventA, param);

        Assert.NotNull(received);
        Assert.Equal(42, received!.Data);
    }

    #endregion

    #region Priority Ordering

    [Fact]
    public void InvokeEvent_DelegatesExecuteInPriorityOrder()
    {
        var manager = CreateManager();
        var order = new List<int>();

        manager.AddNewDelegate(TestEvents.EventA, () => order.Add(2), priority: 2);
        manager.AddNewDelegate(TestEvents.EventA, () => order.Add(0), priority: 0);
        manager.AddNewDelegate(TestEvents.EventA, () => order.Add(1), priority: 1);

        manager.InvokeEvent(TestEvents.EventA);

        Assert.Equal([0, 1, 2], order);
    }

    [Fact]
    public void InvokeEvent_SamePriority_BothCalled()
    {
        var manager = CreateManager();
        var order = new List<string>();

        manager.AddNewDelegate(TestEvents.EventA, () => order.Add("first"), priority: 0);
        manager.AddNewDelegate(TestEvents.EventA, () => order.Add("second"), priority: 0);

        manager.InvokeEvent(TestEvents.EventA);

        Assert.Equal(2, order.Count);
        Assert.Contains("first", order);
        Assert.Contains("second", order);
    }

    [Fact]
    public void InvokeEvent_NegativePriority_ExecutesBeforeZero()
    {
        var manager = CreateManager();
        var order = new List<int>();

        manager.AddNewDelegate(TestEvents.EventA, () => order.Add(0), priority: 0);
        manager.AddNewDelegate(TestEvents.EventA, () => order.Add(-1), priority: -1);

        manager.InvokeEvent(TestEvents.EventA);

        Assert.Equal([-1, 0], order);
    }

    #endregion

    #region Enable / Disable

    [Fact]
    public void DisabledManager_DoesNotInvoke()
    {
        var manager = CreateManager();
        bool called = false;
        manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        manager.Enabled = false;
        manager.InvokeEvent(TestEvents.EventA);

        Assert.False(called);
    }

    [Fact]
    public void ReEnabledManager_Invokes()
    {
        var manager = CreateManager();
        bool called = false;
        manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        manager.Enabled = false;
        manager.Enabled = true;
        manager.InvokeEvent(TestEvents.EventA);

        Assert.True(called);
    }

    [Fact]
    public void DisableEvent_PreventsInvocation()
    {
        var manager = CreateManager();
        bool called = false;
        manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        manager.DisableEvent(TestEvents.EventA);
        manager.InvokeEvent(TestEvents.EventA);

        Assert.False(called);
    }

    [Fact]
    public void EnableEvent_RestoresInvocation()
    {
        var manager = CreateManager();
        bool called = false;
        manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        manager.DisableEvent(TestEvents.EventA);
        manager.EnableEvent(TestEvents.EventA);
        manager.InvokeEvent(TestEvents.EventA);

        Assert.True(called);
    }

    [Fact]
    public void DisabledDelegate_IsNotInvoked()
    {
        var manager = CreateManager();
        bool called = false;
        var container = manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        container.Disable();
        manager.InvokeEvent(TestEvents.EventA);

        Assert.False(called);
    }

    [Fact]
    public void ReEnabledDelegate_IsInvoked()
    {
        var manager = CreateManager();
        bool called = false;
        var container = manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        container.Disable();
        container.Enable();
        manager.InvokeEvent(TestEvents.EventA);

        Assert.True(called);
    }

    #endregion

    #region Add / Remove Delegate

    [Fact]
    public void RemoveDelegate_PreventsInvocation()
    {
        var manager = CreateManager();
        bool called = false;
        var container = manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        manager.RemoveDelegate(container);
        manager.InvokeEvent(TestEvents.EventA);

        Assert.False(called);
    }

    [Fact]
    public void RemoveDelegate_OnlyRemovesSpecificDelegate()
    {
        var manager = CreateManager();
        bool calledFirst = false;
        bool calledSecond = false;
        var first = manager.AddNewDelegate(TestEvents.EventA, () => calledFirst = true);
        manager.AddNewDelegate(TestEvents.EventA, () => calledSecond = true);

        manager.RemoveDelegate(first);
        manager.InvokeEvent(TestEvents.EventA);

        Assert.False(calledFirst);
        Assert.True(calledSecond);
    }

    [Fact]
    public void AddDelegate_AfterRemove_Works()
    {
        var manager = CreateManager();
        bool called = false;
        var container = manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        manager.RemoveDelegate(container);
        manager.AddDelegate(container);
        manager.InvokeEvent(TestEvents.EventA);

        Assert.True(called);
    }

    #endregion

    #region Global Invoke

    [Fact]
    public void GlobalInvokeEvent_InvokesGlobalManagers()
    {
        var manager = CreateManager(global: true);
        bool called = false;
        manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        EventManager<TestEvents>.GlobalInvokeEvent(TestEvents.EventA);

        Assert.True(called);
    }

    [Fact]
    public void GlobalInvokeEvent_SkipsNonGlobalManagers()
    {
        var manager = CreateManager(global: false);
        bool called = false;
        manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        EventManager<TestEvents>.GlobalInvokeEvent(TestEvents.EventA);

        Assert.False(called);
    }

    [Fact]
    public void GlobalInvokeEvent_SkipsDisabledGlobalManagers()
    {
        var manager = CreateManager(global: true);
        bool called = false;
        manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        manager.Enabled = false;
        EventManager<TestEvents>.GlobalInvokeEvent(TestEvents.EventA);

        Assert.False(called);
    }

    [Fact]
    public void LastGlobalInstance_ReturnsLastEnabledGlobal()
    {
        var mgr1 = CreateManager(global: true);
        var mgr2 = CreateManager(global: true);

        var last = EventManager<TestEvents>.LastGlobalInstance;

        Assert.Equal(mgr2, last);
    }

    [Fact]
    public void LastGlobalInstance_SkipsDisabled()
    {
        var mgr1 = CreateManager(global: true);
        var mgr2 = CreateManager(global: true);
        mgr2.Enabled = false;

        var last = EventManager<TestEvents>.LastGlobalInstance;

        Assert.Equal(mgr1, last);
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_RemovesFromGlobalInstances()
    {
        var manager = CreateManager(global: true);
        manager.AddNewDelegate(TestEvents.EventA, () => { });

        manager.Dispose();
        _disposables.Remove(manager);

        // After disposal, the manager should no longer be reachable globally
        bool found = false;
        var check = CreateManager(global: true);
        bool checkCalled = false;
        check.AddNewDelegate(TestEvents.EventA, () => checkCalled = true);

        // Invoke globally — only 'check' should fire
        EventManager<TestEvents>.GlobalInvokeEvent(TestEvents.EventA);
        Assert.True(checkCalled);
    }

    #endregion

    #region Thread Safety

    [Fact]
    public void ConcurrentInvokeAndAdd_DoesNotThrow()
    {
        var manager = CreateManager();
        int invokeCount = 0;

        // Pre-populate some delegates
        for (int i = 0; i < 10; i++)
            manager.AddNewDelegate(TestEvents.EventA, () => Interlocked.Increment(ref invokeCount));

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Invoke on multiple threads while adding new delegates
        var tasks = new List<Task>();
        for (int t = 0; t < 4; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                    manager.InvokeEvent(TestEvents.EventA);
            }));
        }

        tasks.Add(Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
                manager.AddNewDelegate(TestEvents.EventA, () => Interlocked.Increment(ref invokeCount));
        }));

        // Should not throw any exceptions
        Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(3));
        cts.Cancel();

        Assert.True(invokeCount > 0);
    }

    #endregion

    #region Delegate Self-Unsubscription (IDisposable)

    [Fact]
    public void DelegateDispose_RemovesFromEvent()
    {
        var manager = CreateManager();
        bool called = false;
        var container = manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        container.Dispose();
        manager.InvokeEvent(TestEvents.EventA);

        Assert.False(called);
    }

    [Fact]
    public void DelegateDispose_UsingPattern_RemovesFromEvent()
    {
        var manager = CreateManager();
        bool called = false;

        using (var container = manager.AddNewDelegate(TestEvents.EventA, () => called = true))
        {
            manager.InvokeEvent(TestEvents.EventA);
            Assert.True(called);

            called = false;
        }

        manager.InvokeEvent(TestEvents.EventA);
        Assert.False(called);
    }

    [Fact]
    public void DelegateDispose_WhenNotLinked_DoesNotThrow()
    {
        var manager = CreateManager();
        var container = manager.AddNewDelegate(TestEvents.EventA, () => { });

        manager.RemoveDelegate(container);

        // Dispose on already-unlinked delegate should be safe
        var ex = Record.Exception(() => container.Dispose());
        Assert.Null(ex);
    }

    #endregion

    #region Remove/Unlink Bug Fix

    [Fact]
    public void Remove_WrongEvent_DoesNotUnlink()
    {
        var manager = CreateManager();
        bool called = false;
        var container = manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        // Try to remove from EventB — should fail and NOT unlink the delegate from EventA
        manager.RemoveDelegate(new EventDelegateContainer<TestEvents, Unit>(TestEvents.EventB, _ => { }));

        // The original delegate should still be linked and invocable
        manager.InvokeEvent(TestEvents.EventA);
        Assert.True(called);
        Assert.NotNull(container.Event);
    }

    #endregion

    #region Event Cancellation

    [Fact]
    public void Cancellation_StopsPropagation()
    {
        var manager = CreateManager();
        var order = new List<int>();

        manager.AddNewDelegate<CancellableTestArgs>(TestEvents.EventA, args =>
        {
            order.Add(0);
            args.Cancelled = true;
        }, priority: 0);
        manager.AddNewDelegate<CancellableTestArgs>(TestEvents.EventA, args =>
        {
            order.Add(1);
        }, priority: 1);

        var cancellable = new CancellableTestArgs();
        manager.InvokeEvent(TestEvents.EventA, cancellable);

        Assert.Single(order);
        Assert.Equal(0, order[0]);
    }

    [Fact]
    public void Cancellation_NotSet_AllDelegatesCalled()
    {
        var manager = CreateManager();
        var order = new List<int>();

        manager.AddNewDelegate<CancellableTestArgs>(TestEvents.EventA, args => order.Add(0), priority: 0);
        manager.AddNewDelegate<CancellableTestArgs>(TestEvents.EventA, args => order.Add(1), priority: 1);
        manager.AddNewDelegate<CancellableTestArgs>(TestEvents.EventA, args => order.Add(2), priority: 2);

        var cancellable = new CancellableTestArgs();
        manager.InvokeEvent(TestEvents.EventA, cancellable);

        Assert.Equal([0, 1, 2], order);
    }

    [Fact]
    public void Cancellation_MidChain_StopsRemainingDelegates()
    {
        var manager = CreateManager();
        var order = new List<int>();

        manager.AddNewDelegate<CancellableTestArgs>(TestEvents.EventA, args => order.Add(0), priority: 0);
        manager.AddNewDelegate<CancellableTestArgs>(TestEvents.EventA, args =>
        {
            order.Add(1);
            args.Cancelled = true;
        }, priority: 1);
        manager.AddNewDelegate<CancellableTestArgs>(TestEvents.EventA, args => order.Add(2), priority: 2);

        var cancellable = new CancellableTestArgs();
        manager.InvokeEvent(TestEvents.EventA, cancellable);

        Assert.Equal([0, 1], order);
        Assert.True(cancellable.Cancelled);
    }

    #endregion

    #region Self-Removal During Invocation

    [Fact]
    public void SelfRemoval_DuringInvoke_DoesNotThrow()
    {
        var manager = CreateManager();
        EventDelegateContainer<TestEvents, Unit>? selfRef = null;
        int callCount = 0;

        selfRef = manager.AddNewDelegate(TestEvents.EventA, () =>
        {
            callCount++;
            // Self-remove while invocation is in progress
            manager.RemoveDelegate(selfRef!);
        });
        manager.AddNewDelegate(TestEvents.EventA, () => callCount++, priority: 1);

        var ex = Record.Exception(() => manager.InvokeEvent(TestEvents.EventA));

        Assert.Null(ex);
        // Both handlers should run because Invoke uses a snapshot
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void RemoveOtherDelegate_DuringInvoke_DoesNotThrow()
    {
        var manager = CreateManager();
        var order = new List<int>();

        EventDelegateContainer<TestEvents, Unit>? secondRef = null;
        manager.AddNewDelegate(TestEvents.EventA, () =>
        {
            order.Add(0);
            // Remove the next handler while invocation is in progress
            manager.RemoveDelegate(secondRef!);
        }, priority: 0);
        secondRef = manager.AddNewDelegate(TestEvents.EventA, () => order.Add(1), priority: 1);
        manager.AddNewDelegate(TestEvents.EventA, () => order.Add(2), priority: 2);

        var ex = Record.Exception(() => manager.InvokeEvent(TestEvents.EventA));

        Assert.Null(ex);
        // All three run — the snapshot was taken before handler 0 removed handler 1
        Assert.Equal([0, 1, 2], order);
    }

    [Fact]
    public void SelfRemoval_DuringInvoke_PreventsNextInvocation()
    {
        var manager = CreateManager();
        int callCount = 0;
        EventDelegateContainer<TestEvents, Unit>? selfRef = null;

        selfRef = manager.AddNewDelegate(TestEvents.EventA, () =>
        {
            callCount++;
            manager.RemoveDelegate(selfRef!);
        });

        manager.InvokeEvent(TestEvents.EventA);
        Assert.Equal(1, callCount);

        // Second invoke — delegate was removed, should not fire
        manager.InvokeEvent(TestEvents.EventA);
        Assert.Equal(1, callCount);
    }

    #endregion

    #region Disposed Manager Behavior

    [Fact]
    public void DisposedManager_InvokeEvent_SilentlyNoOps()
    {
        var manager = CreateManager();
        bool called = false;
        manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        manager.Dispose();
        _disposables.Remove(manager);

        // InvokeEvent on a disposed manager should not throw and not fire
        var ex = Record.Exception(() => manager.InvokeEvent(TestEvents.EventA));
        Assert.Null(ex);
        Assert.False(called);
    }

    [Fact]
    public void DisposedManager_AddNewDelegate_Throws()
    {
        var manager = CreateManager();
        manager.Dispose();
        _disposables.Remove(manager);

        Assert.Throws<ObjectDisposedException>(() =>
            manager.AddNewDelegate(TestEvents.EventA, () => { }));
    }

    [Fact]
    public void DisposedManager_AddNewDelegateTyped_Throws()
    {
        var manager = CreateManager();
        manager.Dispose();
        _disposables.Remove(manager);

        Assert.Throws<ObjectDisposedException>(() =>
            manager.AddNewDelegate<TestParam>(TestEvents.EventA, _ => { }));
    }

    #endregion

    #region Global Invoke with Typed Parameters

    [Fact]
    public void GlobalInvokeEvent_WithTypedArgs_InvokesGlobalManagers()
    {
        var manager = CreateManager(global: true);
        TestParam? received = null;
        manager.AddNewDelegate<TestParam>(TestEvents.EventA, args => received = args);

        var param = new TestParam { Data = 99 };
        EventManager<TestEvents>.GlobalInvokeEvent(TestEvents.EventA, param);

        Assert.NotNull(received);
        Assert.Equal(99, received!.Data);
    }

    [Fact]
    public void GlobalInvokeEvent_WithTypedArgs_SkipsNonGlobalManagers()
    {
        var manager = CreateManager(global: false);
        TestParam? received = null;
        manager.AddNewDelegate<TestParam>(TestEvents.EventA, args => received = args);

        var param = new TestParam { Data = 77 };
        EventManager<TestEvents>.GlobalInvokeEvent(TestEvents.EventA, param);

        Assert.Null(received);
    }

    [Fact]
    public void GlobalInvokeEvent_WithTypedArgs_MultipleManagers_AllReceive()
    {
        var mgr1 = CreateManager(global: true);
        var mgr2 = CreateManager(global: true);
        int received1 = 0;
        int received2 = 0;
        mgr1.AddNewDelegate<TestParam>(TestEvents.EventA, args => received1 = args.Data);
        mgr2.AddNewDelegate<TestParam>(TestEvents.EventA, args => received2 = args.Data);

        var param = new TestParam { Data = 55 };
        EventManager<TestEvents>.GlobalInvokeEvent(TestEvents.EventA, param);

        Assert.Equal(55, received1);
        Assert.Equal(55, received2);
    }

    #endregion

    #region Type-Mismatched Invocation

    [Fact]
    public void InvokeEvent_TypeMismatch_SilentlySkipsHandlers()
    {
        var manager = CreateManager();
        bool called = false;
        // Register a handler for TestParam
        manager.AddNewDelegate<TestParam>(TestEvents.EventA, _ => called = true);

        // Invoke with a completely different type — should silently skip
        manager.InvokeEvent(TestEvents.EventA, 42);

        Assert.False(called);
    }

    [Fact]
    public void InvokeEvent_TypeMismatch_DoesNotThrow()
    {
        var manager = CreateManager();
        manager.AddNewDelegate<TestParam>(TestEvents.EventA, _ => { });

        var ex = Record.Exception(() => manager.InvokeEvent(TestEvents.EventA, "wrong type"));

        Assert.Null(ex);
    }

    [Fact]
    public void InvokeEvent_MixedTypes_OnlyMatchingHandlersCalled()
    {
        var manager = CreateManager();
        bool intCalled = false;
        bool stringCalled = false;
        manager.AddNewDelegate<int>(TestEvents.EventA, _ => intCalled = true);
        manager.AddNewDelegate<string>(TestEvents.EventA, _ => stringCalled = true);

        manager.InvokeEvent(TestEvents.EventA, 42);

        Assert.True(intCalled);
        Assert.False(stringCalled);
    }

    [Fact]
    public void InvokeEvent_ParameterlessInvoke_DoesNotTriggerTypedHandlers()
    {
        var manager = CreateManager();
        bool typedCalled = false;
        bool parameterlessCalled = false;
        manager.AddNewDelegate<TestParam>(TestEvents.EventA, _ => typedCalled = true);
        manager.AddNewDelegate(TestEvents.EventA, () => parameterlessCalled = true);

        manager.InvokeEvent(TestEvents.EventA);

        Assert.False(typedCalled);
        Assert.True(parameterlessCalled);
    }

    #endregion

    /// <summary>
    /// Test event argument class used by typed-args tests.
    /// </summary>
    private class TestParam
    {
        public int Data { get; set; }
    }

    /// <summary>
    /// Cancellable event argument class used by cancellation tests.
    /// </summary>
    private class CancellableTestArgs : ICancellable
    {
        public bool Cancelled { get; set; }
        public int Data { get; set; }
    }
}
