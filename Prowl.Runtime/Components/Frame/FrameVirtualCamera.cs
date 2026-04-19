// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Prowl.Echo;
using Prowl.Vector;

namespace Prowl.Runtime.Frame;

/// <summary>
/// The primary virtual camera component. Body → Aim → Noise pipeline.
/// Users swap body/aim/noise modules and attach FrameExtension components.
/// </summary>
[AddComponentMenu("Frame/Virtual Camera")]
public class FrameVirtualCamera : MonoBehaviour, IFrameCamera
{
    public string CameraName => Name ?? "VirtualCamera";
    public int Priority { get; set; } = 10;

    /*[SerializeField] private AssetRef<GameObject> _followTarget;
    [SerializeField] private AssetRef<GameObject> _lookAtTarget;

    [SerializeIgnore] private GameObject? _followGameObject;
    [SerializeIgnore] private GameObject? _lookAtGameObject;*/

    [SerializeField] private GameObject? _followGameObject;
    [SerializeField] private GameObject? _lookAtGameObject;

    public Transform? Follow => _followGameObject?.Transform;
    public Transform? LookAt => _lookAtGameObject?.Transform;

    public float FieldOfView = 60f;
    public float NearClip = 0.1f;
    public float FarClip = 100f;
    public float Dutch = 0f;
    public bool IsOrthographic = false;
    public float OrthographicSize = 0.5f;

    [SerializeField] public FrameBodyComponent? Body;
    [SerializeField] public FrameAimComponent? Aim;
    [SerializeField] public FrameNoiseComponent? Noise;

    [SerializeIgnore] private CameraState _state;

    public bool IsLive => Enabled && GameObject.EnabledInHierarchy;
    public CameraState State => _state;

    public override void OnEnable() => FrameCore.RegisterCamera(this);
    public override void OnDisable() => FrameCore.UnregisterCamera(this);

    public void SetFollowTarget(GameObject? target) => _followGameObject = target;
    public void SetLookAtTarget(GameObject? target) => _lookAtGameObject = target;

    public override void LateUpdate() => UpdateCameraState(Time.DeltaTime);

    public void UpdateCameraState(float deltaTime)
    {
        _state = new CameraState
        {
            Position = Transform.Position,
            Orientation = Transform.Rotation,
            FieldOfView = FieldOfView,
            NearClip = NearClip,
            FarClip = FarClip,
            Dutch = Dutch,
            IsOrthographic = IsOrthographic,
            OrthographicSize = OrthographicSize,
            LensShift = Float2.Zero,
        };

        Body?.MutateState(ref _state, Follow, LookAt, deltaTime);
        Aim?.MutateState(ref _state, Follow, LookAt, deltaTime);
        Noise?.MutateState(ref _state, deltaTime);

        foreach (var ext in GameObject.GetComponents<FrameExtension>())
        {
            if (ext.Enabled)
                ext.PostPipelineStage(ref _state, deltaTime);
        }
    }

    public override void OnDispose()
    {
        FrameCore.UnregisterCamera(this);
    }
}
