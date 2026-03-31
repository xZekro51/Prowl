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

    #region [EventArgs] Contract Validation

    private enum ContractedEvents
    {
        [EventArgs(typeof(int))]
        TypedEvent,

        UntypedEvent,
    }

    private EventManager<ContractedEvents> CreateContractedManager()
    {
        var mgr = new EventManager<ContractedEvents>();
        _disposables.Add(mgr);
        return mgr;
    }

    [Fact]
    public void AddNewDelegate_MatchingContract_Succeeds()
    {
        var manager = CreateContractedManager();
        var ex = Record.Exception(() =>
            manager.AddNewDelegate<int>(ContractedEvents.TypedEvent, _ => { }));

        Assert.Null(ex);
    }

    [Fact]
    public void AddNewDelegate_MismatchedContract_ThrowsInvalidOperation()
    {
        var manager = CreateContractedManager();

        Assert.Throws<InvalidOperationException>(() =>
            manager.AddNewDelegate<string>(ContractedEvents.TypedEvent, _ => { }));
    }

    [Fact]
    public void AddNewDelegate_NoContract_AcceptsAnyType()
    {
        var manager = CreateContractedManager();

        var ex1 = Record.Exception(() =>
            manager.AddNewDelegate<int>(ContractedEvents.UntypedEvent, _ => { }));
        var ex2 = Record.Exception(() =>
            manager.AddNewDelegate<string>(ContractedEvents.UntypedEvent, _ => { }));

        Assert.Null(ex1);
        Assert.Null(ex2);
    }

    [Fact]
    public void InvokeEvent_MismatchedContract_DoesNotInvokeAndDoesNotThrow()
    {
        var manager = CreateContractedManager();
        bool called = false;
        manager.AddNewDelegate<int>(ContractedEvents.TypedEvent, _ => called = true);

        // Invoke with wrong type — should be silently rejected by the contract check
        var ex = Record.Exception(() =>
            manager.InvokeEvent(ContractedEvents.TypedEvent, "wrong"));

        Assert.Null(ex);
        Assert.False(called);
    }

    [Fact]
    public void InvokeEvent_MatchingContract_InvokesHandler()
    {
        var manager = CreateContractedManager();
        int received = 0;
        manager.AddNewDelegate<int>(ContractedEvents.TypedEvent, x => received = x);

        manager.InvokeEvent(ContractedEvents.TypedEvent, 42);

        Assert.Equal(42, received);
    }

    #endregion

    #region Lifecycle-Aware Subscriptions

    private class TestEngineObject : EngineObject
    {
        public TestEngineObject() : base("TestObj") { }
    }

    [Fact]
    public void LifecycleSubscription_InvokesWhileOwnerAlive()
    {
        var manager = CreateManager();
        var owner = new TestEngineObject();
        bool called = false;
        manager.AddNewDelegate(owner, TestEvents.EventA, () => called = true);

        manager.InvokeEvent(TestEvents.EventA);

        Assert.True(called);
    }

    [Fact]
    public void LifecycleSubscription_AutoUnsubscribesWhenOwnerDisposed()
    {
        var manager = CreateManager();
        var owner = new TestEngineObject();
        int callCount = 0;
        manager.AddNewDelegate(owner, TestEvents.EventA, () => callCount++);

        manager.InvokeEvent(TestEvents.EventA);
        Assert.Equal(1, callCount);

        owner.Dispose();
        manager.InvokeEvent(TestEvents.EventA);
        // Should not have incremented because the lifecycle container auto-removed itself
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void LifecycleSubscription_Typed_AutoUnsubscribesWhenOwnerDisposed()
    {
        var manager = CreateManager();
        var owner = new TestEngineObject();
        int received = 0;
        manager.AddNewDelegate<TestParam>(owner, TestEvents.EventA, args => received = args.Data);

        manager.InvokeEvent(TestEvents.EventA, new TestParam { Data = 42 });
        Assert.Equal(42, received);

        owner.Dispose();
        manager.InvokeEvent(TestEvents.EventA, new TestParam { Data = 99 });
        // Should still be 42 — handler was auto-removed
        Assert.Equal(42, received);
    }

    [Fact]
    public void LifecycleSubscription_ManualDispose_Works()
    {
        var manager = CreateManager();
        var owner = new TestEngineObject();
        bool called = false;
        var container = manager.AddNewDelegate(owner, TestEvents.EventA, () => called = true);

        container.Dispose();
        manager.InvokeEvent(TestEvents.EventA);

        Assert.False(called);
    }

    #endregion

    #region Batch Subscribe

    [Fact]
    public void BatchSubscribe_DefersSnapshotRebuild()
    {
        var manager = CreateManager();
        int callCount = 0;

        // Subscribe one delegate normally first
        manager.AddNewDelegate(TestEvents.EventA, () => callCount++);

        // Get the event and begin batch
        var evt = manager.GetEvent(TestEvents.EventA)!;
        evt.BeginBatch();

        // Add delegates while batched — they won't be in the snapshot yet
        manager.AddNewDelegate(TestEvents.EventA, () => callCount++);
        manager.AddNewDelegate(TestEvents.EventA, () => callCount++);

        // Invoke — should only see the first delegate (snapshot not yet rebuilt)
        manager.InvokeEvent(TestEvents.EventA);
        Assert.Equal(1, callCount);

        // End batch — snapshot rebuilds
        evt.EndBatch();

        // Now invoke should see all 3
        callCount = 0;
        manager.InvokeEvent(TestEvents.EventA);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public void BatchSubscribe_ManagerLevel_Works()
    {
        var manager = CreateManager();

        // Pre-create event so BeginBatch covers it
        manager.AddNewDelegate(TestEvents.EventA, () => { });

        manager.BeginBatch();

        int callCount = 0;
        manager.AddNewDelegate(TestEvents.EventA, () => callCount++);
        manager.AddNewDelegate(TestEvents.EventA, () => callCount++);

        manager.EndBatch();

        manager.InvokeEvent(TestEvents.EventA);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void BatchSubscribe_NestedBatch_OnlyOutermostRebuilds()
    {
        var manager = CreateManager();
        int callCount = 0;
        manager.AddNewDelegate(TestEvents.EventA, () => callCount++);

        var evt = manager.GetEvent(TestEvents.EventA)!;
        evt.BeginBatch();
        evt.BeginBatch();  // nested

        manager.AddNewDelegate(TestEvents.EventA, () => callCount++);

        evt.EndBatch();  // still batched (depth=1)

        // Snapshot not yet rebuilt
        manager.InvokeEvent(TestEvents.EventA);
        Assert.Equal(1, callCount);

        evt.EndBatch();  // outermost — rebuild

        callCount = 0;
        manager.InvokeEvent(TestEvents.EventA);
        Assert.Equal(2, callCount);
    }

    #endregion

    #region Global Manager Filtering

    [Fact]
    public void GlobalInvoke_OnlyIteratesGlobalManagers()
    {
        // Create several non-global managers and one global
        var nonGlobal1 = CreateManager(global: false);
        var nonGlobal2 = CreateManager(global: false);
        var globalMgr = CreateManager(global: true);

        bool globalCalled = false;
        bool nonGlobalCalled = false;
        globalMgr.AddNewDelegate(TestEvents.EventA, () => globalCalled = true);
        nonGlobal1.AddNewDelegate(TestEvents.EventA, () => nonGlobalCalled = true);
        nonGlobal2.AddNewDelegate(TestEvents.EventA, () => nonGlobalCalled = true);

        EventManager<TestEvents>.GlobalInvokeEvent(TestEvents.EventA);

        Assert.True(globalCalled);
        Assert.False(nonGlobalCalled);
    }

    [Fact]
    public void GlobalSnapshot_UpdatesWhenGlobalFlagChanges()
    {
        var manager = CreateManager(global: false);
        bool called = false;
        manager.AddNewDelegate(TestEvents.EventA, () => called = true);

        // Not global — should not fire
        EventManager<TestEvents>.GlobalInvokeEvent(TestEvents.EventA);
        Assert.False(called);

        // Set to global — should now fire
        manager.Global = true;
        EventManager<TestEvents>.GlobalInvokeEvent(TestEvents.EventA);
        Assert.True(called);

        // Set back to non-global — should stop firing
        called = false;
        manager.Global = false;
        EventManager<TestEvents>.GlobalInvokeEvent(TestEvents.EventA);
        Assert.False(called);
    }

    #endregion

    #region Async Invoke

    [Fact]
    public async Task InvokeEventAsync_CallsAsyncDelegate()
    {
        var manager = CreateManager();
        bool called = false;
        manager.AddNewAsyncDelegate<TestParam>(TestEvents.EventA, async args =>
        {
            await Task.Yield();
            called = true;
        });

        await manager.InvokeEventAsync(TestEvents.EventA, new TestParam { Data = 1 });

        Assert.True(called);
    }

    [Fact]
    public async Task InvokeEventAsync_ParameterlessAsync_Works()
    {
        var manager = CreateManager();
        bool called = false;
        manager.AddNewAsyncDelegate(TestEvents.EventA, async () =>
        {
            await Task.Yield();
            called = true;
        });

        await manager.InvokeEventAsync(TestEvents.EventA);

        Assert.True(called);
    }

    [Fact]
    public async Task InvokeEventAsync_MixedSyncAndAsync_BothCalled()
    {
        var manager = CreateManager();
        var order = new List<int>();

        manager.AddNewDelegate<TestParam>(TestEvents.EventA, _ => order.Add(1), priority: 0);
        manager.AddNewAsyncDelegate<TestParam>(TestEvents.EventA, async _ =>
        {
            await Task.Yield();
            order.Add(2);
        }, priority: 1);

        await manager.InvokeEventAsync(TestEvents.EventA, new TestParam());

        Assert.Equal(2, order.Count);
        Assert.Contains(1, order);
        Assert.Contains(2, order);
    }

    [Fact]
    public async Task InvokeEventAsync_PriorityOrder_Maintained()
    {
        var manager = CreateManager();
        var order = new List<int>();

        manager.AddNewAsyncDelegate<TestParam>(TestEvents.EventA, async _ =>
        {
            await Task.Yield();
            order.Add(2);
        }, priority: 1);
        manager.AddNewDelegate<TestParam>(TestEvents.EventA, _ => order.Add(1), priority: 0);

        await manager.InvokeEventAsync(TestEvents.EventA, new TestParam());

        Assert.Equal(new List<int> { 1, 2 }, order);
    }

    [Fact]
    public async Task InvokeEventAsync_Cancellation_StopsPropagation()
    {
        var manager = CreateManager();
        var order = new List<int>();

        manager.AddNewAsyncDelegate<CancellableTestArgs>(TestEvents.EventA, async args =>
        {
            await Task.Yield();
            order.Add(1);
            args.Cancelled = true;
        }, priority: 0);
        manager.AddNewAsyncDelegate<CancellableTestArgs>(TestEvents.EventA, async _ =>
        {
            await Task.Yield();
            order.Add(2);
        }, priority: 1);

        await manager.InvokeEventAsync(TestEvents.EventA, new CancellableTestArgs());

        Assert.Single(order);
        Assert.Equal(1, order[0]);
    }

    [Fact]
    public async Task InvokeEventAsync_DisabledManager_DoesNotInvoke()
    {
        var manager = CreateManager();
        bool called = false;
        manager.AddNewAsyncDelegate(TestEvents.EventA, async () =>
        {
            await Task.Yield();
            called = true;
        });

        manager.Enabled = false;
        await manager.InvokeEventAsync(TestEvents.EventA);

        Assert.False(called);
    }

    [Fact]
    public async Task GlobalInvokeEventAsync_ReachesGlobalManagers()
    {
        var globalMgr = CreateManager(global: true);
        var nonGlobal = CreateManager(global: false);
        bool globalCalled = false;
        bool nonGlobalCalled = false;

        globalMgr.AddNewAsyncDelegate(TestEvents.EventA, async () =>
        {
            await Task.Yield();
            globalCalled = true;
        });
        nonGlobal.AddNewAsyncDelegate(TestEvents.EventA, async () =>
        {
            await Task.Yield();
            nonGlobalCalled = true;
        });

        await EventManager<TestEvents>.GlobalInvokeEventAsync(TestEvents.EventA);

        Assert.True(globalCalled);
        Assert.False(nonGlobalCalled);
    }

    [Fact]
    public void AsyncDelegate_Dispose_Unsubscribes()
    {
        var manager = CreateManager();
        bool called = false;
        var container = manager.AddNewAsyncDelegate(TestEvents.EventA, async () =>
        {
            await Task.Yield();
            called = true;
        });

        container.Dispose();
        manager.InvokeEvent(TestEvents.EventA);

        Assert.False(called);
    }

    [Fact]
    public async Task InvokeEventAsync_ContractMismatch_DoesNotThrow()
    {
        var manager = CreateContractedManager();
        manager.AddNewAsyncDelegate<int>(ContractedEvents.TypedEvent, async x =>
        {
            await Task.Yield();
        });

        // Invoke with wrong type — silently rejected by contract check
        var ex = await Record.ExceptionAsync(() =>
            manager.InvokeEventAsync(ContractedEvents.TypedEvent, "wrong"));

        Assert.Null(ex);
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
