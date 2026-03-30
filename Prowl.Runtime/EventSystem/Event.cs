using System;
using System.Collections.Generic;

namespace Prowl.Runtime.EventSystem;

public class Event<T> where T : struct, Enum
{
    /// <summary>
    /// JIT-time cache: <c>true</c> when <typeparamref name="TArgs"/> implements
    /// <see cref="ICancellable"/>. Evaluated once per closed generic and stored in a
    /// static field, so the hot-path <see cref="Invoke{TArgs}"/> never boxes value-type
    /// args just to check the interface.
    /// </summary>
    private static class CancellableCheck<TArgs>
    {
        public static readonly bool IsCancellable = typeof(ICancellable).IsAssignableFrom(typeof(TArgs));
    }

    private readonly T _eventType;
    public T EventType => _eventType;

    private readonly EventManager<T> _eventManager;
    public EventManager<T> EventManager => _eventManager;

    private readonly Dictionary<int, List<EventDelegateContainer<T>>> _eventDelegates = new();

    private readonly List<int> _sortedKeys = [];

    private readonly object _lock = new();

    /// <summary>
    /// Copy-on-write snapshot: a flat, priority-sorted array of all delegates.
    /// Rebuilt only when the subscriber list changes (Add/Remove), never on Invoke.
    /// Used by DEBUG diagnostics to detect type-mismatch handlers.
    /// </summary>
    private EventDelegateContainer<T>[] _cachedSnapshot = [];

    /// <summary>
    /// Per-<c>TArgs</c> typed COW snapshots keyed by <see cref="Type"/>.
    /// Each value is a <c>EventDelegateContainer&lt;T, TArgs&gt;[]</c> stored as
    /// <see cref="object"/> (the concrete array type is created via
    /// <see cref="Array.CreateInstance"/> at rebuild time).
    /// <para>
    /// <see cref="Invoke{TArgs}"/> retrieves the matching typed array with a single
    /// dictionary lookup and one array-reference cast — <b>no per-element type check</b>.
    /// </para>
    /// </summary>
    private readonly Dictionary<Type, object> _typedSnapshots = new();

    private volatile bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public Event(EventManager<T> eventManager, T eventType)
    {
        this._eventType = eventType;
        this._eventManager = eventManager;
    }


    public void Invoke<TArgs>(TArgs args)
    {
        if (!Enabled) return;

        EventDelegateContainer<T, TArgs>[] typedSnapshot;
#if DEBUG
        EventDelegateContainer<T>[] fullSnapshot;
#endif
        lock (_lock)
        {
            typedSnapshot = _typedSnapshots.TryGetValue(typeof(TArgs), out var obj)
                ? (EventDelegateContainer<T, TArgs>[])obj
                : [];
#if DEBUG
            fullSnapshot = _cachedSnapshot;
#endif
        }

#if DEBUG
        // Warn about handlers on this event registered with a different TArgs.
        if (fullSnapshot.Length > typedSnapshot.Length)
        {
            for (int j = 0; j < fullSnapshot.Length; j++)
            {
                if (fullSnapshot[j] is not EventDelegateContainer<T, TArgs>)
                    WarnTypeMismatch<TArgs>(fullSnapshot[j]);
            }
        }
#endif

        for (int j = 0; j < typedSnapshot.Length; j++)
        {
            typedSnapshot[j].Invoke(args);

            if (CancellableCheck<TArgs>.IsCancellable && args is ICancellable { Cancelled: true })
                break;
        }
    }

    /// <summary>
    /// Returns a read-only view of the currently registered handlers for the
    /// given <typeparamref name="TArgs"/> type, sorted by priority.
    /// The span references a COW snapshot — it is safe to read after the lock
    /// is released but may become stale if handlers are added or removed.
    /// </summary>
    public ReadOnlySpan<EventDelegateContainer<T, TArgs>> GetHandlers<TArgs>()
    {
        lock (_lock)
        {
            if (_typedSnapshots.TryGetValue(typeof(TArgs), out var obj))
                return (EventDelegateContainer<T, TArgs>[])obj;
            return ReadOnlySpan<EventDelegateContainer<T, TArgs>>.Empty;
        }
    }

#if DEBUG
    /// <summary>
    /// Logs a warning when a registered handler is skipped because its TArgs
    /// does not match the invoked type. Only compiled into DEBUG builds.
    /// </summary>
    private void WarnTypeMismatch<TArgs>(EventDelegateContainer<T> container)
    {
        // Extract the registered TArgs from the concrete generic type.
        Type containerType = container.GetType();
        Type? registeredArgs = null;
        if (containerType.IsGenericType && containerType.GenericTypeArguments.Length == 2)
            registeredArgs = containerType.GenericTypeArguments[1];

        Debug.LogWarning(
            $"[EventSystem] Type mismatch on {typeof(T).Name}.{_eventType}: " +
            $"handler registered for '{registeredArgs?.Name ?? "unknown"}' " +
            $"but invoked with '{typeof(TArgs).Name}'. Handler was skipped. " +
            $"(registered at {container.SourceDescription})");
    }
#endif

    public void Add(EventDelegateContainer<T> eventDelegate)
    {
        lock (_lock)
        {
            if (!_eventDelegates.ContainsKey(eventDelegate.Priority))
            {
                _eventDelegates[eventDelegate.Priority] = new List<EventDelegateContainer<T>>();
                SortKeys();
            }
            if (!_eventDelegates[eventDelegate.Priority].Contains(eventDelegate))
            {
                _eventDelegates[eventDelegate.Priority].Add(eventDelegate);
                eventDelegate.Link(this);
            }
            RebuildSnapshot();
        }
    }

    public bool Remove(EventDelegateContainer<T> eventDelegate)
    {
        lock (_lock)
        {
            bool result = false;
            if (_eventDelegates.ContainsKey(eventDelegate.Priority))
            {
                result = _eventDelegates[eventDelegate.Priority].Remove(eventDelegate);
            }
            if (result)
            {
                eventDelegate.Unlink();
                RebuildSnapshot();
            }
            return result;
        }
    }


    private void SortKeys()
    {
        _sortedKeys.Clear();
        _sortedKeys.AddRange(_eventDelegates.Keys);
        _sortedKeys.Sort();
    }

    /// <summary>
    /// Rebuilds the flat, priority-sorted snapshot array and per-<c>TArgs</c> typed
    /// snapshot arrays from the current delegate buckets.
    /// Must be called under <see cref="_lock"/>.
    /// </summary>
    private void RebuildSnapshot()
    {
        int totalCount = 0;
        for (int i = 0; i < _sortedKeys.Count; i++)
            totalCount += _eventDelegates[_sortedKeys[i]].Count;

        if (totalCount == 0)
        {
            _cachedSnapshot = [];
            _typedSnapshots.Clear();
            return;
        }

        var snapshot = new EventDelegateContainer<T>[totalCount];
        int index = 0;
        for (int i = 0; i < _sortedKeys.Count; i++)
        {
            var bucket = _eventDelegates[_sortedKeys[i]];
            for (int j = 0; j < bucket.Count; j++)
                snapshot[index++] = bucket[j];
        }
        _cachedSnapshot = snapshot;

        RebuildTypedSnapshots();
    }

    /// <summary>
    /// Rebuilds per-<c>TArgs</c> typed snapshot arrays from the priority-sorted
    /// delegate buckets.  Each resulting array is a properly typed
    /// <c>EventDelegateContainer&lt;T, TArgs&gt;[]</c> created via
    /// <see cref="Array.CreateInstance"/>, enabling <see cref="Invoke{TArgs}"/>
    /// to iterate with direct method calls and zero per-element type checks.
    /// Must be called under <see cref="_lock"/>.
    /// </summary>
    private void RebuildTypedSnapshots()
    {
        _typedSnapshots.Clear();

        // First pass: collect containers per ArgsType, maintaining priority order.
        Dictionary<Type, List<EventDelegateContainer<T>>>? groups = null;

        for (int i = 0; i < _sortedKeys.Count; i++)
        {
            var bucket = _eventDelegates[_sortedKeys[i]];
            for (int j = 0; j < bucket.Count; j++)
            {
                var container = bucket[j];
                var argsType = container.ArgsType;

                groups ??= new Dictionary<Type, List<EventDelegateContainer<T>>>();
                if (!groups.TryGetValue(argsType, out var list))
                {
                    list = new List<EventDelegateContainer<T>>();
                    groups[argsType] = list;
                }
                list.Add(container);
            }
        }

        if (groups is null)
            return;

        // Second pass: create properly typed arrays so Invoke<TArgs> can cast
        // the whole array reference once instead of checking each element.
        foreach (var (argsType, list) in groups)
        {
            var elementType = typeof(EventDelegateContainer<,>).MakeGenericType(typeof(T), argsType);
            var typedArray = Array.CreateInstance(elementType, list.Count);
            for (int i = 0; i < list.Count; i++)
                typedArray.SetValue(list[i], i);
            _typedSnapshots[argsType] = typedArray;
        }
    }
}
