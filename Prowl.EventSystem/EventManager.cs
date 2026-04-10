using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Prowl.EventSystem;

public class EventManager<T> : IDisposable where T : struct, Enum
{
    private static readonly List<EventManager<T>> s_instances = new List<EventManager<T>>();
    private static readonly object s_instancesLock = new();
    private bool _disposed;

    /// <summary>
    /// Copy-on-write snapshot of the static instances list, rebuilt only on Add/Remove.
    /// </summary>
    private static EventManager<T>[] s_instancesSnapshot = [];

    /// <summary>
    /// Copy-on-write snapshot containing only global managers.
    /// Rebuilt when instances or their <see cref="Global"/> flag change.
    /// </summary>
    private static EventManager<T>[] s_globalSnapshot = [];

    /// <summary>
    /// Rebuilds the global-only snapshot from the current instances list.
    /// Must be called under <see cref="s_instancesLock"/>.
    /// </summary>
    private static void RebuildGlobalSnapshot()
    {
        List<EventManager<T>> globals = new List<EventManager<T>>();
        for (int i = 0; i < s_instances.Count; i++)
        {
            if (s_instances[i].Global)
                globals.Add(s_instances[i]);
        }
        s_globalSnapshot = globals.Count > 0 ? [.. globals] : [];
    }

    public static EventManager<T> LastGlobalInstance
    {
        get
        {
            EventManager<T>[] snapshot = s_globalSnapshot;
            for (int i = snapshot.Length - 1; i >= 0; i--)
            {
                if (snapshot[i].Enabled)
                    return snapshot[i];
            }
            return null;
        }
    }

    private readonly ConcurrentDictionary<T, Event<T>> _events = new ConcurrentDictionary<T, Event<T>>();

    private bool global = false;
    public bool Global
    {
        get => global;
        set
        {
            if (global == value) return;
            global = value;
            lock (s_instancesLock)
            {
                RebuildGlobalSnapshot();
            }
        }
    }

    private bool enabled = true;
    public bool Enabled
    {
        get => enabled;
        set
        {
            enabled = value;
            foreach (Event<T> xEvent in _events.Values)
            {
                xEvent.Enabled = value;
            }
        }
    }

    public EventManager(bool global = false)
    {
        Global = global;
        lock (s_instancesLock)
        {
            s_instances.Add(this);
            s_instancesSnapshot = [.. s_instances];
            RebuildGlobalSnapshot();
        }
    }

    /// <summary>
    /// Returns the <see cref="Event{T}"/> for the given enum value,
    /// creating it atomically on first access via <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd"/>.
    /// </summary>
    private Event<T> GetOrCreateEvent(T eventType)
    {
        return _events.GetOrAdd(eventType, key =>
        {
            Event<T> evt = new Event<T>(this, key);
            if (!enabled)
                evt.Enabled = false;
            return evt;
        });
    }

    public void AddDelegate(EventDelegateContainer<T> eventDelegate)
    {
        GetOrCreateEvent(eventDelegate.EventType).Add(eventDelegate);
    }

    public void RemoveDelegate(EventDelegateContainer<T> eventDelegate)
    {
        if (_events.TryGetValue(eventDelegate.EventType, out Event<T>? evt))
        {
            evt.Remove(eventDelegate);
        }
    }

    /// <summary>
    /// Removes the first delegate container that wraps the given handler delegate.
    /// Used by the generated event <c>-=</c> accessors.
    /// </summary>
    public bool RemoveDelegate(T eventType, Delegate handler)
    {
        if (_events.TryGetValue(eventType, out Event<T>? evt))
            return evt.RemoveByDelegate(handler);
        return false;
    }

    /// <summary>
    /// Begins a batch operation on all existing events. While batched,
    /// <see cref="Event{T}.Add"/> and <see cref="Event{T}.Remove"/> will not
    /// rebuild COW snapshots. Call <see cref="EndBatch"/> when finished.
    /// Calls may be nested.
    /// </summary>
    public void BeginBatch()
    {
        foreach (Event<T> evt in _events.Values)
            evt.BeginBatch();
    }

    /// <summary>
    /// Ends a batch operation. If this is the outermost batch and mutations
    /// occurred, COW snapshots are rebuilt once per event.
    /// </summary>
    public void EndBatch()
    {
        foreach (Event<T> evt in _events.Values)
            evt.EndBatch();
    }



    public void RemoveEvent(Event<T> xEvent)
    {
        _events.Remove(xEvent.EventType, out _);
    }

    public void EnableEvent(T eventType)
    {
        GetOrCreateEvent(eventType).Enabled = true;
    }

    public void DisableEvent(T eventType)
    {
        GetOrCreateEvent(eventType).Enabled = false;
    }


    /// <summary>
    /// Invoke an event with typed arguments. Only delegates registered
    /// with a matching <typeparamref name="TArgs"/> will be called.
    /// </summary>
    public void InvokeEvent<TArgs>(T eventType, TArgs args)
    {
        if (_disposed || !Enabled) return;

        if (!EventArgsContract<T>.IsValid<TArgs>(eventType))
        {
            EventSystemDiagnostics.LogError?.Invoke(
                $"[EventSystem] Type mismatch on {typeof(T).Name}.{eventType}: " +
                $"invoked with '{typeof(TArgs).Name}' but the event declares " +
                $"'{EventArgsContract<T>.GetDeclaredName(eventType)}' via [EventArgs].");
            return;
        }

        if (_events.TryGetValue(eventType, out Event<T>? evt))
            evt.Invoke(args);
    }

    /// <summary>
    /// Returns the <see cref="Event{T}"/> for the given enum value if it
    /// has been created, or <c>null</c> if no subscribers have been registered.
    /// </summary>
    public Event<T>? GetEvent(T eventType)
    {
        _events.TryGetValue(eventType, out Event<T>? evt);
        return evt;
    }

    /// <summary>
    /// Invoke a parameterless event.
    /// </summary>
    public void InvokeEvent(T eventType)
    {
        InvokeEvent(eventType, default(Unit));
    }

    /// <summary>
    /// Asynchronously invoke an event with typed arguments. Async handlers are
    /// awaited sequentially; synchronous handlers are called inline.
    /// Intended for editor/tool events that perform I/O.
    /// </summary>
    public async Task InvokeEventAsync<TArgs>(T eventType, TArgs args)
    {
        if (_disposed || !Enabled) return;

        if (!EventArgsContract<T>.IsValid<TArgs>(eventType))
        {
            EventSystemDiagnostics.LogError?.Invoke(
                $"[EventSystem] Type mismatch on {typeof(T).Name}.{eventType}: " +
                $"invoked with '{typeof(TArgs).Name}' but the event declares " +
                $"'{EventArgsContract<T>.GetDeclaredName(eventType)}' via [EventArgs].");
            return;
        }

        if (_events.TryGetValue(eventType, out Event<T>? evt))
            await evt.InvokeAsync(args).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously invoke a parameterless event.
    /// </summary>
    public Task InvokeEventAsync(T eventType)
    {
        return InvokeEventAsync(eventType, default(Unit));
    }

    /// <summary>
    /// Register a typed delegate for an event.
    /// </summary>
    public EventDelegateContainer<T, TArgs> AddNewDelegate<TArgs>(
        T eventType, Action<TArgs> eventDelegate, int priority = 0
#if DEBUG
        , [CallerFilePath] string? sourceFile = null,
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string? sourceMember = null
#endif
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!EventArgsContract<T>.IsValid<TArgs>(eventType))
        {
            throw new InvalidOperationException(
                $"[EventSystem] Type mismatch on {typeof(T).Name}.{eventType}: " +
                $"handler registered with '{typeof(TArgs).Name}' but the event " +
                $"declares '{EventArgsContract<T>.GetDeclaredName(eventType)}' " +
                $"via [EventArgs]. Fix the subscriber's type parameter.");
        }
#if DEBUG
        EventDelegateContainer<T, TArgs> container = new(eventType, eventDelegate, priority, sourceFile, sourceLine, sourceMember);
#else
        EventDelegateContainer<T, TArgs> container = new(eventType, eventDelegate, priority);
#endif
        GetOrCreateEvent(eventType).Add(container);
        return container;
    }

    /// <summary>
    /// Register a parameterless delegate for an event.
    /// </summary>
    public EventDelegateContainer<T, Unit> AddNewDelegate(
        T eventType, Action eventDelegate, int priority = 0
#if DEBUG
        , [CallerFilePath] string? sourceFile = null,
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string? sourceMember = null
#endif
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!EventArgsContract<T>.IsValid<Unit>(eventType))
        {
            throw new InvalidOperationException(
                $"[EventSystem] Type mismatch on {typeof(T).Name}.{eventType}: " +
                $"handler registered with 'Unit' (parameterless) but the event " +
                $"declares '{EventArgsContract<T>.GetDeclaredName(eventType)}' " +
                $"via [EventArgs]. Fix the subscriber's type parameter.");
        }

#if DEBUG
        ParameterlessEventDelegateContainer<T> container = new(eventType, eventDelegate, priority, sourceFile, sourceLine, sourceMember);
#else
        ParameterlessEventDelegateContainer<T> container = new(eventType, eventDelegate, priority);
#endif
        GetOrCreateEvent(eventType).Add(container);
        return container;
    }


    /// <summary>
    /// Register a typed async delegate for an event.
    /// </summary>
    public AsyncEventDelegateContainer<T, TArgs> AddNewAsyncDelegate<TArgs>(
        T eventType, Func<TArgs, Task> eventDelegate, int priority = 0
#if DEBUG
        , [CallerFilePath] string? sourceFile = null,
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string? sourceMember = null
#endif
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!EventArgsContract<T>.IsValid<TArgs>(eventType))
        {
            throw new InvalidOperationException(
                $"[EventSystem] Type mismatch on {typeof(T).Name}.{eventType}: " +
                $"async handler registered with '{typeof(TArgs).Name}' but the event " +
                $"declares '{EventArgsContract<T>.GetDeclaredName(eventType)}' " +
                $"via [EventArgs]. Fix the subscriber's type parameter.");
        }
#if DEBUG
        AsyncEventDelegateContainer<T, TArgs> container = new(eventType, eventDelegate, priority, sourceFile, sourceLine, sourceMember);
#else
        AsyncEventDelegateContainer<T, TArgs> container = new(eventType, eventDelegate, priority);
#endif
        GetOrCreateEvent(eventType).Add(container);
        return container;
    }

    /// <summary>
    /// Register a parameterless async delegate for an event.
    /// </summary>
    public AsyncEventDelegateContainer<T, Unit> AddNewAsyncDelegate(
        T eventType, Func<Task> eventDelegate, int priority = 0
#if DEBUG
        , [CallerFilePath] string? sourceFile = null,
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string? sourceMember = null
#endif
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!EventArgsContract<T>.IsValid<Unit>(eventType))
        {
            throw new InvalidOperationException(
                $"[EventSystem] Type mismatch on {typeof(T).Name}.{eventType}: " +
                $"async handler registered with 'Unit' (parameterless) but the event " +
                $"declares '{EventArgsContract<T>.GetDeclaredName(eventType)}' " +
                $"via [EventArgs]. Fix the subscriber's type parameter.");
        }

#if DEBUG
        ParameterlessAsyncEventDelegateContainer<T> container = new(eventType, eventDelegate, priority, sourceFile, sourceLine, sourceMember);
#else
        ParameterlessAsyncEventDelegateContainer<T> container = new(eventType, eventDelegate, priority);
#endif
        GetOrCreateEvent(eventType).Add(container);
        return container;
    }


    /// <summary>
    /// Invoke an event with typed arguments across all global managers.
    /// </summary>
    public static void GlobalInvokeEvent<TArgs>(T eventType, TArgs args)
    {
        EventManager<T>[] snapshot = s_globalSnapshot;

        for (int i = 0; i < snapshot.Length; i++)
        {
            EventManager<T> instance = snapshot[i];
            if (instance.Enabled)
            {
                instance.InvokeEvent(eventType, args);
            }
        }
    }

    /// <summary>
    /// Invoke a parameterless event across all global managers.
    /// </summary>
    public static void GlobalInvokeEvent(T eventType)
    {
        GlobalInvokeEvent(eventType, default(Unit));
    }

    /// <summary>
    /// Asynchronously invoke an event with typed arguments across all global managers.
    /// </summary>
    public static async Task GlobalInvokeEventAsync<TArgs>(T eventType, TArgs args)
    {
        EventManager<T>[] snapshot = s_globalSnapshot;

        for (int i = 0; i < snapshot.Length; i++)
        {
            EventManager<T> instance = snapshot[i];
            if (instance.Enabled)
            {
                await instance.InvokeEventAsync(eventType, args).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Asynchronously invoke a parameterless event across all global managers.
    /// </summary>
    public static Task GlobalInvokeEventAsync(T eventType)
    {
        return GlobalInvokeEventAsync(eventType, default(Unit));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        enabled = false;
        foreach (Event<T> evt in _events.Values)
            evt.Enabled = false;
        _events.Clear();
        lock (s_instancesLock)
        {
            s_instances.Remove(this);
            s_instancesSnapshot = [.. s_instances];
            RebuildGlobalSnapshot();
        }
        GC.SuppressFinalize(this);
    }

    ~EventManager()
    {
        if (!_disposed)
        {
            EventSystemDiagnostics.LogWarning?.Invoke($"EventManager<{typeof(T).Name}> was not disposed before finalization.");
        }
    }
}
