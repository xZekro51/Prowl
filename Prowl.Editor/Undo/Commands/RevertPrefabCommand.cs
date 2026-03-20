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
/// Undoable command that reverts a prefab instance to match its source
/// .prefab asset, discarding local overrides.
/// Undo restores the instance to its pre-revert state via serialization snapshot.
/// </summary>
public sealed class RevertPrefabCommand : IUndoableCommand
{
    private readonly EchoObject _preRevertSnapshot;
    private readonly int? _parentId;
    private readonly PrefabLink _link;
    private readonly Prowl.Vector.Float3 _localPos;
    private readonly Prowl.Vector.Quaternion _localRot;
    private readonly Prowl.Vector.Float3 _localScl;
    private int _instanceRootId;

    public string Description { get; }

    public RevertPrefabCommand(GameObject instanceRoot)
    {
        _instanceRootId = instanceRoot.InstanceID;
        _parentId = instanceRoot.Parent?.InstanceID;
        _link = instanceRoot.PrefabLink!.Clone();
        _localPos = instanceRoot.Transform.LocalPosition;
        _localRot = instanceRoot.Transform.LocalRotation;
        _localScl = instanceRoot.Transform.LocalScale;

        // Snapshot the current state for undo
        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);
        _preRevertSnapshot = Serializer.Serialize(typeof(GameObject), instanceRoot, ctx);

        Description = $"Revert Prefab '{instanceRoot.Name}'";
    }

    public void Execute()
    {
        var sceneService = EditorServices.Get<ISceneService>();
        var instanceRoot = FindById(sceneService, _instanceRootId);
        if (instanceRoot == null) return;

        var mgr = new PrefabManager();
        mgr.RevertInstance(instanceRoot);

        // The revert replaces the GO, so we need to find the new instance
        // for potential future undo/redo references. The selection service
        // is updated by RevertInstance itself.
    }

    public void Undo()
    {
        // Restore the pre-revert snapshot
        var sceneService = EditorServices.Get<ISceneService>();
        var scene = sceneService.CurrentScene;
        if (scene == null) return;

        // Find and remove the reverted instance (the new one created by Execute)
        // We can't use _instanceRootId because that was the OLD one which was disposed.
        // Instead, find by prefab link match + parent
        GameObject? toRemove = null;
        foreach (var go in scene.AllObjects)
        {
            if (go.PrefabLink is { IsRoot: true } pl &&
                pl.PrefabAssetPath == _link.PrefabAssetPath)
            {
                // Check parent match
                if (_parentId == null && go.Parent == null)
                {
                    toRemove = go;
                    break;
                }
                if (_parentId != null && go.Parent?.InstanceID == _parentId)
                {
                    toRemove = go;
                    break;
                }
            }
        }

        if (toRemove != null)
        {
            scene.Remove(toRemove);
            toRemove.Dispose();
        }

        // Deserialize the snapshot
        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);
        GameObject? restored = Serializer.Deserialize<GameObject>(_preRevertSnapshot, ctx);
        if (restored == null) return;

        scene.Add(restored);

        if (_parentId != null)
        {
            var parent = FindById(sceneService, _parentId.Value);
            if (parent != null)
                restored.SetParent(parent, false);
        }

        _instanceRootId = restored.InstanceID;

        if (EditorServices.TryGet<ISelectionService>(out var sel))
            sel!.ActiveObject = restored;
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
