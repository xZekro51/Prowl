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
    private EnumArray<KeyCode, bool> _keyDownThisFrameWrite = new EnumArray<KeyCode, bool>();
    private EnumArray<KeyCode, bool> _keyUpThisFrameWrite = new EnumArray<KeyCode, bool>();
    private EnumArray<KeyCode, bool> _keyStateWrite = new EnumArray<KeyCode, bool>();
    private EnumArray<KeyCode, bool> _keyState = new EnumArray<KeyCode, bool>();
    private EnumArray<KeyCode, bool> _keyDownThisFrame = new EnumArray<KeyCode, bool>();
    private EnumArray<KeyCode, bool> _keyUpThisFrame = new EnumArray<KeyCode, bool>();
    private List<(int index, bool isDown)> _pendingKeyEvents = new();


    // ── State: Mouse ───────────────────────────────────────────
    private EnumArray<MouseButton, bool> _mouseDownThisFrameWrite = new EnumArray<MouseButton, bool>();
    private EnumArray<MouseButton, bool> _mouseUpThisFrameWrite = new EnumArray<MouseButton, bool>();
    private EnumArray<MouseButton, bool> _mouseStateWrite = new EnumArray<MouseButton, bool>();
    private EnumArray<MouseButton, bool> _mouseState = new EnumArray<MouseButton, bool>();
    private EnumArray<MouseButton, bool> _mouseDownThisFrame = new EnumArray<MouseButton, bool>();
    private EnumArray<MouseButton, bool> _mouseUpThisFrame = new EnumArray<MouseButton, bool>();
    private List<(int index, bool isDown)> _pendingMouseEvents = new();
    private Int2 _currentMousePos;
    private Int2 _prevMousePos;


    // ── State: Gamepad ─────────────────────────────────────────
    private EnumArray<GamepadButton, bool> _gamepadStateWrite = new EnumArray<GamepadButton, bool>();
    private EnumArray<GamepadButton, bool> _gamepadDownThisFrameWrite = new EnumArray<GamepadButton, bool>();
    private EnumArray<GamepadButton, bool> _gamepadUpThisFrameWrite = new EnumArray<GamepadButton, bool>();
    private EnumArray<GamepadButton, bool> _gamepadState = new EnumArray<GamepadButton, bool>();
    private EnumArray<GamepadButton, bool> _gamepadDownThisFrame = new EnumArray<GamepadButton, bool>();
    private EnumArray<GamepadButton, bool> _gamepadUpThisFrame = new EnumArray<GamepadButton, bool>();
    private List<(GamepadButton index, bool isDown)> _pendingGamepadEvents = new();


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
    public bool IsAnyKeyDown => _keyState.Any(pressed => pressed);

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
            gamepad.ButtonUp += SilkGamepad_ButtonUp;
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
        _mouseDownThisFrameWrite[buttonIndex] = true;
        _pendingMouseEvents.Add((buttonIndex, true));
    }
    private void SilkMouse_ButtonUp(IMouse mouse, Silk.NET.Input.MouseButton button)
    {
        int buttonIndex = (int)button;
        _mouseStateWrite[buttonIndex] = false;
        _mouseUpThisFrameWrite[buttonIndex] = true;
        _pendingMouseEvents.Add((buttonIndex, false));
    }
    private void SilkGamepad_ButtonDown(IGamepad gamepad, Silk.NET.Input.Button button)
    {
        int buttonIndex = (int)button.Name;

        _gamepadStateWrite[buttonIndex] = true;
        _gamepadDownThisFrameWrite[buttonIndex] = true;
        _pendingGamepadEvents.Add(((GamepadButton)button.Name, true));

    }

    private void SilkGamepad_ButtonUp(IGamepad gamepad, Silk.NET.Input.Button button)
    {
        int buttonIndex = (int)button.Name;

        _gamepadStateWrite[buttonIndex] = false;
        _gamepadUpThisFrameWrite[buttonIndex] = true;
        _pendingGamepadEvents.Add(((GamepadButton)button.Name, false));

    }

    private void SilkKeyboard_KeyDown(IKeyboard keyboard, Silk.NET.Input.Key key, int keyCode)
    {

        _keyStateWrite[(int)key] = true;
        _keyDownThisFrameWrite[(int)key] = true;
        _pendingKeyEvents.Add(((int)key, true));

    }
    private void SilkKeyboard_KeyUp(IKeyboard keyboard, Silk.NET.Input.Key key, int keyCode)
    {

        _keyStateWrite[(int)key] = false;
        _keyUpThisFrameWrite[(int)key] = true;
        _pendingKeyEvents.Add(((int)key, false));

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
        EnumArray<KeyCode, bool>.Swap(ref _keyUpThisFrame, ref _keyUpThisFrameWrite);
        _keyUpThisFrameWrite.Clear();

        EnumArray<KeyCode, bool>.Swap(ref _keyDownThisFrame, ref _keyDownThisFrameWrite);
        _keyDownThisFrameWrite.Clear();

        _keyStateWrite.CopyTo(_keyState);

        foreach ((int index, bool isDown) in _pendingKeyEvents)
        {
            OnKeyEvent?.Invoke((KeyCode)index, isDown);
        }
        _pendingKeyEvents.Clear();
    }
    private void SwapMouseBuffers()
    {
        EnumArray<MouseButton, bool>.Swap(ref _mouseDownThisFrame, ref _mouseDownThisFrameWrite);
        _mouseDownThisFrameWrite.Clear();

        EnumArray<MouseButton, bool>.Swap(ref _mouseUpThisFrame, ref _mouseUpThisFrameWrite);
        _mouseUpThisFrameWrite.Clear();

        _mouseStateWrite.CopyTo(_mouseState);

        foreach ((int index, bool isDown) in _pendingMouseEvents)
        {
            OnMouseEvent?.Invoke((MouseButton)index, MousePosition.X, MousePosition.Y, isDown, false);
        }
        _pendingMouseEvents.Clear();
    }
    private void SwapGamepadBuffers()
    {
        EnumArray<GamepadButton, bool>.Swap(ref _gamepadDownThisFrame, ref _gamepadDownThisFrameWrite);
        _gamepadDownThisFrameWrite.Clear();

        EnumArray<GamepadButton, bool>.Swap(ref _gamepadUpThisFrame, ref _gamepadUpThisFrameWrite);
        _gamepadUpThisFrameWrite.Clear();

        _gamepadStateWrite.CopyTo(_gamepadState);

        //foreach ((GamepadButton index, bool isDown) in _pendingGamepadEvents)
        //{
        //}
        _pendingGamepadEvents.Clear();
    }
    private void UpdateMousePosition()
    {
        _prevMousePos = _currentMousePos;
        _currentMousePos = (Int2)(Float2)Mice[0].Position;
        if (!_prevMousePos.Equals(_currentMousePos))
        {
            if (_mouseState[MouseButton.Left])
                OnMouseEvent?.Invoke(MouseButton.Left, MousePosition.X, MousePosition.Y, false, true);
            else if (_mouseState[MouseButton.Right])
                OnMouseEvent?.Invoke(MouseButton.Right, MousePosition.X, MousePosition.Y, false, true);
            else if (_mouseState[MouseButton.Middle])
                OnMouseEvent?.Invoke(MouseButton.Middle, MousePosition.X, MousePosition.Y, false, true);
            else
                OnMouseEvent?.Invoke(MouseButton.Unknown, MousePosition.X, MousePosition.Y, false, true);
        }
    }


    // ── Public Query API: Keyboard ─────────────────────────────
    public char? GetPressedChar()
    {
        if (_pressedChars.TryDequeue(out char c))
            return c;
        return null;
    }
    public bool GetKey(KeyCode key) => _keyState[(int)key];
    public bool GetKeyDown(KeyCode key)
    {
        return _keyDownThisFrame[(int)key];
    }
    public bool GetKeyUp(KeyCode key) => _keyUpThisFrame[(int)key];


    // ── Public Query API: Mouse ────────────────────────────────
    public bool GetMouseButton(int button) => _mouseState[button];
    public bool GetMouseButtonDown(int button)
    {
        return _mouseDownThisFrame[button];// && !_mouseUpThisFrame[button];
    }
    public bool GetMouseButtonUp(int button) => _mouseUpThisFrame[button];
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

        if ((int)button < 0 || (int)button >= _gamepadState.Length) return false;

        return _gamepadState[button];
    }
    public bool GetGamepadButtonDown(int gamepadIndex, GamepadButton button)
    {
        if (!IsGamepadConnected(gamepadIndex))
            return false;
        if ((int)button < 0 || (int)button >= _gamepadDownThisFrame.Length) return false;
        return _gamepadDownThisFrame[button];
    }
    public bool GetGamepadButtonUp(int gamepadIndex, GamepadButton button)
    {
        if (!IsGamepadConnected(gamepadIndex))
            return false;
        if ((int)button < 0 || (int)button >= _gamepadUpThisFrame.Length) return false;
        return _gamepadUpThisFrame[button];
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
