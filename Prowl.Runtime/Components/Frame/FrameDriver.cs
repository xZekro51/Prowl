// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Echo;
using Prowl.Vector;

namespace Prowl.Runtime.Frame;

/// <summary>
/// Brain component — attach to the same GameObject as <see cref="Camera"/>.
/// Picks the highest-priority live virtual camera, manages blending,
/// and writes the resulting state to the Camera + Transform.
/// </summary>
[AddComponentMenu("Frame/Driver")]
[RequireComponent(typeof(Camera))]
[ExecuteAlways]
public class FrameDriver : MonoBehaviour
{
    [Range(0f, 10f)]
    public float DefaultBlendTime = 2.0f;

    public BlendStyle DefaultBlendStyle = BlendStyle.EaseInOut;
    public bool UpdateInLateUpdate = true;

    public Float3 WorldUpOverride = Float3.Zero;
    public Float3 WorldUp => Float3.LengthSquared(WorldUpOverride) > 0.001f ? Float3.Normalize(WorldUpOverride) : Float3.UnitY;

    [SerializeIgnore] private Camera? _camera;
    [SerializeIgnore] private IFrameCamera? _activeCam;
    [SerializeIgnore] private IFrameCamera? _previousCam;
    [SerializeIgnore] private FrameBlend _activeBlend;
    [SerializeIgnore] private bool _isBlending;

    public IFrameCamera? ActiveVirtualCamera => _activeCam;
    public bool IsBlending => _isBlending;

    public override void OnEnable()
    {
        _camera = GameObject.GetComponent<Camera>();
        FrameCore.RegisterBrain(this);
    }

    public override void OnDisable() => FrameCore.UnregisterBrain(this);

    public override void Update()
    {
        if (!UpdateInLateUpdate) ProcessFrame(Time.DeltaTime);
    }

    public override void LateUpdate()
    {
        if (UpdateInLateUpdate) ProcessFrame(Time.DeltaTime);
    }

    public void CutTo(IFrameCamera cam)
    {
        _previousCam = _activeCam;
        _activeCam = cam;
        _isBlending = false;
    }

    private void ProcessFrame(float deltaTime)
    {
        if (_camera.IsNotValid()) return;

        IFrameCamera? top = FrameCore.GetTopCamera();
        if (top == null) return;

        if (top != _activeCam)
        {
            _previousCam = _activeCam;
            _activeCam = top;

            if (_previousCam != null && DefaultBlendTime > 0f && DefaultBlendStyle != BlendStyle.Cut)
            {
                _activeBlend = new FrameBlend
                {
                    CameraA = _previousCam,
                    CameraB = _activeCam,
                    Style = DefaultBlendStyle,
                    Duration = DefaultBlendTime,
                    TimeInBlend = 0f,
                };
                _isBlending = true;
            }
            else
            {
                _isBlending = false;
            }
        }

        _activeCam?.UpdateCameraState(deltaTime);
        if (_isBlending) _previousCam?.UpdateCameraState(deltaTime);

        CameraState finalState;
        if (_isBlending)
        {
            _activeBlend.TimeInBlend += deltaTime;
            finalState = _activeBlend.State;
            if (_activeBlend.IsComplete) _isBlending = false;
        }
        else
        {
            finalState = _activeCam?.State ?? CameraState.Default;
        }

        ApplyState(finalState);
    }

    private void ApplyState(in CameraState state)
    {
        Transform.Position = state.Position;
        Transform.Rotation = state.Orientation;
        _camera!.FieldOfView = state.FieldOfView;
        _camera.NearClipPlane = state.NearClip;
        _camera.FarClipPlane = state.FarClip;
        _camera.OrthographicSize = state.OrthographicSize;
        _camera.ProjectionMode = state.IsOrthographic
            ? Camera.ProjectionType.Orthographic
            : Camera.ProjectionType.Perspective;
    }
}
