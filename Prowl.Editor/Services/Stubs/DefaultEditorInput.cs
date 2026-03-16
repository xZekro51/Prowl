// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Editor.Services;

/// <summary>
/// Default input service that delegates to <see cref="Prowl.Runtime.Input"/>.
/// </summary>
public sealed class DefaultEditorInput : IEditorInput
{
    public Float2 MousePosition => new(Input.MousePosition.X, Input.MousePosition.Y);
    public Float2 MouseDelta => Input.MouseDelta;
    public float ScrollDelta => Input.MouseWheelDelta;

    public bool IsMouseButton(int button) => Input.GetMouseButton(button);
    public bool IsMouseButtonDown(int button) => Input.GetMouseButtonDown(button);
    public bool IsMouseButtonUp(int button) => Input.GetMouseButtonUp(button);

    public bool IsKey(KeyCode key) => Input.GetKey(key);
    public bool IsKeyDown(KeyCode key) => Input.GetKeyDown(key);
}
