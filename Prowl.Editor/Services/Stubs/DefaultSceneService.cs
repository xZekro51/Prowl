// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Editor.Services;

/// <summary>
/// Default scene service backed by Prowl's built-in Scene class.
/// Populates new scenes with sample GameObjects for development/testing.
/// Uses Echo serialization for full-fidelity play-mode snapshot/restore.
/// </summary>
public sealed class DefaultSceneService : ISceneService
{
    /// <summary> Full scene data serialized via Echo for play-mode save/restore. </summary>
    private EchoObject? _snapshot;

    public Scene? CurrentScene => Scene.Current;

    private bool _isDirty;
    public bool IsDirty => _isDirty;

    public string? SceneFilePath { get; set; }

    public SceneServiceEvents Events { get; } = new();

    public void MarkDirty()
    {
        if (!_isDirty)
        {
            _isDirty = true;
            Events.InvokeOnDirtyStateChanged(new DirtyStateChangedArgs(true));
        }
    }

    public void ClearDirty()
    {
        if (_isDirty)
        {
            _isDirty = false;
            Events.InvokeOnDirtyStateChanged(new DirtyStateChangedArgs(false));
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
        Events.InvokeOnSceneLoaded(new SceneLoadedArgs(scene));

        return scene;
    }

    public void SetScene(Scene scene)
    {
        Scene.Load(scene);
        ClearDirty();
        Events.InvokeOnSceneLoaded(new SceneLoadedArgs(scene));
        Debug.Log($"Loaded Scene: {scene.Name}");
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

        try
        {
            var ctx = new SerializationContext();
            _snapshot = Serializer.Serialize(typeof(Scene), CurrentScene, ctx);
            return _snapshot;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PlayMode] Failed to snapshot scene: {ex.Message}");
            return null;
        }
    }

    public void RestoreScene(object? snapshot)
    {
        if (snapshot is not EchoObject echoData) return;

        try
        {
            var ctx = new SerializationContext();
            AssetDatabase.ConfigureContext(ctx);
            Scene? restored = Serializer.Deserialize<Scene>(echoData, ctx);

            if (restored != null)
            {
                Scene.Load(restored);
                Events.InvokeOnSceneLoaded(new SceneLoadedArgs(restored));
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PlayMode] Failed to restore scene: {ex.Message}");
        }

        _snapshot = null;
    }

    public Scene? CloneCurrentScene()
    {
        // Cloning is no longer needed — the editor plays the current scene
        // directly and restores from the serialized snapshot on exit.
        // Kept for interface compliance; returns null.
        return null;
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
