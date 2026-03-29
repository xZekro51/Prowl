using System;

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Non-generic base class for delegate containers, enabling heterogeneous storage
/// within a single <see cref="Event{T}"/>. Subscribe via the typed
/// <see cref="EventDelegateContainer{T, TArgs}"/> derived class.
/// Implements <see cref="IDisposable"/> for self-unsubscription.
/// </summary>
public abstract class EventDelegateContainer<T> : IDisposable where T : struct, Enum
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

    private readonly T eventType;
    public T EventType => eventType;

    private bool enabled = true;
    public bool Enabled
    {
        get => enabled;
        private set => enabled = value;
    }

    private readonly int priority;
    public int Priority => priority;

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
        this.eventType = eventType;
    }

    public void Enable()
    {
        Enabled = true;
    }
    public void Disable()
    {
        Enabled = false;
    }

    /// <summary>
    /// Removes this delegate from its parent event, enabling <c>using</c> patterns
    /// and preventing leaks.
    /// </summary>
    public void Dispose()
    {
        Event?.Remove(this);
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
