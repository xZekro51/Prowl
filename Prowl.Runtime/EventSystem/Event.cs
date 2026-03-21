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


    public void Invoke(EventParam[] args)
    {
        if (!Enabled) return;

        List<(int key, EventDelegateContainer<T>[] delegates)> snapshot;
        lock (_lock)
        {
            snapshot = new List<(int, EventDelegateContainer<T>[])>(_sortedKeys.Count);
            for (int i = 0; i < _sortedKeys.Count; i++)
            {
                int key = _sortedKeys[i];
                snapshot.Add((key, [.. _eventDelegates[key]]));
            }
        }

        for (int i = 0; i < snapshot.Count; i++)
        {
            var delegates = snapshot[i].delegates;
            for (int j = 0; j < delegates.Length; j++)
            {
                delegates[j].Invoke(args);
            }
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
            eventDelegate.Unlink();
            return result;
        }
    }


    private void SortKeys()
    {
        _sortedKeys.Clear();
        _sortedKeys.AddRange(_eventDelegates.Keys);
        _sortedKeys.Sort();
    }
}
