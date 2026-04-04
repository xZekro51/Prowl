// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Prowl.Runtime.Graphite;

/// <summary>
/// Minimal SPIR-V reflection for discovering descriptor set/binding assignments
/// after shaderc auto-assigns them via AutoBindUniforms / AutoMapLocations.
/// Only extracts the information needed for creating Vulkan bind groups:
/// UBO names + member layouts and combined image-sampler names + bindings.
/// </summary>
internal static class SpirvReflection
{
    public enum ResourceType { UniformBuffer, CombinedImageSampler, SampledTexture, Sampler, StorageBuffer }

    public sealed class ResourceBinding
    {
        public uint Set;
        public uint Binding;
        public ResourceType Type;
        public string? Name;
        /// <summary>Members of this UBO (only populated for <see cref="ResourceType.UniformBuffer"/>).</summary>
        public List<MemberInfo>? Members;
        /// <summary>Total buffer size in bytes, rounded to 16-byte alignment (only for UBOs).</summary>
        public uint BufferSize;
        /// <summary>
        /// SPIR-V image dimensionality for sampler/image bindings.
        /// 0 = 1D, 1 = 2D, 2 = 3D, 3 = Cube. Only meaningful for
        /// <see cref="ResourceType.CombinedImageSampler"/> and
        /// <see cref="ResourceType.SampledTexture"/> bindings.
        /// </summary>
        public uint ImageDim;
    }

    public sealed class MemberInfo
    {
        public string? Name;
        public uint Offset;
        public uint Size;
    }

    public sealed class ReflectionResult
    {
        public List<ResourceBinding> Bindings { get; } = [];
    }

    /// <summary>
    /// Parses a SPIR-V binary and extracts all descriptor bindings.
    /// </summary>
    public static ReflectionResult Reflect(ReadOnlySpan<byte> spirv)
    {
        if (spirv.Length < 20)
            throw new ArgumentException("SPIR-V binary too short.");

        var words = MemoryMarshal.Cast<byte, uint>(spirv);
        if (words[0] != 0x07230203)
            throw new ArgumentException("Invalid SPIR-V magic number.");

        // Collect decorations, names, types, variables
        var names = new Dictionary<uint, string>();
        var memberNames = new Dictionary<(uint, uint), string>();
        var bindings = new Dictionary<uint, uint>();
        var sets = new Dictionary<uint, uint>();
        var memberOffsets = new Dictionary<(uint, uint), uint>();
        var typeInfos = new Dictionary<uint, TypeInfo>();
        var variables = new List<(uint typeId, uint resultId, uint storageClass)>();
        var pointers = new Dictionary<uint, (uint storageClass, uint pointeeType)>();
        var structMemberTypes = new Dictionary<uint, uint[]>();

        int i = 5; // Skip 5-word header
        while (i < words.Length)
        {
            uint word0 = words[i];
            uint opcode = word0 & 0xFFFF;
            int wordCount = (int)(word0 >> 16);

            if (wordCount == 0 || i + wordCount > words.Length)
                break;

            switch (opcode)
            {
                case 5: // OpName
                    if (wordCount >= 3)
                        names[words[i + 1]] = ReadString(words, i + 2, wordCount - 2);
                    break;

                case 6: // OpMemberName
                    if (wordCount >= 4)
                        memberNames[(words[i + 1], words[i + 2])] = ReadString(words, i + 3, wordCount - 3);
                    break;

                case 71: // OpDecorate
                    if (wordCount >= 4)
                    {
                        uint target = words[i + 1];
                        uint decoration = words[i + 2];
                        if (decoration == 33) bindings[target] = words[i + 3];      // Binding
                        else if (decoration == 34) sets[target] = words[i + 3];      // DescriptorSet
                    }
                    break;

                case 72: // OpMemberDecorate
                    if (wordCount >= 5)
                    {
                        uint structType = words[i + 1];
                        uint member = words[i + 2];
                        uint decoration = words[i + 3];
                        if (decoration == 35) // Offset
                            memberOffsets[(structType, member)] = words[i + 4];
                    }
                    break;

                case 21: // OpTypeInt
                    if (wordCount >= 3)
                        typeInfos[words[i + 1]] = new TypeInfo(TypeKind.Scalar, words[i + 2] / 8, 0, 0);
                    break;

                case 22: // OpTypeFloat
                    if (wordCount >= 3)
                        typeInfos[words[i + 1]] = new TypeInfo(TypeKind.Scalar, words[i + 2] / 8, 0, 0);
                    break;

                case 23: // OpTypeVector
                    if (wordCount >= 4)
                        typeInfos[words[i + 1]] = new TypeInfo(TypeKind.Vector, 0, words[i + 2], words[i + 3]);
                    break;

                case 24: // OpTypeMatrix
                    if (wordCount >= 4)
                        typeInfos[words[i + 1]] = new TypeInfo(TypeKind.Matrix, 0, words[i + 2], words[i + 3]);
                    break;

                case 25: // OpTypeImage — word layout: %result %sampledType Dim Depth Arrayed MS Sampled Format
                    {
                        // Dim: 0=1D, 1=2D, 2=3D, 3=Cube, 4=Rect, 5=Buffer, 6=SubpassData
                        uint dim = wordCount >= 4 ? words[i + 3] : 1;
                        typeInfos[words[i + 1]] = new TypeInfo(TypeKind.Image, dim, 0, 0);
                    }
                    break;

                case 26: // OpTypeSampler
                    typeInfos[words[i + 1]] = new TypeInfo(TypeKind.Sampler, 0, 0, 0);
                    break;

                case 27: // OpTypeSampledImage — word layout: %result %imageType
                    {
                        uint imageTypeId = wordCount >= 3 ? words[i + 2] : 0;
                        uint sampledDim = 1; // default 2D
                        if (imageTypeId != 0 && typeInfos.TryGetValue(imageTypeId, out TypeInfo imgTi) && imgTi.Kind == TypeKind.Image)
                            sampledDim = imgTi.Size; // Size stores Dim for Image types
                        typeInfos[words[i + 1]] = new TypeInfo(TypeKind.SampledImage, sampledDim, 0, 0);
                    }
                    break;

                case 28: // OpTypeArray
                    if (wordCount >= 4)
                        typeInfos[words[i + 1]] = new TypeInfo(TypeKind.Array, 0, words[i + 2], 0);
                    break;

                case 30: // OpTypeStruct
                    {
                        uint resultId = words[i + 1];
                        int memberCount = wordCount - 2;
                        var mTypes = new uint[memberCount];
                        for (int m = 0; m < memberCount; m++)
                            mTypes[m] = words[i + 2 + m];
                        structMemberTypes[resultId] = mTypes;
                        typeInfos[resultId] = new TypeInfo(TypeKind.Struct, 0, 0, 0);
                    }
                    break;

                case 32: // OpTypePointer
                    if (wordCount >= 4)
                        pointers[words[i + 1]] = (words[i + 2], words[i + 3]);
                    break;

                case 59: // OpVariable
                    if (wordCount >= 4)
                        variables.Add((words[i + 1], words[i + 2], words[i + 3]));
                    break;
            }

            i += wordCount;
        }

        // Build the reflection result
        var result = new ReflectionResult();

        foreach (var (typeId, resultId, storageClass) in variables)
        {
            // Only Uniform (2) and UniformConstant (0) storage classes
            if (storageClass != 0 && storageClass != 2)
                continue;

            if (!bindings.TryGetValue(resultId, out uint binding))
                continue;

            uint set = sets.TryGetValue(resultId, out uint s) ? s : 0;
            string? name = names.TryGetValue(resultId, out string? n) ? n : null;

            if (!pointers.TryGetValue(typeId, out var ptrInfo))
                continue;

            uint pointeeType = ptrInfo.pointeeType;
            ResourceType resType;
            List<MemberInfo>? members = null;
            uint bufferSize = 0;

            uint imageDim = 1; // default 2D

            if (storageClass == 0) // UniformConstant (opaque types: samplers, images)
            {
                if (typeInfos.TryGetValue(pointeeType, out var ti))
                {
                    resType = ti.Kind switch
                    {
                        TypeKind.SampledImage => ResourceType.CombinedImageSampler,
                        TypeKind.Image => ResourceType.SampledTexture,
                        TypeKind.Sampler => ResourceType.Sampler,
                        _ => ResourceType.CombinedImageSampler,
                    };
                    // Propagate Dim from Image/SampledImage types (stored in Size field)
                    if (ti.Kind is TypeKind.SampledImage or TypeKind.Image)
                        imageDim = ti.Size;
                }
                else
                {
                    resType = ResourceType.CombinedImageSampler;
                }
            }
            else // storageClass == 2 (Uniform → UBO)
            {
                resType = ResourceType.UniformBuffer;

                if (structMemberTypes.TryGetValue(pointeeType, out var memberTypeIds))
                {
                    members = [];
                    uint maxEnd = 0;
                    for (uint mi = 0; mi < (uint)memberTypeIds.Length; mi++)
                    {
                        string? memberName = memberNames.TryGetValue((pointeeType, mi), out string? mn) ? mn : null;
                        uint offset = memberOffsets.TryGetValue((pointeeType, mi), out uint mo) ? mo : 0;
                        uint memberSize = ComputeTypeSize(memberTypeIds[mi], typeInfos);
                        members.Add(new MemberInfo { Name = memberName, Offset = offset, Size = memberSize });
                        maxEnd = Math.Max(maxEnd, offset + memberSize);
                    }
                    // Round up to 16-byte alignment (std140 struct alignment)
                    bufferSize = (maxEnd + 15u) & ~15u;
                }
            }

            // For UBOs with empty/null names, try the struct type name
            if (resType == ResourceType.UniformBuffer && string.IsNullOrEmpty(name))
            {
                if (names.TryGetValue(pointeeType, out string? structName))
                    name = structName;
            }

            result.Bindings.Add(new ResourceBinding
            {
                Set = set,
                Binding = binding,
                Type = resType,
                Name = name,
                Members = members,
                BufferSize = bufferSize,
                ImageDim = imageDim,
            });
        }

        return result;
    }

    /// <summary>
    /// Merges reflection results from multiple shader stages (vertex + fragment)
    /// into a single result with unique bindings.
    /// When two stages share the same (set, binding) for a UBO, their members
    /// are merged so that the resulting binding contains the union of all members
    /// and the buffer size covers the largest stage's layout.
    /// </summary>
    public static ReflectionResult Merge(params ReflectionResult[] results)
    {
        var merged = new ReflectionResult();
        // Map (set, binding) → index in merged.Bindings
        var seen = new Dictionary<(uint, uint), int>();

        foreach (var r in results)
        {
            foreach (var b in r.Bindings)
            {
                var key = (b.Set, b.Binding);
                if (!seen.TryGetValue(key, out int existingIdx))
                {
                    seen[key] = merged.Bindings.Count;
                    merged.Bindings.Add(b);
                }
                else if (b.Type == ResourceType.UniformBuffer)
                {
                    // Merge UBO members from the additional stage into the existing entry.
                    var existing = merged.Bindings[existingIdx];
                    if (existing.Type != ResourceType.UniformBuffer)
                        continue;

                    if (b.Members != null && b.Members.Count > 0)
                    {
                        existing.Members ??= [];
                        var memberNames = new HashSet<string>();
                        foreach (var m in existing.Members)
                        {
                            if (m.Name != null)
                                memberNames.Add(m.Name);
                        }
                        foreach (var m in b.Members)
                        {
                            if (m.Name != null && memberNames.Add(m.Name))
                                existing.Members.Add(m);
                        }
                    }

                    // Use the largest buffer size across stages
                    if (b.BufferSize > existing.BufferSize)
                        existing.BufferSize = b.BufferSize;
                }
            }
        }

        return merged;
    }

    #region SPIR-V Type Helpers

    private enum TypeKind { Scalar, Vector, Matrix, Struct, Image, Sampler, SampledImage, Array }

    private readonly record struct TypeInfo(TypeKind Kind, uint Size, uint ComponentTypeOrCount, uint Count);

    private static uint ComputeTypeSize(uint typeId, Dictionary<uint, TypeInfo> types)
    {
        if (!types.TryGetValue(typeId, out var info))
            return 16; // unknown, assume vec4

        switch (info.Kind)
        {
            case TypeKind.Scalar:
                return info.Size; // 4 for float/int

            case TypeKind.Vector:
            {
                uint compSize = types.TryGetValue(info.ComponentTypeOrCount, out var comp) ? comp.Size : 4;
                return compSize * info.Count;
            }

            case TypeKind.Matrix:
                // std140: each column occupies 16 bytes (vec4 alignment)
                return 16 * info.Count;

            case TypeKind.Array:
                // std140: each element is rounded up to vec4 alignment
                uint elemSize = types.TryGetValue(info.ComponentTypeOrCount, out var elem) ? ComputeTypeSize(info.ComponentTypeOrCount, types) : 16;
                uint alignedElem = (elemSize + 15u) & ~15u;
                // Array length is an OpConstant id — not directly available here.
                // Fall back to element size (array of 1).
                return alignedElem;

            default:
                return 16;
        }
    }

    private static string ReadString(ReadOnlySpan<uint> words, int startWord, int maxWords)
    {
        Span<byte> bytes = stackalloc byte[maxWords * 4];
        for (int w = 0; w < maxWords; w++)
        {
            uint word = words[startWord + w];
            bytes[w * 4 + 0] = (byte)(word & 0xFF);
            bytes[w * 4 + 1] = (byte)((word >> 8) & 0xFF);
            bytes[w * 4 + 2] = (byte)((word >> 16) & 0xFF);
            bytes[w * 4 + 3] = (byte)((word >> 24) & 0xFF);
        }
        int nullPos = bytes.IndexOf((byte)0);
        if (nullPos >= 0)
            bytes = bytes[..nullPos];
        return Encoding.UTF8.GetString(bytes);
    }

    #endregion
}
