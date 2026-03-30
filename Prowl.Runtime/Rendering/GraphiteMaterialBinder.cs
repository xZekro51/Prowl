// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using Prowl.Runtime.Graphite;
using Prowl.Runtime.Resources;
using Prowl.Vector;

using GTexture = Prowl.Runtime.Graphite.Texture;
using GBuffer = Prowl.Runtime.Graphite.Buffer;

namespace Prowl.Runtime.Rendering;

/// <summary>
/// Creates Graphite <see cref="BindGroup"/> and <see cref="BindGroupLayout"/> from
/// shader reflection data and <see cref="PropertyState"/> property bags.
/// This is the bridge between the engine's stateful GL-style uniform system and
/// the declarative descriptor-set model required by Vulkan.
/// </summary>
internal static class GraphiteMaterialBinder
{
    /// <summary>Shared linear-repeat sampler for all material texture bindings.</summary>
    private static Sampler? s_defaultSampler;

    /// <summary>Shared 1×1 white fallback texture for missing texture bindings.</summary>
    private static GTexture? s_fallbackTexture;

    /// <summary>
    /// Per-frame temporary GPU resources (buffers, bind groups) that must stay
    /// alive until the command buffer finishes executing on the GPU.
    /// Disposed at the start of the next-next frame (after the fence wait).
    /// </summary>
    private static readonly List<IDisposable>[] s_retiredResources = [[], []];
    private static int s_retireSlot;

    /// <summary>
    /// Call at the start of each frame (after <c>GraphiteDevice.BeginFrame</c>
    /// has waited on the fence).  Disposes resources from 2 frames ago.
    /// </summary>
    public static void BeginFrame()
    {
        s_retireSlot = (s_retireSlot + 1) % 2;
        foreach (var r in s_retiredResources[s_retireSlot])
        {
            try { r.Dispose(); } catch { /* ignore */ }
        }
        s_retiredResources[s_retireSlot].Clear();
    }

    /// <summary>
    /// Queues a resource for deferred disposal (after the GPU is done with it).
    /// </summary>
    public static void Retire(IDisposable resource)
    {
        s_retiredResources[s_retireSlot].Add(resource);
    }

    /// <summary>
    /// Creates a <see cref="BindGroupLayout"/> from shader reflection data.
    /// The layout is deterministic for a given shader and should be cached.
    /// </summary>
    public static BindGroupLayout CreateBindGroupLayout(SpirvReflection.ReflectionResult reflection)
    {
        var entries = new List<BindGroupLayoutEntry>();

        foreach (var binding in reflection.Bindings)
        {
            switch (binding.Type)
            {
                case SpirvReflection.ResourceType.UniformBuffer:
                    entries.Add(BindGroupLayoutEntry.UniformBuffer(
                        binding.Binding, ShaderStage.AllGraphics, false, binding.Name));
                    break;

                case SpirvReflection.ResourceType.CombinedImageSampler:
                    entries.Add(BindGroupLayoutEntry.CombinedTextureSampler(
                        binding.Binding, ShaderStage.Fragment, binding.Name));
                    break;

                case SpirvReflection.ResourceType.StorageBuffer:
                    entries.Add(BindGroupLayoutEntry.StorageBuffer(
                        binding.Binding, ShaderStage.AllGraphics, false, false, binding.Name));
                    break;
            }
        }

        return Graphics.Graphite.CreateBindGroupLayout(
            new BindGroupLayoutDescriptor(entries.ToArray()));
    }

    /// <summary>Debug flag to enable verbose logging of bind group creation.</summary>
    public static bool DebugBindGroups = false;

    /// <summary>
    /// Creates a <see cref="BindGroup"/> for a draw call by packing all uniform
    /// values, textures, and buffers from the provided property states.
    /// </summary>
    /// <param name="reflection">Shader reflection data.</param>
    /// <param name="layout">The bind group layout (must match the reflection).</param>
    /// <param name="materialProps">Material-level properties (shared per batch).</param>
    /// <param name="instanceProps">Per-object property overrides (can be null).</param>
    /// <param name="objectToWorld">Per-object model matrix.</param>
    /// <param name="worldToObject">Inverse model matrix.</param>
    /// <returns>A new <see cref="BindGroup"/> that has been queued for deferred disposal.</returns>
    public static BindGroup? CreateBindGroup(
        SpirvReflection.ReflectionResult reflection,
        BindGroupLayout layout,
        PropertyState? materialProps,
        PropertyState? instanceProps,
        Float4x4? objectToWorld = null,
        Float4x4? worldToObject = null)
    {
        EnsureDefaults();

        if (DebugBindGroups)
            Debug.Log($"[BindGroup] Creating bind group, {reflection.Bindings.Count} bindings, objectToWorld={(objectToWorld.HasValue ? "set" : "null")}");

        var entries = new List<BindGroupEntry>();

        foreach (var binding in reflection.Bindings)
        {
            if (DebugBindGroups)
                Debug.Log($"[BindGroup]   Binding: set={binding.Set} binding={binding.Binding} type={binding.Type} name='{binding.Name}' members={binding.Members?.Count ?? 0}");

            switch (binding.Type)
            {
                case SpirvReflection.ResourceType.UniformBuffer:
                    if (binding.Name == "GlobalUniforms")
                    {
                        // Use the per-upload Graphite snapshot so each camera
                        // render binds its own immutable copy of the data.
                        var globalGraphiteBuf = GlobalUniforms.GetGraphiteBuffer();
                        if (globalGraphiteBuf != null)
                        {
                            entries.Add(BindGroupEntry.ForBuffer(
                                binding.Binding, globalGraphiteBuf, 0,
                                (uint)GlobalUniformsData.SizeInBytes));
                            if (DebugBindGroups) Debug.Log($"[BindGroup]     -> GlobalUniforms bound OK");
                        }
                        else
                        {
                            if (DebugBindGroups) Debug.LogError($"[BindGroup]     -> GlobalUniforms FAILED: no graphite snapshot");
                            return null; // Can't render without global uniforms
                        }
                    }
                    else
                    {
                        // Default UBO: pack property data into the per-frame ring buffer
                        var (uboBuffer, uboOffset) = PackDefaultUbo(binding, materialProps, instanceProps, objectToWorld, worldToObject);
                        if (uboBuffer != null)
                        {
                            entries.Add(BindGroupEntry.ForBuffer(
                                binding.Binding, uboBuffer, uboOffset, binding.BufferSize));
                            if (DebugBindGroups) Debug.Log($"[BindGroup]     -> UBO '{binding.Name}' bound OK, size={binding.BufferSize}, offset={uboOffset}");
                        }
                        else
                        {
                            if (DebugBindGroups) Debug.LogError($"[BindGroup]     -> UBO '{binding.Name}' FAILED to pack");
                            return null;
                        }
                    }
                    break;

                case SpirvReflection.ResourceType.CombinedImageSampler:
                    var (tex, sampler) = ResolveTexture(binding.Name, materialProps, instanceProps);
                    entries.Add(BindGroupEntry.ForTextureSampler(binding.Binding, tex, sampler));
                    if (DebugBindGroups) Debug.Log($"[BindGroup]     -> Texture '{binding.Name}' bound: {(tex == s_fallbackTexture ? "FALLBACK" : "OK")}");
                    break;
            }
        }

        if (entries.Count == 0)
        {
            if (DebugBindGroups) Debug.LogError($"[BindGroup] FAILED: No entries created!");
            return null;
        }

        if (DebugBindGroups) Debug.Log($"[BindGroup] SUCCESS: Created with {entries.Count} entries");
        var bindGroup = Graphics.Graphite.CreateBindGroup(
            new BindGroupDescriptor(layout, entries.ToArray()));
        Retire(bindGroup);
        return bindGroup;
    }

    #region UBO Packing

    private static (GBuffer? Buffer, uint Offset) PackDefaultUbo(
        SpirvReflection.ResourceBinding binding,
        PropertyState? materialProps,
        PropertyState? instanceProps,
        Float4x4? objectToWorld,
        Float4x4? worldToObject)
    {
        if (binding.Members == null || binding.BufferSize == 0)
            return (null, 0);

        var data = new byte[binding.BufferSize];

        foreach (var member in binding.Members)
        {
            if (member.Name == null) continue;
            WriteMemberValue(data, member, materialProps, instanceProps, objectToWorld, worldToObject);
        }

        // Sub-allocate from the per-frame ring buffer (Vulkan) or
        // create a temporary CpuToGpu buffer (OpenGL fallback).
        var (buffer, offset) = Graphics.Graphite.AllocateTransientUniform(data);
        return (buffer, offset);
    }

    private static unsafe void WriteMemberValue(
        byte[] data,
        SpirvReflection.MemberInfo member,
        PropertyState? materialProps,
        PropertyState? instanceProps,
        Float4x4? objectToWorld,
        Float4x4? worldToObject)
    {
        string name = member.Name!;
        uint offset = member.Offset;

        // Per-object transform uniforms (always written if present)
        if (name == "prowl_ObjectToWorld" && objectToWorld.HasValue)
        {
            WriteMatrix(data, offset, objectToWorld.Value);
            return;
        }
        if (name == "prowl_WorldToObject" && worldToObject.HasValue)
        {
            WriteMatrix(data, offset, worldToObject.Value);
            return;
        }

        // Per-object previous transform (from global state)
        if (name == "prowl_PrevObjectToWorld")
        {
            var prev = PropertyState.GetGlobalMatrix(name);
            WriteMatrix(data, offset, prev);
            return;
        }

        // Try instance properties first (per-object overrides), then material, then globals
        if (TryWriteFromPropertyState(data, member, instanceProps)) return;
        if (TryWriteFromPropertyState(data, member, materialProps)) return;
        TryWriteFromGlobals(data, member);
    }

    private static bool TryWriteFromPropertyState(byte[] data, SpirvReflection.MemberInfo member, PropertyState? props)
    {
        if (props == null || member.Name == null) return false;
        string name = member.Name;
        uint offset = member.Offset;

        // Try float
        float fVal = props.GetFloat(name);
        if (fVal != 0 || props.GetFloatNames().Contains(name))
        {
            WriteFloat(data, offset, fVal);
            return true;
        }

        // Try int
        int iVal = props.GetInt(name);
        if (iVal != 0 || props.GetIntNames().Contains(name))
        {
            WriteInt(data, offset, iVal);
            return true;
        }

        // Try vec4 (also covers colors stored as vec4)
        var v4 = props.GetVector4(name);
        if (v4 != Float4.Zero || props.GetVector4Names().Contains(name))
        {
            WriteVec4(data, offset, v4);
            return true;
        }

        // Try vec3
        var v3 = props.GetVector3(name);
        if (v3 != Float3.Zero || props.GetVector3Names().Contains(name))
        {
            WriteVec3(data, offset, v3);
            return true;
        }

        // Try vec2
        var v2 = props.GetVector2(name);
        if (v2 != Float2.Zero || props.GetVector2Names().Contains(name))
        {
            WriteVec2(data, offset, v2);
            return true;
        }

        // Try color (written as vec4)
        var color = props.GetColor(name);
        if (color != Color.White || props.GetColorNames().Contains(name))
        {
            WriteVec4(data, offset, new Float4((float)color.R, (float)color.G, (float)color.B, (float)color.A));
            return true;
        }

        // Try mat4
        var mat = props.GetMatrix(name);
        if (!mat.Equals(Float4x4.Identity) || props.GetMatrixNames().Contains(name))
        {
            WriteMatrix(data, offset, mat);
            return true;
        }

        return false;
    }

    private static void TryWriteFromGlobals(byte[] data, SpirvReflection.MemberInfo member)
    {
        if (member.Name == null) return;
        string name = member.Name;
        uint offset = member.Offset;

        // Global floats
        float gf = PropertyState.GetGlobalFloat(name);
        if (gf != 0) { WriteFloat(data, offset, gf); return; }

        // Global ints
        int gi = PropertyState.GetGlobalInt(name);
        if (gi != 0) { WriteInt(data, offset, gi); return; }

        // Global vec4
        var gv4 = PropertyState.GetGlobalVector4(name);
        if (gv4 != Float4.Zero) { WriteVec4(data, offset, gv4); return; }

        // Global vec3
        var gv3 = PropertyState.GetGlobalVector3(name);
        if (gv3 != Float3.Zero) { WriteVec3(data, offset, gv3); return; }

        // Global vec2
        var gv2 = PropertyState.GetGlobalVector2(name);
        if (gv2 != Float2.Zero) { WriteVec2(data, offset, gv2); return; }

        // Global color
        var gc = PropertyState.GetGlobalColor(name);
        if (gc != Color.White)
        {
            WriteVec4(data, offset, new Float4((float)gc.R, (float)gc.G, (float)gc.B, (float)gc.A));
            return;
        }

        // Global mat4
        var gm = PropertyState.GetGlobalMatrix(name);
        if (!gm.Equals(Float4x4.Identity))
        {
            WriteMatrix(data, offset, gm);
        }
    }

    #endregion

    #region Texture Resolution

    private static (GTexture tex, Sampler sampler) ResolveTexture(
        string? name, PropertyState? materialProps, PropertyState? instanceProps)
    {
        EnsureDefaults();

        GTexture? graphiteTex = null;

        if (name != null)
        {
            // Try instance textures first
            graphiteTex = TryGetGraphiteTexture(name, instanceProps);

            // Then material textures
            graphiteTex ??= TryGetGraphiteTexture(name, materialProps);

            // Then global textures
            graphiteTex ??= TryGetGlobalGraphiteTexture(name);

            // Debug: Log if using fallback
            if (graphiteTex == null && DebugBindGroups)
                Debug.Log($"[BindGroup] Texture '{name}' not found, using fallback white");
        }

        return (graphiteTex ?? s_fallbackTexture!, s_defaultSampler!);
    }

    private static GTexture? TryGetGraphiteTexture(string name, PropertyState? props)
    {
        if (props == null) return null;

        var tex2d = props.GetTexture(name);
        if (tex2d != null && tex2d.IsValid() && tex2d.Handle?.GraphiteTexture != null)
            return tex2d.Handle.GraphiteTexture;

        var tex3d = props.GetTexture3D(name);
        if (tex3d != null && tex3d.IsValid() && tex3d.Handle?.GraphiteTexture != null)
            return tex3d.Handle.GraphiteTexture;

        return null;
    }

    private static GTexture? TryGetGlobalGraphiteTexture(string name)
    {
        var tex2d = PropertyState.GetGlobalTexture(name);
        if (tex2d != null && tex2d.IsValid() && tex2d.Handle?.GraphiteTexture != null)
            return tex2d.Handle.GraphiteTexture;

        var tex3d = PropertyState.GetGlobalTexture3D(name);
        if (tex3d != null && tex3d.IsValid() && tex3d.Handle?.GraphiteTexture != null)
            return tex3d.Handle.GraphiteTexture;

        return null;
    }

    #endregion

    #region Data Writers

    private static void WriteFloat(byte[] data, uint offset, float value)
    {
        if (offset + 4 <= data.Length)
            MemoryMarshal.Write(data.AsSpan((int)offset), in value);
    }

    private static void WriteInt(byte[] data, uint offset, int value)
    {
        if (offset + 4 <= data.Length)
            MemoryMarshal.Write(data.AsSpan((int)offset), in value);
    }

    private static void WriteVec2(byte[] data, uint offset, Float2 value)
    {
        if (offset + 8 <= data.Length)
        {
            MemoryMarshal.Write(data.AsSpan((int)offset), in value.X);
            MemoryMarshal.Write(data.AsSpan((int)offset + 4), in value.Y);
        }
    }

    private static void WriteVec3(byte[] data, uint offset, Float3 value)
    {
        if (offset + 12 <= data.Length)
        {
            MemoryMarshal.Write(data.AsSpan((int)offset), in value.X);
            MemoryMarshal.Write(data.AsSpan((int)offset + 4), in value.Y);
            MemoryMarshal.Write(data.AsSpan((int)offset + 8), in value.Z);
        }
    }

    private static void WriteVec4(byte[] data, uint offset, Float4 value)
    {
        if (offset + 16 <= data.Length)
        {
            MemoryMarshal.Write(data.AsSpan((int)offset), in value.X);
            MemoryMarshal.Write(data.AsSpan((int)offset + 4), in value.Y);
            MemoryMarshal.Write(data.AsSpan((int)offset + 8), in value.Z);
            MemoryMarshal.Write(data.AsSpan((int)offset + 12), in value.W);
        }
    }

    private static unsafe void WriteMatrix(byte[] data, uint offset, Float4x4 value)
    {
        // Write the entire 4×4 matrix (64 bytes) in one block copy.
        // Float4x4 is StructLayout(Sequential) with columns c0..c3 as Float4,
        // matching the column-major layout expected by GLSL std140.
        if (offset + 64 <= data.Length)
        {
            fixed (byte* dst = &data[offset])
            {
                *(Float4x4*)dst = value;
            }
        }
    }

    #endregion

    #region Initialization

    private static void EnsureDefaults()
    {
        if (s_defaultSampler != null) return;

        if (!Graphics.IsGraphiteReady) return;

        s_defaultSampler = Graphics.Graphite.CreateSampler(SamplerDescriptor.Anisotropic(16));

        // Create a 1×1 white fallback texture
        var texDesc = new TextureDescriptor
        {
            Width = 1,
            Height = 1,
            Format = TextureFormat.RGBA8Unorm,
            Usage = TextureUsage.Sampled | TextureUsage.CopyDestination,
        };
        s_fallbackTexture = Graphics.Graphite.CreateTexture(in texDesc);
        byte[] white = [255, 255, 255, 255];
        var updateDesc = TextureUpdateDescriptor.FullMip(1, 1);
        Graphics.Graphite.UpdateTexture(s_fallbackTexture, in updateDesc, white);
    }

    /// <summary>Disposes shared resources during shutdown.</summary>
    public static void Dispose()
    {
        s_defaultSampler?.Dispose();
        s_defaultSampler = null;
        s_fallbackTexture?.Dispose();
        s_fallbackTexture = null;

        foreach (var list in s_retiredResources)
        {
            foreach (var r in list)
                try { r.Dispose(); } catch { }
            list.Clear();
        }
    }

    #endregion
}
