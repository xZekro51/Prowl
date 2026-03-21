// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Services;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

using Xunit;

namespace Prowl.Editor.Tests;

/// <summary>
/// A serializable test component with various field types
/// to verify serialization round-trips.
/// </summary>
public class SerializableTestComponent : MonoBehaviour
{
    public int IntValue;
    public float FloatValue;
    public string StringValue = string.Empty;
    public bool BoolValue;
}

/// <summary>
/// Serialization round-trip tests for scenes with various component types.
/// Validates that scene data survives JSON and Binary serialization.
/// </summary>
public sealed class SerializationRoundTripTests : IDisposable
{
    private readonly string _tempDir;

    public SerializationRoundTripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ProwlSerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    #region JSON Round-Trip

    [Fact]
    public void Json_EmptyScene_RoundTrips()
    {
        var serializer = new JsonSceneSerializer();
        var scene = new Scene { Name = "Empty" };

        string path = Path.Combine(_tempDir, "empty.scene");
        serializer.Save(scene, path);
        Scene? loaded = serializer.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(0, loaded!.Count);
    }

    [Fact]
    public void Json_SceneWithGameObject_RoundTrips()
    {
        var serializer = new JsonSceneSerializer();
        var scene = new Scene { Name = "WithGO" };
        var go = new GameObject("TestObj");
        scene.Add(go);

        string path = Path.Combine(_tempDir, "go.scene");
        serializer.Save(scene, path);
        Scene? loaded = serializer.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.Count);

        var loadedGo = loaded.AllObjects.First();
        Assert.Equal("TestObj", loadedGo.Name);
    }

    [Fact]
    public void Json_SceneWithHierarchy_PreservesParentChild()
    {
        var serializer = new JsonSceneSerializer();
        var scene = new Scene { Name = "Hierarchy" };
        var parent = new GameObject("Parent");
        var child = new GameObject("Child");
        child.SetParent(parent);
        scene.Add(parent);

        string path = Path.Combine(_tempDir, "hierarchy.scene");
        serializer.Save(scene, path);
        Scene? loaded = serializer.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Count);

        var roots = loaded.RootObjects.ToList();
        Assert.Single(roots);
        Assert.Equal("Parent", roots[0].Name);
        Assert.Single(roots[0].Children);
        Assert.Equal("Child", roots[0].Children[0].Name);
    }

    [Fact]
    public void Json_SceneWithMultipleObjects_PreservesCount()
    {
        var serializer = new JsonSceneSerializer();
        var scene = new Scene { Name = "Multi" };
        for (int i = 0; i < 5; i++)
            scene.Add(new GameObject($"Obj_{i}"));

        string path = Path.Combine(_tempDir, "multi.scene");
        serializer.Save(scene, path);
        Scene? loaded = serializer.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(5, loaded!.Count);
    }

    [Fact]
    public void Json_SceneWithComponent_PreservesComponentData()
    {
        var serializer = new JsonSceneSerializer();
        var scene = new Scene { Name = "WithComp" };
        var go = new GameObject("CompObj");
        var comp = go.AddComponent<SerializableTestComponent>();
        comp.IntValue = 42;
        comp.FloatValue = 3.14f;
        comp.StringValue = "Hello";
        comp.BoolValue = true;
        scene.Add(go);

        string path = Path.Combine(_tempDir, "comp.scene");
        serializer.Save(scene, path);
        Scene? loaded = serializer.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.Count);

        var loadedGo = loaded.AllObjects.First();
        var loadedComp = loadedGo.GetComponent<SerializableTestComponent>();
        Assert.NotNull(loadedComp);
        Assert.Equal(42, loadedComp!.IntValue);
        Assert.Equal(3.14f, loadedComp.FloatValue, precision: 5);
        Assert.Equal("Hello", loadedComp.StringValue);
        Assert.True(loadedComp.BoolValue);
    }

    [Fact]
    public void Json_DisabledGameObject_PreservesState()
    {
        var serializer = new JsonSceneSerializer();
        var scene = new Scene { Name = "Disabled" };
        var go = new GameObject("DisabledObj") { Enabled = false };
        scene.Add(go);

        string path = Path.Combine(_tempDir, "disabled.scene");
        serializer.Save(scene, path);
        Scene? loaded = serializer.Load(path);

        Assert.NotNull(loaded);
        var loadedGo = loaded!.AllObjects.First();
        Assert.False(loadedGo.Enabled);
    }

    #endregion

    #region Binary Round-Trip

    [Fact]
    public void Binary_SceneWithGameObject_RoundTrips()
    {
        var serializer = new BinarySceneSerializer();
        var scene = new Scene { Name = "BinGO" };
        scene.Add(new GameObject("BinaryObj"));

        string path = Path.Combine(_tempDir, "go.bscene");
        serializer.Save(scene, path);
        Scene? loaded = serializer.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.Count);
        Assert.Equal("BinaryObj", loaded.AllObjects.First().Name);
    }

    [Fact]
    public void Binary_SceneWithHierarchy_PreservesParentChild()
    {
        var serializer = new BinarySceneSerializer();
        var scene = new Scene { Name = "BinHierarchy" };
        var parent = new GameObject("Parent");
        var child = new GameObject("Child");
        child.SetParent(parent);
        scene.Add(parent);

        string path = Path.Combine(_tempDir, "hierarchy.bscene");
        serializer.Save(scene, path);
        Scene? loaded = serializer.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Count);
        var roots = loaded.RootObjects.ToList();
        Assert.Single(roots);
        Assert.Single(roots[0].Children);
    }

    [Fact]
    public void Binary_SceneWithComponent_PreservesComponentData()
    {
        var serializer = new BinarySceneSerializer();
        var scene = new Scene { Name = "BinComp" };
        var go = new GameObject("CompObj");
        var comp = go.AddComponent<SerializableTestComponent>();
        comp.IntValue = 100;
        comp.FloatValue = 2.71f;
        comp.StringValue = "World";
        comp.BoolValue = false;
        scene.Add(go);

        string path = Path.Combine(_tempDir, "comp.bscene");
        serializer.Save(scene, path);
        Scene? loaded = serializer.Load(path);

        Assert.NotNull(loaded);
        var loadedComp = loaded!.AllObjects.First().GetComponent<SerializableTestComponent>();
        Assert.NotNull(loadedComp);
        Assert.Equal(100, loadedComp!.IntValue);
        Assert.Equal(2.71f, loadedComp.FloatValue, precision: 5);
        Assert.Equal("World", loadedComp.StringValue);
        Assert.False(loadedComp.BoolValue);
    }

    #endregion

    #region Cross-Format

    [Fact]
    public void Json_SaveThenBinary_Load_ProducesSameData()
    {
        var jsonSerializer = new JsonSceneSerializer();
        var binarySerializer = new BinarySceneSerializer();

        var scene = new Scene { Name = "CrossFormat" };
        var go = new GameObject("TestObj");
        var comp = go.AddComponent<SerializableTestComponent>();
        comp.IntValue = 77;
        scene.Add(go);

        // Save as JSON
        string jsonPath = Path.Combine(_tempDir, "cross.scene");
        jsonSerializer.Save(scene, jsonPath);
        Scene? fromJson = jsonSerializer.Load(jsonPath);
        Assert.NotNull(fromJson);

        // Re-save as Binary
        string binaryPath = Path.Combine(_tempDir, "cross.bscene");
        binarySerializer.Save(fromJson!, binaryPath);
        Scene? fromBinary = binarySerializer.Load(binaryPath);
        Assert.NotNull(fromBinary);

        Assert.Equal(fromJson!.Count, fromBinary!.Count);
        Assert.Equal(fromJson.Name, fromBinary.Name);

        var jsonComp = fromJson.AllObjects.First().GetComponent<SerializableTestComponent>();
        var binComp = fromBinary.AllObjects.First().GetComponent<SerializableTestComponent>();
        Assert.NotNull(jsonComp);
        Assert.NotNull(binComp);
        Assert.Equal(jsonComp!.IntValue, binComp!.IntValue);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Json_SceneWithDeepHierarchy_RoundTrips()
    {
        var serializer = new JsonSceneSerializer();
        var scene = new Scene { Name = "Deep" };
        var current = new GameObject("Level0");
        scene.Add(current);

        for (int i = 1; i < 10; i++)
        {
            var next = new GameObject($"Level{i}");
            next.SetParent(current);
            current = next;
        }

        string path = Path.Combine(_tempDir, "deep.scene");
        serializer.Save(scene, path);
        Scene? loaded = serializer.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(10, loaded!.Count);

        // Verify depth: root -> 9 levels
        var root = loaded.RootObjects.First();
        int depth = 0;
        var node = root;
        while (node.Children.Count > 0)
        {
            node = node.Children[0];
            depth++;
        }
        Assert.Equal(9, depth);
    }

    /// <summary>
    /// Scene.Name is inherited from EngineObject (virtual property, not a
    /// [SerializeField] backing field), so it is reset by the constructor
    /// during deserialization. This test documents that known limitation.
    /// </summary>
    [Fact]
    public void Json_SceneName_ResetAfterRoundTrip_KnownLimitation()
    {
        var serializer = new JsonSceneSerializer();
        var scene = new Scene { Name = "MyCustomSceneName" };

        string path = Path.Combine(_tempDir, "named.scene");
        serializer.Save(scene, path);
        Scene? loaded = serializer.Load(path);

        Assert.NotNull(loaded);
        // Name is not preserved through Echo serialization — constructor sets "NewScene"
        Assert.Equal("NewScene", loaded!.Name);
    }

    [Fact]
    public void Json_MultipleComponents_AllPreserved()
    {
        var serializer = new JsonSceneSerializer();
        var scene = new Scene { Name = "MultiComp" };
        var go = new GameObject("MultiCompObj");
        var comp1 = go.AddComponent<SerializableTestComponent>();
        comp1.IntValue = 1;
        var comp2 = go.AddComponent<SerializableTestComponent>();
        comp2.IntValue = 2;
        scene.Add(go);

        string path = Path.Combine(_tempDir, "multicomp.scene");
        serializer.Save(scene, path);
        Scene? loaded = serializer.Load(path);

        Assert.NotNull(loaded);
        var loadedGo = loaded!.AllObjects.First();
        var comps = loadedGo.GetComponents<SerializableTestComponent>().ToList();
        Assert.Equal(2, comps.Count);

        var values = comps.Select(c => c.IntValue).OrderBy(v => v).ToList();
        Assert.Equal([1, 2], values);
    }

    #endregion
}
