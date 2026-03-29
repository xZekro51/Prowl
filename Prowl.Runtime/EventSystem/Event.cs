using System;
using System.Collections.Generic;

namespace Prowl.Runtime.EventSystem;

public class Event<T> where T : struct, Enum
{
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
    /// </summary>
    private EventDelegateContainer<T>[] _cachedSnapshot = [];

    private bool _enabled = true;
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

        EventDelegateContainer<T>[] snapshot;
        lock (_lock)
        {
            snapshot = _cachedSnapshot;
        }

        for (int j = 0; j < snapshot.Length; j++)
        {
            if (snapshot[j] is EventDelegateContainer<T, TArgs> typed)
                typed.Invoke(args);

            if (args is ICancellable { Cancelled: true })
                break;
        }
    }

    public void Add(EventDelegateContainer<T> eventDelegate)
    {
        lock (_lock)
        {
            if (!_eventDelegates.ContainsKey(eventDelegate.Priority))
            {
                _eventDelegates[eventDelegate.Priority] = new List<EventDelegateContainer<T>>();
            }
            if (!_eventDelegates[eventDelegate.Priority].Contains(eventDelegate))
            {
                _eventDelegates[eventDelegate.Priority].Add(eventDelegate);
                eventDelegate.Link(this);
            }
            SortKeys();
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
    /// Rebuilds the flat, priority-sorted snapshot array from the current delegate buckets.
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
    }
}
