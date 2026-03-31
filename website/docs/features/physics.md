---
id: physics
title: Physics
sidebar_position: 2
---

# Physics

Prowl integrates [Jitter Physics 2](https://github.com/notgiven688/jitterphysics2) for its physics simulation.

## Collider Types

| Collider | Description |
|----------|-------------|
| **Box** | Axis-aligned or oriented box shape |
| **Sphere** | Simple sphere collider |
| **Capsule** | Cylinder with hemispherical caps |
| **Cylinder** | Cylindrical shape |
| **Cone** | Conical shape |
| **Convex Mesh** | Arbitrary convex hull from mesh data |

## Features

- **Collision Layers** — Control which objects can interact with each other
- **Physics Events** — `OnPrePhysicsStep` and `OnPostPhysicsStep` events via the [Event System](/docs/architecture/event-system)
- **Fixed Timestep** — Physics runs at a fixed timestep independent of frame rate
- **Shape Casting** — Ray and shape cast queries

## Physics Events

The physics system integrates with Prowl's event system:

```csharp
// Subscribe to physics events
PhysicsEvents.OnPrePhysicsStep += (args) =>
{
    // Called before each physics step
    // args.FixedDeltaTime contains the timestep duration
};

PhysicsEvents.OnPostPhysicsStep += (args) =>
{
    // Called after each physics step
};
```
