// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Editor.Services;

/// <summary>
/// Default scene service backed by Prowl's built-in Scene class.
/// Populates new scenes with sample GameObjects for development/testing.
/// </summary>
public sealed class DefaultSceneService : ISceneService
{
    /// <summary> Stores root-object snapshots for play-mode save/restore. </summary>
    private List<SnapshotEntry>? _snapshot;

    private record SnapshotEntry(string Name, Prowl.Vector.Float3 Position, Prowl.Vector.Float3 Rotation, Prowl.Vector.Float3 Scale);

    public Scene? CurrentScene => Scene.Current;

    private bool _isDirty;
    public bool IsDirty => _isDirty;

    public string? SceneFilePath { get; set; }

    public event Action<bool>? DirtyStateChanged;
    public event Action<Scene>? SceneLoaded;

    public void MarkDirty()
    {
        if (!_isDirty)
        {
            _isDirty = true;
            DirtyStateChanged?.Invoke(true);
        }
    }

    public void ClearDirty()
    {
        if (_isDirty)
        {
            _isDirty = false;
            DirtyStateChanged?.Invoke(false);
        }
    }

    public Scene CreateNewScene(string name = "Untitled")
    {
        var scene = new Scene { Name = name };
        Scene.Load(scene);

        // Populate with sample objects so the hierarchy has content
        PopulateSampleScene(scene);

        SceneFilePath = null;
        ClearDirty();
        SceneLoaded?.Invoke(scene);

        return scene;
    }

    public void SetScene(Scene scene)
    {
        Scene.Load(scene);
        ClearDirty();
        SceneLoaded?.Invoke(scene);
    }

    public IEnumerable<GameObject> GetRootGameObjects()
    {
        if (CurrentScene == null)
            return Enumerable.Empty<GameObject>();

        return CurrentScene.RootObjects
            .Where(o => !o.HideFlags.HasFlag(HideFlags.Hide)
                     && !o.HideFlags.HasFlag(HideFlags.HideAndDontSave));
    }

    public GameObject CreateGameObject(string name = "New GameObject")
    {
        if (CurrentScene == null)
            CreateNewScene();

        var go = new GameObject(name);
        CurrentScene!.Add(go);
        return go;
    }

    public void DestroyGameObject(GameObject go)
    {
        if (CurrentScene == null) return;

        CurrentScene.Remove(go);
        go.Dispose();
    }

    public object? SnapshotScene()
    {
        if (CurrentScene == null) return null;

        var entries = new List<SnapshotEntry>();
        foreach (var go in CurrentScene.AllObjects)
        {
            entries.Add(new SnapshotEntry(
                go.Name,
                go.Transform.LocalPosition,
                go.Transform.LocalEulerAngles,
                go.Transform.LocalScale));
        }
        _snapshot = entries;
        return _snapshot;
    }

    public void RestoreScene(object? snapshot)
    {
        if (snapshot is not List<SnapshotEntry> entries) return;
        if (CurrentScene == null) return;

        // Restore transforms of existing objects by matching name+order
        var allObjects = CurrentScene.AllObjects.ToList();
        for (int i = 0; i < Math.Min(entries.Count, allObjects.Count); i++)
        {
            var go = allObjects[i];
            var e = entries[i];
            go.Name = e.Name;
            go.Transform.LocalPosition = e.Position;
            go.Transform.LocalEulerAngles = e.Rotation;
            go.Transform.LocalScale = e.Scale;
        }

        _snapshot = null;
    }

    public Scene? CloneCurrentScene()
    {
        if (CurrentScene == null) return null;

        // Create a new scene and duplicate root objects with their hierarchy
        var clone = new Scene { Name = CurrentScene.Name + " (Play)" };
        foreach (var rootGo in CurrentScene.RootObjects)
        {
            CloneGameObjectHierarchy(rootGo, clone, null);
        }
        return clone;
    }

    private static void CloneGameObjectHierarchy(GameObject source, Scene targetScene, GameObject? parent)
    {
        var clone = new GameObject(source.Name);
        clone.Transform.LocalPosition = source.Transform.LocalPosition;
        clone.Transform.LocalEulerAngles = source.Transform.LocalEulerAngles;
        clone.Transform.LocalScale = source.Transform.LocalScale;

        if (parent != null)
            clone.SetParent(parent, false);
        else
            targetScene.Add(clone);

        foreach (var child in source.Children)
        {
            CloneGameObjectHierarchy(child, targetScene, clone);
        }
    }

    private static void PopulateSampleScene(Scene scene)
    {
        // Directional Light (with component)
        var lightGO = new GameObject("Directional Light");
        lightGO.AddComponent<DirectionalLight>();
        lightGO.Transform.LocalEulerAngles = new Float3(-80, 5, 0);
        scene.Add(lightGO);

        // Main Camera (with Camera component, HDR, and post-processing effects)
        var cameraGO = new GameObject("Main Camera");
        cameraGO.Tag = "Main Camera";
        cameraGO.Transform.LocalPosition = new Float3(0, 2, -8);
        Camera cam = cameraGO.AddComponent<Camera>();
        cam.Depth = -1;
        cam.HDR = true;
        cam.Effects =
        [
            new FXAAEffect(),
            new KawaseBloomEffect(),
            new TonemapperEffect(),
        ];
        scene.Add(cameraGO);

        // Ground plane (visible mesh)
        var groundGO = new GameObject("Ground");
        MeshRenderer mr = groundGO.AddComponent<MeshRenderer>();
        mr.Mesh = Mesh.CreateCube(Float3.One);
        mr.Material = new Material(Shader.LoadDefault(DefaultShader.Standard));
        groundGO.Transform.LocalPosition = new Float3(0, -3, 0);
        groundGO.Transform.LocalScale = new Float3(20, 1, 20);
        scene.Add(groundGO);
    }
}
