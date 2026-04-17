// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

//
// MonoBehaviour Lifecycle Stress Test
//
// Spawns thousands of invisible GameObjects, each with multiple lightweight
// components that override Update, LateUpdate, and FixedUpdate. The goal is
// to stress the engine's component lifecycle dispatch — NOT the GPU.
//
// A single visible reference cube spins so you can confirm the scene is alive.
//
// Controls:
//   Movement:  WASD
//   Look:      Mouse Move (when RMB held)
//   Fly Up:    E
//   Fly Down:  Q
//   Sprint:    Left Shift
//

using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

using MouseButton = Prowl.Runtime.MouseButton;

namespace StressTest;

internal class Program
{
    static void Main(string[] args)
    {
        new StressTestGame().Run("Lifecycle Stress Test", 1280, 720);
    }
}

// ---------------------------------------------------------------------------
// Lightweight components that exercise every lifecycle callback.
// No rendering — pure CPU work in Update / LateUpdate / FixedUpdate.
// ---------------------------------------------------------------------------

/// <summary>
/// Exercises Update(). Accumulates a timer and does cheap transform writes.
/// </summary>
public class TickCounter : MonoBehaviour
{
    [SerializeIgnore] private float _accumulator;
    [SerializeIgnore] private int _ticks;

    public float Speed = 1.0f;

    public override void Update()
    {
        _accumulator += Time.DeltaTime * Speed;
        _ticks++;
        // Cheap transform mutation to simulate real gameplay components
        GameObject.Transform.LocalEulerAngles = new Float3(0, _accumulator * 30f, 0);
    }
}

/// <summary>
/// Exercises LateUpdate(). Reads another object's transform (parent) and
/// applies a smoothed follow — a common gameplay pattern.
/// </summary>
public class LateFollower : MonoBehaviour
{
    [SerializeIgnore] private Float3 _smoothedPosition;

    public float SmoothFactor = 5.0f;

    public override void Start()
    {
        _smoothedPosition = GameObject.Transform.Position;
    }

    public override void LateUpdate()
    {
        Float3 target = GameObject.Transform.Position;
        _smoothedPosition = _smoothedPosition + (target - _smoothedPosition) * SmoothFactor * Time.DeltaTime;
        // Write back a derived value so the work isn't optimized away
        GameObject.Transform.LocalScale = new Float3(
            1.0f + (_smoothedPosition.X - target.X) * 0.01f,
            1.0f + (_smoothedPosition.Y - target.Y) * 0.01f,
            1.0f + (_smoothedPosition.Z - target.Z) * 0.01f
        );
    }
}

/// <summary>
/// Exercises FixedUpdate(). Simulates a simple velocity integration step —
/// the kind of work a custom physics or AI steering component would do.
/// </summary>
public class FixedStepper : MonoBehaviour
{
    [SerializeIgnore] private Float3 _velocity;
    [SerializeIgnore] private float _phase;

    public float Magnitude = 1.0f;

    public override void Start()
    {
        _phase = GameObject.Transform.Position.X * 0.1f;
    }

    public override void FixedUpdate()
    {
        _phase += Time.FixedDeltaTime;
        // Oscillate a velocity vector
        _velocity = new Float3(
            MathF.Sin(_phase * 2.0f) * Magnitude,
            MathF.Cos(_phase * 1.3f) * Magnitude * 0.5f,
            MathF.Sin(_phase * 0.7f) * Magnitude
        );
        // Integrate — write position
        GameObject.Transform.Position += _velocity * Time.FixedDeltaTime;
    }
}

/// <summary>
/// A pure-overhead component: overrides Update + LateUpdate + FixedUpdate
/// and does minimal math in each, maximizing the number of virtual dispatch
/// calls the engine has to make per frame.
/// </summary>
public class TripleCycler : MonoBehaviour
{
    [SerializeIgnore] private float _a;
    [SerializeIgnore] private float _b;
    [SerializeIgnore] private float _c;

    public override void Update()
    {
        _a += Time.DeltaTime;
    }

    public override void LateUpdate()
    {
        _b = MathF.Sin(_a);
    }

    public override void FixedUpdate()
    {
        _c = _a + _b;
        // Prevent the compiler from treating _c as dead code
        if (_c > 1_000_000f) _c = 0;
    }
}

// ---------------------------------------------------------------------------
// Game
// ---------------------------------------------------------------------------

public sealed class StressTestGame : Game
{
    // --- Tunables ---
    // Total GameObjects = ObjectCount. Each gets 3-4 components.
    // With 5000 objects × ~3.5 components = ~17,500 lifecycle calls per frame.
    private const int ObjectCount = 100;

    private GameObject? _cameraGO;
    private Scene? _scene;

    // Input
    private InputActionMap _cameraMap = null!;
    private InputAction _moveAction = null!;
    private InputAction _lookAction = null!;
    private InputAction _lookEnableAction = null!;
    private InputAction _flyUpAction = null!;
    private InputAction _flyDownAction = null!;
    private InputAction _sprintAction = null!;

    // Camera fly
    private float _yaw;
    private float _pitch;

    public override void Initialize()
    {
        _scene = new Scene();
        SetupInput();

        Material mat = new Material(Shader.LoadDefault(DefaultShader.Standard));

        // --- Directional light (minimal, just so the reference cube is lit) ---
        GameObject lightGO = new("Directional Light");
        lightGO.AddComponent<DirectionalLight>();
        lightGO.Transform.LocalEulerAngles = new Float3(-50, 30, 0);
        _scene.Add(lightGO);

        // --- Camera ---
        _cameraGO = new("Main Camera");
        _cameraGO.Tag = "Main Camera";
        _cameraGO.Transform.Position = new Float3(0, 5, -15);
        _cameraGO.Transform.LocalEulerAngles = new Float3(20, 0, 0);
        Camera cam = _cameraGO.AddComponent<Camera>();
        cam.Depth = -1;
        cam.HDR = true;
        cam.Effects =
        [
            new FXAAEffect(),
            new TonemapperEffect(),
        ];
        _scene.Add(_cameraGO);

        // --- Single visible reference cube (proves the scene is alive) ---
        GameObject refCube = new("ReferenceCube");
        MeshRenderer mr = refCube.AddComponent<MeshRenderer>();
        mr.Mesh = Mesh.CreateCube(Float3.One);
        mr.Material = mat;
        refCube.Transform.Position = new Float3(0, 1, 0);
        refCube.AddComponent<TickCounter>().Speed = 2.0f;
        _scene.Add(refCube);

        // --- Spawn thousands of invisible GameObjects with lifecycle components ---
        Random rng = new(42);
        int componentCount = 0;

        for (int i = 0; i < ObjectCount; i++)
        {
            GameObject go = new($"Entity_{i}");

            // Spread them out so transforms aren't degenerate
            go.Transform.Position = new Float3(
                (rng.NextSingle() - 0.5f) * 200f,
                rng.NextSingle() * 10f,
                (rng.NextSingle() - 0.5f) * 200f
            );

            // Every object gets a TickCounter (Update)
            TickCounter tc = go.AddComponent<TickCounter>();
            tc.Speed = 0.5f + rng.NextSingle() * 2.0f;
            componentCount++;

            // Every object gets a TripleCycler (Update + LateUpdate + FixedUpdate)
            go.AddComponent<TripleCycler>();
            componentCount++;

            // 60% get a LateFollower (LateUpdate)
            if (rng.NextSingle() < 0.6f)
            {
                LateFollower lf = go.AddComponent<LateFollower>();
                lf.SmoothFactor = 1.0f + rng.NextSingle() * 10.0f;
                componentCount++;
            }

            // 40% get a FixedStepper (FixedUpdate)
            if (rng.NextSingle() < 0.4f)
            {
                FixedStepper fs = go.AddComponent<FixedStepper>();
                fs.Magnitude = 0.1f + rng.NextSingle() * 0.5f;
                componentCount++;
            }

            _scene.Add(go);
        }

        Debug.Log($"[StressTest] Spawned {ObjectCount} GameObjects with {componentCount} lifecycle components.");
        Debug.Log($"[StressTest] Estimated per-frame calls: ~{componentCount} Update + LateUpdate + FixedUpdate dispatches.");

        Input.SetCursorVisible(false);
        Scene.Load(_scene);
        Application.IsPlaying = true;
    }

    public override void BeginUpdate()
    {
        HandleCameraMovement();
    }

    private void SetupInput()
    {
        _cameraMap = new InputActionMap("Camera");

        _moveAction = _cameraMap.AddAction("Move", InputActionType.Value);
        _moveAction.ExpectedValueType = typeof(Float2);
        _moveAction.AddBinding(new Vector2CompositeBinding(
            InputBinding.CreateKeyBinding(KeyCode.W),
            InputBinding.CreateKeyBinding(KeyCode.S),
            InputBinding.CreateKeyBinding(KeyCode.A),
            InputBinding.CreateKeyBinding(KeyCode.D),
            true
        ));

        _lookAction = _cameraMap.AddAction("Look", InputActionType.Value);
        _lookAction.ExpectedValueType = typeof(Float2);
        var mouse = new DualAxisCompositeBinding(
            InputBinding.CreateMouseAxisBinding(0),
            InputBinding.CreateMouseAxisBinding(1));
        mouse.Processors.Add(new ScaleProcessor(0.1f));
        _lookAction.AddBinding(mouse);

        _lookEnableAction = _cameraMap.AddAction("LookEnable", InputActionType.Button);
        _lookEnableAction.AddBinding(MouseButton.Right);

        _flyUpAction = _cameraMap.AddAction("FlyUp", InputActionType.Button);
        _flyUpAction.AddBinding(KeyCode.E);

        _flyDownAction = _cameraMap.AddAction("FlyDown", InputActionType.Button);
        _flyDownAction.AddBinding(KeyCode.Q);

        _sprintAction = _cameraMap.AddAction("Sprint", InputActionType.Button);
        _sprintAction.AddBinding(KeyCode.ShiftLeft);

        Input.RegisterActionMap(_cameraMap);
        _cameraMap.Enable();
    }

    private void HandleCameraMovement()
    {
        if (_cameraGO.IsNotValid()) return;

        float speed = 10f;
        if (_sprintAction.IsPressed()) speed = 30f;

        // Look
        if (_lookEnableAction.IsPressed())
        {
            Float2 look = _lookAction.ReadValue<Float2>();
            _yaw += look.X * 0.15f;
            _pitch -= look.Y * 0.15f;
            _pitch = Math.Clamp(_pitch, -89f, 89f);
            _cameraGO!.Transform.LocalEulerAngles = new Float3(_pitch, _yaw, 0);
        }

        // Move
        Float2 move = _moveAction.ReadValue<Float2>();
        Float3 forward = _cameraGO!.Transform.Forward;
        Float3 right = _cameraGO!.Transform.Right;
        Float3 velocity = (forward * move.Y + right * move.X) * speed * Time.DeltaTime;

        if (_flyUpAction.IsPressed()) velocity += new Float3(0, 1, 0) * speed * Time.DeltaTime;
        if (_flyDownAction.IsPressed()) velocity -= new Float3(0, 1, 0) * speed * Time.DeltaTime;

        _cameraGO!.Transform.Position += velocity;
    }
}
