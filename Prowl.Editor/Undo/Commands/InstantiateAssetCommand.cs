// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Prowl.Editor.Prefabs;
using Prowl.Editor.Services;

namespace Prowl.Editor.Undo.Commands;

/// <summary>
/// Undoable command that instantiates an asset (prefab, model, or other) in the scene.
/// Optionally places the new object at a specific world position.
/// </summary>
public sealed class InstantiateAssetCommand : IUndoableCommand
{
    private readonly string _absolutePath;
    private readonly string _assetName;
    private readonly Float3? _position;
    private GameObject? _instantiated;

    /// <summary>
    /// Model file extensions supported for hierarchy instantiation.
    /// </summary>
    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".obj", ".fbx", ".gltf", ".glb", ".dae", ".blend", ".3ds", ".ply", ".stl"
    };

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
        else if (ModelExtensions.Contains(ext))
        {
            _instantiated = InstantiateModel(sceneService);
        }
        else
        {
            // For other asset types, create a placeholder GameObject
            _instantiated = sceneService.CreateGameObject(Path.GetFileNameWithoutExtension(_absolutePath));
        }

        // Place at the requested world position
        if (_instantiated != null && _position.HasValue)
            _instantiated.Transform.Position = _position.Value;

        // Select the new object
        if (_instantiated != null && EditorServices.TryGet<ISelectionService>(out var sel))
            sel!.ActiveObject = _instantiated;
    }

    /// <summary>
    /// Loads a model file and creates a full GameObject hierarchy
    /// (with MeshRenderers and materials), similar to Unity's model import.
    /// </summary>
    private GameObject? InstantiateModel(ISceneService sceneService)
    {
        try
        {
            var model = Model.LoadFromFile(_absolutePath);
            if (model == null)
            {
                Debug.LogWarning($"[InstantiateAsset] Failed to load model: {_absolutePath}");
                return sceneService.CreateGameObject(_assetName);
            }

            // Build the full hierarchy from the model's node structure
            GameObject root = model.CreateGameObjectHierarchy();

            // Add the root (and all children) to the current scene
            if (sceneService.CurrentScene == null)
                sceneService.CreateNewScene();
            sceneService.CurrentScene!.Add(root);

            return root;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[InstantiateAsset] Error loading model '{_assetName}': {ex.Message}");
            return sceneService.CreateGameObject(_assetName);
        }
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
