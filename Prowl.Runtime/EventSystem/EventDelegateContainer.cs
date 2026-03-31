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

    /// <summary>
    /// Returns <c>true</c> when this container wraps the specified handler delegate.
    /// Used by the generated <c>-=</c> event accessor path.
    /// </summary>
    public abstract bool MatchesDelegate(Delegate handler);

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

    /// <inheritdoc />
    public override bool MatchesDelegate(Delegate handler) => handler != null && handler.Equals(eventDelegate);

    private readonly Action<TArgs>? eventDelegate;

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

    /// <summary>
    /// Protected constructor for subclasses that provide their own invocation
    /// logic and do not use the <see cref="eventDelegate"/> field.
    /// </summary>
    protected EventDelegateContainer(T eventType, int priority)
        : base(eventType, priority)
    {
    }

#if DEBUG
    protected EventDelegateContainer(T eventType, int priority,
        string? sourceFile, int sourceLine, string? sourceMember)
        : base(eventType, priority, sourceFile, sourceLine, sourceMember)
    {
    }
#endif

    public virtual void Invoke(TArgs args)
    {
        if (!Enabled) return;
        eventDelegate?.Invoke(args);
    }
}

/// <summary>
/// Specialized container for parameterless events that stores an <see cref="Action"/>
/// directly, avoiding the closure allocation that wrapping in an
/// <see cref="Action{Unit}"/> would incur.
/// </summary>
public sealed class ParameterlessEventDelegateContainer<T> : EventDelegateContainer<T, Unit> where T : struct, Enum
{
    /// <inheritdoc />
    public override bool MatchesDelegate(Delegate handler) => handler != null && handler.Equals(_action);

    private readonly Action _action;

    public ParameterlessEventDelegateContainer(T eventType, Action action, int priority = 0)
        : base(eventType, priority)
    {
        _action = action;
    }

#if DEBUG
    public ParameterlessEventDelegateContainer(T eventType, Action action, int priority,
        string? sourceFile, int sourceLine, string? sourceMember)
        : base(eventType, priority, sourceFile, sourceLine, sourceMember)
    {
        _action = action;
    }
#endif

    public override void Invoke(Unit args)
    {
        if (!Enabled) return;
        _action?.Invoke();
    }
}
