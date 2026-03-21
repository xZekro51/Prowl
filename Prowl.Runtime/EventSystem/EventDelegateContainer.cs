using System;

namespace Prowl.Runtime.EventSystem;

public class EventDelegateContainer<T> where T : struct, Enum
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

    private readonly System.Action<EventParam[]> eventDelegate;

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

    public EventDelegateContainer(T eventType, System.Action<EventParam[]> eventDelegate, int priority = 0)
    {
        this.eventDelegate = eventDelegate;
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

    public void Invoke(EventParam[] args)
    {
        if (!Enabled) return;
        eventDelegate?.Invoke(args);
    }
}
