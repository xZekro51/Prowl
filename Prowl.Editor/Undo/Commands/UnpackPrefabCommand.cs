// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Runtime.Prefabs;
using Prowl.Editor.Prefabs;
using Prowl.Editor.Services;

namespace Prowl.Editor.Undo.Commands;

/// <summary>
/// Undoable command that unpacks a prefab instance, removing its
/// <see cref="PrefabLink"/> and converting it to a regular GameObject hierarchy.
/// Undo re-applies the prefab link.
/// </summary>
public sealed class UnpackPrefabCommand : IUndoableCommand
{
    private readonly int _instanceRootId;
    private readonly PrefabLink _savedLink;

    public string Description { get; }

    public UnpackPrefabCommand(GameObject instanceRoot)
    {
        _instanceRootId = instanceRoot.InstanceID;
        _savedLink = instanceRoot.PrefabLink!.Clone();
        Description = $"Unpack Prefab '{instanceRoot.Name}'";
    }

    public void Execute()
    {
        var go = FindById(_instanceRootId);
        if (go != null)
            PrefabManager.UnpackInstance(go);
    }

    public void Undo()
    {
        var go = FindById(_instanceRootId);
        if (go == null) return;

        // Re-apply the saved prefab link to the entire hierarchy
        go.PrefabLink = _savedLink.Clone();
        ReapplyChildLinks(go, _savedLink.PrefabAssetPath, _savedLink.PrefabAssetGuid);
    }

    private static void ReapplyChildLinks(GameObject go, string path, string guid)
    {
        foreach (var child in go.Children)
        {
            child.PrefabLink = new PrefabLink
            {
                PrefabAssetPath = path,
                PrefabAssetGuid = guid,
                IsRoot = false,
            };
            ReapplyChildLinks(child, path, guid);
        }
    }

    private static GameObject? FindById(int id)
    {
        var sceneService = EditorServices.Get<ISceneService>();
        foreach (var root in sceneService.GetRootGameObjects())
        {
            var found = FindRecursive(root, id);
            if (found != null) return found;
        }
        return null;
    }

    private static GameObject? FindRecursive(GameObject go, int id)
    {
        if (go.InstanceID == id) return go;
        foreach (var child in go.Children)
        {
            var found = FindRecursive(child, id);
            if (found != null) return found;
        }
        return null;
    }
}
