// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

using Prowl.Vector;

namespace Prowl.Runtime.Resources;

/// <summary>
/// Represents a node in the imported model's scene hierarchy.
/// Each node corresponds to a node in the source file (FBX, glTF, etc.)
/// and may reference zero or more meshes from <see cref="Model.Meshes"/>.
/// </summary>
public class ModelNode
{
    public string Name { get; set; }
    public Float3 LocalPosition { get; set; }
    public Quaternion LocalRotation { get; set; }
    public Float3 LocalScale { get; set; }

    /// <summary>
    /// Indices into <see cref="Model.Meshes"/> for meshes attached to this node.
    /// </summary>
    public List<int> MeshIndices { get; set; } = [];

    public List<ModelNode> Children { get; set; } = [];

    public ModelNode(string name)
    {
        Name = name;
        LocalPosition = Float3.Zero;
        LocalRotation = Quaternion.Identity;
        LocalScale = Float3.One;
    }
}

public class Model : EngineObject
{
    public new string Name { get; set; }
    public List<Material> Materials { get; set; } = [];
    public List<ModelMesh> Meshes { get; set; } = [];
    public List<AnimationClip> Animations { get; set; } = [];
    public Skeleton Skeleton { get; set; }
    public float UnitScale { get; set; } = 1.0f;

    /// <summary>
    /// The root of the imported node hierarchy.
    /// When not null, this mirrors the scene graph from the source file.
    /// </summary>
    public ModelNode RootNode { get; set; }

    /// <summary> Cameras found in the source file (populated when <c>ImportCameras</c> is enabled). </summary>
    public List<ModelCamera> Cameras { get; set; } = [];

    /// <summary> Lights found in the source file (populated when <c>ImportLights</c> is enabled). </summary>
    public List<ModelLight> Lights { get; set; } = [];

    public Model(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Generates a deterministic GUID for a sub-resource within a parent asset.
    /// The same <paramref name="parentId"/> and <paramref name="subResourceKey"/> will
    /// always produce the same GUID.
    /// </summary>
    public static Guid GenerateSubResourceId(Guid parentId, string subResourceKey)
    {
        byte[] parentBytes = parentId.ToByteArray();
        byte[] keyBytes = System.Text.Encoding.UTF8.GetBytes(subResourceKey);
        byte[] combined = new byte[parentBytes.Length + keyBytes.Length];
        Buffer.BlockCopy(parentBytes, 0, combined, 0, parentBytes.Length);
        Buffer.BlockCopy(keyBytes, 0, combined, parentBytes.Length, keyBytes.Length);

        byte[] hash = SHA256.HashData(combined);
        byte[] guidBytes = new byte[16];
        Buffer.BlockCopy(hash, 0, guidBytes, 0, 16);

        // Set version 5 (name-based) and variant bits
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }

    /// <summary>
    /// Stamps deterministic <see cref="EngineObject.AssetID"/> and
    /// <see cref="EngineObject.AssetPath"/> values on all sub-resources
    /// (meshes and materials) based on this Model's own <see cref="EngineObject.AssetID"/>.
    /// Call this after the Model receives its AssetID from the asset database.
    /// </summary>
    public void StampSubResourceIds()
    {
        if (AssetID == Guid.Empty)
            return;

        for (int i = 0; i < Meshes.Count; i++)
        {
            var mesh = Meshes[i].Mesh;
            if (mesh == null) continue;

            string key = $"Mesh:{i}";
            mesh.AssetID = GenerateSubResourceId(AssetID, key);
            mesh.AssetPath = $"{AssetPath}#{key}";
        }

        for (int i = 0; i < Materials.Count; i++)
        {
            var mat = Materials[i];
            if (mat == null) continue;

            string key = $"Material:{i}";
            mat.AssetID = GenerateSubResourceId(AssetID, key);
            mat.AssetPath = $"{AssetPath}#{key}";
        }
    }

    /// <summary>
    /// Creates a <see cref="GameObject"/> hierarchy that mirrors the imported
    /// scene graph, similar to how Unity interprets imported models.
    /// Each <see cref="ModelNode"/> becomes a child <see cref="GameObject"/>
    /// and nodes that reference meshes receive <see cref="MeshRenderer"/> components.
    /// </summary>
    public GameObject CreateGameObjectHierarchy()
    {
        // Ensure sub-resources have AssetIDs if this model is a registered asset
        if (AssetID != Guid.Empty)
            StampSubResourceIds();

        var root = new GameObject(Name);

        if (RootNode != null)
        {
            foreach (ModelNode child in RootNode.Children)
            {
                CreateNodeGameObject(child, root);
            }
        }
        else
        {
            // Fallback for models without hierarchy: create a flat list of children
            for (int i = 0; i < Meshes.Count; i++)
            {
                ModelMesh modelMesh = Meshes[i];
                var childGO = new GameObject(modelMesh.Name ?? $"Mesh_{i}");
                childGO.SetParent(root, false);

                var renderer = childGO.AddComponent<MeshRenderer>();
                renderer.Mesh = modelMesh.Mesh;
                renderer.Material = modelMesh.Material;
            }
        }

        return root;
    }

    private void CreateNodeGameObject(ModelNode node, GameObject parent)
    {
        var go = new GameObject(node.Name);
        go.SetParent(parent, false);
        go.Transform.LocalPosition = node.LocalPosition;
        go.Transform.LocalRotation = node.LocalRotation;
        go.Transform.LocalScale = node.LocalScale;

        // Attach MeshRenderers for each mesh on this node
        foreach (int meshIndex in node.MeshIndices)
        {
            if (meshIndex < 0 || meshIndex >= Meshes.Count)
                continue;

            ModelMesh modelMesh = Meshes[meshIndex];

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.Mesh = modelMesh.Mesh;
            renderer.Material = modelMesh.Material;
        }

        // Recurse into children
        foreach (ModelNode child in node.Children)
        {
            CreateNodeGameObject(child, go);
        }
    }

    /// <summary>
    /// Loads a model from a file (.obj, .fbx, .gltf, etc.)
    /// </summary>
    public static Model LoadFromFile(string filePath, AssetImporting.ModelImporterSettings? settings = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Model file not found: {filePath}");

        var importer = new AssetImporting.ModelImporter();
        Model model = importer.Import(new FileInfo(filePath), settings);
        model.AssetPath = filePath;
        return model;
    }

    /// <summary>
    /// Loads a model from a stream
    /// </summary>
    public static Model LoadFromStream(Stream stream, string virtualPath, AssetImporting.ModelImporterSettings? settings = null)
    {
        var importer = new AssetImporting.ModelImporter();
        Model model = importer.Import(stream, virtualPath, settings);
        model.AssetPath = virtualPath;
        return model;
    }

    /// <summary>
    /// Loads a default embedded model
    /// </summary>
    public static Model LoadDefault(DefaultModel model)
    {
        string fileName = model switch
        {
            DefaultModel.Cube => "Cube.obj",
            DefaultModel.Sphere => "Sphere.obj",
            DefaultModel.Cylinder => "Cylinder.obj",
            DefaultModel.Plane => "Plane.obj",
            DefaultModel.SkyDome => "SkyDome.obj",
            DefaultModel.UnitCube => "1mcube.obj",
            _ => throw new ArgumentException($"Unknown default model: {model}")
        };

        string resourcePath = $"Assets/Defaults/{fileName}";
        using (Stream stream = EmbeddedResources.GetStream(resourcePath))
        {
            var importer = new AssetImporting.ModelImporter();
            Model result = importer.Import(stream, resourcePath);
            result.AssetPath = $"$Default:{model}";
            return result;
        }
    }
}

public class ModelMesh
{
    public string Name { get; set; }
    public Mesh Mesh { get; set; }
    public Material Material { get; set; }
    public bool HasBones { get; set; }

    public ModelMesh(string name, Mesh mesh, Material material, bool hasBones = false)
    {
        Name = name;
        Mesh = mesh;
        Material = material;
        HasBones = hasBones;
    }
}

/// <summary>
/// Describes a camera embedded in the imported model file.
/// </summary>
public class ModelCamera
{
    public string Name { get; set; }
    public float FieldOfView { get; set; }
    public float NearPlane { get; set; }
    public float FarPlane { get; set; }
    public float AspectRatio { get; set; }
    public Float3 Position { get; set; }
    public Float3 LookAt { get; set; }
    public Float3 Up { get; set; }
}

/// <summary>
/// Describes a light embedded in the imported model file.
/// </summary>
public class ModelLight
{
    public enum LightKind { Directional, Point, Spot, Area, Ambient }

    public string Name { get; set; }
    public LightKind Kind { get; set; }
    public Color DiffuseColor { get; set; }
    public Color SpecularColor { get; set; }
    public Color AmbientColor { get; set; }
    public Float3 Position { get; set; }
    public Float3 Direction { get; set; }
    public float InnerConeAngle { get; set; }
    public float OuterConeAngle { get; set; }
    public float AttenuationConstant { get; set; }
    public float AttenuationLinear { get; set; }
    public float AttenuationQuadratic { get; set; }
}
