// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

using Prowl.EventSystem;

namespace Prowl.Runtime;

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

/// <summary>
/// Extension methods on <see cref="EventManager{T}"/> that register lifecycle-aware
/// delegates bound to an <see cref="EngineObject"/> owner. The subscription is
/// automatically removed when the owner is disposed.
/// </summary>
public static class EventManagerLifecycleExtensions
{
    /// <summary>
    /// Register a typed delegate bound to an <see cref="EngineObject"/> owner.
    /// The subscription is automatically removed when the owner is disposed.
    /// </summary>
    public static LifecycleEventDelegateContainer<T, TArgs> AddNewDelegate<T, TArgs>(
        this EventManager<T> manager,
        EngineObject owner, T eventType, Action<TArgs> eventDelegate, int priority = 0
#if DEBUG
        , [CallerFilePath] string? sourceFile = null,
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string? sourceMember = null
#endif
    ) where T : struct, Enum
    {
#if DEBUG
        LifecycleEventDelegateContainer<T, TArgs> container = new(owner, eventType, eventDelegate, priority, sourceFile, sourceLine, sourceMember);
#else
        LifecycleEventDelegateContainer<T, TArgs> container = new(owner, eventType, eventDelegate, priority);
#endif
        manager.AddDelegate(container);
        return container;
    }

    /// <summary>
    /// Register a parameterless delegate bound to an <see cref="EngineObject"/> owner.
    /// The subscription is automatically removed when the owner is disposed.
    /// </summary>
    public static LifecycleParameterlessEventDelegateContainer<T> AddNewDelegate<T>(
        this EventManager<T> manager,
        EngineObject owner, T eventType, Action eventDelegate, int priority = 0
#if DEBUG
        , [CallerFilePath] string? sourceFile = null,
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string? sourceMember = null
#endif
    ) where T : struct, Enum
    {
#if DEBUG
        LifecycleParameterlessEventDelegateContainer<T> container = new(owner, eventType, eventDelegate, priority, sourceFile, sourceLine, sourceMember);
#else
        LifecycleParameterlessEventDelegateContainer<T> container = new(owner, eventType, eventDelegate, priority);
#endif
        manager.AddDelegate(container);
        return container;
    }
}
