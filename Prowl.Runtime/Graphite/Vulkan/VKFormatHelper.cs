// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Silk.NET.Vulkan;

namespace Prowl.Runtime.Graphite.Vulkan;

/// <summary>
/// Converts Graphite enums to Vulkan equivalents.
/// </summary>
internal static class VKFormatHelper
{
    internal static Format ToVkFormat(TextureFormat format) => format switch
    {
        TextureFormat.R8Unorm => Format.R8Unorm,
        TextureFormat.R8Snorm => Format.R8SNorm,
        TextureFormat.R8Uint => Format.R8Uint,
        TextureFormat.R8Sint => Format.R8Sint,
        TextureFormat.RG8Unorm => Format.R8G8Unorm,
        TextureFormat.RG8Snorm => Format.R8G8SNorm,
        TextureFormat.RG8Uint => Format.R8G8Uint,
        TextureFormat.RG8Sint => Format.R8G8Sint,
        TextureFormat.RGBA8Unorm => Format.R8G8B8A8Unorm,
        TextureFormat.RGBA8UnormSrgb => Format.R8G8B8A8Srgb,
        TextureFormat.RGBA8Snorm => Format.R8G8B8A8SNorm,
        TextureFormat.RGBA8Uint => Format.R8G8B8A8Uint,
        TextureFormat.RGBA8Sint => Format.R8G8B8A8Sint,
        TextureFormat.BGRA8Unorm => Format.B8G8R8A8Unorm,
        TextureFormat.BGRA8UnormSrgb => Format.B8G8R8A8Srgb,
        TextureFormat.R16Uint => Format.R16Uint,
        TextureFormat.R16Sint => Format.R16Sint,
        TextureFormat.R16Float => Format.R16Sfloat,
        TextureFormat.RG16Uint => Format.R16G16Uint,
        TextureFormat.RG16Sint => Format.R16G16Sint,
        TextureFormat.RG16Float => Format.R16G16Sfloat,
        TextureFormat.RGBA16Uint => Format.R16G16B16A16Uint,
        TextureFormat.RGBA16Sint => Format.R16G16B16A16Sint,
        TextureFormat.RGBA16Float => Format.R16G16B16A16Sfloat,
        TextureFormat.R32Uint => Format.R32Uint,
        TextureFormat.R32Sint => Format.R32Sint,
        TextureFormat.R32Float => Format.R32Sfloat,
        TextureFormat.RG32Uint => Format.R32G32Uint,
        TextureFormat.RG32Sint => Format.R32G32Sint,
        TextureFormat.RG32Float => Format.R32G32Sfloat,
        TextureFormat.RGBA32Uint => Format.R32G32B32A32Uint,
        TextureFormat.RGBA32Sint => Format.R32G32B32A32Sint,
        TextureFormat.RGBA32Float => Format.R32G32B32A32Sfloat,
        TextureFormat.RGB10A2Unorm => Format.A2B10G10R10UnormPack32,
        TextureFormat.RG11B10Float => Format.B10G11R11UfloatPack32,
        TextureFormat.Depth16Unorm => Format.D16Unorm,
        TextureFormat.Depth24Plus => Format.D24UnormS8Uint,
        TextureFormat.Depth24PlusStencil8 => Format.D24UnormS8Uint,
        TextureFormat.Depth32Float => Format.D32Sfloat,
        TextureFormat.Depth32FloatStencil8 => Format.D32SfloatS8Uint,
        TextureFormat.BC1Unorm => Format.BC1RgbaUnormBlock,
        TextureFormat.BC1UnormSrgb => Format.BC1RgbaSrgbBlock,
        TextureFormat.BC2Unorm => Format.BC2UnormBlock,
        TextureFormat.BC2UnormSrgb => Format.BC2SrgbBlock,
        TextureFormat.BC3Unorm => Format.BC3UnormBlock,
        TextureFormat.BC3UnormSrgb => Format.BC3SrgbBlock,
        TextureFormat.BC4Unorm => Format.BC4UnormBlock,
        TextureFormat.BC4Snorm => Format.BC4SNormBlock,
        TextureFormat.BC5Unorm => Format.BC5UnormBlock,
        TextureFormat.BC5Snorm => Format.BC5SNormBlock,
        TextureFormat.BC6HUfloat => Format.BC6HUfloatBlock,
        TextureFormat.BC6HSfloat => Format.BC6HSfloatBlock,
        TextureFormat.BC7Unorm => Format.BC7UnormBlock,
        TextureFormat.BC7UnormSrgb => Format.BC7SrgbBlock,
        _ => Format.R8G8B8A8Unorm,
    };

    internal static SampleCountFlags ToVkSampleCount(SampleCount sampleCount) => sampleCount switch
    {
        SampleCount.Count1 => SampleCountFlags.Count1Bit,
        SampleCount.Count2 => SampleCountFlags.Count2Bit,
        SampleCount.Count4 => SampleCountFlags.Count4Bit,
        SampleCount.Count8 => SampleCountFlags.Count8Bit,
        SampleCount.Count16 => SampleCountFlags.Count16Bit,
        _ => SampleCountFlags.Count1Bit,
    };

    internal static Silk.NET.Vulkan.Filter ToVkFilter(TextureFilter filter) => filter switch
    {
        TextureFilter.Nearest => Silk.NET.Vulkan.Filter.Nearest,
        TextureFilter.Linear => Silk.NET.Vulkan.Filter.Linear,
        _ => Silk.NET.Vulkan.Filter.Linear,
    };

    internal static SamplerMipmapMode ToVkMipmapMode(TextureFilter filter) => filter switch
    {
        TextureFilter.Nearest => SamplerMipmapMode.Nearest,
        TextureFilter.Linear => SamplerMipmapMode.Linear,
        _ => SamplerMipmapMode.Linear,
    };

    internal static SamplerAddressMode ToVkAddressMode(TextureAddressMode mode) => mode switch
    {
        TextureAddressMode.Repeat => SamplerAddressMode.Repeat,
        TextureAddressMode.MirrorRepeat => SamplerAddressMode.MirroredRepeat,
        TextureAddressMode.ClampToEdge => SamplerAddressMode.ClampToEdge,
        TextureAddressMode.ClampToBorder => SamplerAddressMode.ClampToBorder,
        _ => SamplerAddressMode.Repeat,
    };

    internal static Silk.NET.Vulkan.BorderColor ToVkBorderColor(BorderColor color) => color switch
    {
        BorderColor.TransparentBlack => Silk.NET.Vulkan.BorderColor.FloatTransparentBlack,
        BorderColor.OpaqueBlack => Silk.NET.Vulkan.BorderColor.FloatOpaqueBlack,
        BorderColor.OpaqueWhite => Silk.NET.Vulkan.BorderColor.FloatOpaqueWhite,
        _ => Silk.NET.Vulkan.BorderColor.FloatTransparentBlack,
    };

    internal static Silk.NET.Vulkan.CompareOp ToVkCompareOp(CompareFunction func) => func switch
    {
        CompareFunction.Never => Silk.NET.Vulkan.CompareOp.Never,
        CompareFunction.Less => Silk.NET.Vulkan.CompareOp.Less,
        CompareFunction.Equal => Silk.NET.Vulkan.CompareOp.Equal,
        CompareFunction.LessEqual => Silk.NET.Vulkan.CompareOp.LessOrEqual,
        CompareFunction.Greater => Silk.NET.Vulkan.CompareOp.Greater,
        CompareFunction.NotEqual => Silk.NET.Vulkan.CompareOp.NotEqual,
        CompareFunction.GreaterEqual => Silk.NET.Vulkan.CompareOp.GreaterOrEqual,
        CompareFunction.Always => Silk.NET.Vulkan.CompareOp.Always,
        _ => Silk.NET.Vulkan.CompareOp.Always,
    };

    internal static Silk.NET.Vulkan.StencilOp ToVkStencilOp(StencilOp op) => op switch
    {
        StencilOp.Keep => Silk.NET.Vulkan.StencilOp.Keep,
        StencilOp.Zero => Silk.NET.Vulkan.StencilOp.Zero,
        StencilOp.Replace => Silk.NET.Vulkan.StencilOp.Replace,
        StencilOp.IncrementClamp => Silk.NET.Vulkan.StencilOp.IncrementAndClamp,
        StencilOp.DecrementClamp => Silk.NET.Vulkan.StencilOp.DecrementAndClamp,
        StencilOp.Invert => Silk.NET.Vulkan.StencilOp.Invert,
        StencilOp.IncrementWrap => Silk.NET.Vulkan.StencilOp.IncrementAndWrap,
        StencilOp.DecrementWrap => Silk.NET.Vulkan.StencilOp.DecrementAndWrap,
        _ => Silk.NET.Vulkan.StencilOp.Keep,
    };

    internal static Silk.NET.Vulkan.BlendFactor ToVkBlendFactor(BlendFactor factor) => factor switch
    {
        BlendFactor.Zero => Silk.NET.Vulkan.BlendFactor.Zero,
        BlendFactor.One => Silk.NET.Vulkan.BlendFactor.One,
        BlendFactor.SrcColor => Silk.NET.Vulkan.BlendFactor.SrcColor,
        BlendFactor.OneMinusSrcColor => Silk.NET.Vulkan.BlendFactor.OneMinusSrcColor,
        BlendFactor.DstColor => Silk.NET.Vulkan.BlendFactor.DstColor,
        BlendFactor.OneMinusDstColor => Silk.NET.Vulkan.BlendFactor.OneMinusDstColor,
        BlendFactor.SrcAlpha => Silk.NET.Vulkan.BlendFactor.SrcAlpha,
        BlendFactor.OneMinusSrcAlpha => Silk.NET.Vulkan.BlendFactor.OneMinusSrcAlpha,
        BlendFactor.DstAlpha => Silk.NET.Vulkan.BlendFactor.DstAlpha,
        BlendFactor.OneMinusDstAlpha => Silk.NET.Vulkan.BlendFactor.OneMinusDstAlpha,
        BlendFactor.ConstantColor => Silk.NET.Vulkan.BlendFactor.ConstantColor,
        BlendFactor.OneMinusConstantColor => Silk.NET.Vulkan.BlendFactor.OneMinusConstantColor,
        BlendFactor.SrcAlphaSaturate => Silk.NET.Vulkan.BlendFactor.SrcAlphaSaturate,
        _ => Silk.NET.Vulkan.BlendFactor.One,
    };

    internal static Silk.NET.Vulkan.BlendOp ToVkBlendOp(BlendOp op) => op switch
    {
        BlendOp.Add => Silk.NET.Vulkan.BlendOp.Add,
        BlendOp.Subtract => Silk.NET.Vulkan.BlendOp.Subtract,
        BlendOp.ReverseSubtract => Silk.NET.Vulkan.BlendOp.ReverseSubtract,
        BlendOp.Min => Silk.NET.Vulkan.BlendOp.Min,
        BlendOp.Max => Silk.NET.Vulkan.BlendOp.Max,
        _ => Silk.NET.Vulkan.BlendOp.Add,
    };

    internal static ColorComponentFlags ToVkColorWriteMask(ColorWriteMask mask)
    {
        ColorComponentFlags result = 0;
        if ((mask & ColorWriteMask.Red) != 0) result |= ColorComponentFlags.RBit;
        if ((mask & ColorWriteMask.Green) != 0) result |= ColorComponentFlags.GBit;
        if ((mask & ColorWriteMask.Blue) != 0) result |= ColorComponentFlags.BBit;
        if ((mask & ColorWriteMask.Alpha) != 0) result |= ColorComponentFlags.ABit;
        return result;
    }

    internal static Silk.NET.Vulkan.PrimitiveTopology ToVkTopology(PrimitiveTopology topology) => topology switch
    {
        PrimitiveTopology.PointList => Silk.NET.Vulkan.PrimitiveTopology.PointList,
        PrimitiveTopology.LineList => Silk.NET.Vulkan.PrimitiveTopology.LineList,
        PrimitiveTopology.LineStrip => Silk.NET.Vulkan.PrimitiveTopology.LineStrip,
        PrimitiveTopology.TriangleList => Silk.NET.Vulkan.PrimitiveTopology.TriangleList,
        PrimitiveTopology.TriangleStrip => Silk.NET.Vulkan.PrimitiveTopology.TriangleStrip,
        _ => Silk.NET.Vulkan.PrimitiveTopology.TriangleList,
    };

    internal static Silk.NET.Vulkan.PolygonMode ToVkPolygonMode(PolygonMode mode) => mode switch
    {
        PolygonMode.Fill => Silk.NET.Vulkan.PolygonMode.Fill,
        PolygonMode.Line => Silk.NET.Vulkan.PolygonMode.Line,
        PolygonMode.Point => Silk.NET.Vulkan.PolygonMode.Point,
        _ => Silk.NET.Vulkan.PolygonMode.Fill,
    };

    internal static Silk.NET.Vulkan.CullModeFlags ToVkCullMode(CullMode mode) => mode switch
    {
        CullMode.None => Silk.NET.Vulkan.CullModeFlags.None,
        CullMode.Front => Silk.NET.Vulkan.CullModeFlags.FrontBit,
        CullMode.Back => Silk.NET.Vulkan.CullModeFlags.BackBit,
        _ => Silk.NET.Vulkan.CullModeFlags.None,
    };

    internal static Silk.NET.Vulkan.FrontFace ToVkFrontFace(FrontFace face) => face switch
    {
        FrontFace.CounterClockwise => Silk.NET.Vulkan.FrontFace.CounterClockwise,
        FrontFace.Clockwise => Silk.NET.Vulkan.FrontFace.Clockwise,
        _ => Silk.NET.Vulkan.FrontFace.CounterClockwise,
    };

    internal static Format ToVkVertexFormat(VertexFormat format) => format switch
    {
        VertexFormat.Float => Format.R32Sfloat,
        VertexFormat.Float2 => Format.R32G32Sfloat,
        VertexFormat.Float3 => Format.R32G32B32Sfloat,
        VertexFormat.Float4 => Format.R32G32B32A32Sfloat,
        VertexFormat.Int => Format.R32Sint,
        VertexFormat.Int2 => Format.R32G32Sint,
        VertexFormat.Int3 => Format.R32G32B32Sint,
        VertexFormat.Int4 => Format.R32G32B32A32Sint,
        VertexFormat.Uint => Format.R32Uint,
        VertexFormat.Uint2 => Format.R32G32Uint,
        VertexFormat.Uint3 => Format.R32G32B32Uint,
        VertexFormat.Uint4 => Format.R32G32B32A32Uint,
        VertexFormat.Short2 => Format.R16G16Sint,
        VertexFormat.Short4 => Format.R16G16B16A16Sint,
        VertexFormat.Short2Norm => Format.R16G16SNorm,
        VertexFormat.Short4Norm => Format.R16G16B16A16SNorm,
        VertexFormat.Byte4 => Format.R8G8B8A8Sint,
        VertexFormat.Byte4Norm => Format.R8G8B8A8SNorm,
        VertexFormat.UByte4 => Format.R8G8B8A8Uint,
        VertexFormat.UByte4Norm => Format.R8G8B8A8Unorm,
        _ => Format.R32G32B32A32Sfloat,
    };

    internal static Silk.NET.Vulkan.IndexType ToVkIndexType(IndexFormat format) => format switch
    {
        IndexFormat.Uint16 => Silk.NET.Vulkan.IndexType.Uint16,
        IndexFormat.Uint32 => Silk.NET.Vulkan.IndexType.Uint32,
        _ => Silk.NET.Vulkan.IndexType.Uint32,
    };

    internal static AttachmentLoadOp ToVkLoadOp(LoadOp op) => op switch
    {
        LoadOp.Load => AttachmentLoadOp.Load,
        LoadOp.Clear => AttachmentLoadOp.Clear,
        LoadOp.DontCare => AttachmentLoadOp.DontCare,
        _ => AttachmentLoadOp.DontCare,
    };

    internal static AttachmentStoreOp ToVkStoreOp(StoreOp op) => op switch
    {
        StoreOp.Store => AttachmentStoreOp.Store,
        StoreOp.DontCare => AttachmentStoreOp.DontCare,
        _ => AttachmentStoreOp.Store,
    };

    internal static ImageAspectFlags GetAspectFlags(TextureFormat format) => format switch
    {
        TextureFormat.Depth16Unorm or TextureFormat.Depth32Float
            => ImageAspectFlags.DepthBit,
        TextureFormat.Depth24Plus
            => ImageAspectFlags.DepthBit,
        TextureFormat.Depth24PlusStencil8 or TextureFormat.Depth32FloatStencil8
            => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
        _ => ImageAspectFlags.ColorBit,
    };

    internal static bool IsDepthFormat(TextureFormat format) => format is
        TextureFormat.Depth16Unorm or TextureFormat.Depth24Plus or
        TextureFormat.Depth24PlusStencil8 or TextureFormat.Depth32Float or
        TextureFormat.Depth32FloatStencil8;

    internal static bool IsSrgbFormat(TextureFormat format) => format is
        TextureFormat.RGBA8UnormSrgb or TextureFormat.BGRA8UnormSrgb or
        TextureFormat.BC1UnormSrgb or TextureFormat.BC2UnormSrgb or
        TextureFormat.BC3UnormSrgb or TextureFormat.BC7UnormSrgb;

    internal static bool IsCompressedFormat(TextureFormat format) => format is
        TextureFormat.BC1Unorm or TextureFormat.BC1UnormSrgb or
        TextureFormat.BC2Unorm or TextureFormat.BC2UnormSrgb or
        TextureFormat.BC3Unorm or TextureFormat.BC3UnormSrgb or
        TextureFormat.BC4Unorm or TextureFormat.BC4Snorm or
        TextureFormat.BC5Unorm or TextureFormat.BC5Snorm or
        TextureFormat.BC6HUfloat or TextureFormat.BC6HSfloat or
        TextureFormat.BC7Unorm or TextureFormat.BC7UnormSrgb;

    internal static DescriptorType ToVkDescriptorType(BindingType type) => type switch
    {
        BindingType.UniformBuffer => DescriptorType.UniformBuffer,
        BindingType.StorageBuffer => DescriptorType.StorageBuffer,
        BindingType.ReadOnlyStorageBuffer => DescriptorType.StorageBuffer,
        BindingType.Sampler => DescriptorType.Sampler,
        BindingType.SampledTexture => DescriptorType.SampledImage,
        BindingType.StorageTexture => DescriptorType.StorageImage,
        BindingType.CombinedTextureSampler => DescriptorType.CombinedImageSampler,
        _ => DescriptorType.UniformBuffer,
    };

    internal static DescriptorType ToVkDynamicDescriptorType(BindingType type) => type switch
    {
        BindingType.UniformBuffer => DescriptorType.UniformBufferDynamic,
        BindingType.StorageBuffer => DescriptorType.StorageBufferDynamic,
        BindingType.ReadOnlyStorageBuffer => DescriptorType.StorageBufferDynamic,
        _ => ToVkDescriptorType(type),
    };

    internal static ShaderStageFlags ToVkShaderStageFlags(ShaderStage stage)
    {
        ShaderStageFlags flags = 0;
        if ((stage & ShaderStage.Vertex) != 0) flags |= ShaderStageFlags.VertexBit;
        if ((stage & ShaderStage.Fragment) != 0) flags |= ShaderStageFlags.FragmentBit;
        if ((stage & ShaderStage.Geometry) != 0) flags |= ShaderStageFlags.GeometryBit;
        if ((stage & ShaderStage.TessellationControl) != 0) flags |= ShaderStageFlags.TessellationControlBit;
        if ((stage & ShaderStage.TessellationEvaluation) != 0) flags |= ShaderStageFlags.TessellationEvaluationBit;
        if ((stage & ShaderStage.Compute) != 0) flags |= ShaderStageFlags.ComputeBit;
        return flags;
    }

    internal static ShaderStageFlags ToVkSingleStageFlag(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => ShaderStageFlags.VertexBit,
        ShaderStage.Fragment => ShaderStageFlags.FragmentBit,
        ShaderStage.Geometry => ShaderStageFlags.GeometryBit,
        ShaderStage.TessellationControl => ShaderStageFlags.TessellationControlBit,
        ShaderStage.TessellationEvaluation => ShaderStageFlags.TessellationEvaluationBit,
        ShaderStage.Compute => ShaderStageFlags.ComputeBit,
        _ => ShaderStageFlags.VertexBit,
    };

    internal static uint GetFormatSizeInBytes(TextureFormat format) => format switch
    {
        TextureFormat.R8Unorm or TextureFormat.R8Snorm or TextureFormat.R8Uint or TextureFormat.R8Sint => 1,
        TextureFormat.RG8Unorm or TextureFormat.RG8Snorm or TextureFormat.RG8Uint or TextureFormat.RG8Sint => 2,
        TextureFormat.R16Uint or TextureFormat.R16Sint or TextureFormat.R16Float => 2,
        TextureFormat.RGBA8Unorm or TextureFormat.RGBA8UnormSrgb or TextureFormat.RGBA8Snorm or
        TextureFormat.RGBA8Uint or TextureFormat.RGBA8Sint or TextureFormat.BGRA8Unorm or
        TextureFormat.BGRA8UnormSrgb => 4,
        TextureFormat.RG16Uint or TextureFormat.RG16Sint or TextureFormat.RG16Float => 4,
        TextureFormat.R32Uint or TextureFormat.R32Sint or TextureFormat.R32Float => 4,
        TextureFormat.RGB10A2Unorm or TextureFormat.RG11B10Float => 4,
        TextureFormat.RGBA16Uint or TextureFormat.RGBA16Sint or TextureFormat.RGBA16Float => 8,
        TextureFormat.RG32Uint or TextureFormat.RG32Sint or TextureFormat.RG32Float => 8,
        TextureFormat.RGBA32Uint or TextureFormat.RGBA32Sint or TextureFormat.RGBA32Float => 16,
        TextureFormat.Depth16Unorm => 2,
        TextureFormat.Depth24Plus or TextureFormat.Depth24PlusStencil8 => 4,
        TextureFormat.Depth32Float => 4,
        TextureFormat.Depth32FloatStencil8 => 8,
        _ => 4,
    };
}
