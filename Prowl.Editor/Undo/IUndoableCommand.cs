// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Editor.Undo;

/// <summary>
/// Represents a single undoable/redoable editor action.
/// </summary>
public interface IUndoableCommand
{
    /// <summary> Human-readable description for the undo history. </summary>
    string Description { get; }

    /// <summary> Execute (or re-execute) the action. </summary>
    void Execute();

    /// <summary> Reverse the action. </summary>
    void Undo();
}
