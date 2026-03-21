// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Services;
using Prowl.Editor.Undo;

using Xunit;

namespace Prowl.Editor.Tests;

/// <summary>
/// Tests for <see cref="UndoRedoService"/>: execute, undo, redo,
/// history trimming, and stack clearing.
/// </summary>
public sealed class UndoRedoServiceTests : IDisposable
{
    public UndoRedoServiceTests()
    {
        // Ensure EditorServices does not interfere with tests
        EditorServices.Clear();
    }

    public void Dispose()
    {
        EditorServices.Clear();
    }

    #region Execute / Undo / Redo

    [Fact]
    public void Execute_RunsCommandAndPushesToUndoStack()
    {
        var service = new UndoRedoService();
        var cmd = new FakeCommand("Set X");

        service.Execute(cmd);

        Assert.True(cmd.Executed);
        Assert.True(service.CanUndo);
        Assert.Equal("Set X", service.UndoDescription);
    }

    [Fact]
    public void Undo_ReversesLastCommand()
    {
        var service = new UndoRedoService();
        var cmd = new FakeCommand("Set X");
        service.Execute(cmd);

        service.Undo();

        Assert.True(cmd.Undone);
        Assert.False(service.CanUndo);
        Assert.True(service.CanRedo);
    }

    [Fact]
    public void Redo_ReExecutesUndoneCommand()
    {
        var service = new UndoRedoService();
        var cmd = new FakeCommand("Set X");
        service.Execute(cmd);
        service.Undo();

        service.Redo();

        Assert.Equal(2, cmd.ExecuteCount);
        Assert.True(service.CanUndo);
        Assert.False(service.CanRedo);
    }

    [Fact]
    public void Undo_WhenEmpty_IsNoOp()
    {
        var service = new UndoRedoService();

        service.Undo(); // should not throw

        Assert.False(service.CanUndo);
    }

    [Fact]
    public void Redo_WhenEmpty_IsNoOp()
    {
        var service = new UndoRedoService();

        service.Redo(); // should not throw

        Assert.False(service.CanRedo);
    }

    #endregion

    #region Stack Behavior

    [Fact]
    public void Execute_ClearsRedoStack()
    {
        var service = new UndoRedoService();
        service.Execute(new FakeCommand("A"));
        service.Undo();
        Assert.True(service.CanRedo);

        service.Execute(new FakeCommand("B"));

        Assert.False(service.CanRedo);
    }

    [Fact]
    public void MultipleUndoRedo_MaintainsCorrectOrder()
    {
        var service = new UndoRedoService();
        var cmd1 = new FakeCommand("First");
        var cmd2 = new FakeCommand("Second");
        var cmd3 = new FakeCommand("Third");

        service.Execute(cmd1);
        service.Execute(cmd2);
        service.Execute(cmd3);

        Assert.Equal("Third", service.UndoDescription);

        service.Undo();
        Assert.Equal("Second", service.UndoDescription);
        Assert.Equal("Third", service.RedoDescription);

        service.Undo();
        Assert.Equal("First", service.UndoDescription);
        Assert.Equal("Second", service.RedoDescription);

        service.Redo();
        Assert.Equal("Second", service.UndoDescription);
    }

    #endregion

    #region Push (without executing)

    [Fact]
    public void Push_AddsToUndoStack_WithoutExecuting()
    {
        var service = new UndoRedoService();
        var cmd = new FakeCommand("Pushed");

        service.Push(cmd);

        Assert.False(cmd.Executed);
        Assert.True(service.CanUndo);
        Assert.Equal("Pushed", service.UndoDescription);
    }

    [Fact]
    public void Push_ClearsRedoStack()
    {
        var service = new UndoRedoService();
        service.Execute(new FakeCommand("A"));
        service.Undo();
        Assert.True(service.CanRedo);

        service.Push(new FakeCommand("B"));

        Assert.False(service.CanRedo);
    }

    #endregion

    #region History Trimming

    [Fact]
    public void Execute_TrimsHistory_WhenExceedingMaxSize()
    {
        var service = new UndoRedoService { MaxHistorySize = 3 };

        service.Execute(new FakeCommand("1"));
        service.Execute(new FakeCommand("2"));
        service.Execute(new FakeCommand("3"));
        service.Execute(new FakeCommand("4"));

        // Undo everything possible — should only be able to undo 3 times
        int undoCount = 0;
        while (service.CanUndo)
        {
            service.Undo();
            undoCount++;
        }

        Assert.True(undoCount <= 3);
    }

    [Fact]
    public void Push_TrimsHistory_WhenExceedingMaxSize()
    {
        var service = new UndoRedoService { MaxHistorySize = 2 };

        service.Push(new FakeCommand("1"));
        service.Push(new FakeCommand("2"));
        service.Push(new FakeCommand("3"));

        int undoCount = 0;
        while (service.CanUndo)
        {
            service.Undo();
            undoCount++;
        }

        Assert.True(undoCount <= 2);
    }

    #endregion

    #region Clear

    [Fact]
    public void Clear_EmptiesBothStacks()
    {
        var service = new UndoRedoService();
        service.Execute(new FakeCommand("A"));
        service.Execute(new FakeCommand("B"));
        service.Undo();

        service.Clear();

        Assert.False(service.CanUndo);
        Assert.False(service.CanRedo);
        Assert.Null(service.UndoDescription);
        Assert.Null(service.RedoDescription);
    }

    #endregion

    #region Descriptions

    [Fact]
    public void UndoDescription_IsNull_WhenEmpty()
    {
        var service = new UndoRedoService();

        Assert.Null(service.UndoDescription);
    }

    [Fact]
    public void RedoDescription_IsNull_WhenEmpty()
    {
        var service = new UndoRedoService();

        Assert.Null(service.RedoDescription);
    }

    [Fact]
    public void RedoDescription_MatchesUndoneCommand()
    {
        var service = new UndoRedoService();
        service.Execute(new FakeCommand("MyAction"));

        service.Undo();

        Assert.Equal("MyAction", service.RedoDescription);
    }

    #endregion

    #region State Mutation Verification

    [Fact]
    public void Execute_Undo_Redo_MaintainsState()
    {
        var service = new UndoRedoService();
        int value = 0;
        var cmd = new StatefulCommand("Increment",
            execute: () => value++,
            undo: () => value--);

        service.Execute(cmd);
        Assert.Equal(1, value);

        service.Undo();
        Assert.Equal(0, value);

        service.Redo();
        Assert.Equal(1, value);
    }

    [Fact]
    public void MultipleCommands_UndoAll_RestoresOriginalState()
    {
        var service = new UndoRedoService();
        int value = 0;

        service.Execute(new StatefulCommand("Add 10", () => value += 10, () => value -= 10));
        service.Execute(new StatefulCommand("Multiply 2", () => value *= 2, () => value /= 2));
        service.Execute(new StatefulCommand("Add 5", () => value += 5, () => value -= 5));

        Assert.Equal(25, value); // (0+10)*2+5

        service.Undo(); // undo Add 5
        Assert.Equal(20, value);

        service.Undo(); // undo Multiply 2
        Assert.Equal(10, value);

        service.Undo(); // undo Add 10
        Assert.Equal(0, value);
    }

    #endregion

    /// <summary>
    /// Simple fake command that tracks execution and undo calls.
    /// </summary>
    private sealed class FakeCommand : IUndoableCommand
    {
        public string Description { get; }
        public bool Executed { get; private set; }
        public bool Undone { get; private set; }
        public int ExecuteCount { get; private set; }

        public FakeCommand(string description) => Description = description;

        public void Execute()
        {
            Executed = true;
            ExecuteCount++;
        }

        public void Undo() => Undone = true;
    }

    /// <summary>
    /// Command that mutates external state for verification.
    /// </summary>
    private sealed class StatefulCommand : IUndoableCommand
    {
        public string Description { get; }
        private readonly Action _execute;
        private readonly Action _undo;

        public StatefulCommand(string description, Action execute, Action undo)
        {
            Description = description;
            _execute = execute;
            _undo = undo;
        }

        public void Execute() => _execute();
        public void Undo() => _undo();
    }
}
