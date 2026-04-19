// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Echo;
using Prowl.Vector;

namespace Prowl.Runtime.Frame;

/// <summary>
/// Three-rig orbital camera (top/middle/bottom) blended by vertical axis.
/// Equivalent to Cinemachine FreeLook.
/// </summary>
[AddComponentMenu("Frame/FreeLook Camera")]
public class FrameFreeLookCamera : MonoBehaviour, IFrameCamera
{
    public string CameraName => Name ?? "FreeLook";
    public int Priority { get; set; } = 10;
    public bool IsLive => Enabled && GameObject.EnabledInHierarchy;

    [SerializeIgnore] private GameObject? _followTarget;
    [SerializeIgnore] private GameObject? _lookAtTarget;

    public Transform? Follow => _followTarget?.Transform;
    public Transform? LookAt => _lookAtTarget?.Transform;

    public void SetFollowTarget(GameObject? target) => _followTarget = target;
    public void SetLookAtTarget(GameObject? target) => _lookAtTarget = target;

    public float FieldOfView = 60f;
    public float NearClip = 0.1f;
    public float FarClip = 100f;

    /// <summary>Top rig (height, radius).</summary>
    public Float2 TopRig = new(4f, 1.5f);
    /// <summary>Middle rig (height, radius).</summary>
    public Float2 MiddleRig = new(2f, 5f);
    /// <summary>Bottom rig (height, radius).</summary>
    public Float2 BottomRig = new(0.5f, 3f);

    /// <summary>Vertical axis (0=bottom, 0.5=middle, 1=top). Drive from input.</summary>
    [Range(0f, 1f)] public float VerticalAxis = 0.5f;
    /// <summary>Horizontal orbit angle in degrees. Drive from input.</summary>
    public float HorizontalAxis = 0f;
    [Range(0f, 20f)] public float Damping = 1f;

    [SerializeIgnore] private CameraState _state;
    [SerializeIgnore] private Float3 _previousPosition;
    [SerializeIgnore] private bool _initialized;

    public CameraState State => _state;

    public override void OnEnable() => FrameCore.RegisterCamera(this);
    public override void OnDisable() => FrameCore.UnregisterCamera(this);
    public override void LateUpdate() => UpdateCameraState(Time.DeltaTime);

    public void UpdateCameraState(float deltaTime)
    {
        Float3 targetPos = Follow?.Position ?? Transform.Position;

        Float2 orbit;
        if (VerticalAxis <= 0.5f)
        {
            float t = VerticalAxis * 2f;
            orbit = new Float2(
                Maths.Lerp(BottomRig.X, MiddleRig.X, t),
                Maths.Lerp(BottomRig.Y, MiddleRig.Y, t));
        }
        else
        {
            float t = (VerticalAxis - 0.5f) * 2f;
            orbit = new Float2(
                Maths.Lerp(MiddleRig.X, TopRig.X, t),
                Maths.Lerp(MiddleRig.Y, TopRig.Y, t));
        }

        float height = orbit.X;
        float radius = orbit.Y;
        float hRad = Maths.ToRadians(HorizontalAxis);

        Float3 desiredPos = targetPos + new Float3(
            Maths.Sin(hRad) * radius, height, Maths.Cos(hRad) * radius);

        if (!_initialized) { _previousPosition = desiredPos; _initialized = true; }

        float damp = Damping <= 0f ? 1f : 1f - Maths.Exp(-deltaTime / (Damping * 0.1f));
        Float3 dampedPos = Maths.Lerp(_previousPosition, desiredPos, damp);
        _previousPosition = dampedPos;

        Float3 lookAtPos = LookAt?.Position ?? targetPos;
        Float3 dir = lookAtPos - dampedPos;
        Quaternion orientation = Float3.LengthSquared(dir) > 0.0001f
            ? Quaternion.LookRotation(Float3.Normalize(dir), Float3.UnitY)
            : Quaternion.Identity;

        _state = new CameraState
        {
            Position = dampedPos,
            Orientation = orientation,
            FieldOfView = FieldOfView,
            NearClip = NearClip,
            FarClip = FarClip,
            IsOrthographic = false,
        };
    }
}

/// <summary>
/// Selects among child virtual cameras based on an integer state value.
/// Equivalent to Cinemachine StateDrivenCamera.
/// </summary>
[AddComponentMenu("Frame/State-Driven Camera")]
public class FrameStateDrivenCamera : MonoBehaviour, IFrameCamera
{
    public string CameraName => Name ?? "StateDriven";
    public int Priority { get; set; } = 10;
    public bool IsLive => Enabled && GameObject.EnabledInHierarchy;
    public Transform? Follow => _activeCamera?.Follow;
    public Transform? LookAt => _activeCamera?.LookAt;

    [SerializeIgnore] public Dictionary<int, IFrameCamera> StateCameraMap = [];
    public int CurrentState = 0;
    [Range(0f, 10f)] public float BlendTime = 0.5f;
    public BlendStyle BlendStyle = BlendStyle.EaseInOut;

    [SerializeIgnore] private IFrameCamera? _activeCamera;
    [SerializeIgnore] private IFrameCamera? _previousCamera;
    [SerializeIgnore] private FrameBlend _blend;
    [SerializeIgnore] private bool _isBlending;
    [SerializeIgnore] private int _lastState = -1;

    public CameraState State { get; private set; }

    public override void OnEnable() => FrameCore.RegisterCamera(this);
    public override void OnDisable() => FrameCore.UnregisterCamera(this);
    public override void LateUpdate() => UpdateCameraState(Time.DeltaTime);

    public void UpdateCameraState(float deltaTime)
    {
        if (CurrentState != _lastState)
        {
            _lastState = CurrentState;
            if (StateCameraMap.TryGetValue(CurrentState, out var newCam) && newCam != _activeCamera)
            {
                _previousCamera = _activeCamera;
                _activeCamera = newCam;
                if (_previousCamera != null && BlendTime > 0f)
                {
                    _blend = new FrameBlend
                    {
                        CameraA = _previousCamera, CameraB = _activeCamera,
                        Style = BlendStyle, Duration = BlendTime, TimeInBlend = 0f,
                    };
                    _isBlending = true;
                }
            }
        }

        _activeCamera?.UpdateCameraState(deltaTime);
        if (_isBlending) _previousCamera?.UpdateCameraState(deltaTime);

        if (_isBlending)
        {
            _blend.TimeInBlend += deltaTime;
            State = _blend.State;
            if (_blend.IsComplete) _isBlending = false;
        }
        else
        {
            State = _activeCamera?.State ?? CameraState.Default;
        }
    }
}

/// <summary>
/// Picks the child camera with the best "shot quality".
/// Equivalent to Cinemachine ClearShot.
/// </summary>
[AddComponentMenu("Frame/ClearShot Camera")]
public class FrameClearShotCamera : MonoBehaviour, IFrameCamera
{
    public string CameraName => Name ?? "ClearShot";
    public int Priority { get; set; } = 10;
    public bool IsLive => Enabled && GameObject.EnabledInHierarchy;
    public Transform? Follow => _activeCamera?.Follow;
    public Transform? LookAt => _activeCamera?.LookAt;

    [SerializeIgnore] public List<IFrameCamera> ChildCameras = [];
    [Range(0f, 10f)] public float BlendTime = 0.5f;
    public BlendStyle BlendStyle = BlendStyle.EaseInOut;
    [Range(0f, 5f)] public float MinActivationTime = 1f;

    /// <summary>Override for custom shot quality evaluation. Higher is better.</summary>
    [SerializeIgnore] public Func<IFrameCamera, float>? QualityEvaluator;

    [SerializeIgnore] private IFrameCamera? _activeCamera;
    [SerializeIgnore] private IFrameCamera? _previousCamera;
    [SerializeIgnore] private FrameBlend _blend;
    [SerializeIgnore] private bool _isBlending;
    [SerializeIgnore] private float _timeSinceActivation;

    public CameraState State { get; private set; }

    public override void OnEnable() => FrameCore.RegisterCamera(this);
    public override void OnDisable() => FrameCore.UnregisterCamera(this);
    public override void LateUpdate() => UpdateCameraState(Time.DeltaTime);

    public void UpdateCameraState(float deltaTime)
    {
        _timeSinceActivation += deltaTime;

        if (_timeSinceActivation >= MinActivationTime || _activeCamera == null)
        {
            IFrameCamera? best = null;
            float bestQuality = float.MinValue;
            for (int i = 0; i < ChildCameras.Count; i++)
            {
                var cam = ChildCameras[i];
                if (!cam.IsLive) continue;
                float q = QualityEvaluator?.Invoke(cam) ?? 1f;
                if (q > bestQuality) { bestQuality = q; best = cam; }
            }

            if (best != null && best != _activeCamera)
            {
                _previousCamera = _activeCamera;
                _activeCamera = best;
                _timeSinceActivation = 0f;
                if (_previousCamera != null && BlendTime > 0f)
                {
                    _blend = new FrameBlend
                    {
                        CameraA = _previousCamera, CameraB = _activeCamera,
                        Style = BlendStyle, Duration = BlendTime, TimeInBlend = 0f,
                    };
                    _isBlending = true;
                }
            }
        }

        _activeCamera?.UpdateCameraState(deltaTime);
        if (_isBlending) _previousCamera?.UpdateCameraState(deltaTime);

        if (_isBlending)
        {
            _blend.TimeInBlend += deltaTime;
            State = _blend.State;
            if (_blend.IsComplete) _isBlending = false;
        }
        else
        {
            State = _activeCamera?.State ?? CameraState.Default;
        }
    }
}

/// <summary>
/// Blends between two virtual cameras based on an external weight parameter.
/// Equivalent to Cinemachine MixingCamera.
/// </summary>
[AddComponentMenu("Frame/Mixing Camera")]
public class FrameMixingCamera : MonoBehaviour, IFrameCamera
{
    public string CameraName => Name ?? "Mixing";
    public int Priority { get; set; } = 10;
    public bool IsLive => Enabled && GameObject.EnabledInHierarchy;
    public Transform? Follow => null;
    public Transform? LookAt => null;

    [SerializeIgnore] public IFrameCamera? CameraA;
    [SerializeIgnore] public IFrameCamera? CameraB;

    [Range(0f, 1f)] public float Weight = 0f;

    public CameraState State { get; private set; }

    public override void OnEnable() => FrameCore.RegisterCamera(this);
    public override void OnDisable() => FrameCore.UnregisterCamera(this);
    public override void LateUpdate() => UpdateCameraState(Time.DeltaTime);

    public void UpdateCameraState(float deltaTime)
    {
        CameraA?.UpdateCameraState(deltaTime);
        CameraB?.UpdateCameraState(deltaTime);
        CameraState a = CameraA?.State ?? CameraState.Default;
        CameraState b = CameraB?.State ?? CameraState.Default;
        State = CameraState.Lerp(a, b, Maths.Clamp(Weight, 0f, 1f));
    }
}
