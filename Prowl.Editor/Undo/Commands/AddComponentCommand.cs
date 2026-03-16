// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;

namespace Prowl.Editor.Undo.Commands;

/// <summary>
/// Undoable command that adds a component to a GameObject.
/// </summary>
public sealed class AddComponentCommand : IUndoableCommand
{
    private readonly GameObject _gameObject;
    private readonly Type _componentType;
    private MonoBehaviour? _addedComponent;

    public string Description { get; }

    public AddComponentCommand(GameObject gameObject, Type componentType)
    {
        _gameObject = gameObject;
        _componentType = componentType;
        Description = $"Add {_componentType.Name} to {_gameObject.Name}";
    }

    public void Execute()
    {
        _addedComponent = _gameObject.AddComponent(_componentType);
    }

    public void Undo()
    {
        if (_addedComponent != null)
        {
            _gameObject.RemoveComponent(_addedComponent);
            _addedComponent = null;
        }
    }
}
