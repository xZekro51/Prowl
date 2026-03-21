using System;
using System.Collections.Generic;
using System.Linq;

namespace Prowl.Runtime.EventSystem;

public class EventManager<T> : IDisposable where T : struct, Enum
{
    private static readonly List<EventManager<T>> s_instances = new List<EventManager<T>>();
    private static readonly object s_instancesLock = new();

    public static EventManager<T> LastGlobalInstance
    {
        get
        {
            lock (s_instancesLock)
                return s_instances.LastOrDefault(x => x.Enabled && x.Global);
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
            s_instances.Add(this);
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
        int valuesLength = System.Enum.GetValues(typeof(T)).Length;
        for (int i = 0; i < valuesLength; i++)
        {
            AddEvent(new Event<T>(this, i.ToEnum<T>()));
        }
    }


    public void InvokeEvent(T eventType, params EventParam[] args)
    {
        if (!Enabled) return;
        if (_events.TryGetValue(eventType, out var evt))
            evt.Invoke(args);
    }

    public void InvokeEvent(T eventType)
    {
        if (!Enabled) return;
        if (_events.TryGetValue(eventType, out var evt))
            evt.Invoke([]);
    }

    public EventDelegateContainer<T> AddNewDelegate(T eventType, System.Action<EventParam[]> eventDelegate, int priority = 0)
    {
        EventDelegateContainer<T> container = new EventDelegateContainer<T>(eventType, eventDelegate, priority);
        _events[eventType].Add(container);
        return container;
    }


    public static void GlobalInvokeEvent(T eventType, params EventParam[] args)
    {
        List<EventManager<T>> snapshot;
        lock (s_instancesLock)
            snapshot = [.. s_instances];

        for (int i = 0; i < snapshot.Count; i++)
        {
            var instance = snapshot[i];
            if (instance.Enabled && instance.Global)
            {
                instance.InvokeEvent(eventType, args);
            }
        }
    }

    public void Dispose()
    {
        lock (s_instancesLock)
            s_instances.Remove(this);
    }

}
