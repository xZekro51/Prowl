// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Runtime;

public class MeshRenderer : MonoBehaviour, IRenderable
{
    public Mesh Mesh;

    /// <summary>
    /// Materials used to render this mesh.
    /// When the mesh defines sub-meshes, each element corresponds to one sub-mesh (by index).
    /// If there are fewer materials than sub-meshes, the last material is reused.
    /// When the array is empty or null, a single default material placeholder is expected.
    /// </summary>
    public Material[] Materials = [];

    /// <summary>
    /// Convenience accessor for the first (or only) material.
    /// Setting this replaces the entire <see cref="Materials"/> array with a single element.
    /// </summary>
    public Material Material
    {
        get => Materials is { Length: > 0 } ? Materials[0] : default;
        set => Materials = [value];
    }

    public Color MainColor = Color.White;

    private PropertyState _properties = new();

    public override void Update()
    {
        if (!Mesh.IsValid()) return;

        int subMeshCount = Mesh.SubMeshCount;

        if (subMeshCount > 1 && Materials is { Length: > 0 })
        {
            // Multi-material path: push one renderable per sub-mesh
            for (int i = 0; i < subMeshCount; i++)
            {
                Material mat = i < Materials.Length ? Materials[i] : Materials[^1];
                if (!mat.IsValid()) continue;

                var props = new PropertyState();
                props.SetInt("_ObjectID", InstanceID);
                props.SetColor("_MainColor", MainColor);

                GameObject.Scene.PushRenderable(new MeshRenderable(
                    Mesh, mat, Transform.LocalToWorldMatrix,
                    GameObject.LayerIndex, props, i));
            }
        }
        else
        {
            // Single-material path (original behaviour)
            Material mat = Material;
            if (!mat.IsValid()) return;

            _properties.Clear();
            _properties.SetInt("_ObjectID", InstanceID);
            _properties.SetColor("_MainColor", MainColor);
            GameObject.Scene.PushRenderable(this);
        }
    }

    public Material GetMaterial() => Material;
    public Resources.Mesh? GetMesh() => Mesh;
    public int GetLayer() => GameObject.LayerIndex;
    public Float3 GetPosition() => Transform.Position;

    public void GetRenderingData(ViewerData viewer, out PropertyState properties, out Mesh drawData, out Float4x4 model, out InstanceData[]? instanceData)
    {
        drawData = Mesh;
        properties = _properties;
        model = Transform.LocalToWorldMatrix;
        instanceData = null; // Single instance rendering
    }

    public void GetCullingData(out bool isRenderable, out AABB bounds)
    {
        isRenderable = true;
        bounds = Mesh.bounds.TransformBy(Transform.LocalToWorldMatrix);
    }
}
