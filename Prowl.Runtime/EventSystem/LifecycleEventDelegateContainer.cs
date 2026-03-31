// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// A typed delegate container that is bound to an <see cref="EngineObject"/>
/// owner. On each <see cref="Invoke"/>, the container checks whether the owner
/// has been disposed. If so, it automatically removes itself from the parent
/// event, preventing leaked subscriptions from destroyed objects.
/// </summary>
public sealed class LifecycleEventDelegateContainer<T, TArgs> : EventDelegateContainer<T, TArgs>
    where T : struct, Enum
{
    private readonly EngineObject _owner;
    private readonly Action<TArgs> _handler;

    public LifecycleEventDelegateContainer(EngineObject owner, T eventType, Action<TArgs> handler, int priority)
        : base(eventType, priority)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _handler = handler;
    }

#if DEBUG
    public LifecycleEventDelegateContainer(EngineObject owner, T eventType, Action<TArgs> handler, int priority,
        string? sourceFile, int sourceLine, string? sourceMember)
        : base(eventType, priority, sourceFile, sourceLine, sourceMember)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _handler = handler;
    }
#endif

    /// <inheritdoc />
    public override bool MatchesDelegate(Delegate handler) => handler != null && handler.Equals(_handler);

    /// <inheritdoc />
    public override void Invoke(TArgs args)
    {
        if (_owner.IsDisposed)
        {
            // Owner is gone — auto-unsubscribe.
            Event?.Remove(this);
            return;
        }

        if (!Enabled) return;
        _handler.Invoke(args);
    }
}

/// <summary>
/// A parameterless delegate container bound to an <see cref="EngineObject"/>
/// owner. Automatically unsubscribes when the owner is disposed.
/// </summary>
public sealed class LifecycleParameterlessEventDelegateContainer<T> : EventDelegateContainer<T, Unit>
    where T : struct, Enum
{
    private readonly EngineObject _owner;
    private readonly Action _action;

    public LifecycleParameterlessEventDelegateContainer(EngineObject owner, T eventType, Action action, int priority)
        : base(eventType, priority)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _action = action;
    }

#if DEBUG
    public LifecycleParameterlessEventDelegateContainer(EngineObject owner, T eventType, Action action, int priority,
        string? sourceFile, int sourceLine, string? sourceMember)
        : base(eventType, priority, sourceFile, sourceLine, sourceMember)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _action = action;
    }
#endif

    /// <inheritdoc />
    public override bool MatchesDelegate(Delegate handler) => handler != null && handler.Equals(_action);

    /// <inheritdoc />
    public override void Invoke(Unit args)
    {
        if (_owner.IsDisposed)
        {
            Event?.Remove(this);
            return;
        }

        if (!Enabled) return;
        _action.Invoke();
    }
}
