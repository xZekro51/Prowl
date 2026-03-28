// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;

using Silk.NET.OpenGL;

using Graphite = Prowl.Runtime.Graphite;

namespace Prowl.Runtime;

public unsafe class GraphicsTexture : IDisposable
{
    /// <summary>
    /// The GL texture handle. Only valid when <see cref="Graphics.IsOpenGL"/> is <c>true</c>.
    /// On non-GL backends this will be <c>0</c>.
    /// </summary>
    public uint Handle { get; private set; }
    public TextureType Type { get; protected set; }

    public readonly TextureTarget Target;

    /// <summary>The internal format of the pixels, such as RGBA, RGB, R32f, or even different depth/stencil formats.</summary>
    public readonly InternalFormat PixelInternalFormat;

    /// <summary>The data type of the components of the <see cref="Texture"/>'s pixels.</summary>
    public readonly PixelType PixelType;

    /// <summary>The format of the pixel data.</summary>
    public readonly PixelFormat PixelFormat;

    /// <summary>The original legacy image format, preserved for Graphite mapping.</summary>
    public readonly TextureImageFormat ImageFormat;

    /// <summary>
    /// The Graphite texture backing this resource.
    /// On OpenGL this is a shadow created alongside the GL texture.
    /// On non-GL backends this is the primary GPU texture.
    /// Created on the first <see cref="TexImage2D"/> / <see cref="TexImage3D"/> call (mip 0).
    /// Will be <c>null</c> before the first image upload.
    /// </summary>
    public Graphite.Texture? GraphiteTexture { get; private set; }

    private static bool IsGL => Graphics.IsOpenGL;

    public GraphicsTexture(TextureType type, TextureImageFormat format)
    {
        Type = type;
        ImageFormat = format;
        Target = type switch
        {
            TextureType.Texture2D => TextureTarget.Texture2D,
            TextureType.Texture3D => TextureTarget.Texture3D,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };
        GetTextureFormatEnums(format, out PixelInternalFormat, out PixelType, out PixelFormat);

        if (IsGL)
            Handle = Graphics.GL.GenTexture();
    }

    private static uint? currentlyBound = null;

    /// <summary>
    /// Invalidates the static bind cache.  Call this after external code
    /// (e.g. Graphite command list execution) changes the GL texture binding
    /// without going through <see cref="Bind"/>.
    /// </summary>
    internal static void InvalidateBindCache() => currentlyBound = null;

    public void Bind(bool force = true)
    {
        if (!IsGL) return;

        if (!force && currentlyBound == Handle)
            return;

        Graphics.GL.BindTexture(Target, Handle);
        currentlyBound = Handle;
    }

    public void GenerateMipmap()
    {
        if (IsGL)
        {
            Bind(false);
            Graphics.GL.GenerateMipmap(Target);
        }

        if (GraphiteTexture != null && Graphics.IsGraphiteReady)
        {
            // Vulkan requires mip storage to be allocated upfront (unlike OpenGL which
            // auto-allocates during glGenerateMipmap).  If the Graphite texture only has
            // 1 mip level, recreate it with a full mip chain and copy mip 0 data over.
            if (!IsGL && GraphiteTexture.MipLevels <= 1)
            {
                var oldTex = GraphiteTexture;

                // Compute per-pixel size in the Graphite format (accounts for RGB→RGBA padding).
                uint legacyBpp = GetBytesPerPixel(ImageFormat);
                uint graphiteBpp = GraphiteFormatMapper.IsRgbFormat(ImageFormat)
                    ? (legacyBpp / 3 * 4)
                    : legacyBpp;

                int dataSize = (int)(oldTex.Width * oldTex.Height * Math.Max(1u, oldTex.Depth) * graphiteBpp);
                byte[] mip0Data = ArrayPool<byte>.Shared.Rent(dataSize);
                try
                {
                    Graphics.Graphite.ReadbackTexture(oldTex, 0, 0, mip0Data.AsSpan(0, dataSize));

                    var desc = new Graphite.TextureDescriptor
                    {
                        Dimension = oldTex.Dimension,
                        Width = oldTex.Width,
                        Height = oldTex.Height,
                        Depth = oldTex.Depth,
                        Format = oldTex.Format,
                        Usage = oldTex.Usage,
                        ArrayLayers = oldTex.ArrayLayers,
                        SampleCount = oldTex.SampleCount,
                        DebugName = oldTex.DebugName,
                    };
                    desc.MipLevels = desc.CalculateMaxMipLevels();

                    GraphiteTexture = Graphics.Graphite.CreateTexture(in desc);

                    var updateDesc = Graphite.TextureUpdateDescriptor.FullMip(oldTex.Width, oldTex.Height, 0);
                    updateDesc.Depth = Math.Max(1u, oldTex.Depth);
                    Graphics.Graphite.UpdateTexture(GraphiteTexture, in updateDesc, mip0Data.AsSpan(0, dataSize));

                    oldTex.Dispose();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(mip0Data);
                }
            }

            Graphics.Graphite.GenerateMipmaps(GraphiteTexture);
        }
    }

    public void SetWrapS(TextureWrap wrap)
    {
        if (!IsGL) return;

        Bind(false);
        GLEnum wrapMode = wrap switch
        {
            TextureWrap.Repeat => GLEnum.Repeat,
            TextureWrap.ClampToEdge => GLEnum.ClampToEdge,
            TextureWrap.MirroredRepeat => GLEnum.MirroredRepeat,
            TextureWrap.ClampToBorder => GLEnum.ClampToBorder,
            _ => throw new ArgumentException("Invalid texture wrap mode", nameof(wrap)),
        };
        Graphics.GL.TexParameter(Target, GLEnum.TextureWrapS, (int)wrapMode);
    }

    public void SetWrapT(TextureWrap wrap)
    {
        if (!IsGL) return;

        Bind(false);
        GLEnum wrapMode = wrap switch
        {
            TextureWrap.Repeat => GLEnum.Repeat,
            TextureWrap.ClampToEdge => GLEnum.ClampToEdge,
            TextureWrap.MirroredRepeat => GLEnum.MirroredRepeat,
            TextureWrap.ClampToBorder => GLEnum.ClampToBorder,
            _ => throw new ArgumentException("Invalid texture wrap mode", nameof(wrap)),
        };
        Graphics.GL.TexParameter(Target, GLEnum.TextureWrapT, (int)wrapMode);
    }

    public void SetWrapR(TextureWrap wrap)
    {
        if (!IsGL) return;

        Bind(false);
        GLEnum wrapMode = wrap switch
        {
            TextureWrap.Repeat => GLEnum.Repeat,
            TextureWrap.ClampToEdge => GLEnum.ClampToEdge,
            TextureWrap.MirroredRepeat => GLEnum.MirroredRepeat,
            TextureWrap.ClampToBorder => GLEnum.ClampToBorder,
            _ => throw new ArgumentException("Invalid texture wrap mode", nameof(wrap)),
        };
        Graphics.GL.TexParameter(Target, GLEnum.TextureWrapR, (int)wrapMode);
    }

    public void SetTextureFilters(TextureMin min, TextureMag mag)
    {
        if (!IsGL) return;

        Bind(false);
        GLEnum minFilter = min switch
        {
            TextureMin.Nearest => GLEnum.Nearest,
            TextureMin.Linear => GLEnum.Linear,
            TextureMin.NearestMipmapNearest => GLEnum.NearestMipmapNearest,
            TextureMin.LinearMipmapNearest => GLEnum.LinearMipmapNearest,
            TextureMin.NearestMipmapLinear => GLEnum.NearestMipmapLinear,
            TextureMin.LinearMipmapLinear => GLEnum.LinearMipmapLinear,
            _ => throw new ArgumentException("Invalid texture min filter", nameof(min)),
        };
        GLEnum magFilter = mag switch
        {
            TextureMag.Nearest => GLEnum.Nearest,
            TextureMag.Linear => GLEnum.Linear,
            _ => throw new ArgumentException("Invalid texture mag filter", nameof(mag)),
        };
        Graphics.GL.TexParameter(Target, GLEnum.TextureMinFilter, (int)minFilter);
        Graphics.GL.TexParameter(Target, GLEnum.TextureMagFilter, (int)magFilter);
    }

    public void GetTexImage(int level, void* ptr)
    {
        if (IsGL)
        {
            Bind(false);
            Graphics.GL.GetTexImage(Target, level, PixelFormat, PixelType, ptr);
            return;
        }

        // Non-GL path: use the Graphite readback API
        if (GraphiteTexture == null)
            throw new InvalidOperationException("No Graphite texture available for readback.");

        uint mipWidth = Math.Max(1, GraphiteTexture.Width >> level);
        uint mipHeight = Math.Max(1, GraphiteTexture.Height >> level);
        uint mipDepth = Math.Max(1, GraphiteTexture.Depth >> level);
        uint bpp = GetBytesPerPixel(ImageFormat);
        uint dataSize = mipWidth * mipHeight * mipDepth * bpp;

        var span = new Span<byte>(ptr, (int)dataSize);
        Graphics.Graphite.ReadbackTexture(GraphiteTexture, (uint)level, 0, span);
    }

    public bool IsDisposed { get; protected set; }

    public void Dispose()
    {
        if (IsDisposed)
            return;

        if (currentlyBound == Handle)
            currentlyBound = null;

        GraphiteTexture?.Dispose();
        GraphiteTexture = null;

        if (IsGL)
            Graphics.GL.DeleteTexture(Handle);

        IsDisposed = true;
    }

    public string ToString()
    {
        return Handle.ToString();
    }


    public void TexImage2D(TextureTarget type, int mip, uint width, uint height, int v2, void* data)
    {
        if (IsGL)
        {
            Bind(false);
            Graphics.GL.TexImage2D(type, mip, PixelInternalFormat, width, height, v2, PixelFormat, PixelType, data);
        }

        // Create (or recreate) the Graphite texture on mip level 0.
        if (mip == 0 && Graphics.IsGraphiteReady)
        {
            GraphiteTexture?.Dispose();
            var desc = Graphite.TextureDescriptor.Texture2D(
                width, height,
                GraphiteFormatMapper.MapTextureFormat(ImageFormat),
                GraphiteFormatMapper.InferTextureUsage(ImageFormat));
            GraphiteTexture = Graphics.Graphite.CreateTexture(in desc);

            // GLTexture constructor binds/unbinds a GL texture, invalidating our cache.
            if (IsGL) currentlyBound = null;

            // On non-GL backends, upload the initial data to the Graphite texture.
            if (!IsGL && data != null)
                UploadToGraphiteTexture(data, width, height, 1, 0);
        }
    }

    public void TexImage3D(TextureTarget type, int level, uint width, uint height, uint depth, void* data)
    {
        if (IsGL)
        {
            Bind(false);
            Graphics.GL.TexImage3D(type, level, PixelInternalFormat, width, height, depth, 0, PixelFormat, PixelType, data);
        }

        // Create (or recreate) the Graphite texture on mip level 0.
        if (level == 0 && Graphics.IsGraphiteReady)
        {
            GraphiteTexture?.Dispose();
            var desc = Graphite.TextureDescriptor.Texture3D(
                width, height, depth,
                GraphiteFormatMapper.MapTextureFormat(ImageFormat),
                GraphiteFormatMapper.InferTextureUsage(ImageFormat));
            GraphiteTexture = Graphics.Graphite.CreateTexture(in desc);

            // GLTexture constructor binds/unbinds a GL texture, invalidating our cache.
            if (IsGL) currentlyBound = null;

            // On non-GL backends, upload the initial data to the Graphite texture.
            if (!IsGL && data != null)
                UploadToGraphiteTexture(data, width, height, depth, 0);
        }
    }

    internal void TexSubImage2D(TextureTarget type, int mip, int x, int y, uint width, uint height, void* data)
    {
        if (IsGL)
        {
            Bind(false);
            Graphics.GL.TexSubImage2D(type, mip, x, y, width, height, PixelFormat, PixelType, data);
        }

        // On non-GL backends, upload to the Graphite texture directly.
        if (!IsGL && GraphiteTexture != null && data != null)
        {
            uint bpp = GetBytesPerPixel(ImageFormat);
            if (GraphiteFormatMapper.IsRgbFormat(ImageFormat))
            {
                UploadRgbToRgba(data, width, height, 1, (uint)mip, (uint)x, (uint)y, 0, bpp);
            }
            else
            {
                var updateDesc = new Graphite.TextureUpdateDescriptor
                {
                    MipLevel = (uint)mip,
                    X = (uint)x,
                    Y = (uint)y,
                    Z = 0,
                    Width = width,
                    Height = height,
                    Depth = 1,
                };
                var span = new ReadOnlySpan<byte>(data, (int)(width * height * bpp));
                Graphics.Graphite.UpdateTexture(GraphiteTexture, in updateDesc, span);
            }
        }
    }

    internal void TexSubImage3D(TextureTarget type, int level, int x, int y, int z, uint width, uint height, uint depth, void* data)
    {
        if (IsGL)
        {
            Bind(false);
            Graphics.GL.TexSubImage3D(type, level, x, y, z, width, height, depth, PixelFormat, PixelType, data);
        }

        // On non-GL backends, upload to the Graphite texture directly.
        if (!IsGL && GraphiteTexture != null && data != null)
        {
            uint bpp = GetBytesPerPixel(ImageFormat);
            if (GraphiteFormatMapper.IsRgbFormat(ImageFormat))
            {
                UploadRgbToRgba(data, width, height, depth, (uint)level, (uint)x, (uint)y, (uint)z, bpp);
            }
            else
            {
                var updateDesc = new Graphite.TextureUpdateDescriptor
                {
                    MipLevel = (uint)level,
                    X = (uint)x,
                    Y = (uint)y,
                    Z = (uint)z,
                    Width = width,
                    Height = height,
                    Depth = depth,
                };
                var span = new ReadOnlySpan<byte>(data, (int)(width * height * depth * bpp));
                Graphics.Graphite.UpdateTexture(GraphiteTexture, in updateDesc, span);
            }
        }
    }

    /// <summary>
    /// Uploads data to the Graphite texture, handling RGB-to-RGBA padding when necessary.
    /// </summary>
    private void UploadToGraphiteTexture(void* data, uint width, uint height, uint depth, uint mipLevel)
    {
        if (GraphiteTexture == null || data == null) return;

        uint bpp = GetBytesPerPixel(ImageFormat);

        if (GraphiteFormatMapper.IsRgbFormat(ImageFormat))
        {
            UploadRgbToRgba(data, width, height, depth, mipLevel, 0, 0, 0, bpp);
        }
        else
        {
            var updateDesc = Graphite.TextureUpdateDescriptor.FullMip(width, height, mipLevel);
            updateDesc.Depth = depth;
            var span = new ReadOnlySpan<byte>(data, (int)(width * height * depth * bpp));
            Graphics.Graphite.UpdateTexture(GraphiteTexture, in updateDesc, span);
        }
    }

    /// <summary>
    /// Pads 3-channel (RGB) source data to 4-channel (RGBA) and uploads to the Graphite texture.
    /// </summary>
    private void UploadRgbToRgba(void* data, uint width, uint height, uint depth, uint mipLevel,
        uint offsetX, uint offsetY, uint offsetZ, uint srcBpp)
    {
        if (GraphiteTexture == null) return;

        uint pixelCount = width * height * depth;
        uint srcChannelSize = srcBpp / 3;
        uint dstBpp = srcChannelSize * 4;
        uint dstSize = pixelCount * dstBpp;

        byte[] padded = new byte[dstSize];
        byte* src = (byte*)data;
        fixed (byte* dst = padded)
        {
            for (uint i = 0; i < pixelCount; i++)
            {
                System.Buffer.MemoryCopy(src + i * srcBpp, dst + i * dstBpp, srcChannelSize * 3, srcChannelSize * 3);
            }
        }

        var updateDesc = new Graphite.TextureUpdateDescriptor
        {
            MipLevel = mipLevel,
            X = offsetX,
            Y = offsetY,
            Z = offsetZ,
            Width = width,
            Height = height,
            Depth = depth,
        };
        Graphics.Graphite.UpdateTexture(GraphiteTexture, in updateDesc, padded);
    }

    /// <summary>
    /// Returns the number of bytes per pixel for the given legacy texture format.
    /// </summary>
    internal static uint GetBytesPerPixel(TextureImageFormat format) => format switch
    {
        TextureImageFormat.Color4b => 4,
        TextureImageFormat.Byte => 1,
        TextureImageFormat.Float => 4,
        TextureImageFormat.Float2 => 8,
        TextureImageFormat.Float3 => 12,
        TextureImageFormat.Float4 => 16,
        TextureImageFormat.Short => 2,
        TextureImageFormat.Short2 => 4,
        TextureImageFormat.Short3 => 6,
        TextureImageFormat.Short4 => 8,
        TextureImageFormat.Int => 4,
        TextureImageFormat.Int2 => 8,
        TextureImageFormat.Int3 => 12,
        TextureImageFormat.Int4 => 16,
        TextureImageFormat.UnsignedShort => 2,
        TextureImageFormat.UnsignedShort2 => 4,
        TextureImageFormat.UnsignedShort3 => 6,
        TextureImageFormat.UnsignedShort4 => 8,
        TextureImageFormat.UnsignedInt => 4,
        TextureImageFormat.UnsignedInt2 => 8,
        TextureImageFormat.UnsignedInt3 => 12,
        TextureImageFormat.UnsignedInt4 => 16,
        TextureImageFormat.Depth16f => 2,
        TextureImageFormat.Depth24f => 4,
        TextureImageFormat.Depth32f => 4,
        TextureImageFormat.Depth24Stencil8 => 4,
        _ => 4,
    };

    /// <summary>
    /// Turns a value from the <see cref="Texture.TextureImageFormat"/> enum into the necessary
    /// enums to create a <see cref="Texture"/>'s image/storage.
    /// </summary>
    /// <param name="imageFormat">The requested image format.</param>
    /// <param name="pixelInternalFormat">The pixel's internal format.</param>
    /// <param name="pixelType">The pixel's type.</param>
    /// <param name="pixelFormat">The pixel's format.</param>
    public static void GetTextureFormatEnums(TextureImageFormat imageFormat, out InternalFormat pixelInternalFormat, out PixelType pixelType, out PixelFormat pixelFormat)
    {

        pixelType = imageFormat switch
        {
            TextureImageFormat.Color4b => PixelType.UnsignedByte,
            TextureImageFormat.Byte => PixelType.UnsignedByte,
            TextureImageFormat.Float => PixelType.Float,
            TextureImageFormat.Float2 => PixelType.Float,
            TextureImageFormat.Float3 => PixelType.Float,
            TextureImageFormat.Float4 => PixelType.Float,
            TextureImageFormat.Short => PixelType.Short,
            TextureImageFormat.Short2 => PixelType.Short,
            TextureImageFormat.Short3 => PixelType.Short,
            TextureImageFormat.Short4 => PixelType.Short,
            TextureImageFormat.Int => PixelType.Int,
            TextureImageFormat.Int2 => PixelType.Int,
            TextureImageFormat.Int3 => PixelType.Int,
            TextureImageFormat.Int4 => PixelType.Int,
            TextureImageFormat.UnsignedShort => PixelType.UnsignedShort,
            TextureImageFormat.UnsignedShort2 => PixelType.UnsignedShort,
            TextureImageFormat.UnsignedShort3 => PixelType.UnsignedShort,
            TextureImageFormat.UnsignedShort4 => PixelType.UnsignedShort,
            TextureImageFormat.UnsignedInt => PixelType.UnsignedInt,
            TextureImageFormat.UnsignedInt2 => PixelType.UnsignedInt,
            TextureImageFormat.UnsignedInt3 => PixelType.UnsignedInt,
            TextureImageFormat.UnsignedInt4 => PixelType.UnsignedInt,
            TextureImageFormat.Depth16f => PixelType.Float,
            TextureImageFormat.Depth24f => PixelType.Float,
            TextureImageFormat.Depth32f => PixelType.Float,
            TextureImageFormat.Depth24Stencil8 => (PixelType)GLEnum.UnsignedInt248,
            _ => throw new ArgumentException("Image format is not a valid TextureImageFormat value", nameof(imageFormat)),
        };

        pixelInternalFormat = imageFormat switch
        {
            TextureImageFormat.Color4b => InternalFormat.Rgba8,
            TextureImageFormat.Byte => InternalFormat.R8ui,
            TextureImageFormat.Float => InternalFormat.R32f,
            TextureImageFormat.Float2 => InternalFormat.RG32f,
            TextureImageFormat.Float3 => InternalFormat.Rgb32f,
            TextureImageFormat.Float4 => InternalFormat.Rgba32f,
            TextureImageFormat.Short => InternalFormat.R16f,
            TextureImageFormat.Short2 => InternalFormat.RG16f,
            TextureImageFormat.Short3 => InternalFormat.Rgb16f,
            TextureImageFormat.Short4 => InternalFormat.Rgba16f,
            TextureImageFormat.Int => InternalFormat.R32i,
            TextureImageFormat.Int2 => InternalFormat.RG32i,
            TextureImageFormat.Int3 => InternalFormat.Rgb32i,
            TextureImageFormat.Int4 => InternalFormat.Rgba32i,
            TextureImageFormat.UnsignedShort => InternalFormat.R16f,
            TextureImageFormat.UnsignedShort2 => InternalFormat.RG16f,
            TextureImageFormat.UnsignedShort3 => InternalFormat.Rgb16f,
            TextureImageFormat.UnsignedShort4 => InternalFormat.Rgba16f,
            TextureImageFormat.UnsignedInt => InternalFormat.R32ui,
            TextureImageFormat.UnsignedInt2 => InternalFormat.RG32ui,
            TextureImageFormat.UnsignedInt3 => InternalFormat.Rgb32ui,
            TextureImageFormat.UnsignedInt4 => InternalFormat.Rgba32ui,
            TextureImageFormat.Depth16f => InternalFormat.DepthComponent16,
            TextureImageFormat.Depth24f => InternalFormat.DepthComponent24,
            TextureImageFormat.Depth32f => InternalFormat.DepthComponent32f,
            TextureImageFormat.Depth24Stencil8 => InternalFormat.Depth24Stencil8,
            _ => throw new ArgumentException("Image format is not a valid TextureImageFormat value", nameof(imageFormat)),
        };

        pixelFormat = imageFormat switch
        {
            TextureImageFormat.Color4b => PixelFormat.Rgba,
            TextureImageFormat.Byte => PixelFormat.RedInteger,
            TextureImageFormat.Short => PixelFormat.Red,
            TextureImageFormat.Short2 => PixelFormat.RG,
            TextureImageFormat.Short3 => PixelFormat.Rgb,
            TextureImageFormat.Short4 => PixelFormat.Rgba,
            TextureImageFormat.Float => PixelFormat.Red,
            TextureImageFormat.Float2 => PixelFormat.RG,
            TextureImageFormat.Float3 => PixelFormat.Rgb,
            TextureImageFormat.Float4 => PixelFormat.Rgba,
            TextureImageFormat.Int => PixelFormat.RgbaInteger,
            TextureImageFormat.Int2 => PixelFormat.RGInteger,
            TextureImageFormat.Int3 => PixelFormat.RgbInteger,
            TextureImageFormat.Int4 => PixelFormat.RgbaInteger,
            TextureImageFormat.UnsignedShort => PixelFormat.Red,
            TextureImageFormat.UnsignedShort2 => PixelFormat.RG,
            TextureImageFormat.UnsignedShort3 => PixelFormat.Rgb,
            TextureImageFormat.UnsignedShort4 => PixelFormat.Rgba,
            TextureImageFormat.UnsignedInt => PixelFormat.RedInteger,
            TextureImageFormat.UnsignedInt2 => PixelFormat.RGInteger,
            TextureImageFormat.UnsignedInt3 => PixelFormat.RgbInteger,
            TextureImageFormat.UnsignedInt4 => PixelFormat.RgbaInteger,
            TextureImageFormat.Depth16f => PixelFormat.DepthComponent,
            TextureImageFormat.Depth24f => PixelFormat.DepthComponent,
            TextureImageFormat.Depth32f => PixelFormat.DepthComponent,
            TextureImageFormat.Depth24Stencil8 => PixelFormat.DepthStencil,
            _ => throw new ArgumentException("Image format is not a valid TextureImageFormat value", nameof(imageFormat)),
        };
    }
}
