using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Prowl.Runtime.EventSystem;

public class EventManager<T> : IDisposable where T : struct, Enum
{
    private static readonly List<EventManager<T>> s_instances = new List<EventManager<T>>();
    private static readonly object s_instancesLock = new();
    private bool _disposed;

    /// <summary>
    /// Copy-on-write snapshot of the static instances list, rebuilt only on Add/Remove.
    /// </summary>
    private static EventManager<T>[] s_instancesSnapshot = [];

    public static EventManager<T> LastGlobalInstance
    {
        get
        {
            lock (s_instancesLock)
            {
                for (int i = s_instances.Count - 1; i >= 0; i--)
                {
                    var instance = s_instances[i];
                    if (instance.Enabled && instance.Global)
                        return instance;
                }
                return null;
            }
        }
    }

    private readonly Dictionary<T, Event<T>> _events = new Dictionary<T, Event<T>>();

    private bool global = false;
    public bool Global
    {
        get => global;
        set
        {
            global = value;
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
        Initialize();
        lock (s_instancesLock)
        {
            s_instances.Add(this);
            s_instancesSnapshot = [.. s_instances];
        }
    }

    private void AddEvent(Event<T> xEvent)
    {
        _events.TryAdd(xEvent.EventType, xEvent);
    }

    public void AddDelegate(EventDelegateContainer<T> eventDelegate)
    {
        if (_events.TryGetValue(eventDelegate.EventType, out Event<T>? value))
        {
            value.Add(eventDelegate);
        }
    }

    public void RemoveDelegate(EventDelegateContainer<T> eventDelegate)
    {
        if (_events.TryGetValue(eventDelegate.EventType, out Event<T>? value))
        {
            value.Remove(eventDelegate);
        }
    }



    public void RemoveEvent(Event<T> xEvent)
    {

        _events.Remove(xEvent.EventType);


    }

    public void EnableEvent(T eventType)
    {
        if (_events.TryGetValue(eventType, out Event<T>? evt))
        {
            evt.Enabled = true;
        }
    }

    public void DisableEvent(T eventType)
    {
        if (_events.TryGetValue(eventType, out Event<T>? value))
        {
            value.Enabled = false;
        }
    }

    private void Initialize()
    {
        T[] values = Enum.GetValues<T>();
        for (int i = 0; i < values.Length; i++)
        {
            AddEvent(new Event<T>(this, values[i]));
        }
    }


    /// <summary>
    /// Invoke an event with typed arguments. Only delegates registered
    /// with a matching <typeparamref name="TArgs"/> will be called.
    /// </summary>
    public void InvokeEvent<TArgs>(T eventType, TArgs args)
    {
        if (_disposed || !Enabled) return;
        if (_events.TryGetValue(eventType, out var evt))
            evt.Invoke(args);
    }

    /// <summary>
    /// Invoke a parameterless event.
    /// </summary>
    public void InvokeEvent(T eventType)
    {
        InvokeEvent(eventType, default(Unit));
    }

    /// <summary>
    /// Register a typed delegate for an event.
    /// </summary>
    public EventDelegateContainer<T, TArgs> AddNewDelegate<TArgs>(
        T eventType, Action<TArgs> eventDelegate, int priority = 0,
#if DEBUG
        [CallerFilePath] string? sourceFile = null,
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string? sourceMember = null
#endif
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
#if DEBUG
        EventDelegateContainer<T, TArgs> container = new(eventType, eventDelegate, priority, sourceFile, sourceLine, sourceMember);
#else
        EventDelegateContainer<T, TArgs> container = new(eventType, eventDelegate, priority);
#endif
        _events[eventType].Add(container);
        return container;
    }

    /// <summary>
    /// Register a parameterless delegate for an event.
    /// </summary>
    public EventDelegateContainer<T, Unit> AddNewDelegate(
        T eventType, Action eventDelegate, int priority = 0,
#if DEBUG
        [CallerFilePath] string? sourceFile = null,
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string? sourceMember = null
#endif
    )
    {
#if DEBUG
        return AddNewDelegate<Unit>(eventType, _ => eventDelegate(), priority, sourceFile, sourceLine, sourceMember);
#else
        return AddNewDelegate<Unit>(eventType, _ => eventDelegate(), priority);
#endif
    }


    /// <summary>
    /// Invoke an event with typed arguments across all global managers.
    /// </summary>
    public static void GlobalInvokeEvent<TArgs>(T eventType, TArgs args)
    {
        EventManager<T>[] snapshot;
        lock (s_instancesLock)
            snapshot = s_instancesSnapshot;

        for (int i = 0; i < snapshot.Length; i++)
        {
            var instance = snapshot[i];
            if (instance.Enabled && instance.Global)
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        enabled = false;
        foreach (var evt in _events.Values)
            evt.Enabled = false;
        _events.Clear();
        lock (s_instancesLock)
        {
            s_instances.Remove(this);
            s_instancesSnapshot = [.. s_instances];
        }
        GC.SuppressFinalize(this);
    }

    ~EventManager()
    {
        if (!_disposed)
        {
            Debug.LogWarning($"EventManager<{typeof(T).Name}> was not disposed before finalization.");
        }
    }
}
