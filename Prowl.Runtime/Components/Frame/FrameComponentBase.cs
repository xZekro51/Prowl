// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Echo;
using Prowl.Vector;

namespace Prowl.Runtime.Frame;

/// <summary>
/// Abstract base for body (position) pipeline stages.
/// </summary>
public abstract class FrameBodyComponent
{
    public abstract void MutateState(ref CameraState state, Transform? follow, Transform? lookAt, float deltaTime);
}

/// <summary>
/// Abstract base for aim (rotation) pipeline stages.
/// </summary>
public abstract class FrameAimComponent
{
    public abstract void MutateState(ref CameraState state, Transform? follow, Transform? lookAt, float deltaTime);
}

/// <summary>
/// Abstract base for noise pipeline stages (camera shake).
/// </summary>
public abstract class FrameNoiseComponent
{
    public abstract void MutateState(ref CameraState state, float deltaTime);
}

/// <summary>
/// Abstract base for post-pipeline extensions (confiner, collider, etc).
/// Attach as a MonoBehaviour on the same GameObject as the virtual camera.
/// </summary>
public abstract class FrameExtension : MonoBehaviour
{
    public abstract void PostPipelineStage(ref CameraState state, float deltaTime);
}
