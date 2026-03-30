// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;

using ImageMagick;

using Prowl.Echo;

namespace Prowl.Runtime.Resources;

public sealed class RenderTexture : EngineObject, ISerializable
{
    public GraphicsFrameBuffer frameBuffer { get; private set; }
    public Texture2D MainTexture => InternalTextures[0];
    public Texture2D[] InternalTextures { get; private set; }
    public Texture2D InternalDepth { get; private set; }

    public int Width { get; private set; }
    public int Height { get; private set; }
    private int numTextures;
    private bool hasDepthAttachment;
    private TextureImageFormat[] textureFormats;

    public RenderTexture() : base("RenderTexture")
    {
        Width = 0;
        Height = 0;
        numTextures = 0;
        hasDepthAttachment = false;
        textureFormats = [];
    }

    public RenderTexture(int Width, int Height, bool hasDepthAttachment, TextureImageFormat[] formats) : base("RenderTexture")
    {
        this.Width = Width;
        this.Height = Height;
        numTextures = formats?.Length ?? throw new ArgumentNullException(nameof(formats), "Texture formats cannot be null.");
        this.hasDepthAttachment = hasDepthAttachment;

        if (numTextures < 0 || numTextures > Graphics.MaxFramebufferColorAttachments)
            throw new Exception("Invalid number of textures! [0-" + Graphics.MaxFramebufferColorAttachments + "]");

        textureFormats = formats;

        GraphicsFrameBuffer.Attachment[] attachments = new GraphicsFrameBuffer.Attachment[numTextures + (hasDepthAttachment ? 1 : 0)];
        InternalTextures = new Texture2D[numTextures];
        for (int i = 0; i < numTextures; i++)
        {
            InternalTextures[i] = new Texture2D((uint)Width, (uint)Height, false, textureFormats[i]);
            InternalTextures[i].SetTextureFilters(TextureMin.Linear, TextureMag.Linear);
            InternalTextures[i].SetWrapModes(TextureWrap.ClampToEdge, TextureWrap.ClampToEdge);
            attachments[i] = new GraphicsFrameBuffer.Attachment { Texture = InternalTextures[i].Handle, IsDepth = false };
        }

        if (hasDepthAttachment)
        {
            InternalDepth = new Texture2D((uint)Width, (uint)Height, false, TextureImageFormat.Depth24f);
            InternalDepth.SetWrapModes(TextureWrap.ClampToEdge, TextureWrap.ClampToEdge);
            attachments[numTextures] = new GraphicsFrameBuffer.Attachment { Texture = InternalDepth.Handle, IsDepth = true };
        }

        frameBuffer = Graphics.CreateFramebuffer(attachments, (uint)Width, (uint)Height);
    }

    public void Begin()
    {
        Graphics.BindFramebuffer(frameBuffer);
    }

    public void End()
    {
        Graphics.UnbindFramebuffer();
    }

    public override void OnDispose()
    {
        if (frameBuffer == null) return;
        foreach (Texture2D texture in InternalTextures)
            texture.Dispose();

        InternalDepth?.Dispose();
        frameBuffer.Dispose();
    }

    /// <summary>
    /// Saves the contents of one of this <see cref="RenderTexture"/>'s color attachments to a PNG file.
    /// </summary>
    /// <param name="filePath">The destination file path.</param>
    /// <param name="textureIndex">The index of the color attachment to save (default 0 = <see cref="MainTexture"/>).</param>
    public void SaveToPng(string filePath, int textureIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (InternalTextures == null || textureIndex < 0 || textureIndex >= InternalTextures.Length)
            throw new ArgumentOutOfRangeException(nameof(textureIndex), InternalTextures == null ? "Internal Textures array is null." : $"Texture Index: {textureIndex} is outside of the range (0,{InternalTextures.Length})");

        Texture2D texture = InternalTextures[textureIndex];
        int byteSize = texture.GetSize();
        byte[] pixelData = new byte[byteSize];
        texture.GetData<byte>(pixelData);

        var (storageType, mapping) = GetPixelSettings(texture.ImageFormat);
        var readSettings = new PixelReadSettings((uint)Width, (uint)Height, storageType, mapping);

        using var image = new MagickImage();
        image.ReadPixels(pixelData, readSettings);
        image.Flip();
        image.Write(filePath, MagickFormat.Png);
    }

    private static (StorageType storageType, string mapping) GetPixelSettings(TextureImageFormat format) => format switch
    {
        TextureImageFormat.Color4b => (StorageType.Char, "RGBA"),
        TextureImageFormat.Byte => (StorageType.Char, "R"),
        TextureImageFormat.Float => (StorageType.Float, "R"),
        TextureImageFormat.Float2 => (StorageType.Float, "RG"),
        TextureImageFormat.Float3 => (StorageType.Float, "RGB"),
        TextureImageFormat.Float4 => (StorageType.Float, "RGBA"),
        TextureImageFormat.Short or TextureImageFormat.UnsignedShort => (StorageType.Short, "R"),
        TextureImageFormat.Short2 or TextureImageFormat.UnsignedShort2 => (StorageType.Short, "RG"),
        TextureImageFormat.Short3 or TextureImageFormat.UnsignedShort3 => (StorageType.Short, "RGB"),
        TextureImageFormat.Short4 or TextureImageFormat.UnsignedShort4 => (StorageType.Short, "RGBA"),
        TextureImageFormat.Int or TextureImageFormat.UnsignedInt => (StorageType.Int32, "R"),
        TextureImageFormat.Int2 or TextureImageFormat.UnsignedInt2 => (StorageType.Int32, "RG"),
        TextureImageFormat.Int3 or TextureImageFormat.UnsignedInt3 => (StorageType.Int32, "RGB"),
        TextureImageFormat.Int4 or TextureImageFormat.UnsignedInt4 => (StorageType.Int32, "RGBA"),
        _ => throw new NotSupportedException($"Texture format '{format}' is not supported for PNG export.")
    };

    public void Serialize(ref EchoObject compoundTag, SerializationContext ctx)
    {
        compoundTag.Add("Width", new(Width));
        compoundTag.Add("Height", new(Height));
        compoundTag.Add("NumTextures", new(numTextures));
        compoundTag.Add("HasDepthAttachment", new((byte)(hasDepthAttachment ? 1 : 0)));
        EchoObject textureFormatsTag = EchoObject.NewList();
        foreach (TextureImageFormat format in textureFormats)
            textureFormatsTag.ListAdd(new((byte)format));
        compoundTag.Add("TextureFormats", textureFormatsTag);
    }

    public void Deserialize(EchoObject value, SerializationContext ctx)
    {
        Width = value["Width"].IntValue;
        Height = value["Height"].IntValue;
        numTextures = value["NumTextures"].IntValue;
        hasDepthAttachment = value["HasDepthAttachment"].ByteValue == 1;
        textureFormats = new TextureImageFormat[numTextures];
        EchoObject? textureFormatsTag = value.Get("TextureFormats");
        for (int i = 0; i < numTextures; i++)
            textureFormats[i] = (TextureImageFormat)textureFormatsTag[i].ByteValue;

        Type[] param = new[] { typeof(int), typeof(int), typeof(int), typeof(bool), typeof(TextureImageFormat[]) };
        object[] values = new object[] { Width, Height, numTextures, hasDepthAttachment, textureFormats };
        typeof(RenderTexture).GetConstructor(param).Invoke(this, values);
    }

    #region Pool

    private struct RenderTextureKey(int width, int height, bool hasDepth, TextureImageFormat[] format)
    {
        public int Width = width;
        public int Height = height;
        public bool HasDepth = hasDepth;
        public TextureImageFormat[] Format = format;

        public override bool Equals(object? obj)
        {
            if (obj is RenderTextureKey key)
            {
                if (Width == key.Width && Height == key.Height && HasDepth == key.HasDepth && Format.Length == key.Format.Length)
                {
                    for (int i = 0; i < Format.Length; i++)
                        if (Format[i] != key.Format[i])
                            return false;
                    return true;
                }
            }
            return false;
        }
        public override int GetHashCode()
        {
            int hash = 17;
            hash = hash * 23 + Width.GetHashCode();
            hash = hash * 23 + Height.GetHashCode();
            hash = hash * 23 + HasDepth.GetHashCode();
            foreach (TextureImageFormat format in Format)
                hash = hash * 23 + ((int)format).GetHashCode();
            return hash;
        }
        public static bool operator ==(RenderTextureKey left, RenderTextureKey right) => left.Equals(right);
        public static bool operator !=(RenderTextureKey left, RenderTextureKey right) => !(left == right);
    }

    private static Dictionary<RenderTextureKey, List<(RenderTexture, long frameCreated)>> pool = [];
    private static Dictionary<RenderTextureKey, List<(RenderTexture, long frameAcquired)>> active = [];
    private const int MaxUnusedFrames = 10;
    private const int MaxActiveFrames = 3; // Warn if held longer than 3 frames

    public static RenderTexture GetTemporaryRT(int width, int height, bool hasDepth, TextureImageFormat[] format)
    {
        var key = new RenderTextureKey(width, height, hasDepth, format);

        RenderTexture renderTexture;
        if (pool.TryGetValue(key, out List<(RenderTexture, long frameCreated)>? list) && list.Count > 0)
        {
            // With multiple frames in flight (e.g. Vulkan double-buffering),
            // the GPU may still be reading a render target from a previous
            // frame.  Only reuse textures whose release frame is old enough
            // that the corresponding GPU fence has been waited on.
            int minFrameAge = Graphics.IsGraphiteReady ? Graphics.Graphite.FramesInFlight : 1;
            int foundIdx = -1;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (Time.FrameCount - list[i].frameCreated >= minFrameAge)
                {
                    foundIdx = i;
                    break;
                }
            }

            if (foundIdx >= 0)
            {
                renderTexture = list[foundIdx].Item1;
                list.RemoveAt(foundIdx);
            }
            else
            {
                renderTexture = new RenderTexture(width, height, hasDepth, format);
            }
        }
        else
        {
            renderTexture = new RenderTexture(width, height, hasDepth, format);
        }

        // Track in active pool
        if (!active.TryGetValue(key, out List<(RenderTexture, long frameAcquired)>? activeList))
        {
            activeList = [];
            active[key] = activeList;
        }
        activeList.Add((renderTexture, Time.FrameCount));

        return renderTexture;
    }

    public static void ReleaseTemporaryRT(RenderTexture renderTexture)
    {
        var key = new RenderTextureKey(renderTexture.Width, renderTexture.Height, renderTexture.hasDepthAttachment, [.. renderTexture.InternalTextures.Select(t => t.ImageFormat)]);

        // Remove from active pool
        if (active.TryGetValue(key, out List<(RenderTexture, long frameAcquired)>? activeList))
        {
            for (int i = activeList.Count - 1; i >= 0; i--)
            {
                if (activeList[i].Item1 == renderTexture)
                {
                    activeList.RemoveAt(i);
                    break;
                }
            }
        }

        // Add to pool for reuse
        if (!pool.TryGetValue(key, out List<(RenderTexture, long frameCreated)>? list))
        {
            list = [];
            pool[key] = list;
        }

        list.Add((renderTexture, Time.FrameCount));
    }

    // Reusable buffer for UpdatePool to avoid per-frame List allocation.
    private static readonly List<RenderTexture> s_disposableBuffer = [];

    public static void UpdatePool()
    {
        s_disposableBuffer.Clear();

        // Check for leaked active render textures (held longer than MaxActiveFrames)
        foreach (KeyValuePair<RenderTextureKey, List<(RenderTexture, long frameAcquired)>> pair in active)
        {
            for (int i = pair.Value.Count - 1; i >= 0; i--)
            {
                (RenderTexture renderTexture, long frameAcquired) = pair.Value[i];
                long framesActive = Time.FrameCount - frameAcquired;

                if (framesActive > MaxActiveFrames)
                {
                    Debug.LogWarning($"RenderTexture leak detected! Texture ({renderTexture.Width}x{renderTexture.Height}) has been active for {framesActive} frames (max: {MaxActiveFrames}). Auto-disposing to prevent memory leak.");
                    s_disposableBuffer.Add(renderTexture);
                    pair.Value.RemoveAt(i);
                }
            }
        }

        // Clean up unused textures in pool
        foreach (KeyValuePair<RenderTextureKey, List<(RenderTexture, long frameCreated)>> pair in pool)
        {
            for (int i = pair.Value.Count - 1; i >= 0; i--)
            {
                (RenderTexture renderTexture, long frameCreated) = pair.Value[i];
                if (Time.FrameCount - frameCreated > MaxUnusedFrames)
                {
                    s_disposableBuffer.Add(renderTexture);
                    pair.Value.RemoveAt(i);
                }
            }
        }

        for (int i = 0; i < s_disposableBuffer.Count; i++)
            s_disposableBuffer[i].Dispose();
    }

    #endregion

}
