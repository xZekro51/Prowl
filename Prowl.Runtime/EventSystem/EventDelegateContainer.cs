using System;

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Non-generic base class for delegate containers, enabling heterogeneous storage
/// within a single <see cref="Event{T}"/>. Subscribe via the typed
/// <see cref="EventDelegateContainer{T, TArgs}"/> derived class.
/// </summary>
public abstract class EventDelegateContainer<T> where T : struct, Enum
{
    public EventManager<T>? EventManager => Event?.EventManager;

    private Event<T> _event;
    public Event<T> Event
    {
        get { return _event; }
        private set
        {
            _event = value;
        }
    }

    private int priority;

    private T eventType;
    public T EventType
    {
        get { return eventType; }
        private set { eventType = value; }
    }

    private bool enabled = true;
    public bool Enabled
    {
        get => enabled;
        private set => enabled = value;
    }

    public int Priority
    {
        get => priority;
        private set => priority = value;
    }

    public void Link(Event<T> @event)
    {
        Event = @event;
    }

    public void Unlink()
    {
        Event = null;
    }

    protected EventDelegateContainer(T eventType, int priority)
    {
        this.priority = priority;
        this.EventType = eventType;
    }

    public void Enable()
    {
        Enabled = true;
    }
    public void Disable()
    {
        Enabled = false;
    }
}

/// <summary>
/// Typed delegate container wrapping an <see cref="Action{TArgs}"/>.
/// </summary>
public class EventDelegateContainer<T, TArgs> : EventDelegateContainer<T> where T : struct, Enum
{
    private readonly Action<TArgs> eventDelegate;

    public EventDelegateContainer(T eventType, Action<TArgs> eventDelegate, int priority = 0)
        : base(eventType, priority)
    {
        this.eventDelegate = eventDelegate;
    }

    public void Invoke(TArgs args)
    {
        if (!Enabled) return;
        eventDelegate?.Invoke(args);
    }
}
