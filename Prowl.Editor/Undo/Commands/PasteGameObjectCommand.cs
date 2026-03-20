// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Editor.Services;

namespace Prowl.Editor.Undo.Commands;

/// <summary>
/// Undoable command that pastes (or duplicates) a GameObject into the scene
/// from serialized Echo data.
/// </summary>
public sealed class PasteGameObjectCommand : IUndoableCommand
{
    private readonly EchoObject _data;
    private readonly string _suffix;
    private readonly int? _parentInstanceId;
    private GameObject? _pasted;

    public string Description { get; }

    public PasteGameObjectCommand(EchoObject data, GameObject? parent, string description, string suffix = " (Copy)")
    {
        _data = data;
        _parentInstanceId = parent?.InstanceID;
        _suffix = suffix;
        Description = description;
    }

    public void Execute()
    {
        var sceneService = EditorServices.Get<ISceneService>();
        var scene = sceneService.CurrentScene;
        if (scene == null) return;

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);
        _pasted = Serializer.Deserialize<GameObject>(_data, ctx);
        if (_pasted == null) return;

        _pasted.Name += _suffix;
        _pasted.RegenerateIdentifiers();

        scene.Add(_pasted);

        if (_parentInstanceId != null)
        {
            foreach (var root in sceneService.GetRootGameObjects())
            {
                var parent = FindRecursive(root, _parentInstanceId.Value);
                if (parent != null)
                {
                    _pasted.SetParent(parent);
                    break;
                }
            }
        }
    }

    public void Undo()
    {
        if (_pasted != null)
        {
            var sceneService = EditorServices.Get<ISceneService>();
            sceneService.DestroyGameObject(_pasted);
            _pasted = null;
        }
    }

    /// <summary> The pasted/duplicated GameObject (available after Execute). </summary>
    public GameObject? PastedObject => _pasted;

    private static GameObject? FindRecursive(GameObject go, int instanceId)
    {
        if (go.InstanceID == instanceId) return go;
        foreach (var child in go.Children)
        {
            var found = FindRecursive(child, instanceId);
            if (found != null) return found;
        }
        return null;
    }
}
