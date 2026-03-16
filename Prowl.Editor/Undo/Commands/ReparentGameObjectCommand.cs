// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;

namespace Prowl.Editor.Undo.Commands;

/// <summary>
/// Undoable command that reparents a GameObject to a new parent (or to root if null).
/// </summary>
public sealed class ReparentGameObjectCommand : IUndoableCommand
{
    private readonly GameObject _target;
    private readonly GameObject? _newParent;
    private readonly GameObject? _oldParent;

    public string Description { get; }

    public ReparentGameObjectCommand(GameObject target, GameObject? newParent)
    {
        _target = target;
        _newParent = newParent;
        _oldParent = target.Parent;
        Description = $"Reparent '{target.Name}' to '{newParent?.Name ?? "Root"}'";
    }

    public void Execute()
    {
        if (_newParent != null)
            _target.SetParent(_newParent);
        else
        {
            // Move to root: remove from current parent
            _target.Parent?.Children.Remove(_target);
            _target.SetParent(null!);
        }
    }

    public void Undo()
    {
        if (_oldParent != null)
            _target.SetParent(_oldParent);
        else
        {
            _target.Parent?.Children.Remove(_target);
            _target.SetParent(null!);
        }
    }
}
