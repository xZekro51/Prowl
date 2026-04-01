// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

using Xunit;

namespace Prowl.Runtime.Test;

/// <summary>
/// Tests that scene serialization correctly uses asset references ($assetId)
/// for EngineObjects that have an AssetID set, and serializes inline otherwise.
/// Also verifies deserialization resolves asset references back to objects.
/// </summary>
public sealed class AssetReferenceSerializationTests : IDisposable
{
    private IAssetDatabase? _previousDb;

    public AssetReferenceSerializationTests()
    {
        // Save and clear the global AssetDatabase so tests are isolated
        _previousDb = AssetDatabase.Current;
    }

    public void Dispose()
    {
        AssetDatabase.Current = _previousDb;
    }

    #region Helpers

    /// <summary>
    /// Simple in-memory asset database for tests.
    /// </summary>
    private sealed class MockAssetDatabase : IAssetDatabase
    {
        private readonly Dictionary<Guid, EngineObject> _assets = [];

        public void Register(Guid id, EngineObject obj)
        {
            obj.AssetID = id;
            _assets[id] = obj;
        }

        public EngineObject? Get(Guid assetId) =>
            _assets.TryGetValue(assetId, out var obj) ? obj : null;

        public Guid ResolveAssetId(string assetPath)
        {
            foreach (var kvp in _assets)
                if (kvp.Value.AssetPath == assetPath)
                    return kvp.Key;
            return Guid.Empty;
        }
    }

    /// <summary>Creates a simple triangle mesh for testing.</summary>
    private static Mesh CreateTestMesh()
    {
        var mesh = new Mesh();
        mesh.Vertices = [new Float3(0, 0, 0), new Float3(1, 0, 0), new Float3(0, 1, 0)];
        mesh.Indices = [0, 1, 2];
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Creates a Model with two meshes and one material for testing.</summary>
    private static Model CreateTestModel()
    {
        var mat = new Material(Shader.LoadDefault(DefaultShader.Standard));
        mat.Name = "TestMat";

        var mesh0 = CreateTestMesh();
        mesh0.Name = "Mesh_0";

        var mesh1 = CreateTestMesh();
        mesh1.Name = "Mesh_1";

        var model = new Model("TestModel");
        model.Materials.Add(mat);
        model.Meshes.Add(new ModelMesh("Mesh_0", mesh0, mat));
        model.Meshes.Add(new ModelMesh("Mesh_1", mesh1, mat));

        return model;
    }

    /// <summary>
    /// Mock database that resolves a Model by GUID and supports
    /// sub-resource path resolution, but does NOT directly resolve
    /// sub-resource GUIDs via Get(). This tests the fallback path.
    /// </summary>
    private sealed class SubResourceMockDatabase : IAssetDatabase
    {
        private readonly Guid _modelId;
        private readonly Model _model;

        public SubResourceMockDatabase(Guid modelId, Model model)
        {
            _modelId = modelId;
            _model = model;
        }

        public EngineObject? Get(Guid assetId) =>
            assetId == _modelId ? _model : null;

        public Guid ResolveAssetId(string assetPath) =>
            assetPath == _model.AssetPath ? _modelId : Guid.Empty;

        public EngineObject? ResolveByPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;

            int hashIdx = assetPath.IndexOf('#');
            if (hashIdx < 0)
            {
                Guid id = ResolveAssetId(assetPath);
                return id != Guid.Empty ? Get(id) : null;
            }

            string parentPath = assetPath[..hashIdx];
            string fragment = assetPath[(hashIdx + 1)..];

            if (ResolveAssetId(parentPath) == Guid.Empty) return null;

            string[] parts = fragment.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int index))
                return null;

            return parts[0] switch
            {
                "Mesh" when index >= 0 && index < _model.Meshes.Count => _model.Meshes[index].Mesh,
                "Material" when index >= 0 && index < _model.Materials.Count => _model.Materials[index],
                _ => null
            };
        }
    }

    #endregion

    #region OnSerialize – asset reference emission

    [Fact]
    public void Serialize_EngineObjectWithAssetID_EmitsAssetReference()
    {
        // Arrange: a Mesh with a known AssetID
        var meshId = Guid.NewGuid();
        var mesh = CreateTestMesh();
        mesh.AssetID = meshId;

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act: serialize the Mesh through the configured context
        EchoObject result = Serializer.Serialize(typeof(Mesh), mesh, ctx);

        // Assert: the output should contain "$assetId" and NOT contain mesh data
        Assert.True(result.TryGet("$assetId", out EchoObject? assetIdTag),
            "Expected $assetId tag in serialized output for EngineObject with AssetID.");
        Assert.Equal(meshId.ToString(), assetIdTag!.StringValue);

        // Should NOT contain inline mesh data
        Assert.False(result.TryGet("MeshData", out _),
            "EngineObject with AssetID should NOT be serialized inline.");
    }

    [Fact]
    public void Serialize_AssetTypeWithoutAssetID_EmitsNullMarker()
    {
        // Arrange: a Mesh (an asset type) with no AssetID
        var mesh = CreateTestMesh();
        Assert.Equal(Guid.Empty, mesh.AssetID); // ensure no asset ID

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act
        EchoObject result = Serializer.Serialize(typeof(Mesh), mesh, ctx);

        // Assert: asset types without AssetID are blocked from inline serialization.
        // A null marker with $assetId = Guid.Empty is emitted instead.
        Assert.True(result.TryGet("$assetId", out EchoObject? assetIdTag),
            "Asset type without AssetID should emit a null-marker $assetId.");
        Assert.Equal(Guid.Empty.ToString(), assetIdTag!.StringValue);
        Assert.False(result.TryGet("MeshData", out _),
            "Asset type without AssetID should NOT be serialized inline.");
    }

    [Fact]
    public void Serialize_EngineObjectWithAssetPath_ResolvesAssetID()
    {
        // Arrange: a Mesh with AssetPath but no AssetID; a database that can resolve it
        var meshId = Guid.NewGuid();
        var mesh = CreateTestMesh();
        mesh.AssetPath = "Models/test.mesh";

        var db = new MockAssetDatabase();
        db.Register(meshId, mesh);
        // Clear the AssetID so it has to be resolved from path
        mesh.AssetID = Guid.Empty;

        AssetDatabase.Current = db;

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act
        EchoObject result = Serializer.Serialize(typeof(Mesh), mesh, ctx);

        // Assert: should resolve via AssetPath and emit $assetId
        Assert.True(result.TryGet("$assetId", out EchoObject? assetIdTag),
            "Expected $assetId tag when AssetPath can be resolved by the database.");
        Assert.Equal(meshId.ToString(), assetIdTag!.StringValue);

        // The object's AssetID should have been stamped for future use
        Assert.Equal(meshId, mesh.AssetID);
    }

    #endregion

    #region OnDeserialize – asset reference resolution

    [Fact]
    public void Deserialize_AssetReference_ResolvesFromDatabase()
    {
        // Arrange: build a "$assetId" compound manually
        var meshId = Guid.NewGuid();
        var assetMesh = CreateTestMesh();

        var db = new MockAssetDatabase();
        db.Register(meshId, assetMesh);
        AssetDatabase.Current = db;

        var assetRef = EchoObject.NewCompound();
        assetRef["$assetId"] = new EchoObject(meshId.ToString());

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act
        object? result = Serializer.Deserialize(assetRef, typeof(Mesh), ctx);

        // Assert
        Assert.NotNull(result);
        Assert.Same(assetMesh, result);
    }

    [Fact]
    public void Deserialize_AssetReference_ReturnsNull_WhenNotInDatabase()
    {
        // Arrange
        var unknownId = Guid.NewGuid();
        AssetDatabase.Current = new MockAssetDatabase(); // empty DB

        var assetRef = EchoObject.NewCompound();
        assetRef["$assetId"] = new EchoObject(unknownId.ToString());

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act
        object? result = Serializer.Deserialize(assetRef, typeof(Mesh), ctx);

        // Assert: unknown asset returns null rather than throwing
        Assert.Null(result);
    }

    #endregion

    #region Round-trip (serialize → deserialize)

    [Fact]
    public void RoundTrip_AssetReference_ResolvesToSameObject()
    {
        // Arrange
        var meshId = Guid.NewGuid();
        var mesh = CreateTestMesh();

        var db = new MockAssetDatabase();
        db.Register(meshId, mesh);
        AssetDatabase.Current = db;

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act: serialize and then deserialize
        EchoObject serialized = Serializer.Serialize(typeof(Mesh), mesh, ctx);

        var ctx2 = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx2);
        object? deserialized = Serializer.Deserialize(serialized, typeof(Mesh), ctx2);

        // Assert: should resolve back to the same asset
        Assert.NotNull(deserialized);
        Assert.Same(mesh, deserialized);
    }

    [Fact]
    public void RoundTrip_AssetTypeWithoutAssetID_ReturnsNull()
    {
        // Arrange: a Mesh without AssetID – asset types are blocked from inline serialization
        var mesh = CreateTestMesh();

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act
        EchoObject serialized = Serializer.Serialize(typeof(Mesh), mesh, ctx);

        var ctx2 = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx2);
        Mesh? deserialized = Serializer.Deserialize<Mesh>(serialized, ctx2);

        // Assert: asset-type objects without AssetID serialize as null markers
        // and deserialize to null (no database entry to resolve)
        Assert.Null(deserialized);
    }

    #endregion

    #region Component-level serialization

    [Fact]
    public void Serialize_MeshRendererWithAssetMesh_EmitsReference()
    {
        // Arrange: Scene → GameObject → MeshRenderer → asset Mesh
        var meshId = Guid.NewGuid();
        var mesh = CreateTestMesh();
        mesh.AssetID = meshId;

        var scene = new Scene { Name = "TestScene" };
        var go = new GameObject("TestObject");
        var mr = go.AddComponent<MeshRenderer>();
        mr.Mesh = mesh;
        scene.Add(go);

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act: serialize the entire scene
        EchoObject sceneData = Serializer.Serialize(typeof(Scene), scene, ctx);

        // Assert: somewhere in the serialized tree, the Mesh should be a $assetId ref
        bool found = ContainsAssetRef(sceneData, meshId.ToString());
        Assert.True(found,
            "Scene serialization should contain $assetId reference for the MeshRenderer's asset Mesh.");

        // Cleanup
        scene.Dispose();
    }

    [Fact]
    public void Serialize_MeshRendererWithNoAssetIdMesh_EmitsNullMarker()
    {
        // Arrange: Scene → GameObject → MeshRenderer → procedural Mesh (no AssetID)
        var mesh = CreateTestMesh();

        var scene = new Scene { Name = "TestScene" };
        var go = new GameObject("TestObject");
        var mr = go.AddComponent<MeshRenderer>();
        mr.Mesh = mesh;
        scene.Add(go);

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act
        EchoObject sceneData = Serializer.Serialize(typeof(Scene), scene, ctx);

        // Assert: asset-type Mesh without AssetID is NOT serialized inline;
        // a null marker ($assetId = Guid.Empty) is emitted instead.
        bool hasInlineMesh = ContainsKey(sceneData, "MeshData");
        Assert.False(hasInlineMesh,
            "Asset-type Mesh without AssetID should NOT be serialized inline in a scene.");
        bool hasNullMarker = ContainsAssetRef(sceneData, Guid.Empty.ToString());
        Assert.True(hasNullMarker,
            "Asset-type Mesh without AssetID should emit a null-marker $assetId.");

        // Cleanup
        scene.Dispose();
    }

    [Fact]
    public void RoundTrip_SceneWithAssetReferences_RestoresMeshFromDatabase()
    {
        // Arrange
        var meshId = Guid.NewGuid();
        var assetMesh = CreateTestMesh();

        var db = new MockAssetDatabase();
        db.Register(meshId, assetMesh);
        AssetDatabase.Current = db;

        // Build a scene referencing the asset mesh
        var scene = new Scene { Name = "AssetRefScene" };
        var go = new GameObject("MeshObj");
        var mr = go.AddComponent<MeshRenderer>();
        mr.Mesh = assetMesh;
        scene.Add(go);

        // Serialize
        var ctxSer = new SerializationContext();
        AssetDatabase.ConfigureContext(ctxSer);
        EchoObject sceneData = Serializer.Serialize(typeof(Scene), scene, ctxSer);

        // Deserialize into a new scene
        var ctxDes = new SerializationContext();
        AssetDatabase.ConfigureContext(ctxDes);
        Scene? restored = Serializer.Deserialize<Scene>(sceneData, ctxDes);

        // Assert
        Assert.NotNull(restored);
        var restoredGo = restored!.AllObjects.FirstOrDefault(o => o.Name == "MeshObj");
        Assert.NotNull(restoredGo);
        var restoredMr = restoredGo!.GetComponent<MeshRenderer>();
        Assert.NotNull(restoredMr);

        // The restored MeshRenderer's Mesh should be the SAME asset instance
        Assert.Same(assetMesh, restoredMr!.Mesh);

        // Cleanup
        scene.Dispose();
        restored.Dispose();
    }

    [Fact]
    public void RoundTrip_SceneWithMixedReferences_HandlesCorrectly()
    {
        // Arrange: one asset mesh and one procedural mesh in the same scene
        var assetMeshId = Guid.NewGuid();
        var assetMesh = CreateTestMesh();

        var db = new MockAssetDatabase();
        db.Register(assetMeshId, assetMesh);
        AssetDatabase.Current = db;

        var proceduralMesh = CreateTestMesh(); // no AssetID

        var scene = new Scene { Name = "MixedScene" };

        var goAsset = new GameObject("AssetMeshObj");
        var mrAsset = goAsset.AddComponent<MeshRenderer>();
        mrAsset.Mesh = assetMesh;
        scene.Add(goAsset);

        var goProc = new GameObject("ProceduralMeshObj");
        var mrProc = goProc.AddComponent<MeshRenderer>();
        mrProc.Mesh = proceduralMesh;
        scene.Add(goProc);

        // Serialize
        var ctxSer = new SerializationContext();
        AssetDatabase.ConfigureContext(ctxSer);
        EchoObject sceneData = Serializer.Serialize(typeof(Scene), scene, ctxSer);

        // Deserialize
        var ctxDes = new SerializationContext();
        AssetDatabase.ConfigureContext(ctxDes);
        Scene? restored = Serializer.Deserialize<Scene>(sceneData, ctxDes);

        // Assert
        Assert.NotNull(restored);

        // Asset mesh should be resolved from database
        var restoredAssetGo = restored!.AllObjects.FirstOrDefault(o => o.Name == "AssetMeshObj");
        Assert.NotNull(restoredAssetGo);
        var restoredAssetMr = restoredAssetGo!.GetComponent<MeshRenderer>();
        Assert.NotNull(restoredAssetMr);
        Assert.Same(assetMesh, restoredAssetMr!.Mesh);

        // Procedural mesh (no AssetID) is blocked from inline serialization,
        // so it deserializes to null.
        var restoredProcGo = restored.AllObjects.FirstOrDefault(o => o.Name == "ProceduralMeshObj");
        Assert.NotNull(restoredProcGo);
        var restoredProcMr = restoredProcGo!.GetComponent<MeshRenderer>();
        Assert.NotNull(restoredProcMr);
        Assert.Null(restoredProcMr!.Mesh);

        // Cleanup
        scene.Dispose();
        restored.Dispose();
    }

    #endregion

    #region Edge cases

    [Fact]
    public void Serialize_NullField_HandlesGracefully()
    {
        // Arrange: MeshRenderer with null Mesh
        var scene = new Scene { Name = "NullMeshScene" };
        var go = new GameObject("NullMeshObj");
        var mr = go.AddComponent<MeshRenderer>();
        mr.Mesh = null!;
        scene.Add(go);

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act & Assert: should not throw
        EchoObject sceneData = Serializer.Serialize(typeof(Scene), scene, ctx);
        Assert.NotNull(sceneData);

        // Cleanup
        scene.Dispose();
    }

    [Fact]
    public void Serialize_SameAssetUsedTwice_BothEmitReference()
    {
        // Arrange: two MeshRenderers sharing the same asset Mesh
        var meshId = Guid.NewGuid();
        var mesh = CreateTestMesh();
        mesh.AssetID = meshId;

        var scene = new Scene { Name = "SharedMeshScene" };

        var go1 = new GameObject("MeshObj1");
        var mr1 = go1.AddComponent<MeshRenderer>();
        mr1.Mesh = mesh;
        scene.Add(go1);

        var go2 = new GameObject("MeshObj2");
        var mr2 = go2.AddComponent<MeshRenderer>();
        mr2.Mesh = mesh;
        scene.Add(go2);

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act
        EchoObject sceneData = Serializer.Serialize(typeof(Scene), scene, ctx);

        // Assert: the asset reference should appear at least twice (or via Echo's $id dedup)
        // Either way, no inline MeshData should be present
        bool hasInlineMesh = ContainsKey(sceneData, "MeshData");
        Assert.False(hasInlineMesh,
            "Shared asset Mesh should not be serialized inline when both references have AssetID.");

        // Cleanup
        scene.Dispose();
    }

    [Fact]
    public void Deserialize_WithoutDatabaseSet_ReturnsNull()
    {
        // Arrange: no database configured
        AssetDatabase.Current = null;

        var meshId = Guid.NewGuid();
        var assetRef = EchoObject.NewCompound();
        assetRef["$assetId"] = new EchoObject(meshId.ToString());

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act
        object? result = Serializer.Deserialize(assetRef, typeof(Mesh), ctx);

        // Assert: should gracefully return null
        Assert.Null(result);
    }

    #endregion

    #region Sub-resource GUID generation

    [Fact]
    public void GenerateSubResourceId_IsDeterministic()
    {
        var parentId = Guid.NewGuid();
        string key = "Mesh:0";

        Guid id1 = Model.GenerateSubResourceId(parentId, key);
        Guid id2 = Model.GenerateSubResourceId(parentId, key);

        Assert.Equal(id1, id2);
        Assert.NotEqual(Guid.Empty, id1);
    }

    [Fact]
    public void GenerateSubResourceId_DifferentKeys_ProduceDifferentIds()
    {
        var parentId = Guid.NewGuid();

        Guid meshId = Model.GenerateSubResourceId(parentId, "Mesh:0");
        Guid matId = Model.GenerateSubResourceId(parentId, "Material:0");
        Guid mesh1Id = Model.GenerateSubResourceId(parentId, "Mesh:1");

        Assert.NotEqual(meshId, matId);
        Assert.NotEqual(meshId, mesh1Id);
        Assert.NotEqual(matId, mesh1Id);
    }

    [Fact]
    public void GenerateSubResourceId_DifferentParents_ProduceDifferentIds()
    {
        var parent1 = Guid.NewGuid();
        var parent2 = Guid.NewGuid();
        string key = "Mesh:0";

        Guid id1 = Model.GenerateSubResourceId(parent1, key);
        Guid id2 = Model.GenerateSubResourceId(parent2, key);

        Assert.NotEqual(id1, id2);
    }

    #endregion

    #region StampSubResourceIds

    [Fact]
    public void StampSubResourceIds_StampsMeshIds()
    {
        var model = CreateTestModel();
        var modelId = Guid.NewGuid();
        model.AssetID = modelId;
        model.AssetPath = "Models/test.obj";

        model.StampSubResourceIds();

        var mesh0 = model.Meshes[0].Mesh;
        var mesh1 = model.Meshes[1].Mesh;

        Assert.NotEqual(Guid.Empty, mesh0.AssetID);
        Assert.NotEqual(Guid.Empty, mesh1.AssetID);
        Assert.NotEqual(mesh0.AssetID, mesh1.AssetID);

        // Verify AssetPaths
        Assert.Equal("Models/test.obj#Mesh:0", mesh0.AssetPath);
        Assert.Equal("Models/test.obj#Mesh:1", mesh1.AssetPath);

        // Verify determinism
        Assert.Equal(Model.GenerateSubResourceId(modelId, "Mesh:0"), mesh0.AssetID);
        Assert.Equal(Model.GenerateSubResourceId(modelId, "Mesh:1"), mesh1.AssetID);
    }

    [Fact]
    public void StampSubResourceIds_StampsMaterialIds()
    {
        var model = CreateTestModel();
        var modelId = Guid.NewGuid();
        model.AssetID = modelId;
        model.AssetPath = "Models/test.obj";

        model.StampSubResourceIds();

        var mat = model.Materials[0];
        Assert.NotEqual(Guid.Empty, mat.AssetID);
        Assert.Equal("Models/test.obj#Material:0", mat.AssetPath);
        Assert.Equal(Model.GenerateSubResourceId(modelId, "Material:0"), mat.AssetID);
    }

    [Fact]
    public void StampSubResourceIds_NoOpWithoutAssetID()
    {
        var model = CreateTestModel();
        Assert.Equal(Guid.Empty, model.AssetID);

        model.StampSubResourceIds();

        // Sub-resources should remain without AssetIDs
        Assert.Equal(Guid.Empty, model.Meshes[0].Mesh.AssetID);
    }

    #endregion

    #region Serialization with sub-resource AssetIDs

    [Fact]
    public void Serialize_ModelSubMesh_EmitsAssetReference()
    {
        // Arrange: a Model with AssetID, stamped sub-resources
        var modelId = Guid.NewGuid();
        var model = CreateTestModel();
        model.AssetID = modelId;
        model.AssetPath = "Models/test.obj";
        model.StampSubResourceIds();

        var mesh = model.Meshes[0].Mesh;
        Assert.NotEqual(Guid.Empty, mesh.AssetID);

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act: serialize the mesh
        EchoObject result = Serializer.Serialize(typeof(Mesh), mesh, ctx);

        // Assert: should emit $assetId and $assetPath
        Assert.True(result.TryGet("$assetId", out EchoObject? assetIdTag));
        Assert.Equal(mesh.AssetID.ToString(), assetIdTag!.StringValue);

        Assert.True(result.TryGet("$assetPath", out EchoObject? pathTag));
        Assert.Equal("Models/test.obj#Mesh:0", pathTag!.StringValue);
    }

    [Fact]
    public void Serialize_AssetPath_IncludedInReference()
    {
        // Arrange: any EngineObject with AssetID and AssetPath
        var meshId = Guid.NewGuid();
        var mesh = CreateTestMesh();
        mesh.AssetID = meshId;
        mesh.AssetPath = "Assets/test.mesh";

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act
        EchoObject result = Serializer.Serialize(typeof(Mesh), mesh, ctx);

        // Assert: both $assetId and $assetPath should be present
        Assert.True(result.TryGet("$assetId", out _));
        Assert.True(result.TryGet("$assetPath", out EchoObject? pathTag));
        Assert.Equal("Assets/test.mesh", pathTag!.StringValue);
    }

    [Fact]
    public void Deserialize_SubResource_FallsBackToPath()
    {
        // Arrange: a mock database that supports ResolveByPath
        var modelId = Guid.NewGuid();
        var model = CreateTestModel();
        model.AssetID = modelId;
        model.AssetPath = "Models/test.obj";
        model.StampSubResourceIds();

        var mesh = model.Meshes[0].Mesh;
        var meshGuid = mesh.AssetID;

        // Create a database that resolves models but NOT mesh sub-GUIDs directly
        var db = new SubResourceMockDatabase(modelId, model);
        AssetDatabase.Current = db;

        // Build the $assetId + $assetPath compound manually
        var assetRef = EchoObject.NewCompound();
        assetRef["$assetId"] = new EchoObject(meshGuid.ToString());
        assetRef["$assetPath"] = new EchoObject("Models/test.obj#Mesh:0");

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        // Act: deserialize — Get(meshGuid) returns null, falls back to ResolveByPath
        object? result = Serializer.Deserialize(assetRef, typeof(Mesh), ctx);

        // Assert: should resolve via path fallback
        Assert.NotNull(result);
        Assert.Same(mesh, result);
    }

    [Fact]
    public void RoundTrip_SceneWithModelSubMesh_SerializesAndRestores()
    {
        // Arrange: create model with AssetID, stamp sub-resources
        var modelId = Guid.NewGuid();
        var model = CreateTestModel();
        model.AssetID = modelId;
        model.AssetPath = "Models/test.obj";
        model.StampSubResourceIds();

        var mesh = model.Meshes[0].Mesh;

        // Database that can resolve both direct and path-based lookups
        var db = new MockAssetDatabase();
        db.Register(mesh.AssetID, mesh);
        AssetDatabase.Current = db;

        // Build scene with MeshRenderer referencing the model's mesh
        var scene = new Scene { Name = "SubResourceScene" };
        var go = new GameObject("ModelObj");
        var mr = go.AddComponent<MeshRenderer>();
        mr.Mesh = mesh;
        scene.Add(go);

        // Serialize
        var ctxSer = new SerializationContext();
        AssetDatabase.ConfigureContext(ctxSer);
        EchoObject sceneData = Serializer.Serialize(typeof(Scene), scene, ctxSer);

        // Verify $assetId is present (not inline mesh data)
        Assert.True(ContainsAssetRef(sceneData, mesh.AssetID.ToString()),
            "Scene should contain $assetId reference for model sub-mesh.");
        Assert.False(ContainsKey(sceneData, "MeshData"),
            "Model sub-mesh should NOT be serialized inline.");

        // Deserialize
        var ctxDes = new SerializationContext();
        AssetDatabase.ConfigureContext(ctxDes);
        Scene? restored = Serializer.Deserialize<Scene>(sceneData, ctxDes);

        // Assert
        Assert.NotNull(restored);
        var restoredGo = restored!.AllObjects.FirstOrDefault(o => o.Name == "ModelObj");
        Assert.NotNull(restoredGo);
        var restoredMr = restoredGo!.GetComponent<MeshRenderer>();
        Assert.NotNull(restoredMr);
        Assert.Same(mesh, restoredMr!.Mesh);

        // Cleanup
        scene.Dispose();
        restored.Dispose();
    }

    #endregion

    #region Tree traversal helpers

    /// <summary>
    /// Recursively searches an EchoObject tree for a "$assetId" entry with
    /// the given value.
    /// </summary>
    private static bool ContainsAssetRef(EchoObject echo, string expectedId)
    {
        if (echo.TagType == EchoType.Compound)
        {
            if (echo.TryGet("$assetId", out EchoObject? tag) && tag!.StringValue == expectedId)
                return true;

            foreach (string name in echo.GetNames())
            {
                if (ContainsAssetRef(echo[name], expectedId))
                    return true;
            }
        }
        else if (echo.TagType == EchoType.List)
        {
            foreach (EchoObject item in echo.List)
            {
                if (ContainsAssetRef(item, expectedId))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Recursively searches an EchoObject tree for a compound that contains the given key.
    /// </summary>
    private static bool ContainsKey(EchoObject echo, string key)
    {
        if (echo.TagType == EchoType.Compound)
        {
            if (echo.TryGet(key, out _))
                return true;

            foreach (string name in echo.GetNames())
            {
                if (ContainsKey(echo[name], key))
                    return true;
            }
        }
        else if (echo.TagType == EchoType.List)
        {
            foreach (EchoObject item in echo.List)
            {
                if (ContainsKey(item, key))
                    return true;
            }
        }
        return false;
    }

    #endregion
}
