// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Editor.Undo.Commands;

/// <summary>
/// Undoable command that captures the before/after state of a
/// GameObject's local transform (position, rotation, scale).
/// </summary>
public sealed class TransformChangeCommand : IUndoableCommand
{
    private readonly Transform _transform;
    private readonly Float3 _oldLocalPosition;
    private readonly Float3 _oldLocalEulerAngles;
    private readonly Float3 _oldLocalScale;
    private readonly Float3 _newLocalPosition;
    private readonly Float3 _newLocalEulerAngles;
    private readonly Float3 _newLocalScale;

    public string Description { get; }

    public TransformChangeCommand(
        Transform transform,
        Float3 oldLocalPosition, Float3 oldLocalEulerAngles, Float3 oldLocalScale,
        Float3 newLocalPosition, Float3 newLocalEulerAngles, Float3 newLocalScale,
        string description = "Transform Change")
    {
        _transform = transform;
        _oldLocalPosition = oldLocalPosition;
        _oldLocalEulerAngles = oldLocalEulerAngles;
        _oldLocalScale = oldLocalScale;
        _newLocalPosition = newLocalPosition;
        _newLocalEulerAngles = newLocalEulerAngles;
        _newLocalScale = newLocalScale;
        Description = description;
    }

    public void Execute()
    {
        _transform.LocalPosition = _newLocalPosition;
        _transform.LocalEulerAngles = _newLocalEulerAngles;
        _transform.LocalScale = _newLocalScale;
    }

    public void Undo()
    {
        _transform.LocalPosition = _oldLocalPosition;
        _transform.LocalEulerAngles = _oldLocalEulerAngles;
        _transform.LocalScale = _oldLocalScale;
    }
}
