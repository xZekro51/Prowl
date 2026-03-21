// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Runtime.Resources;

using Xunit;

namespace Prowl.Runtime.Test;

/// <summary>
/// Tests for Scene and GameObject hierarchy operations beyond what
/// LifecycleTests covers: add/remove, parent/child, find operations,
/// scene clear/flush, indexed lookups, and multiple-scene scenarios.
/// </summary>
public class SceneAndGameObjectTests : IDisposable
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

    #region Scene.Add / Scene.Remove

    [Fact]
    public void Add_IncreasesCount()
    {
        var scene = CreateScene();
        var go = CreateGameObject();

        scene.Add(go);

        Assert.Equal(1, scene.Count);
    }

    [Fact]
    public void Add_SetsSceneOnGameObject()
    {
        var scene = CreateScene();
        var go = CreateGameObject();

        scene.Add(go);

        Assert.Equal(scene, go.Scene);
    }

    [Fact]
    public void Remove_DecrementsCount()
    {
        var scene = CreateScene();
        var go = CreateGameObject();
        scene.Add(go);

        scene.Remove(go);

        Assert.Equal(0, scene.Count);
    }

    [Fact]
    public void Remove_ClearsSceneOnGameObject()
    {
        var scene = CreateScene();
        var go = CreateGameObject();
        scene.Add(go);

        scene.Remove(go);

        Assert.Null(go.Scene);
    }

    [Fact]
    public void Add_WithChildren_AddsAllToScene()
    {
        var scene = CreateScene();
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.SetParent(parent);

        scene.Add(parent);

        Assert.Equal(2, scene.Count);
        Assert.Equal(scene, child.Scene);
    }

    [Fact]
    public void Remove_WithChildren_RemovesAllFromScene()
    {
        var scene = CreateScene();
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.SetParent(parent);
        scene.Add(parent);

        scene.Remove(parent);

        Assert.Equal(0, scene.Count);
        Assert.Null(parent.Scene);
        Assert.Null(child.Scene);
    }

    [Fact]
    public void Add_TransfersFromAnotherScene()
    {
        var scene1 = CreateScene();
        var scene2 = CreateScene();
        var go = CreateGameObject();
        scene1.Add(go);

        scene2.Add(go);

        Assert.Equal(0, scene1.Count);
        Assert.Equal(1, scene2.Count);
        Assert.Equal(scene2, go.Scene);
    }

    [Fact]
    public void Add_DuplicateIsNoOp()
    {
        var scene = CreateScene();
        var go = CreateGameObject();
        scene.Add(go);

        scene.Add(go);

        Assert.Equal(1, scene.Count);
    }

    #endregion

    #region Scene.Clear / Scene.Flush

    [Fact]
    public void Clear_RemovesAllObjects()
    {
        var scene = CreateScene();
        scene.Enable();
        scene.Add(CreateGameObject("A"));
        scene.Add(CreateGameObject("B"));
        scene.Add(CreateGameObject("C"));

        scene.Clear();

        Assert.Equal(0, scene.Count);
    }

    [Fact]
    public void Flush_RemovesDisposedObjects()
    {
        var scene = CreateScene();
        var go1 = CreateGameObject("Alive");
        var go2 = CreateGameObject("Dead");
        scene.Add(go1);
        scene.Add(go2);

        go2.Dispose();
        _gameObjects.Remove(go2);

        scene.Flush();

        Assert.Equal(1, scene.Count);
    }

    [Fact]
    public void IsEmpty_TrueForNewScene()
    {
        var scene = CreateScene();
        Assert.True(scene.IsEmpty);
    }

    [Fact]
    public void IsEmpty_FalseAfterAdd()
    {
        var scene = CreateScene();
        scene.Add(CreateGameObject());
        Assert.False(scene.IsEmpty);
    }

    #endregion

    #region GameObject Hierarchy — SetParent

    [Fact]
    public void SetParent_EstablishesParentChildRelationship()
    {
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");

        child.SetParent(parent);

        Assert.Equal(parent, child.Parent);
        Assert.Contains(child, parent.Children);
    }

    [Fact]
    public void SetParent_Null_Unparents()
    {
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.SetParent(parent);

        child.SetParent(null);

        Assert.Null(child.Parent);
        Assert.DoesNotContain(child, parent.Children);
    }

    [Fact]
    public void SetParent_ToSelf_ReturnsFalse()
    {
        var go = CreateGameObject();

        bool result = go.SetParent(go);

        Assert.False(result);
    }

    [Fact]
    public void SetParent_CircularReference_ReturnsFalse()
    {
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.SetParent(parent);

        bool result = parent.SetParent(child);

        Assert.False(result);
    }

    [Fact]
    public void SetParent_SameParent_ReturnsTrue()
    {
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.SetParent(parent);

        bool result = child.SetParent(parent);

        Assert.True(result);
    }

    [Fact]
    public void SetParent_MovesChildToNewParent()
    {
        var parent1 = CreateGameObject("Parent1");
        var parent2 = CreateGameObject("Parent2");
        var child = CreateGameObject("Child");
        child.SetParent(parent1);

        child.SetParent(parent2);

        Assert.Equal(parent2, child.Parent);
        Assert.DoesNotContain(child, parent1.Children);
        Assert.Contains(child, parent2.Children);
    }

    [Fact]
    public void IsChildOf_ReturnsTrueForDirectParent()
    {
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.SetParent(parent);

        Assert.True(child.IsChildOf(parent));
    }

    [Fact]
    public void IsChildOf_ReturnsTrueForGrandparent()
    {
        var grandparent = CreateGameObject("Grandparent");
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        parent.SetParent(grandparent);
        child.SetParent(parent);

        Assert.True(child.IsChildOf(grandparent));
    }

    [Fact]
    public void IsChildOf_ReturnsFalseForUnrelated()
    {
        var a = CreateGameObject("A");
        var b = CreateGameObject("B");

        Assert.False(a.IsChildOf(b));
    }

    [Fact]
    public void IsParentOf_ReturnsTrueForDirectChild()
    {
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.SetParent(parent);

        Assert.True(parent.IsParentOf(child));
    }

    [Fact]
    public void IsParentOf_ReturnsTrueForGrandchild()
    {
        var grandparent = CreateGameObject("Grandparent");
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        parent.SetParent(grandparent);
        child.SetParent(parent);

        Assert.True(grandparent.IsParentOf(child));
    }

    [Fact]
    public void GetChildrenDeep_ReturnsAllDescendants()
    {
        var root = CreateGameObject("Root");
        var child1 = CreateGameObject("Child1");
        var child2 = CreateGameObject("Child2");
        var grandchild = CreateGameObject("Grandchild");

        child1.SetParent(root);
        child2.SetParent(root);
        grandchild.SetParent(child1);

        var deep = root.GetChildrenDeep().ToList();

        Assert.Equal(3, deep.Count);
        Assert.Contains(child1, deep);
        Assert.Contains(child2, deep);
        Assert.Contains(grandchild, deep);
    }

    [Fact]
    public void ChildCount_ReturnsDirectChildCount()
    {
        var parent = CreateGameObject("Parent");
        var child1 = CreateGameObject("Child1");
        var child2 = CreateGameObject("Child2");
        child1.SetParent(parent);
        child2.SetParent(parent);

        Assert.Equal(2, parent.ChildCount);
    }

    #endregion

    #region GameObject — Sibling Index

    [Fact]
    public void GetSiblingIndex_ReturnsCorrectIndex()
    {
        var parent = CreateGameObject("Parent");
        var child0 = CreateGameObject("Child0");
        var child1 = CreateGameObject("Child1");
        child0.SetParent(parent);
        child1.SetParent(parent);

        Assert.Equal(0, child0.GetSiblingIndex());
        Assert.Equal(1, child1.GetSiblingIndex());
    }

    [Fact]
    public void SetSiblingIndex_ReordersChildren()
    {
        var parent = CreateGameObject("Parent");
        var child0 = CreateGameObject("Child0");
        var child1 = CreateGameObject("Child1");
        var child2 = CreateGameObject("Child2");
        child0.SetParent(parent);
        child1.SetParent(parent);
        child2.SetParent(parent);

        child2.SetSiblingIndex(0);

        Assert.Equal(0, child2.GetSiblingIndex());
        Assert.Equal(1, child0.GetSiblingIndex());
        Assert.Equal(2, child1.GetSiblingIndex());
    }

    [Fact]
    public void GetSiblingIndex_NoParent_ReturnsNull()
    {
        var go = CreateGameObject();

        Assert.Null(go.GetSiblingIndex());
    }

    #endregion

    #region Scene Enumeration Properties

    [Fact]
    public void AllObjects_EnumeratesNonDisposed()
    {
        var scene = CreateScene();
        var go1 = CreateGameObject("A");
        var go2 = CreateGameObject("B");
        scene.Add(go1);
        scene.Add(go2);

        var all = scene.AllObjects.ToList();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void ActiveObjects_OnlyEnumeratesEnabledInHierarchy()
    {
        var scene = CreateScene();
        var go1 = CreateGameObject("Enabled");
        var go2 = CreateGameObject("Disabled");
        go2.Enabled = false;
        scene.Add(go1);
        scene.Add(go2);

        var active = scene.ActiveObjects.ToList();

        Assert.Single(active);
        Assert.Equal(go1, active[0]);
    }

    [Fact]
    public void RootObjects_ExcludesChildren()
    {
        var scene = CreateScene();
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.SetParent(parent);
        scene.Add(parent);

        var roots = scene.RootObjects.ToList();

        Assert.Single(roots);
        Assert.Equal(parent, roots[0]);
    }

    #endregion

    #region Scene.FindObjectByID / FindObjectByIdentifier

    [Fact]
    public void FindObjectByID_ReturnsGameObject()
    {
        var scene = CreateScene();
        var go = CreateGameObject("Target");
        scene.Add(go);

        var found = scene.FindObjectByID<GameObject>(go.InstanceID);

        Assert.NotNull(found);
        Assert.Equal(go, found);
    }

    [Fact]
    public void FindObjectByID_ReturnsComponent()
    {
        var scene = CreateScene();
        var go = CreateGameObject();
        var comp = go.AddComponent<TestLifecycleComponent>();
        scene.Add(go);

        var found = scene.FindObjectByID<TestLifecycleComponent>(comp.InstanceID);

        Assert.NotNull(found);
        Assert.Equal(comp, found);
    }

    [Fact]
    public void FindObjectByID_ReturnsNull_ForUnknownID()
    {
        var scene = CreateScene();

        var found = scene.FindObjectByID<GameObject>(99999);

        Assert.Null(found);
    }

    [Fact]
    public void FindObjectByIdentifier_ReturnsGameObject()
    {
        var scene = CreateScene();
        var go = CreateGameObject("Target");
        scene.Add(go);

        var found = scene.FindObjectByIdentifier<GameObject>(go.Identifier);

        Assert.NotNull(found);
        Assert.Equal(go, found);
    }

    [Fact]
    public void FindObjectByIdentifier_ReturnsNull_ForUnknownGuid()
    {
        var scene = CreateScene();

        var found = scene.FindObjectByIdentifier<GameObject>(Guid.NewGuid());

        Assert.Null(found);
    }

    #endregion

    #region Scene.FindObjectsOfType

    [Fact]
    public void FindObjectsOfType_FindsComponents()
    {
        var scene = CreateScene();
        var go1 = CreateGameObject("A");
        var go2 = CreateGameObject("B");
        go1.AddComponent<TestLifecycleComponent>();
        go2.AddComponent<TestLifecycleComponent>();
        scene.Add(go1);
        scene.Add(go2);

        var found = scene.FindObjectsOfType<TestLifecycleComponent>();

        Assert.Equal(2, found.Length);
    }

    [Fact]
    public void FindObjectsOfType_FindsGameObjects()
    {
        var scene = CreateScene();
        scene.Add(CreateGameObject("A"));
        scene.Add(CreateGameObject("B"));

        var found = scene.FindObjectsOfType<GameObject>();

        Assert.Equal(2, found.Length);
    }

    [Fact]
    public void FindObjectsOfType_ReturnsEmpty_WhenNoMatch()
    {
        var scene = CreateScene();
        scene.Add(CreateGameObject());

        var found = scene.FindObjectsOfType<TestLifecycleComponent>();

        Assert.Empty(found);
    }

    #endregion

    #region GameObject.Find / FindGameObjectWithTag

    [Fact]
    public void Find_ByName_ReturnsFirstMatch()
    {
        var scene = CreateScene();
        var go1 = CreateGameObject("Target");
        var go2 = CreateGameObject("Other");
        scene.Add(go1);
        scene.Add(go2);

        var found = go1.Find("Target");

        Assert.NotNull(found);
        Assert.Equal("Target", found.Name);
    }

    [Fact]
    public void Find_ByName_ReturnsNull_WhenNotFound()
    {
        var scene = CreateScene();
        var go = CreateGameObject("A");
        scene.Add(go);

        var found = go.Find("NonExistent");

        Assert.Null(found);
    }

    [Fact]
    public void Find_ByName_CaseInsensitive()
    {
        var scene = CreateScene();
        var go = CreateGameObject("Target");
        scene.Add(go);

        var found = go.Find("target", ignoreCase: true);

        Assert.NotNull(found);
    }

    #endregion

    #region Scene.Dispose

    [Fact]
    public void Dispose_ClearsAllObjects()
    {
        var scene = CreateScene();
        scene.Enable();
        var go = CreateGameObject();
        go.AddComponent<TestLifecycleComponent>();
        scene.Add(go);

        scene.Dispose();
        _scenes.Remove(scene);
        _gameObjects.Remove(go);

        Assert.True(scene.IsDisposed);
        Assert.Equal(0, scene.Count);
    }

    #endregion

    #region GameObject — Enabled / EnabledInHierarchy

    [Fact]
    public void EnabledInHierarchy_FalseWhenParentDisabled()
    {
        var scene = CreateScene();
        scene.Enable();
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.SetParent(parent);
        scene.Add(parent);

        parent.Enabled = false;

        Assert.False(child.EnabledInHierarchy);
    }

    [Fact]
    public void EnabledInHierarchy_TrueWhenParentReEnabled()
    {
        var scene = CreateScene();
        scene.Enable();
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.SetParent(parent);
        scene.Add(parent);
        parent.Enabled = false;

        parent.Enabled = true;

        Assert.True(child.EnabledInHierarchy);
    }

    [Fact]
    public void EnabledInHierarchy_StaysFalse_WhenChildSelfDisabled()
    {
        var scene = CreateScene();
        scene.Enable();
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.SetParent(parent);
        scene.Add(parent);

        child.Enabled = false;

        Assert.False(child.EnabledInHierarchy);
        Assert.True(parent.EnabledInHierarchy);
    }

    #endregion

    #region GameObject.FindChildByIdentifier

    [Fact]
    public void FindChildByIdentifier_FindsDirectChild()
    {
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.SetParent(parent);

        var found = parent.FindChildByIdentifier(child.Identifier);

        Assert.Equal(child, found);
    }

    [Fact]
    public void FindChildByIdentifier_FindsGrandchild_Deep()
    {
        var root = CreateGameObject("Root");
        var child = CreateGameObject("Child");
        var grandchild = CreateGameObject("Grandchild");
        child.SetParent(root);
        grandchild.SetParent(child);

        var found = root.FindChildByIdentifier(grandchild.Identifier, deep: true);

        Assert.Equal(grandchild, found);
    }

    [Fact]
    public void FindChildByIdentifier_ReturnsSelf_WhenMatchesSelf()
    {
        var go = CreateGameObject("Self");

        var found = go.FindChildByIdentifier(go.Identifier);

        Assert.Equal(go, found);
    }

    [Fact]
    public void FindChildByIdentifier_ReturnsNull_WhenNotFound()
    {
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.SetParent(parent);

        var found = parent.FindChildByIdentifier(Guid.NewGuid());

        Assert.Null(found);
    }

    #endregion

    #region GetChildAtIndexPath / GetIndexPathOfChild

    [Fact]
    public void GetChildAtIndexPath_NavigatesCorrectly()
    {
        var root = CreateGameObject("Root");
        var child0 = CreateGameObject("Child0");
        var child1 = CreateGameObject("Child1");
        var grandchild = CreateGameObject("Grandchild");

        child0.SetParent(root);
        child1.SetParent(root);
        grandchild.SetParent(child1);

        // Path to grandchild: child at index 1, then its child at index 0
        var found = root.GetChildAtIndexPath([1, 0]);

        Assert.Equal(grandchild, found);
    }

    [Fact]
    public void GetIndexPathOfChild_ReturnsCorrectPath()
    {
        var root = CreateGameObject("Root");
        var child0 = CreateGameObject("Child0");
        var child1 = CreateGameObject("Child1");
        var grandchild = CreateGameObject("Grandchild");

        child0.SetParent(root);
        child1.SetParent(root);
        grandchild.SetParent(child1);

        var path = root.GetIndexPathOfChild(grandchild);

        Assert.Equal([1, 0], path);
    }

    [Fact]
    public void GetChildAtIndexPath_ReturnsNull_ForInvalidIndex()
    {
        var root = CreateGameObject("Root");

        var found = root.GetChildAtIndexPath([99]);

        Assert.Null(found);
    }

    #endregion

    #region RegenerateIdentifiers

    [Fact]
    public void RegenerateIdentifiers_ChangesGuid()
    {
        var go = CreateGameObject();
        var originalId = go.Identifier;

        go.RegenerateIdentifiers();

        Assert.NotEqual(originalId, go.Identifier);
    }

    [Fact]
    public void RegenerateIdentifiers_ChangesChildGuids()
    {
        var parent = CreateGameObject("Parent");
        var child = CreateGameObject("Child");
        child.SetParent(parent);

        var originalParentId = parent.Identifier;
        var originalChildId = child.Identifier;

        parent.RegenerateIdentifiers();

        Assert.NotEqual(originalParentId, parent.Identifier);
        Assert.NotEqual(originalChildId, child.Identifier);
    }

    #endregion
}
