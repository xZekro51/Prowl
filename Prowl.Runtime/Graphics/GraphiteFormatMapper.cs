// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Graphite = Prowl.Runtime.Graphite;

namespace Prowl.Runtime;

/// <summary>
/// Maps legacy rendering enums (<see cref="TextureImageFormat"/>, <see cref="BufferType"/>, etc.)
/// to the corresponding Graphite enums.  Used by the bridge layer so that shadow Graphite
/// resources can be created alongside their legacy OpenGL counterparts.
/// </summary>
internal static class GraphiteFormatMapper
{
    #region Texture Format

    /// <summary>
    /// Maps a legacy <see cref="TextureImageFormat"/> to a Graphite <see cref="Graphite.TextureFormat"/>.
    /// Three-channel formats (RGB) are promoted to four-channel (RGBA) because Graphite
    /// (like Vulkan/WebGPU) does not expose standalone RGB formats.
    /// </summary>
    public static Graphite.TextureFormat MapTextureFormat(TextureImageFormat format) => format switch
    {
        TextureImageFormat.Color4b => Graphite.TextureFormat.RGBA8Unorm,
        TextureImageFormat.Byte => Graphite.TextureFormat.R8Uint,

        // Float channels
        TextureImageFormat.Float => Graphite.TextureFormat.R32Float,
        TextureImageFormat.Float2 => Graphite.TextureFormat.RG32Float,
        TextureImageFormat.Float3 => Graphite.TextureFormat.RGBA32Float, // RGB → RGBA
        TextureImageFormat.Float4 => Graphite.TextureFormat.RGBA32Float,

        // 16-bit half/short channels
        TextureImageFormat.Short => Graphite.TextureFormat.R16Float,
        TextureImageFormat.Short2 => Graphite.TextureFormat.RG16Float,
        TextureImageFormat.Short3 => Graphite.TextureFormat.RGBA16Float, // RGB → RGBA
        TextureImageFormat.Short4 => Graphite.TextureFormat.RGBA16Float,

        // Signed int channels
        TextureImageFormat.Int => Graphite.TextureFormat.R32Sint,
        TextureImageFormat.Int2 => Graphite.TextureFormat.RG32Sint,
        TextureImageFormat.Int3 => Graphite.TextureFormat.RGBA32Sint, // RGB → RGBA
        TextureImageFormat.Int4 => Graphite.TextureFormat.RGBA32Sint,

        // Unsigned short channels (normalized 0–65535 → 0.0–1.0)
        TextureImageFormat.UnsignedShort => Graphite.TextureFormat.R16Unorm,
        TextureImageFormat.UnsignedShort2 => Graphite.TextureFormat.RG16Unorm,
        TextureImageFormat.UnsignedShort3 => Graphite.TextureFormat.RGBA16Unorm, // RGB → RGBA
        TextureImageFormat.UnsignedShort4 => Graphite.TextureFormat.RGBA16Unorm,

        // Unsigned int channels
        TextureImageFormat.UnsignedInt => Graphite.TextureFormat.R32Uint,
        TextureImageFormat.UnsignedInt2 => Graphite.TextureFormat.RG32Uint,
        TextureImageFormat.UnsignedInt3 => Graphite.TextureFormat.RGBA32Uint, // RGB → RGBA
        TextureImageFormat.UnsignedInt4 => Graphite.TextureFormat.RGBA32Uint,

        // Depth / stencil
        TextureImageFormat.Depth16f => Graphite.TextureFormat.Depth16Unorm,
        TextureImageFormat.Depth24f => Graphite.TextureFormat.Depth24Plus,
        TextureImageFormat.Depth32f => Graphite.TextureFormat.Depth32Float,
        TextureImageFormat.Depth24Stencil8 => Graphite.TextureFormat.Depth24PlusStencil8,

        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported TextureImageFormat for Graphite mapping"),
    };

    /// <summary>
    /// Returns <c>true</c> when the legacy format is a three-channel (RGB) type that
    /// gets promoted to four channels during the Graphite mapping.  Callers may need
    /// to pad pixel data when creating or updating the shadow texture.
    /// </summary>
    public static bool IsRgbFormat(TextureImageFormat format) => format is
        TextureImageFormat.Float3 or
        TextureImageFormat.Short3 or
        TextureImageFormat.Int3 or
        TextureImageFormat.UnsignedShort3 or
        TextureImageFormat.UnsignedInt3;

    #endregion

    #region Texture Dimension

    public static Graphite.TextureDimension MapTextureDimension(TextureType type) => type switch
    {
        TextureType.Texture2D => Graphite.TextureDimension.Texture2D,
        TextureType.Texture3D => Graphite.TextureDimension.Texture3D,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    #endregion

    #region Texture Usage Inference

    /// <summary>
    /// Infers a sensible Graphite <see cref="Graphite.TextureUsage"/> from the legacy image format.
    /// Depth/stencil formats get <see cref="Graphite.TextureUsage.DepthStencil"/>;
    /// everything else defaults to <see cref="Graphite.TextureUsage.Sampled"/> with
    /// <see cref="Graphite.TextureUsage.CopyDestination"/> and
    /// <see cref="Graphite.TextureUsage.RenderTarget"/> so the texture can be used as
    /// a framebuffer color attachment (required by Vulkan).
    /// </summary>
    public static Graphite.TextureUsage InferTextureUsage(TextureImageFormat format)
    {
        bool isDepth = format is TextureImageFormat.Depth16f
            or TextureImageFormat.Depth24f
            or TextureImageFormat.Depth32f
            or TextureImageFormat.Depth24Stencil8;

        return isDepth
            ? Graphite.TextureUsage.DepthStencil | Graphite.TextureUsage.Sampled
            : Graphite.TextureUsage.Sampled | Graphite.TextureUsage.CopyDestination | Graphite.TextureUsage.RenderTarget;
    }

    #endregion

    #region Buffer Usage

    public static Graphite.BufferUsage MapBufferUsage(BufferType type) => type switch
    {
        BufferType.VertexBuffer => Graphite.BufferUsage.Vertex | Graphite.BufferUsage.CopyDestination,
        BufferType.ElementsBuffer => Graphite.BufferUsage.Index | Graphite.BufferUsage.CopyDestination,
        BufferType.UniformBuffer => Graphite.BufferUsage.Uniform | Graphite.BufferUsage.CopyDestination,
        BufferType.StructuredBuffer => Graphite.BufferUsage.Storage | Graphite.BufferUsage.CopyDestination,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    #endregion

    #region Texture Filtering

    public static Graphite.TextureFilter MapMinFilter(TextureMin min) => min switch
    {
        TextureMin.Nearest => Graphite.TextureFilter.Nearest,
        TextureMin.NearestMipmapNearest => Graphite.TextureFilter.Nearest,
        TextureMin.NearestMipmapLinear => Graphite.TextureFilter.Nearest,
        TextureMin.Linear => Graphite.TextureFilter.Linear,
        TextureMin.LinearMipmapNearest => Graphite.TextureFilter.Linear,
        TextureMin.LinearMipmapLinear => Graphite.TextureFilter.Linear,
        _ => throw new ArgumentOutOfRangeException(nameof(min), min, null),
    };

    public static Graphite.TextureFilter MapMipmapFilter(TextureMin min) => min switch
    {
        TextureMin.Nearest => Graphite.TextureFilter.Nearest,
        TextureMin.Linear => Graphite.TextureFilter.Linear,
        TextureMin.NearestMipmapNearest => Graphite.TextureFilter.Nearest,
        TextureMin.LinearMipmapNearest => Graphite.TextureFilter.Nearest,
        TextureMin.NearestMipmapLinear => Graphite.TextureFilter.Linear,
        TextureMin.LinearMipmapLinear => Graphite.TextureFilter.Linear,
        _ => throw new ArgumentOutOfRangeException(nameof(min), min, null),
    };

    public static Graphite.TextureFilter MapMagFilter(TextureMag mag) => mag switch
    {
        TextureMag.Nearest => Graphite.TextureFilter.Nearest,
        TextureMag.Linear => Graphite.TextureFilter.Linear,
        _ => throw new ArgumentOutOfRangeException(nameof(mag), mag, null),
    };

    #endregion

    #region Texture Wrap / Address Mode

    public static Graphite.TextureAddressMode MapWrapMode(TextureWrap wrap) => wrap switch
    {
        TextureWrap.Repeat => Graphite.TextureAddressMode.Repeat,
        TextureWrap.MirroredRepeat => Graphite.TextureAddressMode.MirrorRepeat,
        TextureWrap.ClampToEdge => Graphite.TextureAddressMode.ClampToEdge,
        TextureWrap.ClampToBorder => Graphite.TextureAddressMode.ClampToBorder,
        _ => throw new ArgumentOutOfRangeException(nameof(wrap), wrap, null),
    };

    #endregion

    #region Topology

    public static Graphite.PrimitiveTopology MapTopology(Topology topology) => topology switch
    {
        Topology.Points => Graphite.PrimitiveTopology.PointList,
        Topology.Lines => Graphite.PrimitiveTopology.LineList,
        Topology.LineStrip => Graphite.PrimitiveTopology.LineStrip,
        Topology.Triangles => Graphite.PrimitiveTopology.TriangleList,
        Topology.TriangleStrip => Graphite.PrimitiveTopology.TriangleStrip,
        // LineLoop, TriangleFan, and Quads have no direct Graphite equivalent
        _ => Graphite.PrimitiveTopology.TriangleList,
    };

    #endregion

    #region Rasterizer State Mapping

    /// <summary>
    /// Maps the legacy <see cref="RasterizerState"/> to a Graphite
    /// <see cref="Graphite.RasterizerStateDescriptor"/>.
    /// </summary>
    public static Graphite.RasterizerStateDescriptor MapRasterizerState(RasterizerState state) => new()
    {
        PolygonMode = Graphite.PolygonMode.Fill,
        CullMode = MapCullMode(state.CullFace),
        FrontFace = state.Winding == RasterizerState.WindingOrder.CCW
            ? Graphite.FrontFace.CounterClockwise
            : Graphite.FrontFace.Clockwise,
    };

    /// <summary>
    /// Maps the depth and stencil portion of a legacy <see cref="RasterizerState"/>
    /// to a Graphite <see cref="Graphite.DepthStencilStateDescriptor"/>.
    /// </summary>
    public static Graphite.DepthStencilStateDescriptor MapDepthStencilState(RasterizerState state) => new()
    {
        DepthTestEnable = state.DepthTest,
        DepthWriteEnable = state.DepthWrite,
        DepthCompare = MapDepthCompare(state.Depth),
    };

    /// <summary>
    /// Maps the blending portion of a legacy <see cref="RasterizerState"/>
    /// to a Graphite <see cref="Graphite.BlendStateDescriptor"/>.
    /// </summary>
    public static Graphite.BlendStateDescriptor MapBlendState(RasterizerState state)
    {
        if (!state.DoBlend)
            return Graphite.BlendStateDescriptor.Opaque;

        return new Graphite.BlendStateDescriptor(new Graphite.BlendAttachment
        {
            BlendEnable = true,
            SrcColorFactor = MapBlendFactor(state.BlendSrc),
            DstColorFactor = MapBlendFactor(state.BlendDst),
            ColorOp = MapBlendOp(state.Blend),
            SrcAlphaFactor = MapBlendFactor(state.BlendSrc),
            DstAlphaFactor = MapBlendFactor(state.BlendDst),
            AlphaOp = MapBlendOp(state.Blend),
            WriteMask = Graphite.ColorWriteMask.All,
        });
    }

    public static Graphite.CullMode MapCullMode(RasterizerState.PolyFace face) => face switch
    {
        RasterizerState.PolyFace.None => Graphite.CullMode.None,
        RasterizerState.PolyFace.Front => Graphite.CullMode.Front,
        RasterizerState.PolyFace.Back => Graphite.CullMode.Back,
        // FrontAndBack has no direct Graphite equivalent; treat as None
        RasterizerState.PolyFace.FrontAndBack => Graphite.CullMode.None,
        _ => Graphite.CullMode.Back,
    };

    public static Graphite.CompareFunction MapDepthCompare(RasterizerState.DepthMode mode) => mode switch
    {
        RasterizerState.DepthMode.Never => Graphite.CompareFunction.Never,
        RasterizerState.DepthMode.Less => Graphite.CompareFunction.Less,
        RasterizerState.DepthMode.Equal => Graphite.CompareFunction.Equal,
        RasterizerState.DepthMode.Lequal => Graphite.CompareFunction.LessEqual,
        RasterizerState.DepthMode.Greater => Graphite.CompareFunction.Greater,
        RasterizerState.DepthMode.Notequal => Graphite.CompareFunction.NotEqual,
        RasterizerState.DepthMode.Gequal => Graphite.CompareFunction.GreaterEqual,
        RasterizerState.DepthMode.Always => Graphite.CompareFunction.Always,
        _ => Graphite.CompareFunction.LessEqual,
    };

    public static Graphite.BlendFactor MapBlendFactor(RasterizerState.Blending factor) => factor switch
    {
        RasterizerState.Blending.Zero => Graphite.BlendFactor.Zero,
        RasterizerState.Blending.One => Graphite.BlendFactor.One,
        RasterizerState.Blending.SrcColor => Graphite.BlendFactor.SrcColor,
        RasterizerState.Blending.OneMinusSrcColor => Graphite.BlendFactor.OneMinusSrcColor,
        RasterizerState.Blending.DstColor => Graphite.BlendFactor.DstColor,
        RasterizerState.Blending.OneMinusDstColor => Graphite.BlendFactor.OneMinusDstColor,
        RasterizerState.Blending.SrcAlpha => Graphite.BlendFactor.SrcAlpha,
        RasterizerState.Blending.OneMinusSrcAlpha => Graphite.BlendFactor.OneMinusSrcAlpha,
        RasterizerState.Blending.DstAlpha => Graphite.BlendFactor.DstAlpha,
        RasterizerState.Blending.OneMinusDstAlpha => Graphite.BlendFactor.OneMinusDstAlpha,
        RasterizerState.Blending.ConstantColor => Graphite.BlendFactor.ConstantColor,
        RasterizerState.Blending.OneMinusConstantColor => Graphite.BlendFactor.OneMinusConstantColor,
        RasterizerState.Blending.SrcAlphaSaturate => Graphite.BlendFactor.SrcAlphaSaturate,
        _ => Graphite.BlendFactor.One,
    };

    public static Graphite.BlendOp MapBlendOp(RasterizerState.BlendMode mode) => mode switch
    {
        RasterizerState.BlendMode.Add => Graphite.BlendOp.Add,
        RasterizerState.BlendMode.Subtract => Graphite.BlendOp.Subtract,
        RasterizerState.BlendMode.ReverseSubtract => Graphite.BlendOp.ReverseSubtract,
        RasterizerState.BlendMode.Min => Graphite.BlendOp.Min,
        RasterizerState.BlendMode.Max => Graphite.BlendOp.Max,
        _ => Graphite.BlendOp.Add,
    };

    #endregion

    #region Render Pass Layout

    /// <summary>
    /// Builds a <see cref="Graphite.RenderPassLayout"/> from a <see cref="GraphicsFrameBuffer"/>
    /// by inspecting the Graphite shadow textures established in Phase 2.
    /// </summary>
    public static Graphite.RenderPassLayout MapRenderPassLayout(GraphicsFrameBuffer frameBuffer)
    {
        var colorFormats = new List<Graphite.TextureFormat>();
        if (frameBuffer.GraphiteColorAttachments != null)
        {
            foreach (var tex in frameBuffer.GraphiteColorAttachments)
            {
                if (tex != null)
                    colorFormats.Add(tex.Format);
            }
        }

        Graphite.TextureFormat? depthFormat = frameBuffer.GraphiteDepthAttachment?.Format;

        return new Graphite.RenderPassLayout(
            colorFormats.ToArray(),
            depthFormat);
    }

    #endregion

    #region Vertex Layout

    /// <summary>
    /// Converts a legacy <see cref="VertexFormat"/> into a Graphite
    /// <see cref="Graphite.VertexBufferLayout"/> suitable for use in a
    /// <see cref="Graphite.VertexLayoutDescriptor"/>.
    /// </summary>
    public static Graphite.VertexBufferLayout MapVertexBufferLayout(VertexFormat format, Graphite.VertexStepMode stepMode = Graphite.VertexStepMode.Vertex)
    {
        var attributes = new Graphite.VertexAttribute[format.Elements.Length];
        for (int i = 0; i < format.Elements.Length; i++)
        {
            var element = format.Elements[i];
            attributes[i] = new Graphite.VertexAttribute(
                element.Semantic,
                MapVertexElementFormat(element),
                (uint)element.Offset);
        }

        return new Graphite.VertexBufferLayout((uint)format.Size, stepMode, attributes);
    }

    /// <summary>
    /// Maps a single legacy <see cref="VertexFormat.Element"/> to the closest
    /// <see cref="Graphite.VertexFormat"/> value.
    /// </summary>
    public static Graphite.VertexFormat MapVertexElementFormat(VertexFormat.Element element)
    {
        return element.Type switch
        {
            VertexFormat.VertexType.Float => element.Count switch
            {
                1 => Graphite.VertexFormat.Float,
                2 => Graphite.VertexFormat.Float2,
                3 => Graphite.VertexFormat.Float3,
                4 => Graphite.VertexFormat.Float4,
                _ => Graphite.VertexFormat.Float4,
            },
            VertexFormat.VertexType.Int => element.Count switch
            {
                1 => Graphite.VertexFormat.Int,
                2 => Graphite.VertexFormat.Int2,
                3 => Graphite.VertexFormat.Int3,
                4 => Graphite.VertexFormat.Int4,
                _ => Graphite.VertexFormat.Int4,
            },
            VertexFormat.VertexType.Short => element.Count switch
            {
                2 => Graphite.VertexFormat.Short2,
                4 => Graphite.VertexFormat.Short4,
                _ => Graphite.VertexFormat.Short4,
            },
            VertexFormat.VertexType.Byte => element.Count switch
            {
                4 => element.Normalized ? Graphite.VertexFormat.Byte4Norm : Graphite.VertexFormat.Byte4,
                _ => Graphite.VertexFormat.Byte4,
            },
            VertexFormat.VertexType.UnsignedByte => element.Count switch
            {
                4 => element.Normalized ? Graphite.VertexFormat.UByte4Norm : Graphite.VertexFormat.UByte4,
                _ => Graphite.VertexFormat.UByte4,
            },
            _ => Graphite.VertexFormat.Float4,
        };
    }

    #endregion
}
