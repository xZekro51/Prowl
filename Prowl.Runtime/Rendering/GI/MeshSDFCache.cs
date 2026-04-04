// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Runtime.Graphite;
using Prowl.Runtime.Rendering.Compute;
using Prowl.Vector;

using GBuffer = Prowl.Runtime.Graphite.Buffer;

namespace Prowl.Runtime.Rendering.GI;

/// <summary>
/// Caches per-mesh signed distance fields. SDFs are generated once per unique
/// mesh and reused across frames. Invalidated when mesh data changes.
/// </summary>
public static class MeshSDFCache
{
    private static readonly Dictionary<int, Texture3DRT> s_cache = new();

    private static ComputeKernel? s_generateKernel;
    private static ComputeUniforms? s_generateUniforms;

    /// <summary>Resolution of per-mesh SDFs (typically 32 or 64).</summary>
    public static int MeshSDFResolution { get; set; } = 32;

    public static Texture3DRT? GetOrGenerate(Resources.Mesh mesh)
    {
        int id = mesh.InstanceID;
        if (s_cache.TryGetValue(id, out Texture3DRT? existing) && existing.IsValid())
            return existing;

        if (!Graphics.IsGraphiteReady || !mesh.isReadable)
            return null;

        uint res = (uint)MeshSDFResolution;

        // Generate SDF via compute shader (brute-force distance over mesh triangles)
        Texture3DRT sdf = new Texture3DRT(res, res, res, TextureImageFormat.Short);

        // Upload triangle data to GPU storage buffer
        Float3[] verts = mesh.Vertices;
        uint[] indices = mesh.Indices;
        int triCount = indices.Length / 3;

        if (triCount == 0)
        {
            s_cache[id] = sdf;
            return sdf;
        }

        // Pack triangle vertices into float[] (3 verts × vec4 per triangle)
        float[] triData = new float[triCount * 3 * 4];
        for (int t = 0; t < triCount; t++)
        {
            for (int v = 0; v < 3; v++)
            {
                Float3 vert = verts[indices[t * 3 + v]];
                int offset = (t * 3 + v) * 4;
                triData[offset + 0] = vert.X;
                triData[offset + 1] = vert.Y;
                triData[offset + 2] = vert.Z;
                triData[offset + 3] = 0f; // padding
            }
        }

        // Create GPU storage buffer with initial data
        GBuffer triBuf = Graphics.Graphite.CreateBuffer<float>(
            BufferUsage.Storage, triData.AsSpan(),
            MemoryAccess.CpuToGpu, "MeshSDF_Triangles");

        // Compute padded AABB
        AABB bounds = mesh.bounds;
        Float3 extent = bounds.Max - bounds.Min;
        float maxExtent = Math.Max(extent.X, Math.Max(extent.Y, extent.Z)) * 1.1f;
        Float3 boundsMin = bounds.Center - new Float3(maxExtent * 0.5f);

        // Dispatch SDF generation
        RenderCommandBuffer? cmdBuffer = Graphics.ActiveGraphiteCmdBuffer;
        if (cmdBuffer != null)
        {
            CommandList cmd = cmdBuffer.CommandList;

            EnsureGenerateKernel();

            s_generateUniforms!.Clear();
            s_generateUniforms.SetVector3("_BoundsMin", boundsMin);
            s_generateUniforms.SetFloat("_BoundsExtent", maxExtent);
            s_generateUniforms.SetInt("_SDFResolution", (int)res);
            s_generateUniforms.SetInt("_TriangleCount", triCount);

            uint groups = ComputeDispatcher.WorkGroupCount((int)res, 4);
            ComputeDispatcher.Dispatch(cmd, s_generateKernel!, groups, groups, groups,
                s_generateUniforms,
                images: [(0, sdf.GraphiteTexture!)],
                storageBuffers: [(2, triBuf)]);
        }

        // Retire the triangle buffer — GPU will read it, then it gets freed
        GraphiteMaterialBinder.Retire(triBuf);

        s_cache[id] = sdf;
        return sdf;
    }

    private static void EnsureGenerateKernel()
    {
        if (s_generateKernel != null && s_generateKernel.IsValid)
            return;

        s_generateKernel?.Dispose();

        BindGroupLayoutEntry[] layout =
        [
            BindGroupLayoutEntry.StorageTexture(0, ShaderStage.Compute, "MeshSDF"),
            BindGroupLayoutEntry.UniformBuffer(1, ShaderStage.Compute, name: "Params"),
            BindGroupLayoutEntry.StorageBuffer(2, ShaderStage.Compute, readOnly: true, name: "TriangleData"),
        ];
        string source = ComputeShaderLoader.Load("Compute/SDFGI_GenerateMeshSDF");
        s_generateKernel = new ComputeKernel(source, layout, "SDFGI_GenerateMeshSDF");
        s_generateUniforms ??= new ComputeUniforms();
    }

    public static void Invalidate(int meshInstanceID)
    {
        if (s_cache.Remove(meshInstanceID, out Texture3DRT? old))
            old?.Dispose();
    }

    public static void Clear()
    {
        foreach (Texture3DRT sdf in s_cache.Values)
            sdf?.Dispose();
        s_cache.Clear();

        s_generateKernel?.Dispose();
        s_generateKernel = null;
        s_generateUniforms?.Dispose();
        s_generateUniforms = null;
    }
}
