// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.EventSystem;

using Xunit;

namespace Prowl.Runtime.Test;

#region Test event domains (source-generated)

[EventDomain]
public static partial class SimpleTestDomain
{
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnFoo = new();

    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnBar = new();
}

[EventDomain(Global = true)]
public static partial class TypedTestDomain
{
    [EventArgs(typeof(PointArgs))]
    private static readonly EventKey _OnPointMoved = new();

    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnReset = new();

    public readonly record struct PointArgs(int X, int Y);
}

[EventDomain]
public static partial class CancellableTestDomain
{
    [EventArgs(typeof(CancellablePayload))]
    private static readonly EventKey _OnCheck = new();

    public class CancellablePayload : ICancellable
    {
        public bool Cancelled { get; set; }
        public int Value { get; set; }
    }
}

/// <summary>
/// Instance (non-static) event domain — each instance gets its own EventManager.
/// </summary>
[EventDomain]
public partial class InstanceTestDomain : IDisposable
{
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnPing = new();

    [EventArgs(typeof(HitArgs))]
    private static readonly EventKey _OnHit = new();

    public readonly record struct HitArgs(int Damage);

    public void Dispose() => Manager.Dispose();
}

/// <summary>
/// Instance domain marked Global — each instance's EventManager participates in GlobalInvoke.
/// </summary>
[EventDomain(Global = true)]
public partial class GlobalInstanceTestDomain : IDisposable
{
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnAlert = new();

    public void Dispose() => Manager.Dispose();
}

#endregion

/// <summary>
/// Tests for the [EventDomain] source generator: verifies that the generated
/// enum, manager, Invoke, Subscribe, and GlobalInvoke methods work correctly.
/// </summary>
public class EventDomainGeneratorTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
        _disposables.Clear();
    }

    private void Track(IDisposable d) => _disposables.Add(d);

    #region Generated enum & manager existence

    [Fact]
    public void GeneratedEnum_HasExpectedValues()
    {
        // The generator should produce SimpleTestDomain.EventTypes with OnFoo and OnBar
        var values = Enum.GetValues<SimpleTestDomain.EventTypes>();
        Assert.Contains(SimpleTestDomain.EventTypes.OnFoo, values);
        Assert.Contains(SimpleTestDomain.EventTypes.OnBar, values);
    }

    [Fact]
    public void GeneratedManager_IsNotNull()
    {
        Assert.NotNull(SimpleTestDomain.Manager);
    }

    [Fact]
    public void GeneratedManager_IsGlobal_WhenAttributeSaysGlobal()
    {
        Assert.True(TypedTestDomain.Manager.Global);
    }

    [Fact]
    public void GeneratedManager_IsNotGlobal_WhenAttributeOmitsGlobal()
    {
        Assert.False(SimpleTestDomain.Manager.Global);
    }

    #endregion

    #region Subscribe & Invoke (parameterless / Unit events)

    [Fact]
    public void SubscribeAndInvoke_Parameterless_Works()
    {
        bool called = false;
        var sub = SimpleTestDomain.SubscribeOnFoo(() => called = true);
        Track(sub);

        SimpleTestDomain.InvokeOnFoo();

        Assert.True(called);
    }

    [Fact]
    public void Invoke_OnlyFiresMatchingEvent()
    {
        bool fooCalled = false;
        bool barCalled = false;
        Track(SimpleTestDomain.SubscribeOnFoo(() => fooCalled = true));
        Track(SimpleTestDomain.SubscribeOnBar(() => barCalled = true));

        SimpleTestDomain.InvokeOnFoo();

        Assert.True(fooCalled);
        Assert.False(barCalled);
    }

    [Fact]
    public void Subscribe_Priority_OrdersCorrectly()
    {
        var order = new List<int>();
        Track(SimpleTestDomain.SubscribeOnFoo(() => order.Add(2), priority: 2));
        Track(SimpleTestDomain.SubscribeOnFoo(() => order.Add(0), priority: 0));
        Track(SimpleTestDomain.SubscribeOnFoo(() => order.Add(1), priority: 1));

        SimpleTestDomain.InvokeOnFoo();

        Assert.Equal([0, 1, 2], order);
    }

    [Fact]
    public void Dispose_Unsubscribes()
    {
        bool called = false;
        var sub = SimpleTestDomain.SubscribeOnFoo(() => called = true);

        sub.Dispose();
        SimpleTestDomain.InvokeOnFoo();

        Assert.False(called);
    }

    #endregion

    #region Subscribe & Invoke (typed events)

    [Fact]
    public void SubscribeAndInvoke_Typed_Works()
    {
        TypedTestDomain.PointArgs? received = null;
        var sub = TypedTestDomain.SubscribeOnPointMoved(args => received = args);
        Track(sub);

        TypedTestDomain.InvokeOnPointMoved(new TypedTestDomain.PointArgs(10, 20));

        Assert.NotNull(received);
        Assert.Equal(10, received!.Value.X);
        Assert.Equal(20, received!.Value.Y);
    }

    [Fact]
    public void TypedAndUnit_MixedInSameDomain_WorkIndependently()
    {
        bool resetCalled = false;
        TypedTestDomain.PointArgs? moved = null;
        Track(TypedTestDomain.SubscribeOnReset(() => resetCalled = true));
        Track(TypedTestDomain.SubscribeOnPointMoved(args => moved = args));

        TypedTestDomain.InvokeOnReset();

        Assert.True(resetCalled);
        Assert.Null(moved);
    }

    #endregion

    #region GlobalInvoke

    [Fact]
    public void GlobalInvoke_ReachesGlobalManager()
    {
        bool called = false;
        Track(TypedTestDomain.SubscribeOnReset(() => called = true));

        TypedTestDomain.GlobalInvokeOnReset();

        Assert.True(called);
    }

    [Fact]
    public void GlobalInvoke_Typed_ReachesGlobalManager()
    {
        TypedTestDomain.PointArgs? received = null;
        Track(TypedTestDomain.SubscribeOnPointMoved(args => received = args));

        TypedTestDomain.GlobalInvokeOnPointMoved(new TypedTestDomain.PointArgs(42, 99));

        Assert.NotNull(received);
        Assert.Equal(42, received!.Value.X);
    }

    #endregion

    #region Contract validation (via generated [EventArgs] on enum)

    [Fact]
    public void Contract_MismatchedType_ThrowsOnSubscribe()
    {
        // The generated enum has [EventArgs(typeof(PointArgs))] on OnPointMoved.
        // Subscribing with a wrong type should throw.
        Assert.Throws<InvalidOperationException>(() =>
            TypedTestDomain.Manager.AddNewDelegate<string>(
                TypedTestDomain.EventTypes.OnPointMoved, _ => { }));
    }

    [Fact]
    public void Contract_CorrectType_Succeeds()
    {
        var ex = Record.Exception(() =>
        {
            var sub = TypedTestDomain.Manager.AddNewDelegate<TypedTestDomain.PointArgs>(
                TypedTestDomain.EventTypes.OnPointMoved, _ => { });
            Track(sub);
        });

        Assert.Null(ex);
    }

    #endregion

    #region Cancellation through generated invoke

    [Fact]
    public void Cancellation_StopsPropagation_ViaGeneratedInvoke()
    {
        var order = new List<int>();
        Track(CancellableTestDomain.SubscribeOnCheck(args =>
        {
            order.Add(0);
            args.Cancelled = true;
        }, priority: 0));
        Track(CancellableTestDomain.SubscribeOnCheck(args =>
        {
            order.Add(1);
        }, priority: 1));

        var payload = new CancellableTestDomain.CancellablePayload();
        CancellableTestDomain.InvokeOnCheck(payload);

        Assert.Single(order);
        Assert.Equal(0, order[0]);
        Assert.True(payload.Cancelled);
    }

    #endregion

    #region += / -= event subscription syntax

    [Fact]
    public void PlusEquals_Parameterless_SubscribesAndFires()
    {
        bool called = false;
        Action handler = () => called = true;
        SimpleTestDomain.OnFoo += handler;

        SimpleTestDomain.InvokeOnFoo();
        Assert.True(called);

        SimpleTestDomain.OnFoo -= handler;
    }

    [Fact]
    public void MinusEquals_Parameterless_Unsubscribes()
    {
        bool called = false;
        Action handler = () => called = true;
        SimpleTestDomain.OnFoo += handler;
        SimpleTestDomain.OnFoo -= handler;

        SimpleTestDomain.InvokeOnFoo();
        Assert.False(called);
    }

    [Fact]
    public void PlusEquals_Typed_SubscribesAndFires()
    {
        TypedTestDomain.PointArgs? received = null;
        Action<TypedTestDomain.PointArgs> handler = args => received = args;
        TypedTestDomain.OnPointMoved += handler;

        TypedTestDomain.InvokeOnPointMoved(new TypedTestDomain.PointArgs(5, 10));
        Assert.NotNull(received);
        Assert.Equal(5, received!.Value.X);
        Assert.Equal(10, received!.Value.Y);

        TypedTestDomain.OnPointMoved -= handler;
    }

    [Fact]
    public void MinusEquals_Typed_Unsubscribes()
    {
        TypedTestDomain.PointArgs? received = null;
        Action<TypedTestDomain.PointArgs> handler = args => received = args;
        TypedTestDomain.OnPointMoved += handler;
        TypedTestDomain.OnPointMoved -= handler;

        TypedTestDomain.InvokeOnPointMoved(new TypedTestDomain.PointArgs(5, 10));
        Assert.Null(received);
    }

    [Fact]
    public void MinusEquals_OnlyRemovesFirstMatch()
    {
        int callCount = 0;
        Action handler = () => callCount++;
        SimpleTestDomain.OnBar += handler;
        SimpleTestDomain.OnBar += handler;

        // Remove one — the other should still fire
        SimpleTestDomain.OnBar -= handler;
        SimpleTestDomain.InvokeOnBar();
        Assert.Equal(1, callCount);

        // Clean up
        SimpleTestDomain.OnBar -= handler;
    }

    #endregion

    #region Instance domain — basic subscribe & invoke

    [Fact]
    public void InstanceDomain_EachInstanceHasOwnManager()
    {
        using var a = new InstanceTestDomain();
        using var b = new InstanceTestDomain();

        Assert.NotSame(a.Manager, b.Manager);
    }

    [Fact]
    public void InstanceDomain_SubscribeAndInvoke_Parameterless()
    {
        using var domain = new InstanceTestDomain();
        bool called = false;
        var sub = domain.SubscribeOnPing(() => called = true);
        Track(sub);

        domain.InvokeOnPing();

        Assert.True(called);
    }

    [Fact]
    public void InstanceDomain_SubscribeAndInvoke_Typed()
    {
        using var domain = new InstanceTestDomain();
        InstanceTestDomain.HitArgs? received = null;
        var sub = domain.SubscribeOnHit(args => received = args);
        Track(sub);

        domain.InvokeOnHit(new InstanceTestDomain.HitArgs(25));

        Assert.NotNull(received);
        Assert.Equal(25, received!.Value.Damage);
    }

    [Fact]
    public void InstanceDomain_Isolation_OneInstanceDoesNotFireAnother()
    {
        using var a = new InstanceTestDomain();
        using var b = new InstanceTestDomain();
        bool aCalled = false;
        bool bCalled = false;
        Track(a.SubscribeOnPing(() => aCalled = true));
        Track(b.SubscribeOnPing(() => bCalled = true));

        a.InvokeOnPing();

        Assert.True(aCalled);
        Assert.False(bCalled);
    }

    [Fact]
    public void InstanceDomain_Dispose_UnsubscribesAll()
    {
        var domain = new InstanceTestDomain();
        bool called = false;
        domain.SubscribeOnPing(() => called = true);

        domain.Dispose();
        // After disposal, invoke on the disposed manager should not fire.
        var ex = Record.Exception(() => domain.InvokeOnPing());
        Assert.Null(ex);
        Assert.False(called);
    }

    [Fact]
    public void InstanceDomain_PlusEquals_SubscribesAndFires()
    {
        using var domain = new InstanceTestDomain();
        bool called = false;
        Action handler = () => called = true;
        domain.OnPing += handler;

        domain.InvokeOnPing();
        Assert.True(called);

        domain.OnPing -= handler;
    }

    [Fact]
    public void InstanceDomain_MinusEquals_Unsubscribes()
    {
        using var domain = new InstanceTestDomain();
        bool called = false;
        Action handler = () => called = true;
        domain.OnPing += handler;
        domain.OnPing -= handler;

        domain.InvokeOnPing();
        Assert.False(called);
    }

    [Fact]
    public void InstanceDomain_Priority_OrdersCorrectly()
    {
        using var domain = new InstanceTestDomain();
        var order = new List<int>();
        Track(domain.SubscribeOnPing(() => order.Add(2), priority: 2));
        Track(domain.SubscribeOnPing(() => order.Add(0), priority: 0));
        Track(domain.SubscribeOnPing(() => order.Add(1), priority: 1));

        domain.InvokeOnPing();

        Assert.Equal([0, 1, 2], order);
    }

    #endregion

    #region Instance domain — global broadcast

    [Fact]
    public void InstanceDomain_GlobalInvoke_ReachesGlobalInstances()
    {
        using var a = new GlobalInstanceTestDomain();
        using var b = new GlobalInstanceTestDomain();
        bool aCalled = false;
        bool bCalled = false;
        Track(a.SubscribeOnAlert(() => aCalled = true));
        Track(b.SubscribeOnAlert(() => bCalled = true));

        GlobalInstanceTestDomain.GlobalInvokeOnAlert();

        Assert.True(aCalled);
        Assert.True(bCalled);
    }

    [Fact]
    public void InstanceDomain_GlobalInvoke_SkipsDisposed()
    {
        using var alive = new GlobalInstanceTestDomain();
        var dead = new GlobalInstanceTestDomain();
        bool aliveCalled = false;
        bool deadCalled = false;
        Track(alive.SubscribeOnAlert(() => aliveCalled = true));
        dead.SubscribeOnAlert(() => deadCalled = true);
        dead.Dispose();

        GlobalInstanceTestDomain.GlobalInvokeOnAlert();

        Assert.True(aliveCalled);
        Assert.False(deadCalled);
    }

    #endregion
}
