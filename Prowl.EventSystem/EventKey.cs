// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.EventSystem;

/// <summary>
/// Marker type for source-generated event domains.
/// Declare <c>private static readonly EventKey _OnXxx</c> fields inside a class marked with
/// <see cref="EventDomainAttribute"/> to define events. The leading underscore is stripped
/// by the generator to produce the public event name. Optionally decorate each
/// field with <see cref="EventArgsAttribute"/> to specify the event's argument type
/// (defaults to <see cref="Unit"/> when omitted).
/// <para>
/// <b>Example:</b>
/// <code>
/// [EventDomain]
/// public static partial class MyEvents
/// {
///     [EventArgs(typeof(MyPayload))]
///     private static readonly EventKey _OnSomething = new();
/// }
/// </code>
/// The generator will emit <c>public static EventTypes OnSomething => EventTypes.OnSomething;</c>
/// along with convenience methods like <c>InvokeOnSomething</c> and <c>SubscribeOnSomething</c>.
/// </para>
/// </summary>
public readonly struct EventKey;
