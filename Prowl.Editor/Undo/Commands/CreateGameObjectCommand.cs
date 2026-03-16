// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Editor.Services;

namespace Prowl.Editor.Undo.Commands;

/// <summary>
/// Undoable command that creates a new GameObject in the active scene.
/// </summary>
public sealed class CreateGameObjectCommand : IUndoableCommand
{
    private readonly string _name;
    private GameObject? _created;

    public string Description { get; }

    public CreateGameObjectCommand(string name = "New GameObject")
    {
        _name = name;
        Description = $"Create GameObject '{_name}'";
    }

    public void Execute()
    {
        var sceneService = EditorServices.Get<ISceneService>();
        _created = sceneService.CreateGameObject(_name);
    }

    public void Undo()
    {
        if (_created != null)
        {
            var sceneService = EditorServices.Get<ISceneService>();
            sceneService.DestroyGameObject(_created);
            _created = null;
        }
    }

    /// <summary> The created GameObject (available after Execute). </summary>
    public GameObject? CreatedObject => _created;
}
