// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Prowl.Runtime.Utils;
using Prowl.Vector;

using Silk.NET.Input;

using Vortex;

namespace Prowl.Runtime;

public class SilkInputHandler : IInputHandler, IDisposable
{
    // ── Constants ──────────────────────────────────────────────


    // ── State: Keyboard ────────────────────────────────────────
    private EnumArray<KeyCode, bool> _keyStateWrite = new EnumArray<KeyCode, bool>();
    private EnumArray<KeyCode, bool> _keyStateCurrent = new EnumArray<KeyCode, bool>();
    private EnumArray<KeyCode, bool> _keyStatePrevious = new EnumArray<KeyCode, bool>();
    private SortedSet<int> _pendingKeyIndices = new();


    // ── State: Mouse ───────────────────────────────────────────
    private EnumArray<MouseButton, bool> _mouseStateWrite = new EnumArray<MouseButton, bool>();
    private EnumArray<MouseButton, bool> _mouseStateCurrent = new EnumArray<MouseButton, bool>();
    private EnumArray<MouseButton, bool> _mouseStatePrevious = new EnumArray<MouseButton, bool>();
    private SortedSet<int> _pendingMouseButtonIndices = new();
    private Int2 _currentMousePos;
    private Int2 _prevMousePos;


    // ── State: Gamepad ─────────────────────────────────────────
    private EnumArray<GamepadButton, bool> _gamepadStateWrite = new EnumArray<GamepadButton, bool>();
    private EnumArray<GamepadButton, bool> _gamepadStateCurrent = new EnumArray<GamepadButton, bool>();
    private EnumArray<GamepadButton, bool> _gamepadStatePrevious = new EnumArray<GamepadButton, bool>();
    private SortedSet<int> _pendingGamepadButtonIndices = new();


    // ── State: Text Input ──────────────────────────────────────
    private readonly Queue<char> _pressedChars = new();

    public event Action<KeyCode, bool> OnKeyEvent;
    public event Action<MouseButton, float, float, bool, bool> OnMouseEvent;


    // ── Public Properties ──────────────────────────────────────
    public IInputContext Context { get; init; }
    public string Clipboard
    {
        get => Context.Keyboards[0].ClipboardText;
        set
        {
            Context.Keyboards[0].ClipboardText = value;
        }
    }
    public IReadOnlyList<IKeyboard> Keyboards => Context.Keyboards;
    public IReadOnlyList<IMouse> Mice => Context.Mice;
    public IReadOnlyList<IJoystick> Joysticks => Context.Joysticks;
    public Int2 PrevMousePosition => _prevMousePos;
    public Int2 MousePosition
    {
        get => _currentMousePos;
        set
        {
            _prevMousePos = value;
            _currentMousePos = value;
            Mice[0].Position = (Float2)value;
        }
    }
    public Float2 MouseDelta
    {
        get
        {
            Int2 delta = _currentMousePos - _prevMousePos;
            return new Float2(delta.X, delta.Y); // Invert Y to match gamepad (up = positive)
        }
    }
    public float MouseWheelDelta => Mice[0].ScrollWheels[0].Y;
    public bool IsAnyKeyDown => _keyStateCurrent.Any(pressed => pressed);

    // ── Constructor ────────────────────────────────────────────
    public SilkInputHandler(IInputContext context)
    {
        Context = context;

        _prevMousePos = (Int2)(Float2)Mice[0].Position;
        _currentMousePos = (Int2)(Float2)Mice[0].Position;

        foreach (IKeyboard keyboard in Keyboards)
            keyboard.KeyChar += (keyboard, c) => _pressedChars.Enqueue(c);

        SubscribeSilkKeyboardEvents();
        SubscribeSilkGamepadEvents();
        SubscribeSilkMouseEvents();
    }


    // ── Initialization (subscribe to events) ──────────
    private void SubscribeSilkKeyboardEvents()
    {
        foreach (IKeyboard keyboard in Keyboards)
        {
            keyboard.KeyDown += SilkKeyboard_KeyDown;
            keyboard.KeyUp += SilkKeyboard_KeyUp;
        }
    }
    private void SubscribeSilkGamepadEvents()
    {
        foreach (IGamepad gamepad in Context.Gamepads)
        {
            gamepad.ButtonDown += SilkGamepad_ButtonDown;
        }
        foreach (IJoystick joystick in Context.Joysticks)
        {
            joystick.ButtonDown += (joystick, button) =>
            {
                Console.WriteLine("Joystick button down: " + button.Name + " on joystick " + joystick.Index);
            };
        }
    }
    private void SubscribeSilkMouseEvents()
    {
        foreach (IMouse mouse in Mice)
        {
            mouse.MouseDown += SilkMouse_ButtonDown;
            mouse.MouseUp += SilkMouse_ButtonUp;
        }
    }


    // ── Silk.NET → Skirk Bridge Callbacks (private) ────────────
    private void SilkMouse_ButtonDown(IMouse mouse, Silk.NET.Input.MouseButton button)
    {
        int buttonIndex = (int)button;
        _mouseStateWrite[buttonIndex] = true;
        _pendingMouseButtonIndices.Add(buttonIndex);
    }
    private void SilkMouse_ButtonUp(IMouse mouse, Silk.NET.Input.MouseButton button)
    {
    }
    private void SilkGamepad_ButtonDown(IGamepad gamepad, Silk.NET.Input.Button button)
    {
        int buttonIndex = (int)button.Name;
        if (buttonIndex >= 0 && buttonIndex < _gamepadStateWrite.Length)
        {
            _gamepadStateWrite[buttonIndex] = true;
            _pendingGamepadButtonIndices.Add(buttonIndex);
        }
    }
    private void SilkKeyboard_KeyDown(IKeyboard keyboard, Silk.NET.Input.Key key, int keyCode)
    {
        if ((int)key >= 0 && (int)key < _keyStateWrite.Length)
        {
            _keyStateWrite[(int)key] = true;
            _pendingKeyIndices.Add((int)key);
        }
    }
    private void SilkKeyboard_KeyUp(IKeyboard keyboard, Silk.NET.Input.Key key, int keyCode)
    {
    }


    // ── Frame Update ───────────────────────────────────────────
    internal void LateUpdate()
    {
        UpdateMousePosition();
        SwapKeyboardBuffers();
        SwapMouseBuffers();
        SwapGamepadBuffers();
    }
    private void SwapKeyboardBuffers()
    {
        var temp = _keyStateCurrent;
        _keyStateCurrent = _keyStatePrevious;
        _keyStatePrevious = _keyStateWrite;
        _keyStateWrite = temp;
        _keyStateWrite.Clear();

        foreach (var key in _pendingKeyIndices)
        {
            OnKeyEvent?.Invoke((KeyCode)key, _keyStatePrevious[key]);
        }
        _pendingKeyIndices.Clear();
    }
    private void SwapMouseBuffers()
    {
        var temp = _mouseStateCurrent;
        _mouseStateCurrent = _mouseStatePrevious;
        _mouseStatePrevious = _mouseStateWrite;
        _mouseStateWrite = temp;
        _mouseStateWrite.Clear();

        foreach (var button in _pendingMouseButtonIndices)
        {
            OnMouseEvent?.Invoke((MouseButton)button, MousePosition.X, MousePosition.Y, _mouseStatePrevious[button], false);
        }
        _pendingMouseButtonIndices.Clear();
    }
    private void SwapGamepadBuffers()
    {
        var temp = _gamepadStateCurrent;
        _gamepadStateCurrent = _gamepadStatePrevious;
        _gamepadStatePrevious = _gamepadStateWrite;
        _gamepadStateWrite = temp;
        _gamepadStateWrite.Clear();

        //foreach (var button in _pendingGamepadButtonIndices)
        //{
        //    OnButtonEvent?.Invoke((ButtonName)button, _gamepadStatePrevious[button]);
        //}
        _pendingGamepadButtonIndices.Clear();
    }
    private void UpdateMousePosition()
    {
        _prevMousePos = _currentMousePos;
        _currentMousePos = (Int2)(Float2)Mice[0].Position;
    }


    // ── Public Query API: Keyboard ─────────────────────────────
    public char? GetPressedChar()
    {
        if (_pressedChars.TryDequeue(out char c))
            return c;
        return null;
    }
    public bool GetKey(KeyCode key) => _keyStatePrevious[(int)key];
    public bool GetKeyDown(KeyCode key)
    {
        return _keyStatePrevious[(int)key] && !_keyStateCurrent[(int)key];
    }
    public bool GetKeyUp(KeyCode key) => !_keyStatePrevious[(int)key] && _keyStateCurrent[(int)key];


    // ── Public Query API: Mouse ────────────────────────────────
    public bool GetMouseButton(int button) => _mouseStatePrevious[button];
    public bool GetMouseButtonDown(int button)
    {
        return _mouseStatePrevious[button] && !_mouseStateCurrent[button];
    }
    public bool GetMouseButtonUp(int button) => !_mouseStatePrevious[button] && _mouseStateCurrent[button];
    public void SetCursorVisible(bool visible, int miceIndex = 0) => Mice[miceIndex].Cursor.CursorMode = visible ? CursorMode.Normal : CursorMode.Disabled;


    // ── Public Query API: Gamepad ──────────────────────────────
    public int GetGamepadCount() => Context.Gamepads.Count;
    public bool IsGamepadConnected(int gamepadIndex)
    {
        return gamepadIndex >= 0 && gamepadIndex < Context.Gamepads.Count && Context.Gamepads[gamepadIndex].IsConnected;
    }
    public bool GetGamepadButton(int gamepadIndex, GamepadButton button)
    {
        if (!IsGamepadConnected(gamepadIndex))
            return false;

        if ((int)button < 0 || (int)button >= _gamepadStatePrevious.Length) return false;

        return _gamepadStatePrevious[button];
    }
    public bool GetGamepadButtonDown(int gamepadIndex, GamepadButton button)
    {
        if (!IsGamepadConnected(gamepadIndex))
            return false;
        if ((int)button < 0 || (int)button >= _gamepadStatePrevious.Length) return false;
        return _gamepadStatePrevious[button] && !_gamepadStateCurrent[button];
    }
    public bool GetGamepadButtonUp(int gamepadIndex, GamepadButton button)
    {
        if (!IsGamepadConnected(gamepadIndex))
            return false;
        if ((int)button < 0 || (int)button >= _gamepadStatePrevious.Length) return false;
        return !_gamepadStatePrevious[button] && _gamepadStateCurrent[button];
    }
    public Vector2 GetGamepadAxis(int gamepadIndex, int axisIndex)
    {
        if (!IsGamepadConnected(gamepadIndex))
            return Vector2.Zero;

        IGamepad gamepad = Context.Gamepads[gamepadIndex];
        if (axisIndex < 0 || axisIndex >= gamepad.Thumbsticks.Count)
            return Vector2.Zero;

        Thumbstick thumbstick = gamepad.Thumbsticks[axisIndex];
        return new Vector2(thumbstick.X, thumbstick.Y); // We flip y to make UP on the stick positive
    }
    public float GetGamepadTrigger(int gamepadIndex, int triggerIndex)
    {
        if (!IsGamepadConnected(gamepadIndex))
            return 0.0f;

        IGamepad gamepad = Context.Gamepads[gamepadIndex];
        if (triggerIndex < 0 || triggerIndex >= gamepad.Triggers.Count)
            return 0.0f;

        return gamepad.Triggers[triggerIndex].Position;
    }
    public void SetGamepadVibration(int gamepadIndex, float leftMotor, float rightMotor)
    {
        if (!IsGamepadConnected(gamepadIndex))
            return;

        IGamepad gamepad = Context.Gamepads[gamepadIndex];
        if (gamepad.VibrationMotors.Count >= 2)
        {
            gamepad.VibrationMotors[0].Speed = (float)leftMotor;
            gamepad.VibrationMotors[1].Speed = (float)rightMotor;
        }
    }
    Float2 IInputHandler.GetGamepadAxis(int gamepadIndex, int axisIndex) => GetGamepadAxis(gamepadIndex, axisIndex);


    // ── IDisposable ────────────────────────────────────────────
    public void Dispose()
    {
        Context.Dispose();
    }

}
