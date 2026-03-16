// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;

namespace Prowl.Editor.Undo.Commands;

/// <summary>
/// Undoable command that changes a single field value on an object via reflection.
/// </summary>
public sealed class PropertyChangeCommand : IUndoableCommand
{
    private readonly object _target;
    private readonly FieldInfo _field;
    private readonly object? _oldValue;
    private readonly object? _newValue;

    public string Description { get; }

    public PropertyChangeCommand(object target, FieldInfo field, object? oldValue, object? newValue)
    {
        _target = target;
        _field = field;
        _oldValue = oldValue;
        _newValue = newValue;
        Description = $"Change {_target.GetType().Name}.{_field.Name}";
    }

    public void Execute() => _field.SetValue(_target, _newValue);
    public void Undo() => _field.SetValue(_target, _oldValue);
}
