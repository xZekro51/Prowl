// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;

namespace Prowl.Editor.Undo.Commands;

/// <summary>
/// Undoable command that removes a component from a GameObject.
/// Stores the component so it can be re-added on undo.
/// </summary>
public sealed class RemoveComponentCommand : IUndoableCommand
{
    private readonly GameObject _gameObject;
    private readonly MonoBehaviour _component;

    public string Description { get; }

    public RemoveComponentCommand(GameObject gameObject, MonoBehaviour component)
    {
        _gameObject = gameObject;
        _component = component;
        Description = $"Remove {_component.GetType().Name} from {_gameObject.Name}";
    }

    public void Execute()
    {
        _gameObject.RemoveComponent(_component);
    }

    public void Undo()
    {
        _gameObject.AddComponent(_component);
    }
}
