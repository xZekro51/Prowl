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


public class InputStateBuffer<TEnum,TInput> where TEnum : struct, Enum where TInput : IInputDevice
{
    private int _length;
    public int Length
    {
        get { return _length; }
        private set
        {
            _length = value;
        }
    }

    private struct BufferSet
    {
        public EnumArray<TEnum, bool> State;
        public EnumArray<TEnum, bool> DownThisFrame;
        public EnumArray<TEnum, bool> UpThisFrame;

        public BufferSet()
        {
            State = new EnumArray<TEnum, bool>();
            DownThisFrame = new EnumArray<TEnum, bool>();
            UpThisFrame = new EnumArray<TEnum, bool>();
        }

        public void Clear()
        {
            State.Clear();
            DownThisFrame.Clear();
            UpThisFrame.Clear();
        }

        public readonly void ClearTransient()
        {
            DownThisFrame.Clear();
            UpThisFrame.Clear();
        }
    }

    private IReadOnlyList<TInput> _devices;

    public readonly List<(int deviceIndex, TEnum enumIndex, bool isDown)> PendingEvents = [];


    private BufferSet _globalWrite;
    private BufferSet _globalRead;
    private BufferSet[] _deviceWrite;
    private BufferSet[] _deviceRead;

    public void InitializeArrays()
    {
        _globalWrite = new BufferSet();
        _globalRead = new BufferSet();

        _deviceWrite = new BufferSet[Length];
        _deviceRead = new BufferSet[Length];

        for (int i = 0; i < Length; i++)
        {
            _deviceWrite[i] = new BufferSet();
            _deviceRead[i] = new BufferSet();
        }
    }

    public bool GetState(int deviceIndex, int input) => _deviceRead[deviceIndex].State[input];
    public bool GetDownThisFrame(int deviceIndex, int input) => _deviceRead[deviceIndex].DownThisFrame[input];
    public bool GetUpThisFrame(int deviceIndex, int input) => _deviceRead[deviceIndex].UpThisFrame[input];


    public bool GetState(int deviceIndex, TEnum input) => _deviceRead[deviceIndex].State[input];
    public bool GetDownThisFrame(int deviceIndex, TEnum input) => _deviceRead[deviceIndex].DownThisFrame[input];
    public bool GetUpThisFrame(int deviceIndex, TEnum input) => _deviceRead[deviceIndex].UpThisFrame[input];
    public void WriteState(int deviceIndex, TEnum input, bool isDown)
    {
        _deviceWrite[deviceIndex].State[input] = isDown;
        _globalWrite.State[input] = isDown;

        if (isDown)
        {
            _deviceWrite[deviceIndex].DownThisFrame[input] = true;
            _globalWrite.DownThisFrame[input] = true;
        }
        else
        {
            _deviceWrite[deviceIndex].UpThisFrame[input] = true;
            _globalWrite.UpThisFrame[input] = true;
        }

    }

    public bool Any() => _globalRead.State.Any(pressed => pressed);


    public bool GetState(int input) => _globalRead.State[input];

    public bool GetDownThisFrame(int input) => _globalRead.DownThisFrame[input];

    public bool GetUpThisFrame(int input) => _globalRead.UpThisFrame[input];

    public bool GetState(TEnum input) => _globalRead.State[input];
    public bool GetDownThisFrame(TEnum input) => _globalRead.DownThisFrame[input];
    public bool GetUpThisFrame(TEnum input) => _globalRead.UpThisFrame[input];



    private void SwapAllBuffers()
    {
        for (int i = 0; i < Length; i++)
        {
            SwapBuffers(ref _deviceWrite[i], ref _deviceRead[i]);
        }

        SwapBuffers(ref _globalWrite, ref _globalRead);
    }

    private static void SwapBuffers(ref BufferSet write, ref BufferSet read)
    {
        write.State.CopyTo(read.State);

        EnumArray<TEnum, bool>.Swap(ref read.DownThisFrame, ref write.DownThisFrame);
        EnumArray<TEnum, bool>.Swap(ref read.UpThisFrame, ref write.UpThisFrame);

        write.ClearTransient();
    }

    public void Update()
    {
        int newLenth = _devices.Count;
        if (newLenth != Length)
        {
            Length = newLenth;
            InitializeArrays();
        }
        else
        {
            SwapAllBuffers();
        }
    }

    public InputStateBuffer(IReadOnlyList<TInput> devices)
    {
        _devices = devices;
        Length = devices.Count;
        InitializeArrays();
    }
}



public class SilkInputHandler : IInputHandler, IDisposable
{
    // ── Constants ──────────────────────────────────────────────


    // ── State: Keyboard ────────────────────────────────────────
    private InputStateBuffer<KeyCode, IKeyboard> _keyboardStateBuffer;


    // ── State: Mouse ───────────────────────────────────────────

    private InputStateBuffer<MouseButton, IMouse> _mouseStateBuffer;
    private Int2 _currentMousePos;
    private Int2 _prevMousePos;


    // ── State: Gamepad ─────────────────────────────────────────
    private InputStateBuffer<GamepadButton, IGamepad> _gamepadStateBuffer;


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
    public bool IsAnyKeyDown => _keyboardStateBuffer.Any();

    // ── Constructor ────────────────────────────────────────────
    public SilkInputHandler(IInputContext context)
    {
        Context = context;

        _mouseStateBuffer = new(Mice);
        _keyboardStateBuffer = new(Keyboards);
        _gamepadStateBuffer = new(Context.Gamepads);

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
                //Console.WriteLine("Joystick button down: " + button.Name + " on joystick " + joystick.Index);
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
        _mouseStateBuffer.WriteState(mouse.Index, (MouseButton)button, true);
        _mouseStateBuffer.PendingEvents.Add((mouse.Index, (MouseButton)button, true));
    }
    private void SilkMouse_ButtonUp(IMouse mouse, Silk.NET.Input.MouseButton button)
    {
        int buttonIndex = (int)button;
        _mouseStateBuffer.WriteState(mouse.Index, (MouseButton)button, false);
        _mouseStateBuffer.PendingEvents.Add((mouse.Index, (MouseButton)button, false));
    }
    private void SilkGamepad_ButtonDown(IGamepad gamepad, Silk.NET.Input.Button button)
    {
        _gamepadStateBuffer.WriteState(gamepad.Index, (GamepadButton)button.Name, true);
        _gamepadStateBuffer.PendingEvents.Add((gamepad.Index, (GamepadButton)button.Name, true));

    }

    private void SilkGamepad_ButtonUp(IGamepad gamepad, Silk.NET.Input.Button button)
    {
        _gamepadStateBuffer.WriteState(gamepad.Index, (GamepadButton)button.Name, false);
        _gamepadStateBuffer.PendingEvents.Add((gamepad.Index, (GamepadButton)button.Name, false));
    }

    private void SilkKeyboard_KeyDown(IKeyboard keyboard, Silk.NET.Input.Key key, int keyCode)
    {
        _keyboardStateBuffer.WriteState(keyboard.Index, (KeyCode)key, true);
        _keyboardStateBuffer.PendingEvents.Add((keyboard.Index, (KeyCode)key, true));

    }
    private void SilkKeyboard_KeyUp(IKeyboard keyboard, Silk.NET.Input.Key key, int keyCode)
    {

        _keyboardStateBuffer.WriteState(keyboard.Index, (KeyCode)key, false);
        _keyboardStateBuffer.PendingEvents.Add((keyboard.Index, (KeyCode)key, false));

    }


    // ── Frame Update ───────────────────────────────────────────
    internal void LateUpdate()
    {
        UpdateMousePosition();
        _keyboardStateBuffer.Update();
        _mouseStateBuffer.Update();
        _gamepadStateBuffer.Update();
        FireEvents();
    }

    private void FireEvents()
    {
        foreach ((int deviceIndex, KeyCode key, bool isDown) in _keyboardStateBuffer.PendingEvents)
        {
            OnKeyEvent?.Invoke(key, isDown);
        }
        _keyboardStateBuffer.PendingEvents.Clear();
        foreach ((int deviceIndex, MouseButton button, bool isDown) in _mouseStateBuffer.PendingEvents)
        {
            OnMouseEvent?.Invoke(button, MousePosition.X, MousePosition.Y, isDown, false);
        }
        _mouseStateBuffer.PendingEvents.Clear();
    }
    private void UpdateMousePosition()
    {
        _prevMousePos = _currentMousePos;
        _currentMousePos = (Int2)(Float2)Mice[0].Position;
        if (!_prevMousePos.Equals(_currentMousePos))
        {
            if (_mouseStateBuffer.GetState(MouseButton.Left))
                OnMouseEvent?.Invoke(MouseButton.Left, MousePosition.X, MousePosition.Y, false, true);
            else if (_mouseStateBuffer.GetState(MouseButton.Right))
                OnMouseEvent?.Invoke(MouseButton.Right, MousePosition.X, MousePosition.Y, false, true);
            else if (_mouseStateBuffer.GetState(MouseButton.Middle))
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
    public bool GetKey(KeyCode key) => _keyboardStateBuffer.GetState(key);
    public bool GetKeyDown(KeyCode key)
    {
        return _keyboardStateBuffer.GetDownThisFrame(key);
    }
    public bool GetKeyUp(KeyCode key) => _keyboardStateBuffer.GetUpThisFrame(key);


    // ── Public Query API: Mouse ────────────────────────────────
    public bool GetMouseButton(int button) => _mouseStateBuffer.GetState((MouseButton)button);
    public bool GetMouseButtonDown(int button) => _mouseStateBuffer.GetDownThisFrame((MouseButton)button);
    public bool GetMouseButtonUp(int button) => _mouseStateBuffer.GetUpThisFrame((MouseButton)button);
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

        return _gamepadStateBuffer.GetState(gamepadIndex, button);
    }
    public bool GetGamepadButtonDown(int gamepadIndex, GamepadButton button)
    {
        if (!IsGamepadConnected(gamepadIndex))
            return false;
        return _gamepadStateBuffer.GetDownThisFrame(gamepadIndex, button);
    }
    public bool GetGamepadButtonUp(int gamepadIndex, GamepadButton button)
    {
        if (!IsGamepadConnected(gamepadIndex))
            return false;
        return _gamepadStateBuffer.GetUpThisFrame(gamepadIndex, button);
    }
    public Float2 GetGamepadAxis(int gamepadIndex, int axisIndex)
    {
        if (!IsGamepadConnected(gamepadIndex))
            return Float2.Zero;

        IGamepad gamepad = Context.Gamepads[gamepadIndex];
        if (axisIndex < 0 || axisIndex >= gamepad.Thumbsticks.Count)
            return Float2.Zero;

        Thumbstick thumbstick = gamepad.Thumbsticks[axisIndex];
        return new Float2(thumbstick.X, thumbstick.Y); // We flip y to make UP on the stick positive
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
