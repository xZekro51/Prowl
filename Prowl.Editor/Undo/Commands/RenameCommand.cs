// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;

namespace Prowl.Editor.Undo.Commands;

/// <summary>
/// Undoable command that renames a GameObject.
/// </summary>
public sealed class RenameCommand : IUndoableCommand
{
    private readonly GameObject _gameObject;
    private readonly string _oldName;
    private readonly string _newName;

    public string Description => $"Rename '{_oldName}' to '{_newName}'";

    public RenameCommand(GameObject gameObject, string oldName, string newName)
    {
        _gameObject = gameObject;
        _oldName = oldName;
        _newName = newName;
    }

    public void Execute() => _gameObject.Name = _newName;
    public void Undo() => _gameObject.Name = _oldName;
}
