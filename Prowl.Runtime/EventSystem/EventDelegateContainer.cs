using System;
using System.Runtime.CompilerServices;

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

    /// <summary>
    /// The <c>TArgs</c> type this container was registered with.
    /// Used by <see cref="Event{T}"/> to build per-type snapshots without reflection.
    /// </summary>
    public abstract Type ArgsType { get; }

#if DEBUG
    /// <summary>
    /// Source file where this handler was registered. Captured automatically
    /// via <see cref="CallerFilePathAttribute"/> in DEBUG builds.
    /// </summary>
    public string? SourceFile { get; private set; }

    /// <summary>
    /// Source line number where this handler was registered.
    /// </summary>
    public int SourceLine { get; private set; }

    /// <summary>
    /// Name of the member that registered this handler.
    /// </summary>
    public string? SourceMember { get; private set; }

    /// <summary>
    /// Returns a compact "File:Line (Member)" string for diagnostics,
    /// or <c>"unknown"</c> when source info was not captured.
    /// </summary>
    public string SourceDescription =>
        SourceFile is not null
            ? $"{System.IO.Path.GetFileName(SourceFile)}:{SourceLine} ({SourceMember})"
            : "unknown";
#endif

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

#if DEBUG
    protected EventDelegateContainer(T eventType, int priority, string? sourceFile, int sourceLine, string? sourceMember)
        : this(eventType, priority)
    {
        SourceFile = sourceFile;
        SourceLine = sourceLine;
        SourceMember = sourceMember;
    }
#endif

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
public class EventDelegateContainer<T, TArgs> : EventDelegateContainer<T>, IInvocable<TArgs> where T : struct, Enum
{
    public override Type ArgsType => typeof(TArgs);

    private readonly Action<TArgs> eventDelegate;

    public EventDelegateContainer(T eventType, Action<TArgs> eventDelegate, int priority = 0)
        : base(eventType, priority)
    {
        this.eventDelegate = eventDelegate;
    }

#if DEBUG
    public EventDelegateContainer(T eventType, Action<TArgs> eventDelegate, int priority,
        string? sourceFile, int sourceLine, string? sourceMember)
        : base(eventType, priority, sourceFile, sourceLine, sourceMember)
    {
        this.eventDelegate = eventDelegate;
    }
#endif

    public void Invoke(TArgs args)
    {
        if (!Enabled) return;
        eventDelegate?.Invoke(args);
    }
}
