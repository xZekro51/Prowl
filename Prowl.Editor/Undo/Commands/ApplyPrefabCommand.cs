// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Prefabs;
using Prowl.Runtime.Resources;
using Prowl.Editor.Prefabs;
using Prowl.Editor.Services;

namespace Prowl.Editor.Undo.Commands;

/// <summary>
/// Undoable command that applies the current state of a prefab instance
/// back to its source .prefab asset file.
/// Undo restores the prefab file to its previous content.
/// </summary>
public sealed class ApplyPrefabCommand : IUndoableCommand
{
    private readonly int _instanceRootId;
    private string? _previousFileContent;
    private string? _absolutePath;

    public string Description { get; }

    public ApplyPrefabCommand(GameObject instanceRoot)
    {
        _instanceRootId = instanceRoot.InstanceID;
        Description = $"Apply Prefab '{instanceRoot.Name}'";
    }

    public void Execute()
    {
        var sceneService = EditorServices.Get<ISceneService>();
        var instanceRoot = FindById(sceneService, _instanceRootId);
        if (instanceRoot?.PrefabLink == null) return;

        _absolutePath = PrefabManager.ResolvePrefabAbsolutePath(instanceRoot.PrefabLink);

        // Save previous file content for undo
        if (!string.IsNullOrEmpty(_absolutePath) && File.Exists(_absolutePath))
            _previousFileContent = File.ReadAllText(_absolutePath);

        var mgr = new PrefabManager();
        mgr.ApplyInstance(instanceRoot);
    }

    public void Undo()
    {
        if (!string.IsNullOrEmpty(_absolutePath) && _previousFileContent != null)
        {
            File.WriteAllText(_absolutePath, _previousFileContent);

            if (EditorServices.TryGet<IAssetService>(out var assets))
                assets!.Refresh();
        }
    }

    private static GameObject? FindById(ISceneService sceneService, int id)
    {
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
