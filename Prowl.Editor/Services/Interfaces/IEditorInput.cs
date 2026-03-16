// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Editor.Services;

/// <summary>
/// Abstracts input queries so editor panels are decoupled from
/// the concrete input system. Wraps Prowl.Runtime.Input by default.
/// </summary>
public interface IEditorInput
{
    Float2 MousePosition { get; }
    Float2 MouseDelta { get; }
    float ScrollDelta { get; }

    bool IsMouseButton(int button);
    bool IsMouseButtonDown(int button);
    bool IsMouseButtonUp(int button);

    bool IsKey(KeyCode key);
    bool IsKeyDown(KeyCode key);
}
