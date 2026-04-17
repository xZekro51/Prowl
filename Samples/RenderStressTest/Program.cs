// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

//
// Rendering Stress Test
//
// Spawns thousands of VISIBLE cubes, each with a MeshRenderer and a spinning
// component. The goal is to stress the engine's rendering pipeline — draw calls,
// culling, shadow mapping, and transform updates.
//
// Controls:
//   Movement:  WASD
//   Look:      Mouse Move (when RMB held)
//   Fly Up:    E
//   Fly Down:  Q
//   Sprint:    Left Shift
//

using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

using MouseButton = Prowl.Runtime.MouseButton;

namespace RenderStressTest;

internal class Program
{
    static void Main(string[] args)
    {
        new RenderStressTestGame().Run("Rendering Stress Test", 1280, 720);
    }
}

// ---------------------------------------------------------------------------
// Simple spinning component so each cube is animated.
// ---------------------------------------------------------------------------

public class Spinner : MonoBehaviour
{
    [Prowl.Echo.SerializeIgnore] private float _angle;

    public Float3 Axis = Float3.UnitY;
    public float Speed = 90.0f;

    public override void Update()
    {
        _angle += Time.DeltaTime * Speed;
        GameObject.Transform.LocalEulerAngles = Axis * _angle;
    }
}

// ---------------------------------------------------------------------------
// Game
// ---------------------------------------------------------------------------

public sealed class RenderStressTestGame : Game
{
    // --- Tunables ---
    private const int GridSize = 10;       // GridSize^3 = total cubes (10^3 = 1,000)
    private const float Spacing = 3.0f;    // Distance between cube centers

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

    // FPS tracking
    private float _fpsTimer;
    private int _fpsFrameCount;

    public override void Initialize()
    {
        _scene = new Scene();
        SetupInput();

        Material mat = new Material(Shader.LoadDefault(DefaultShader.Standard));
        Mesh cubeMesh = Mesh.CreateCube(Float3.One);

        // --- Directional light with shadows ---
        GameObject lightGO = new("Directional Light");
        DirectionalLight light = lightGO.AddComponent<DirectionalLight>();
        light.ShadowQuality = ShadowQuality.Soft;
        lightGO.Transform.LocalEulerAngles = new Float3(-50, 30, 0);
        _scene.Add(lightGO);

        // --- Camera ---
        _cameraGO = new("Main Camera");
        _cameraGO.Tag = "Main Camera";
        float center = (GridSize - 1) * Spacing * 0.5f;
        _cameraGO.Transform.Position = new Float3(center, center + 10, -20);
        _cameraGO.Transform.LocalEulerAngles = new Float3(25, 0, 0);
        Camera cam = _cameraGO.AddComponent<Camera>();
        cam.Depth = -1;
        cam.HDR = true;
        cam.Effects =
        [
            new FXAAEffect(),
            new TonemapperEffect(),
        ];
        _scene.Add(_cameraGO);

        // --- Floor ---
        float floorSize = GridSize * Spacing + 20f;
        GameObject floor = new("Floor");
        BoxCollider boxCollider = floor.AddComponent<BoxCollider>();
        boxCollider.Size = new Float3(floorSize, 1, floorSize);
        MeshRenderer floorMr = floor.AddComponent<MeshRenderer>();
        floorMr.Mesh = Mesh.CreateCube(new Float3(floorSize, 1, floorSize));
        floorMr.Material = mat;
        floor.Transform.Position = new Float3(center, -1f, center);
        _scene.Add(floor);

        // --- Spawn a 3D grid of visible cubes ---
        Random rng = new(42);
        int totalCubes = 0;

        for (int x = 0; x < GridSize; x++)
        {
            for (int y = 0; y < GridSize; y++)
            {
                for (int z = 0; z < GridSize; z++)
                {
                    GameObject go = new($"Cube_{x}_{y}_{z}");

                    go.Transform.Position = new Float3(
                        x * Spacing,
                        y * Spacing + 1f,
                        z * Spacing
                    );

                    // Random initial rotation so they don't all look identical
                    go.Transform.LocalEulerAngles = new Float3(
                        rng.NextSingle() * 360f,
                        rng.NextSingle() * 360f,
                        rng.NextSingle() * 360f
                    );

                    // Random scale variation
                    float scale = 0.5f + rng.NextSingle() * 0.8f;
                    go.Transform.LocalScale = new Float3(scale, scale, scale);

                    Rigidbody3D rb = go.AddComponent<Rigidbody3D>();
                    rb.AffectedByGravity = true;

                    BoxCollider boxCollider1 = go.AddComponent<BoxCollider>();
                    boxCollider1.Size = Float3.One;

                    MeshRenderer mr = go.AddComponent<MeshRenderer>();
                    mr.Mesh = cubeMesh;
                    mr.Material = mat;

                    // Add spinner with randomized axis and speed
                    Spinner spinner = go.AddComponent<Spinner>();
                    spinner.Speed = 20f + rng.NextSingle() * 120f;
                    spinner.Axis = Float3.Normalize(new Float3(
                        rng.NextSingle() - 0.5f,
                        rng.NextSingle() - 0.5f,
                        rng.NextSingle() - 0.5f
                    ));

                    _scene.Add(go);
                    totalCubes++;
                }
            }
        }

        Debug.Log($"[RenderStressTest] Spawned {totalCubes} visible cubes in a {GridSize}x{GridSize}x{GridSize} grid.");
        Debug.Log($"[RenderStressTest] Each cube has a MeshRenderer + Spinner component.");

        Input.SetCursorVisible(false);
        Scene.Load(_scene);
        Application.IsPlaying = true;
    }

    public override void BeginUpdate()
    {
        _fpsFrameCount++;
        _fpsTimer += Time.DeltaTime;
        if (_fpsTimer >= 5.0f)
        {
            float avgFps = _fpsFrameCount / _fpsTimer;
            Debug.Log($"[RenderStressTest] Average FPS over last {_fpsTimer:F1}s: {avgFps:F1}");
            _fpsFrameCount = 0;
            _fpsTimer = 0f;
        }

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
