// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

//
// TextDemo — SDF Text Rendering Showcase
//
// Demonstrates the TextRenderer component and text pipeline:
// - World-space 3D text at various sizes and distances
// - Rich text tags (<b>, <i>, <color>, <size>, <u>, <s>, etc.)
// - SDF outline and drop shadow (underlay)
// - Animated text effects (wave, typewriter via rich tags)
// - Multiple TextRenderers for stress testing
// - Alignment and overflow modes
//
// Controls:
//   Movement:  WASD / Arrow Keys / Gamepad Left Stick
//   Look:      Mouse Move (when RMB held) / Gamepad Right Stick
//   Fly Up:    E / Gamepad A Button
//   Fly Down:  Q / Gamepad B Button
//   Sprint:    Left Shift / Gamepad Left Stick Click
//

using System.Collections.Generic;

using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Text;
using Prowl.Runtime.Text.Effects;
using Prowl.Vector;

using MouseButton = Prowl.Runtime.MouseButton;

namespace TextDemo;

internal class Program
{
    static void Main(string[] args)
    {
        new TextDemoGame().Run("Text Rendering Demo", 1920, 1080);
    }
}

public sealed class TextDemoGame : Game
{
    private GameObject? _cameraGO;
    private Scene? _scene;

    // Input Actions
    private InputActionMap _cameraMap = null!;
    private InputAction _moveAction = null!;
    private InputAction _lookAction = null!;
    private InputAction _lookEnableAction = null!;
    private InputAction _flyUpAction = null!;
    private InputAction _flyDownAction = null!;
    private InputAction _sprintAction = null!;

    // Stress test
    private readonly List<TextRenderer> _stressRenderers = [];

    public override void Initialize()
    {
        _scene = new Scene();
        SetupInputActions();

        // ── Lighting ────────────────────────────────────────────
        GameObject lightGO = new("Directional Light");
        lightGO.AddComponent<DirectionalLight>();
        lightGO.Transform.LocalEulerAngles = new Float3(-50, 30, 0);
        _scene.Add(lightGO);

        // ── Camera ──────────────────────────────────────────────
        _cameraGO = new("Main Camera");
        _cameraGO.Tag = "Main Camera";
        _cameraGO.Transform.Position = new(0, 2, -6);
        Camera camera = _cameraGO.AddComponent<Camera>();
        camera.Depth = -1;
        camera.HDR = true;
        camera.Effects =
        [
            new FXAAEffect(),
            new TonemapperEffect(),
        ];
        _scene.Add(_cameraGO);

        // ── Ground Plane ────────────────────────────────────────
        GameObject groundGO = new("Ground");
        MeshRenderer groundMR = groundGO.AddComponent<MeshRenderer>();
        groundMR.Mesh = Mesh.CreateCube(Float3.One);
        groundMR.Material = new Material(Shader.LoadDefault(DefaultShader.Standard));
        groundGO.Transform.Position = new(0, -1, 0);
        groundGO.Transform.LocalScale = new(30, 0.2f, 30);
        _scene.Add(groundGO);

        // Create a procedural FontAsset for the demo.
        // In a real project this would come from the editor's FontAssetImporter.
        FontAsset font = CreateProceduralFont();

        // ── 1. Basic world-space text ───────────────────────────
        CreateTextObject("Basic Text", font,
            "Hello, Prowl Engine!",
            fontSize: 1f, position: new(-6, 3, 0));

        // ── 2. Different sizes ──────────────────────────────────
        CreateTextObject("Small Text", font,
            "Small (0.4)",
            fontSize: 0.4f, position: new(-6, 2, 0));

        CreateTextObject("Medium Text", font,
            "Medium (0.7)",
            fontSize: 0.7f, position: new(-6, 1.2f, 0));

        CreateTextObject("Large Text", font,
            "Large (1.5)",
            fontSize: 1.5f, position: new(-6, 0, 0));

        // ── 3. Rich text tags ───────────────────────────────────
        CreateTextObject("Rich Text", font,
            "<b>Bold</b> and <i>Italic</i> and <u>Underline</u>",
            fontSize: 0.8f, position: new(0, 3, 0));

        CreateTextObject("Colored Text", font,
            "<color=#FF4444>Red</color> <color=#44FF44>Green</color> <color=#4444FF>Blue</color>",
            fontSize: 0.8f, position: new(0, 2, 0));

        CreateTextObject("Sized Text", font,
            "Normal <size=48>Big</size> <size=12>Tiny</size> Normal",
            fontSize: 0.6f, position: new(0, 1, 0));

        CreateTextObject("Strikethrough", font,
            "<s>Deleted text</s> Kept text",
            fontSize: 0.7f, position: new(0, 0, 0));

        // ── 4. Outline and Shadow ───────────────────────────────
        TextRenderer outlineTR = CreateTextObject("Outline Text", font,
            "Outlined Text",
            fontSize: 1.2f, position: new(6, 3, 0));
        outlineTR.OutlineWidth = 0.15f;
        outlineTR.OutlineColor = new Color(0.2f, 0.2f, 0.8f, 1f);

        TextRenderer shadowTR = CreateTextObject("Shadow Text", font,
            "Drop Shadow",
            fontSize: 1.2f, position: new(6, 1.5f, 0));
        shadowTR.UnderlayColor = new Color(0f, 0f, 0f, 0.6f);
        shadowTR.UnderlayOffset = new Float2(0.05f, 0.05f);
        shadowTR.UnderlayDilate = 0.1f;
        shadowTR.UnderlaySoftness = 0.2f;

        // ── 5. Animated effects via rich text tags ──────────────
        CreateTextObject("Wave Effect", font,
            "<wave>Wavy text animation!</wave>",
            fontSize: 0.8f, position: new(-3, -2, 0));

        CreateTextObject("Typewriter Effect", font,
            "<typewriter>This text types itself out over time...</typewriter>",
            fontSize: 0.6f, position: new(3, -2, 0));

        // ── 6. Alignment showcase ───────────────────────────────
        TextRenderer leftTR = CreateTextObject("Left Aligned", font,
            "Left\nAligned\nText",
            fontSize: 0.5f, position: new(-6, -4, 0));
        leftTR.Alignment = TextAlignment.Left;
        leftTR.RectSize = new Float2(6f, 4f);
        leftTR.Overflow = TextOverflowMode.WordWrap;

        TextRenderer centerTR = CreateTextObject("Center Aligned", font,
            "Center\nAligned\nText",
            fontSize: 0.5f, position: new(0, -4, 0));
        centerTR.Alignment = TextAlignment.Center;
        centerTR.RectSize = new Float2(6f, 4f);
        centerTR.Overflow = TextOverflowMode.WordWrap;

        TextRenderer rightTR = CreateTextObject("Right Aligned", font,
            "Right\nAligned\nText",
            fontSize: 0.5f, position: new(6, -4, 0));
        rightTR.Alignment = TextAlignment.Right;
        rightTR.RectSize = new Float2(6f, 4f);
        rightTR.Overflow = TextOverflowMode.WordWrap;

        // ── 7. Overflow modes ───────────────────────────────────
        CreateOverflowDemo(font, "Word Wrap", TextOverflowMode.WordWrap, new(-6, -7, 0));
        CreateOverflowDemo(font, "Truncate", TextOverflowMode.Truncate, new(0, -7, 0));
        CreateOverflowDemo(font, "Ellipsis", TextOverflowMode.Ellipsis, new(6, -7, 0));

        // ── 8. Stress test — many text objects ──────────────────
        CreateStressTestGrid(font, origin: new(-10, 6, 8), rows: 10, cols: 10);

        DrawGizmos = false;
        Scene.Load(_scene);
    }

    private FontAsset CreateProceduralFont()
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();

        // Build simple ASCII glyph data (monospaced).
        // A real font would come from the editor's FontAssetImporter with
        // proper SDF atlas generation. This procedural font enables the sample
        // to run standalone without shipping a .ttf.
        int atlasWidth = 320;
        int atlasHeight = 192;
        float pxRange = 4f;

        GlyphData[] glyphs = new GlyphData[128];
        Dictionary<uint, int> charTable = [];

        for (uint c = 32; c < 128; c++)
        {
            int idx = (int)c;
            glyphs[idx] = new GlyphData(
                GlyphIndex: c,
                Width: 12f,
                Height: 20f,
                BearingX: 1f,
                BearingY: 18f,
                Advance: 14f,
                AtlasX: (c % 16) * 20f,
                AtlasY: (c / 16) * 24f,
                AtlasWidth: 14f,
                AtlasHeight: 22f,
                Scale: 1f);
            charTable[c] = idx;
        }

        // Space glyph — no visible area
        glyphs[32] = new GlyphData(32, 0, 0, 0, 0, 14f, 0, 0, 0, 0, 1f);

        font.SetMetrics(
            pointSize: 32f,
            lineHeight: 40f,
            ascender: 30f,
            descender: -10f,
            baseline: 0f);

        font.SetGlyphData(glyphs, charTable, []);

        // Generate a procedural MSDF atlas with rectangular glyph shapes
        Texture2D atlas = CreateProceduralAtlas(atlasWidth, atlasHeight, pxRange, glyphs);
        font.SetAtlas(atlas, atlasWidth, atlasHeight, AtlasType.MSDF, pxRange, 1);

        return font;
    }

    /// <summary>
    /// Generates a procedural MSDF atlas texture with a built-in 5x7 bitmap font.
    /// Each glyph cell contains a signed distance field computed from the character bitmap:
    /// 0.5 = boundary, &gt;0.5 = inside, &lt;0.5 = outside.
    /// </summary>
    private static Texture2D CreateProceduralAtlas(int atlasWidth, int atlasHeight, float pxRange, GlyphData[] glyphs)
    {
        byte[] pixels = new byte[atlasWidth * atlasHeight * 4];

        // Background: A = 255, RGB = 0 (fully "outside")
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        // 5x7 bitmap font for ASCII 33-126 (94 chars × 7 rows = 658 bytes).
        // Each byte encodes one row: bit 4 = leftmost pixel, bit 0 = rightmost.
        ReadOnlySpan<byte> font =
        [
            // !        "        #        $        %        &        '
            4,4,4,4,0,4,0,  10,10,0,0,0,0,0,  10,31,10,31,10,0,0,  4,15,20,14,5,30,4,
            24,25,2,4,8,19,3,  12,18,20,8,21,18,13,  4,4,0,0,0,0,0,
            // (        )        *        +        ,        -        .        /
            2,4,8,8,8,4,2,  8,4,2,2,2,4,8,  0,10,4,31,4,10,0,  0,4,4,31,4,4,0,
            0,0,0,0,4,4,8,  0,0,0,31,0,0,0,  0,0,0,0,0,4,0,  1,2,4,8,16,0,0,
            // 0        1        2        3        4        5        6        7        8        9
            14,17,19,21,25,17,14,  4,12,4,4,4,4,14,  14,17,1,6,8,16,31,  14,17,1,6,1,17,14,
            2,6,10,18,31,2,2,  31,16,30,1,1,17,14,  6,8,16,30,17,17,14,  31,1,2,4,8,8,8,
            14,17,17,14,17,17,14,  14,17,17,15,1,2,12,
            // :        ;        <        =        >        ?        @
            0,0,4,0,4,0,0,  0,0,4,0,4,4,8,  2,4,8,16,8,4,2,  0,0,31,0,31,0,0,
            8,4,2,1,2,4,8,  14,17,1,6,4,0,4,  14,17,23,21,22,16,14,
            // A        B        C        D        E        F        G
            4,10,17,17,31,17,17,  30,17,17,30,17,17,30,  14,17,16,16,16,17,14,
            28,18,17,17,17,18,28,  31,16,16,30,16,16,31,  31,16,16,30,16,16,16,
            14,17,16,19,17,17,14,
            // H        I        J        K        L        M        N
            17,17,17,31,17,17,17,  14,4,4,4,4,4,14,  7,2,2,2,2,18,12,
            17,18,20,24,20,18,17,  16,16,16,16,16,16,31,  17,27,21,21,17,17,17,
            17,25,25,21,19,19,17,
            // O        P        Q        R        S        T        U
            14,17,17,17,17,17,14,  30,17,17,30,16,16,16,  14,17,17,17,21,18,13,
            30,17,17,30,20,18,17,  14,17,16,14,1,17,14,  31,4,4,4,4,4,4,
            17,17,17,17,17,17,14,
            // V        W        X        Y        Z
            17,17,17,10,10,4,4,  17,17,17,21,21,21,10,  17,17,10,4,10,17,17,
            17,17,10,4,4,4,4,  31,1,2,4,8,16,31,
            // [        \        ]        ^        _        `
            12,8,8,8,8,8,12,  16,8,4,2,1,0,0,  6,2,2,2,2,2,6,
            4,10,17,0,0,0,0,  0,0,0,0,0,0,31,  8,4,2,0,0,0,0,
            // a        b        c        d        e        f        g
            0,0,14,1,15,17,15,  16,16,30,17,17,17,30,  0,0,14,17,16,17,14,
            1,1,15,17,17,17,15,  0,0,14,17,31,16,14,  6,8,8,28,8,8,8,
            0,15,17,17,15,1,14,
            // h        i        j        k        l        m        n
            16,16,30,17,17,17,17,  4,0,12,4,4,4,14,  2,0,6,2,2,18,12,
            16,16,18,20,24,20,18,  12,4,4,4,4,4,14,  0,0,26,21,21,17,17,
            0,0,30,17,17,17,17,
            // o        p        q        r        s        t        u
            0,0,14,17,17,17,14,  0,30,17,17,30,16,16,  0,15,17,17,15,1,1,
            0,0,22,25,16,16,16,  0,0,14,16,14,1,14,  8,8,28,8,8,8,6,
            0,0,17,17,17,17,14,
            // v        w        x        y        z
            0,0,17,17,17,10,4,  0,0,17,17,21,21,10,  0,0,17,10,4,10,17,
            0,0,17,17,15,1,14,  0,0,31,2,4,8,31,
            // {        |        }        ~
            6,4,4,8,4,4,6,  4,4,4,4,4,4,4,  12,4,4,2,4,4,12,  0,0,8,21,2,0,0,
        ];

        const int bmCols = 5;
        const int bmRows = 7;

        // Scratch arrays for filled-cell rectangles (max 35 per glyph)
        float[] rx0 = new float[bmCols * bmRows];
        float[] ry0 = new float[bmCols * bmRows];
        float[] rx1 = new float[bmCols * bmRows];
        float[] ry1 = new float[bmCols * bmRows];

        for (uint c = 33; c < 127; c++)
        {
            GlyphData glyph = glyphs[(int)c];
            if (glyph.AtlasWidth <= 0 || glyph.AtlasHeight <= 0)
                continue;

            int cellX = (int)glyph.AtlasX;
            int cellY = (int)glyph.AtlasY;
            int cellW = (int)glyph.AtlasWidth;
            int cellH = (int)glyph.AtlasHeight;

            // Usable glyph area within atlas cell (1px SDF padding on each side)
            float usableX = cellX + 1f;
            float usableY = cellY + 1f;
            float usableW = cellW - 2f;  // 12
            float usableH = cellH - 2f;  // 20

            // Collect "on" bitmap cells as rectangles in atlas space
            int fontOffset = (int)(c - 33) * bmRows;
            int onCount = 0;

            for (int by = 0; by < bmRows; by++)
            {
                byte row = font[fontOffset + by];
                for (int bx = 0; bx < bmCols; bx++)
                {
                    if ((row & (1 << (4 - bx))) != 0)
                    {
                        rx0[onCount] = usableX + bx * usableW / bmCols;
                        ry0[onCount] = usableY + by * usableH / bmRows;
                        rx1[onCount] = usableX + (bx + 1) * usableW / bmCols;
                        ry1[onCount] = usableY + (by + 1) * usableH / bmRows;
                        onCount++;
                    }
                }
            }

            // Compute SDF for each pixel in the atlas cell
            for (int py = cellY; py < cellY + cellH && py < atlasHeight; py++)
            {
                for (int px = cellX; px < cellX + cellW && px < atlasWidth; px++)
                {
                    float x = px + 0.5f;
                    float y = py + 0.5f;

                    // Signed distance = max across all filled rectangles
                    // (union SDF: positive inside, negative outside)
                    float sd = -pxRange;

                    for (int i = 0; i < onCount; i++)
                    {
                        float dx = MathF.Max(rx0[i] - x, x - rx1[i]);
                        float dy = MathF.Max(ry0[i] - y, y - ry1[i]);

                        float rectSd;
                        if (dx <= 0f && dy <= 0f)
                            rectSd = -MathF.Max(dx, dy);  // Inside rectangle
                        else if (dx > 0f && dy > 0f)
                            rectSd = -MathF.Sqrt(dx * dx + dy * dy);  // Outside at corner
                        else
                            rectSd = -MathF.Max(dx, dy);  // Outside along edge

                        if (rectSd > sd)
                            sd = rectSd;
                    }

                    float mapped = Math.Clamp(0.5f + sd / pxRange, 0f, 1f);
                    byte val = (byte)(mapped * 255f);

                    int idx = (py * atlasWidth + px) * 4;
                    pixels[idx + 0] = val;  // R
                    pixels[idx + 1] = val;  // G
                    pixels[idx + 2] = val;  // B
                    pixels[idx + 3] = 255;  // A
                }
            }
        }

        Texture2D texture = new Texture2D(
            (uint)atlasWidth, (uint)atlasHeight,
            false, TextureImageFormat.Color4b);
        texture.SetData<byte>(pixels.AsMemory());
        return texture;
    }

    private TextRenderer CreateTextObject(string name, FontAsset font, string text,
        float fontSize, Float3 position)
    {
        GameObject go = new(name);
        TextRenderer tr = go.AddComponent<TextRenderer>();
        tr.Font = font;
        tr.Text = text;
        tr.FontSize = fontSize;
        tr.Color = Color.White;
        tr.RichText = true;
        go.Transform.Position = position;
        _scene!.Add(go);
        return tr;
    }

    private void CreateOverflowDemo(FontAsset font, string label, TextOverflowMode mode, Float3 position)
    {
        string longText = $"{label}: The quick brown fox jumps over the lazy dog repeatedly.";
        TextRenderer tr = CreateTextObject($"Overflow_{label}", font, longText,
            fontSize: 0.4f, position: position);
        tr.Overflow = mode;
        tr.RectSize = new Float2(4f, 1.5f);
    }

    private void CreateStressTestGrid(FontAsset font, Float3 origin, int rows, int cols)
    {
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                Float3 pos = origin + new Float3(c * 2f, -r * 1.2f, 0);
                TextRenderer tr = CreateTextObject($"Stress_{r}_{c}", font,
                    $"[{r},{c}]",
                    fontSize: 0.3f, position: pos);
                _stressRenderers.Add(tr);
            }
        }
    }

    private void SetupInputActions()
    {
        _cameraMap = new InputActionMap("Camera");

        // Movement (WASD + Gamepad)
        _moveAction = _cameraMap.AddAction("Move", InputActionType.Value);
        _moveAction.ExpectedValueType = typeof(Float2);
        _moveAction.AddBinding(new Vector2CompositeBinding(
            InputBinding.CreateKeyBinding(KeyCode.W),
            InputBinding.CreateKeyBinding(KeyCode.S),
            InputBinding.CreateKeyBinding(KeyCode.A),
            InputBinding.CreateKeyBinding(KeyCode.D),
            true
        ));
        var leftStick = InputBinding.CreateGamepadAxisBinding(0, deviceIndex: 0);
        leftStick.Processors.Add(new DeadzoneProcessor(0.15f));
        leftStick.Processors.Add(new NormalizeProcessor());
        _moveAction.AddBinding(leftStick);

        // Look enable (RMB)
        _lookEnableAction = _cameraMap.AddAction("LookEnable", InputActionType.Button);
        _lookEnableAction.AddBinding(MouseButton.Right);

        // Look (Mouse + Gamepad)
        _lookAction = _cameraMap.AddAction("Look", InputActionType.Value);
        _lookAction.ExpectedValueType = typeof(Float2);
        var mouse = new DualAxisCompositeBinding(
            InputBinding.CreateMouseAxisBinding(0),
            InputBinding.CreateMouseAxisBinding(1));
        mouse.Processors.Add(new ScaleProcessor(0.1f));
        _lookAction.AddBinding(mouse);
        var rightStick = InputBinding.CreateGamepadAxisBinding(1, deviceIndex: 0);
        rightStick.Processors.Add(new DeadzoneProcessor(0.15f));
        rightStick.Processors.Add(new NormalizeProcessor());
        _lookAction.AddBinding(rightStick);

        // Fly Up/Down
        _flyUpAction = _cameraMap.AddAction("FlyUp", InputActionType.Button);
        _flyUpAction.AddBinding(KeyCode.E);
        _flyUpAction.AddBinding(GamepadButton.A);
        _flyDownAction = _cameraMap.AddAction("FlyDown", InputActionType.Button);
        _flyDownAction.AddBinding(KeyCode.Q);
        _flyDownAction.AddBinding(GamepadButton.B);

        // Sprint
        _sprintAction = _cameraMap.AddAction("Sprint", InputActionType.Button);
        _sprintAction.AddBinding(KeyCode.ShiftLeft);
        _sprintAction.AddBinding(GamepadButton.LeftStick);

        Input.RegisterActionMap(_cameraMap);
        _cameraMap.Enable();
    }

    public override void BeginUpdate()
    {
        if (_cameraGO == null)
            return;

        // Camera controls
        Float2 movement = _moveAction.ReadValue<Float2>();
        float speedMultiplier = _sprintAction.IsPressed() ? 5f : 2f;
        float moveSpeed = speedMultiplier * (float)Time.DeltaTime;

        _cameraGO.Transform.Position += _cameraGO.Transform.Forward * movement.Y * moveSpeed;
        _cameraGO.Transform.Position += _cameraGO.Transform.Right * movement.X * moveSpeed;

        float upDown = 0;
        if (_flyUpAction.IsPressed()) upDown += 1;
        if (_flyDownAction.IsPressed()) upDown -= 1;
        _cameraGO.Transform.Position += Float3.UnitY * upDown * moveSpeed;

        Float2 lookInput = _lookAction.ReadValue<Float2>();
        if (_lookEnableAction.IsPressed() || Maths.Abs(lookInput.X) > 0.01f || Maths.Abs(lookInput.Y) > 0.01f)
        {
            _cameraGO.Transform.LocalEulerAngles += new Float3(lookInput.Y, lookInput.X, 0);
        }

        if (Input.GetKeyDown(KeyCode.Escape))
            Input.SetCursorVisible(true);
    }
}
