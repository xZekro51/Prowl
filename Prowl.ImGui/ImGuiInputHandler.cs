// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

using ImGuiNET;

using Prowl.Runtime;

using Silk.NET.Input;
using Silk.NET.Input.Extensions;

using SilkMouseButton = Silk.NET.Input.MouseButton;

namespace Prowl.ImGuiIntegration;

/// <summary>
/// Backend-agnostic ImGui input handler. Forwards Silk.NET input events to
/// Dear ImGui without any dependency on a specific graphics backend.
/// Extracted from the Silk.NET OpenGL ImGui controller.
/// </summary>
public sealed class ImGuiInputHandler : IDisposable
{
    private readonly IInputContext _input;
    private readonly List<char> _pressedChars = new();
    private bool _disposed;

    // Keyboard tracking
    private readonly bool[] _keyStates = new bool[512];

    // Clipboard support — static because ImGui callbacks require persistent function pointers
    private static IKeyboard? s_clipboardKeyboard;
    private static IntPtr s_clipboardBuffer;

    // Delegate types matching ImGui's clipboard callback signatures
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetClipboardTextFn(IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetClipboardTextFn(IntPtr userData, IntPtr text);

    // Static delegate instances prevent GC collection while ImGui holds the function pointers
    private static GetClipboardTextFn? s_getClipboardDelegate;
    private static SetClipboardTextFn? s_setClipboardDelegate;

    public ImGuiInputHandler(IInputContext input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));

        // Subscribe to input events
        foreach (IKeyboard keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyUp += OnKeyUp;
            keyboard.KeyChar += OnKeyChar;
        }

        // Wire up system clipboard for ImGui text fields (Ctrl+C / Ctrl+V)
        if (_input.Keyboards.Count > 0)
            SetupClipboard(_input.Keyboards[0]);
    }

    /// <summary>
    /// Updates ImGui IO state for the current frame. Must be called before
    /// <c>ImGui.NewFrame()</c>.
    /// </summary>
    public void Update(float deltaTime, int fbWidth, int fbHeight)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(fbWidth, fbHeight);
        io.DisplayFramebufferScale = new Vector2(1, 1);
        io.DeltaTime = deltaTime > 0 ? deltaTime : 1.0f / 60.0f;

        UpdateMouse(io);
        UpdateKeyboard(io);

        // Flush pressed characters
        foreach (char c in _pressedChars)
            io.AddInputCharacter(c);
        _pressedChars.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (IKeyboard keyboard in _input.Keyboards)
        {
            keyboard.KeyDown -= OnKeyDown;
            keyboard.KeyUp -= OnKeyUp;
            keyboard.KeyChar -= OnKeyChar;
        }

        CleanupClipboard();
    }

    // ── Private helpers ─────────────────────────────────────────────

    private void UpdateMouse(ImGuiIOPtr io)
    {
        if (_input.Mice.Count == 0)
            return;

        var mouse = _input.Mice[0];

        io.MousePos = new Vector2(mouse.Position.X, mouse.Position.Y);

        var wheel = mouse.ScrollWheels;
        if (wheel.Count > 0)
        {
            io.MouseWheel = wheel[0].Y;
            io.MouseWheelH = wheel[0].X;
        }

        // Mouse buttons: ImGui expects Left=0, Right=1, Middle=2, Extra1=3, Extra2=4
        for (int i = 0; i < 5; i++)
        {
            io.MouseDown[i] = mouse.IsButtonPressed((SilkMouseButton)i);
        }
    }

    private void UpdateKeyboard(ImGuiIOPtr io)
    {
        if (_input.Keyboards.Count == 0)
            return;

        var keyboard = _input.Keyboards[0];

        // Modifier keys
        io.KeyCtrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
        io.KeyShift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
        io.KeyAlt = keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight);
        io.KeySuper = keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight);

        // Key states via the ImGui key map
        foreach (var (silkKey, imguiKey) in s_keyMap)
        {
            io.AddKeyEvent(imguiKey, keyboard.IsKeyPressed(silkKey));
        }
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        // Handled in UpdateKeyboard
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int scancode)
    {
        // Handled in UpdateKeyboard
    }

    private void OnKeyChar(IKeyboard keyboard, char character)
    {
        _pressedChars.Add(character);
    }

    // ── Clipboard ───────────────────────────────────────────────────

    private static void SetupClipboard(IKeyboard keyboard)
    {
        s_clipboardKeyboard = keyboard;

        // Store delegates as static fields so the GC does not collect them
        // while ImGui holds the corresponding native function pointers.
        s_getClipboardDelegate = GetClipboardTextImpl;
        s_setClipboardDelegate = SetClipboardTextImpl;

        ImGuiIOPtr io = ImGui.GetIO();
        io.GetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(s_getClipboardDelegate);
        io.SetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(s_setClipboardDelegate);
    }

    private static void CleanupClipboard()
    {
        s_getClipboardDelegate = null;
        s_setClipboardDelegate = null;
        s_clipboardKeyboard = null;

        if (s_clipboardBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(s_clipboardBuffer);
            s_clipboardBuffer = IntPtr.Zero;
        }
    }

    private static IntPtr GetClipboardTextImpl(IntPtr userData)
    {
        try
        {
            if (s_clipboardBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(s_clipboardBuffer);
                s_clipboardBuffer = IntPtr.Zero;
            }

            string? text = s_clipboardKeyboard?.ClipboardText;
            if (string.IsNullOrEmpty(text))
                return IntPtr.Zero;

            int byteCount = Encoding.UTF8.GetByteCount(text) + 1;
            s_clipboardBuffer = Marshal.AllocHGlobal(byteCount);
            unsafe
            {
                fixed (char* pText = text)
                {
                    int written = Encoding.UTF8.GetBytes(pText, text.Length, (byte*)s_clipboardBuffer, byteCount - 1);
                    ((byte*)s_clipboardBuffer)[written] = 0; // null terminator
                }
            }

            return s_clipboardBuffer;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ImGui Clipboard] GetClipboardText failed: {ex}");
            return IntPtr.Zero;
        }
    }

    private static void SetClipboardTextImpl(IntPtr userData, IntPtr text)
    {
        try
        {
            if (s_clipboardKeyboard == null || text == IntPtr.Zero)
                return;

            string? str = Marshal.PtrToStringUTF8(text);
            if (str != null)
                s_clipboardKeyboard.ClipboardText = str;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ImGui Clipboard] SetClipboardText failed: {ex}");
        }
    }

    // ── Key mapping table ───────────────────────────────────────────

    private static readonly (Key silk, ImGuiKey imgui)[] s_keyMap =
    [
        (Key.Tab, ImGuiKey.Tab),
        (Key.Left, ImGuiKey.LeftArrow),
        (Key.Right, ImGuiKey.RightArrow),
        (Key.Up, ImGuiKey.UpArrow),
        (Key.Down, ImGuiKey.DownArrow),
        (Key.PageUp, ImGuiKey.PageUp),
        (Key.PageDown, ImGuiKey.PageDown),
        (Key.Home, ImGuiKey.Home),
        (Key.End, ImGuiKey.End),
        (Key.Insert, ImGuiKey.Insert),
        (Key.Delete, ImGuiKey.Delete),
        (Key.Backspace, ImGuiKey.Backspace),
        (Key.Space, ImGuiKey.Space),
        (Key.Enter, ImGuiKey.Enter),
        (Key.Escape, ImGuiKey.Escape),
        (Key.Apostrophe, ImGuiKey.Apostrophe),
        (Key.Comma, ImGuiKey.Comma),
        (Key.Minus, ImGuiKey.Minus),
        (Key.Period, ImGuiKey.Period),
        (Key.Slash, ImGuiKey.Slash),
        (Key.Semicolon, ImGuiKey.Semicolon),
        (Key.Equal, ImGuiKey.Equal),
        (Key.LeftBracket, ImGuiKey.LeftBracket),
        (Key.BackSlash, ImGuiKey.Backslash),
        (Key.RightBracket, ImGuiKey.RightBracket),
        (Key.GraveAccent, ImGuiKey.GraveAccent),
        (Key.CapsLock, ImGuiKey.CapsLock),
        (Key.ScrollLock, ImGuiKey.ScrollLock),
        (Key.NumLock, ImGuiKey.NumLock),
        (Key.PrintScreen, ImGuiKey.PrintScreen),
        (Key.Pause, ImGuiKey.Pause),
        (Key.Keypad0, ImGuiKey.Keypad0),
        (Key.Keypad1, ImGuiKey.Keypad1),
        (Key.Keypad2, ImGuiKey.Keypad2),
        (Key.Keypad3, ImGuiKey.Keypad3),
        (Key.Keypad4, ImGuiKey.Keypad4),
        (Key.Keypad5, ImGuiKey.Keypad5),
        (Key.Keypad6, ImGuiKey.Keypad6),
        (Key.Keypad7, ImGuiKey.Keypad7),
        (Key.Keypad8, ImGuiKey.Keypad8),
        (Key.Keypad9, ImGuiKey.Keypad9),
        (Key.KeypadDecimal, ImGuiKey.KeypadDecimal),
        (Key.KeypadDivide, ImGuiKey.KeypadDivide),
        (Key.KeypadMultiply, ImGuiKey.KeypadMultiply),
        (Key.KeypadSubtract, ImGuiKey.KeypadSubtract),
        (Key.KeypadAdd, ImGuiKey.KeypadAdd),
        (Key.KeypadEnter, ImGuiKey.KeypadEnter),
        (Key.KeypadEqual, ImGuiKey.KeypadEqual),
        (Key.ShiftLeft, ImGuiKey.LeftShift),
        (Key.ControlLeft, ImGuiKey.LeftCtrl),
        (Key.AltLeft, ImGuiKey.LeftAlt),
        (Key.SuperLeft, ImGuiKey.LeftSuper),
        (Key.ShiftRight, ImGuiKey.RightShift),
        (Key.ControlRight, ImGuiKey.RightCtrl),
        (Key.AltRight, ImGuiKey.RightAlt),
        (Key.SuperRight, ImGuiKey.RightSuper),
        (Key.Menu, ImGuiKey.Menu),
        (Key.Number0, ImGuiKey._0),
        (Key.Number1, ImGuiKey._1),
        (Key.Number2, ImGuiKey._2),
        (Key.Number3, ImGuiKey._3),
        (Key.Number4, ImGuiKey._4),
        (Key.Number5, ImGuiKey._5),
        (Key.Number6, ImGuiKey._6),
        (Key.Number7, ImGuiKey._7),
        (Key.Number8, ImGuiKey._8),
        (Key.Number9, ImGuiKey._9),
        (Key.A, ImGuiKey.A),
        (Key.B, ImGuiKey.B),
        (Key.C, ImGuiKey.C),
        (Key.D, ImGuiKey.D),
        (Key.E, ImGuiKey.E),
        (Key.F, ImGuiKey.F),
        (Key.G, ImGuiKey.G),
        (Key.H, ImGuiKey.H),
        (Key.I, ImGuiKey.I),
        (Key.J, ImGuiKey.J),
        (Key.K, ImGuiKey.K),
        (Key.L, ImGuiKey.L),
        (Key.M, ImGuiKey.M),
        (Key.N, ImGuiKey.N),
        (Key.O, ImGuiKey.O),
        (Key.P, ImGuiKey.P),
        (Key.Q, ImGuiKey.Q),
        (Key.R, ImGuiKey.R),
        (Key.S, ImGuiKey.S),
        (Key.T, ImGuiKey.T),
        (Key.U, ImGuiKey.U),
        (Key.V, ImGuiKey.V),
        (Key.W, ImGuiKey.W),
        (Key.X, ImGuiKey.X),
        (Key.Y, ImGuiKey.Y),
        (Key.Z, ImGuiKey.Z),
        (Key.F1, ImGuiKey.F1),
        (Key.F2, ImGuiKey.F2),
        (Key.F3, ImGuiKey.F3),
        (Key.F4, ImGuiKey.F4),
        (Key.F5, ImGuiKey.F5),
        (Key.F6, ImGuiKey.F6),
        (Key.F7, ImGuiKey.F7),
        (Key.F8, ImGuiKey.F8),
        (Key.F9, ImGuiKey.F9),
        (Key.F10, ImGuiKey.F10),
        (Key.F11, ImGuiKey.F11),
        (Key.F12, ImGuiKey.F12),
    ];
}
