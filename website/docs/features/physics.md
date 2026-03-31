---
id: physics
title: Physics
sidebar_position: 2
description: Prowl's physics system powered by Jitter Physics 2 — colliders, events, and shape casting.
keywords: [prowl, physics, jitter, colliders, raycasting]
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

:::info Key Capabilities

- **Collision Layers** — Control which objects can interact with each other
- **Fixed Timestep** — Physics runs at a fixed timestep independent of frame rate
- **Shape Casting** — Ray and shape cast queries for gameplay logic
- **Physics Events** — `OnPrePhysicsStep` and `OnPostPhysicsStep` events via the [Event System](../architecture/event-system)

:::

## Physics Events

The physics system integrates with Prowl's [Event System](../architecture/event-system):

```csharp title="Subscribing to physics events"
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

:::tip

Physics events use the same priority-ordered, cancellable dispatch as all other Prowl events. See the [Event System architecture doc](../architecture/event-system) for details on priority ordering and lifecycle-aware subscriptions.

:::
