// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Runtime.Resources;

using Xunit;

namespace Prowl.Runtime.Test;

/// <summary>
/// A second test component to verify multi-type component queries.
/// </summary>
public class SecondTestComponent : MonoBehaviour
{
    public int Value { get; set; }
}

/// <summary>
/// Tests for GameObject component query APIs: AddComponent, RemoveComponent,
/// GetComponent, GetComponents, GetComponentInParent, GetComponentInChildren, etc.
/// </summary>
public class GameObjectComponentQueryTests : IDisposable
{
    private readonly List<Scene> _scenes = [];
    private readonly List<GameObject> _gameObjects = [];

    public void Dispose()
    {
        foreach (var scene in _scenes)
        {
            if (!scene.IsDisposed)
            {
                if (scene.IsActive)
                    scene.Disable();
                scene.Dispose();
            }
        }
        _scenes.Clear();

        foreach (var go in _gameObjects)
        {
            if (!go.IsDisposed)
                go.Dispose();
        }
        _gameObjects.Clear();
    }

    private Scene CreateScene()
    {
        var scene = new Scene();
        _scenes.Add(scene);
        return scene;
    }

    private GameObject CreateGameObject(string name = "TestObject")
    {
        var go = new GameObject(name);
        _gameObjects.Add(go);
        return go;
    }

    #region AddComponent

    [Fact]
    public void AddComponent_Generic_ReturnsCorrectType()
    {
        var go = CreateGameObject();

        var comp = go.AddComponent<TestLifecycleComponent>();

        Assert.NotNull(comp);
        Assert.IsType<TestLifecycleComponent>(comp);
    }

    [Fact]
    public void AddComponent_ByType_ReturnsCorrectType()
    {
        var go = CreateGameObject();

        var comp = go.AddComponent(typeof(TestLifecycleComponent));

        Assert.NotNull(comp);
        Assert.IsType<TestLifecycleComponent>(comp);
    }

    [Fact]
    public void AddComponent_SetsGameObject()
    {
        var go = CreateGameObject();

        var comp = go.AddComponent<TestLifecycleComponent>();

        Assert.Equal(go, comp.GameObject);
    }

    [Fact]
    public void AddComponent_Multiple_AllPresent()
    {
        var go = CreateGameObject();

        var comp1 = go.AddComponent<TestLifecycleComponent>();
        var comp2 = go.AddComponent<SecondTestComponent>();

        Assert.NotNull(comp1);
        Assert.NotNull(comp2);
        Assert.Equal(2, go.GetComponents<MonoBehaviour>().Count());
    }

    [Fact]
    public void AddComponent_ExistingInstance_Attaches()
    {
        var go = CreateGameObject();
        var comp = new TestLifecycleComponent();

        go.AddComponent(comp);

        Assert.Equal(go, comp.GameObject);
        Assert.Contains(comp, go.GetComponents<TestLifecycleComponent>());
    }

    #endregion

    #region RemoveComponent

    [Fact]
    public void RemoveComponent_RemovesFromList()
    {
        var scene = CreateScene();
        scene.Enable();
        var go = CreateGameObject();
        var comp = go.AddComponent<TestLifecycleComponent>();
        scene.Add(go);

        go.RemoveComponent(comp);

        Assert.Empty(go.GetComponents<TestLifecycleComponent>());
    }

    [Fact]
    public void RemoveAll_RemovesAllOfType()
    {
        var scene = CreateScene();
        scene.Enable();
        var go = CreateGameObject();
        go.AddComponent<TestLifecycleComponent>();
        go.AddComponent<TestLifecycleComponent>();
        go.AddComponent<SecondTestComponent>();
        scene.Add(go);

        go.RemoveAll<TestLifecycleComponent>();

        Assert.Empty(go.GetComponents<TestLifecycleComponent>());
        Assert.Single(go.GetComponents<SecondTestComponent>());
    }

    #endregion

    #region GetComponent

    [Fact]
    public void GetComponent_Generic_ReturnsFirst()
    {
        var go = CreateGameObject();
        var comp = go.AddComponent<TestLifecycleComponent>();

        var found = go.GetComponent<TestLifecycleComponent>();

        Assert.Equal(comp, found);
    }

    [Fact]
    public void GetComponent_ReturnsNull_WhenNotPresent()
    {
        var go = CreateGameObject();

        var found = go.GetComponent<TestLifecycleComponent>();

        Assert.Null(found);
    }

    [Fact]
    public void TryGetComponent_ReturnsTrueWhenFound()
    {
        var go = CreateGameObject();
        go.AddComponent<TestLifecycleComponent>();

        bool result = go.TryGetComponent<TestLifecycleComponent>(out var comp);

        Assert.True(result);
        Assert.NotNull(comp);
    }

    [Fact]
    public void TryGetComponent_ReturnsFalseWhenNotFound()
    {
        var go = CreateGameObject();

        bool result = go.TryGetComponent<TestLifecycleComponent>(out var comp);

        Assert.False(result);
    }

    #endregion

    #region GetComponents

    [Fact]
    public void GetComponents_ReturnsAll()
    {
        var go = CreateGameObject();
        go.AddComponent<TestLifecycleComponent>();
        go.AddComponent<TestLifecycleComponent>();

        var all = go.GetComponents<TestLifecycleComponent>().ToList();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void GetComponents_MonoBehaviour_ReturnsAllTypes()
    {
        var go = CreateGameObject();
        go.AddComponent<TestLifecycleComponent>();
        go.AddComponent<SecondTestComponent>();

        var all = go.GetComponents<MonoBehaviour>().ToList();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void GetComponentByIdentifier_ReturnsCorrectComponent()
    {
        var go = CreateGameObject();
        var comp = go.AddComponent<TestLifecycleComponent>();

        var found = go.GetComponentByIdentifier(comp.Identifier);

        Assert.Equal(comp, found);
    }

    [Fact]
    public void GetComponentByIdentifier_ReturnsNull_ForUnknownGuid()
    {
        var go = CreateGameObject();
        go.AddComponent<TestLifecycleComponent>();

        var found = go.GetComponentByIdentifier(Guid.NewGuid());

        Assert.Null(found);
    }

    #endregion

    #region GetComponentInParent

    [Fact]
    public void GetComponentInParent_FindsOnSelf()
    {
        var scene = CreateScene();
        scene.Enable();
        var go = CreateGameObject();
        go.AddComponent<TestLifecycleComponent>();
        scene.Add(go);

        var found = go.GetComponentInParent<TestLifecycleComponent>();

        Assert.NotNull(found);
    }

    [Fact]
    public void GetComponentInParent_FindsOnParent()
    {
        var scene = CreateScene();
        scene.Enable();
        var parent = CreateGameObject("Parent");
        parent.AddComponent<TestLifecycleComponent>();
        var child = CreateGameObject("Child");
        child.SetParent(parent);
        scene.Add(parent);

        var found = child.GetComponentInParent<TestLifecycleComponent>();

        Assert.NotNull(found);
    }

    [Fact]
    public void GetComponentInParent_ExcludesSelf()
    {
        var scene = CreateScene();
        scene.Enable();
        var parent = CreateGameObject("Parent");
        parent.AddComponent<TestLifecycleComponent>();
        var child = CreateGameObject("Child");
        child.AddComponent<SecondTestComponent>();
        child.SetParent(parent);
        scene.Add(parent);

        var found = child.GetComponentInParent<TestLifecycleComponent>(includeSelf: false);

        Assert.NotNull(found);
        Assert.Equal(parent, found!.GameObject);
    }

    [Fact]
    public void GetComponentInParent_ReturnsNull_WhenNotFound()
    {
        var scene = CreateScene();
        scene.Enable();
        var go = CreateGameObject();
        scene.Add(go);

        var found = go.GetComponentInParent<TestLifecycleComponent>();

        Assert.Null(found);
    }

    #endregion

    #region GetComponentInChildren

    [Fact]
    public void GetComponentInChildren_FindsOnSelf()
    {
        var scene = CreateScene();
        scene.Enable();
        var go = CreateGameObject();
        go.AddComponent<TestLifecycleComponent>();
        scene.Add(go);

        var found = go.GetComponentInChildren<TestLifecycleComponent>();

        Assert.NotNull(found);
    }

    [Fact]
    public void GetComponentInChildren_FindsOnChild()
    {
        var scene = CreateScene();
        scene.Enable();
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.AddComponent<TestLifecycleComponent>();
        child.SetParent(parent);
        scene.Add(parent);

        var found = parent.GetComponentInChildren<TestLifecycleComponent>();

        Assert.NotNull(found);
    }

    [Fact]
    public void GetComponentInChildren_FindsOnGrandchild()
    {
        var scene = CreateScene();
        scene.Enable();
        var root = CreateGameObject("Root");
        var child = CreateGameObject("Child");
        var grandchild = CreateGameObject("Grandchild");
        grandchild.AddComponent<TestLifecycleComponent>();
        child.SetParent(root);
        grandchild.SetParent(child);
        scene.Add(root);

        var found = root.GetComponentInChildren<TestLifecycleComponent>();

        Assert.NotNull(found);
    }

    [Fact]
    public void GetComponentInChildren_ExcludesSelf()
    {
        var scene = CreateScene();
        scene.Enable();
        var parent = CreateGameObject("Parent");
        parent.AddComponent<TestLifecycleComponent>();
        var child = CreateGameObject("Child");
        child.AddComponent<SecondTestComponent>();
        child.SetParent(parent);
        scene.Add(parent);

        var found = parent.GetComponentInChildren<SecondTestComponent>(includeSelf: false);

        Assert.NotNull(found);
        Assert.Equal(child, found!.GameObject);
    }

    [Fact]
    public void GetComponentInChildren_SkipsDisabledChildren()
    {
        var scene = CreateScene();
        scene.Enable();
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.Enabled = false;
        child.AddComponent<TestLifecycleComponent>();
        child.SetParent(parent);
        scene.Add(parent);

        var found = parent.GetComponentInChildren<TestLifecycleComponent>(includeSelf: false);

        Assert.Null(found);
    }

    [Fact]
    public void GetComponentInChildren_IncludesInactive_WhenFlagSet()
    {
        var scene = CreateScene();
        scene.Enable();
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.Enabled = false;
        child.AddComponent<TestLifecycleComponent>();
        child.SetParent(parent);
        scene.Add(parent);

        var found = parent.GetComponentInChildren<TestLifecycleComponent>(includeSelf: false, includeInactive: true);

        Assert.NotNull(found);
    }

    #endregion

    #region GetComponentsInChildren

    [Fact]
    public void GetComponentsInChildren_ReturnsAll()
    {
        var scene = CreateScene();
        scene.Enable();
        var parent = CreateGameObject("Parent");
        parent.AddComponent<TestLifecycleComponent>();
        var child = CreateGameObject("Child");
        child.AddComponent<TestLifecycleComponent>();
        child.SetParent(parent);
        scene.Add(parent);

        var all = parent.GetComponentsInChildren<TestLifecycleComponent>().ToList();

        Assert.Equal(2, all.Count);
    }

    #endregion

    #region GetComponentsInParent

    [Fact]
    public void GetComponentsInParent_ReturnsAll()
    {
        var scene = CreateScene();
        scene.Enable();
        var grandparent = CreateGameObject("Grandparent");
        grandparent.AddComponent<TestLifecycleComponent>();
        var parent = CreateGameObject("Parent");
        parent.AddComponent<TestLifecycleComponent>();
        var child = CreateGameObject("Child");
        child.AddComponent<TestLifecycleComponent>();

        parent.SetParent(grandparent);
        child.SetParent(parent);
        scene.Add(grandparent);

        var all = child.GetComponentsInParent<TestLifecycleComponent>().ToList();

        Assert.Equal(3, all.Count);
    }

    #endregion

    #region Component SiblingIndex

    [Fact]
    public void Component_GetSiblingIndex_ReturnsCorrectOrder()
    {
        var go = CreateGameObject();
        var comp0 = go.AddComponent<TestLifecycleComponent>();
        var comp1 = go.AddComponent<SecondTestComponent>();

        // They should have distinct indices
        int? idx0 = comp0.GetSiblingIndex();
        int? idx1 = comp1.GetSiblingIndex();

        Assert.NotNull(idx0);
        Assert.NotNull(idx1);
        Assert.NotEqual(idx0, idx1);
    }

    #endregion
}
