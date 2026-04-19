// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Vector;

namespace Prowl.Runtime.Frame;

/// <summary>
/// Interface for any object that can act as a virtual camera in the Frame system.
/// </summary>
public interface IFrameCamera
{
    string CameraName { get; }
    int Priority { get; set; }
    bool IsLive { get; }
    CameraState State { get; }
    Transform? Follow { get; }
    Transform? LookAt { get; }
    void UpdateCameraState(float deltaTime);
}
