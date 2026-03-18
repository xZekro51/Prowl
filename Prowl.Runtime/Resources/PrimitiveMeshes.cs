// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Vector;

namespace Prowl.Runtime.Resources;

/// <summary>
/// Provides engine-wide singleton primitive meshes with fixed asset paths.
/// These meshes are lazily created and cached for the lifetime of the engine.
/// They can be referenced by any component or system without loading from disk.
/// </summary>
public static class PrimitiveMeshes
{
    /// <summary> Fixed asset path prefix for primitive meshes. </summary>
    public const string AssetPathPrefix = "$Primitive:";

    private static Mesh? s_cube;
    private static Mesh? s_sphere;
    private static Mesh? s_cylinder;
    private static Mesh? s_capsule;
    private static Mesh? s_cone;
    private static Mesh? s_plane;

    /// <summary> A unit cube (1×1×1) centered at the origin. </summary>
    public static Mesh Cube => s_cube ??= CreatePrimitive("Cube", () => Mesh.CreateCube(Float3.One));

    /// <summary> A unit sphere (radius 0.5, 24 rings × 24 slices) centered at the origin. </summary>
    public static Mesh Sphere => s_sphere ??= CreatePrimitive("Sphere", () => Mesh.CreateSphere(0.5f, 24, 24));

    /// <summary> A cylinder (radius 0.5, height 1, 24 slices) centered at the origin. </summary>
    public static Mesh Cylinder => s_cylinder ??= CreatePrimitive("Cylinder", () => Mesh.CreateCylinder(0.5f, 1f, 24));

    /// <summary> A capsule (radius 0.25, height 1, 16 slices) centered at the origin. </summary>
    public static Mesh Capsule => s_capsule ??= CreatePrimitive("Capsule", () => Mesh.CreateCapsule(0.25f, 1f, 16, 4));

    /// <summary> A cone (radius 0.5, height 1, 24 slices) centered at the origin. </summary>
    public static Mesh Cone => s_cone ??= CreatePrimitive("Cone", () => Mesh.CreateCone(0.5f, 1f, 24));

    /// <summary> A flat 1×1 plane in the XZ plane, facing up. </summary>
    public static Mesh Plane => s_plane ??= CreatePrimitive("Plane", () => CreatePlaneMesh());

    /// <summary>
    /// All available primitive meshes, useful for populating picker UIs.
    /// </summary>
    public static IReadOnlyList<(string Name, Mesh Mesh)> All =>
    [
        ("Cube", Cube),
        ("Sphere", Sphere),
        ("Cylinder", Cylinder),
        ("Capsule", Capsule),
        ("Cone", Cone),
        ("Plane", Plane),
    ];

    /// <summary>
    /// Tries to find a primitive mesh by its asset path (e.g. "$Primitive:Cube").
    /// Returns null if the path doesn't match a known primitive.
    /// </summary>
    public static Mesh? GetByAssetPath(string assetPath)
    {
        if (!assetPath.StartsWith(AssetPathPrefix, StringComparison.Ordinal))
            return null;

        string name = assetPath[AssetPathPrefix.Length..];
        return name switch
        {
            "Cube" => Cube,
            "Sphere" => Sphere,
            "Cylinder" => Cylinder,
            "Capsule" => Capsule,
            "Cone" => Cone,
            "Plane" => Plane,
            _ => null,
        };
    }

    private static Mesh CreatePrimitive(string name, Func<Mesh> factory)
    {
        Mesh mesh = factory();
        mesh.Name = name;
        mesh.AssetPath = AssetPathPrefix + name;
        return mesh;
    }

    private static Mesh CreatePlaneMesh()
    {
        Mesh mesh = new();

        mesh.Vertices =
        [
            new Float3(-0.5f, 0, -0.5f),
            new Float3( 0.5f, 0, -0.5f),
            new Float3( 0.5f, 0,  0.5f),
            new Float3(-0.5f, 0,  0.5f),
        ];

        mesh.UV =
        [
            new Float2(0, 0),
            new Float2(1, 0),
            new Float2(1, 1),
            new Float2(0, 1),
        ];

        mesh.Indices = [0, 2, 1, 0, 3, 2];

        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();

        return mesh;
    }
}
