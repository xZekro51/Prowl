// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Events raised during the physics simulation step.
/// </summary>
public enum PhysicsEvents
{
    /// <summary>Raised before the physics world steps forward.</summary>
    [EventArgs(typeof(PhysicsStepArgs))]
    OnPrePhysicsStep,

    /// <summary>Raised after the physics world has completed a step.</summary>
    [EventArgs(typeof(PhysicsStepArgs))]
    OnPostPhysicsStep,
}

/// <summary>
/// Typed argument for <see cref="PhysicsEvents"/>.
/// </summary>
/// <param name="DeltaTime">The fixed timestep duration in seconds.</param>
public readonly record struct PhysicsStepArgs(float DeltaTime);
