// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Prowl.Editor.Prefabs;
using Prowl.Editor.Services;

namespace Prowl.Editor.Undo.Commands;

/// <summary>
/// Undoable command that instantiates an asset (prefab or mesh) in the scene.
/// Optionally places the new object at a specific world position.
/// </summary>
public sealed class InstantiateAssetCommand : IUndoableCommand
{
    private readonly string _absolutePath;
    private readonly string _assetName;
    private readonly Float3? _position;
    private GameObject? _instantiated;

    public string Description { get; }

    public InstantiateAssetCommand(string absolutePath, string assetName, Float3? position = null)
    {
        _absolutePath = absolutePath;
        _assetName = assetName;
        _position = position;
        Description = $"Instantiate '{_assetName}'";
    }

    public void Execute()
    {
        var sceneService = EditorServices.Get<ISceneService>();
        string ext = Path.GetExtension(_absolutePath).ToLowerInvariant();

        if (ext == PrefabManager.PrefabExtension)
        {
            var prefabMgr = new PrefabManager();
            _instantiated = prefabMgr.InstantiatePrefabInScene(_absolutePath, sceneService);
        }
        else
        {
            // For non-prefab assets (meshes, etc.), create a placeholder GameObject
            _instantiated = sceneService.CreateGameObject(Path.GetFileNameWithoutExtension(_absolutePath));
        }

        // Place at the requested world position
        if (_instantiated != null && _position.HasValue)
            _instantiated.Transform.Position = _position.Value;

        // Select the new object
        if (_instantiated != null && EditorServices.TryGet<ISelectionService>(out var sel))
            sel!.ActiveObject = _instantiated;
    }

    public void Undo()
    {
        if (_instantiated != null)
        {
            var sceneService = EditorServices.Get<ISceneService>();
            sceneService.DestroyGameObject(_instantiated);
            _instantiated = null;
        }
    }

    public GameObject? InstantiatedObject => _instantiated;
}
