// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Marks a <c>static partial class</c> as an event domain.
/// The source generator will produce a backing enum, an <see cref="EventManager{T}"/>,
/// and typed <c>Invoke</c> / <c>Subscribe</c> / <c>GlobalInvoke</c> convenience methods
/// for each <see cref="EventKey"/> field declared in the class.
/// <para>
/// <b>Example:</b>
/// <code>
/// [EventDomain(Global = true)]
/// public static partial class MyEvents
/// {
///     [EventArgs(typeof(MyArgs))]
///     public static readonly EventKey OnSomething = new();
///
///     public readonly struct MyArgs { public int Value; }
/// }
/// </code>
/// The generator emits <c>MyEvents.InvokeOnSomething(args)</c>,
/// <c>MyEvents.SubscribeOnSomething(handler)</c>, etc.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EventDomainAttribute : Attribute
{
    /// <summary>
    /// When <c>true</c>, the generated <see cref="EventManager{T}"/> is created
    /// with <c>global: true</c>, making it participate in
    /// <see cref="EventManager{T}.GlobalInvokeEvent"/> calls.
    /// </summary>
    public bool Global { get; set; }
}
