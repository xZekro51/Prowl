// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Editor.Services;

namespace Prowl.Editor.Undo;

/// <summary>
/// Manages undo/redo stacks for the editor. Every editor action that modifies
/// the scene pushes an <see cref="IUndoableCommand"/> onto the undo stack.
/// </summary>
public sealed class UndoRedoService
{
    private readonly Stack<IUndoableCommand> _undoStack = new();
    private readonly Stack<IUndoableCommand> _redoStack = new();

    /// <summary> Maximum number of undo steps to keep. </summary>
    public int MaxHistorySize { get; set; } = 256;

    /// <summary> True if there is at least one action to undo. </summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary> True if there is at least one action to redo. </summary>
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary> Description of the next undo action, or null. </summary>
    public string? UndoDescription => CanUndo ? _undoStack.Peek().Description : null;

    /// <summary> Description of the next redo action, or null. </summary>
    public string? RedoDescription => CanRedo ? _redoStack.Peek().Description : null;

    /// <summary>
    /// Executes a command and pushes it onto the undo stack.
    /// Clears the redo stack (branching history).
    /// </summary>
    public void Execute(IUndoableCommand command)
    {
        command.Execute();
        _undoStack.Push(command);
        _redoStack.Clear();

        // Trim history if it exceeds the max size
        if (_undoStack.Count > MaxHistorySize)
            TrimStack(_undoStack, MaxHistorySize);

        MarkSceneDirty();
        Debug.Log($"[Undo] Executed: {command.Description}");
    }

    /// <summary> Undoes the most recent action. </summary>
    public void Undo()
    {
        if (!CanUndo) return;

        var command = _undoStack.Pop();
        command.Undo();
        _redoStack.Push(command);
        MarkSceneDirty();
        Debug.Log($"[Undo] Undone: {command.Description}");
    }

    /// <summary> Redoes the most recently undone action. </summary>
    public void Redo()
    {
        if (!CanRedo) return;

        var command = _redoStack.Pop();
        command.Execute();
        _undoStack.Push(command);
        MarkSceneDirty();
        Debug.Log($"[Undo] Redone: {command.Description}");
    }

    /// <summary> Clears all undo/redo history. </summary>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }

    private static void MarkSceneDirty()
    {
        if (EditorServices.TryGet<ISceneService>(out var sceneService))
            sceneService!.MarkDirty();
    }

    private static void TrimStack(Stack<IUndoableCommand> stack, int maxSize)
    {
        if (stack.Count <= maxSize) return;
        var temp = stack.ToArray();
        stack.Clear();
        for (int i = Math.Min(temp.Length - 1, maxSize - 1); i >= 0; i--)
            stack.Push(temp[i]);
    }
}
