// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;

using Prowl.EventSystem;
using Prowl.Runtime.EventSystem;

namespace Prowl.Benchmarks;

public enum BenchEvents
{
    [EventArgs(typeof(Unit))]
    Parameterless,

    [EventArgs(typeof(IntArg))]
    WithArg,

    [EventArgs(typeof(CancellableArg))]
    Cancellable,
}

public readonly record struct IntArg(int Value);

public record struct CancellableArg : ICancellable
{
    public int Value;
    public bool Cancelled { get; set; }
}

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns(Column.Error, Column.StdDev)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class EventSystemBenchmarks : IDisposable
{
    private EventManager<BenchEvents> _manager = null!;
    private readonly List<IDisposable> _handles = [];

    [Params(1, 10, 100, 1000)]
    public int SubscriberCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _manager = new EventManager<BenchEvents>();

        for (int i = 0; i < SubscriberCount; i++)
        {
            _handles.Add(_manager.AddNewDelegate(BenchEvents.Parameterless, () => { }));
            _handles.Add(_manager.AddNewDelegate<IntArg>(BenchEvents.WithArg, _ => { }));
            _handles.Add(_manager.AddNewDelegate<CancellableArg>(BenchEvents.Cancellable, _ => { }));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var h in _handles) h.Dispose();
        _handles.Clear();
        _manager.Dispose();
    }

    // ── Invoke ────────────────────────────────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Invoke")]
    public void Invoke_Parameterless()
    {
        _manager.InvokeEvent(BenchEvents.Parameterless);
    }

    [Benchmark]
    [BenchmarkCategory("Invoke")]
    public void Invoke_WithArg()
    {
        _manager.InvokeEvent(BenchEvents.WithArg, new IntArg(42));
    }

    [Benchmark]
    [BenchmarkCategory("Invoke")]
    public void Invoke_Cancellable_NoCancellation()
    {
        _manager.InvokeEvent(BenchEvents.Cancellable, new CancellableArg { Value = 1 });
    }

    public void Dispose()
    {
        Cleanup();
    }
}

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns(Column.Error, Column.StdDev)]
public class EventSystemCancellationBenchmarks : IDisposable
{
    private EventManager<BenchEvents> _manager = null!;
    private readonly List<IDisposable> _handles = [];

    [Params(10, 100, 1000)]
    public int SubscriberCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _manager = new EventManager<BenchEvents>();

        // All subscribers are no-ops — measures the per-element ICancellable check overhead
        // when the event is never actually cancelled. (CancellableArg is a struct, so
        // handlers cannot mutate the caller's copy to set Cancelled = true.)
        for (int i = 0; i < SubscriberCount; i++)
        {
            _handles.Add(_manager.AddNewDelegate<CancellableArg>(BenchEvents.Cancellable, _ => { }));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var h in _handles) h.Dispose();
        _handles.Clear();
        _manager.Dispose();
    }

    [Benchmark]
    public void Invoke_Cancellable_CheckOverhead()
    {
        _manager.InvokeEvent(BenchEvents.Cancellable, new CancellableArg { Value = 1 });
    }

    public void Dispose()
    {
        Cleanup();
    }
}

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns(Column.Error, Column.StdDev)]
public class EventSystemAddRemoveBenchmarks
{
    private EventManager<BenchEvents> _manager = null!;

    [GlobalSetup]
    public void Setup()
    {
        _manager = new EventManager<BenchEvents>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _manager.Dispose();
    }

    [Benchmark]
    public void AddAndRemove_Parameterless()
    {
        var handle = _manager.AddNewDelegate(BenchEvents.Parameterless, () => { });
        handle.Dispose();
    }

    [Benchmark]
    public void AddAndRemove_WithArg()
    {
        var handle = _manager.AddNewDelegate<IntArg>(BenchEvents.WithArg, _ => { });
        handle.Dispose();
    }
}

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns(Column.Error, Column.StdDev)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class EventSystemGlobalInvokeBenchmarks : IDisposable
{
    private EventManager<BenchEvents> _localManager = null!;
    private EventManager<BenchEvents> _globalManager = null!;
    private readonly List<IDisposable> _handles = [];

    [Params(1, 10, 100)]
    public int SubscriberCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _localManager = new EventManager<BenchEvents>(global: false);
        _globalManager = new EventManager<BenchEvents>(global: true);

        for (int i = 0; i < SubscriberCount; i++)
        {
            _handles.Add(_localManager.AddNewDelegate(BenchEvents.Parameterless, () => { }));
            _handles.Add(_globalManager.AddNewDelegate(BenchEvents.Parameterless, () => { }));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var h in _handles) h.Dispose();
        _handles.Clear();
        _localManager.Dispose();
        _globalManager.Dispose();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("GlobalVsLocal")]
    public void DirectInvoke()
    {
        _globalManager.InvokeEvent(BenchEvents.Parameterless);
    }

    [Benchmark]
    [BenchmarkCategory("GlobalVsLocal")]
    public void GlobalInvoke()
    {
        EventManager<BenchEvents>.GlobalInvokeEvent(BenchEvents.Parameterless);
    }

    public void Dispose()
    {
        Cleanup();
    }
}

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns(Column.Error, Column.StdDev)]
public class EventSystemPriorityBenchmarks : IDisposable
{
    private EventManager<BenchEvents> _singlePriority = null!;
    private EventManager<BenchEvents> _manyPriorities = null!;
    private readonly List<IDisposable> _handles = [];

    [Params(10, 100)]
    public int SubscriberCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _singlePriority = new EventManager<BenchEvents>();
        _manyPriorities = new EventManager<BenchEvents>();

        for (int i = 0; i < SubscriberCount; i++)
        {
            // All at same priority
            _handles.Add(_singlePriority.AddNewDelegate(BenchEvents.Parameterless, () => { }, priority: 0));
            // Each at a unique priority
            _handles.Add(_manyPriorities.AddNewDelegate(BenchEvents.Parameterless, () => { }, priority: i));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var h in _handles) h.Dispose();
        _handles.Clear();
        _singlePriority.Dispose();
        _manyPriorities.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Invoke_SinglePriority()
    {
        _singlePriority.InvokeEvent(BenchEvents.Parameterless);
    }

    [Benchmark]
    public void Invoke_ManyPriorities()
    {
        _manyPriorities.InvokeEvent(BenchEvents.Parameterless);
    }

    public void Dispose()
    {
        Cleanup();
    }
}
