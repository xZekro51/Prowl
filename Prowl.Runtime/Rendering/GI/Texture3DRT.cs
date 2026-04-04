// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;

namespace Prowl.Runtime.Rendering.GI;

/// <summary>
/// Lightweight 3D render-texture wrapper for compute read/write.
/// Analogous to RenderTexture but for 3D (volumetric) textures.
/// Supports temporary pooling to avoid per-frame allocation.
/// </summary>
public sealed class Texture3DRT : EngineObject
{
    public uint Width { get; }
    public uint Height { get; }
    public uint Depth { get; }
    public TextureImageFormat Format { get; }
    public bool HasMipmaps { get; }

    /// <summary>
    /// Graphite texture handle for compute read/write and sampling.
    /// </summary>
    public Graphite.Texture? GraphiteTexture { get; private set; }

    public Texture3DRT(uint width, uint height, uint depth,
                       TextureImageFormat format, bool generateMipmaps = false)
        : base("Texture3DRT")
    {
        Width = width;
        Height = height;
        Depth = depth;
        Format = format;
        HasMipmaps = generateMipmaps;

        CreateGPUResources();
    }

    private void CreateGPUResources()
    {
        if (!Graphics.IsGraphiteReady)
        {
            Debug.LogWarning("Texture3DRT: Graphite device not ready, GPU resources not created.");
            return;
        }

        Graphite.GraphiteDevice device = Graphics.Graphite;

        uint mipLevels = 1;
        if (HasMipmaps)
        {
            uint maxDim = Math.Max(Width, Math.Max(Height, Depth));
            mipLevels = (uint)Math.Floor(Math.Log2(maxDim)) + 1;
        }

        Graphite.TextureFormat graphiteFormat = GraphiteFormatMapper.MapTextureFormat(Format);

        GraphiteTexture = device.CreateTexture(new Graphite.TextureDescriptor
        {
            Width = Width,
            Height = Height,
            Depth = Depth,
            MipLevels = mipLevels,
            ArrayLayers = 1,
            Format = graphiteFormat,
            Dimension = Graphite.TextureDimension.Texture3D,
            Usage = Graphite.TextureUsage.Storage | Graphite.TextureUsage.Sampled
                  | Graphite.TextureUsage.CopyDestination | Graphite.TextureUsage.CopySource,
        });
    }

    public override void OnDispose()
    {
        GraphiteTexture?.Dispose();
        GraphiteTexture = null;
    }

    #region Temporary Pool

    private struct PoolEntry
    {
        public Texture3DRT Texture;
        public uint ReturnedFrame;
    }

    /// <summary>Minimum number of frames a texture must sit in the pool before reuse.</summary>
    private const uint MinFrameAge = 2;

    private static readonly ConcurrentDictionary<(uint, uint, uint, TextureImageFormat, bool), ConcurrentBag<PoolEntry>> s_pool = new();

    /// <summary>
    /// Gets a temporary 3D render texture from the pool, or creates a new one.
    /// Textures returned to the pool must be at least <see cref="MinFrameAge"/> frames
    /// old before they can be reused, preventing GPU read-after-free hazards.
    /// </summary>
    public static Texture3DRT GetTemporary(uint w, uint h, uint d,
                                            TextureImageFormat fmt, bool mipmaps = false)
    {
        (uint, uint, uint, TextureImageFormat, bool) key = (w, h, d, fmt, mipmaps);
        uint currentFrame = (uint)Time.FrameCount;

        if (s_pool.TryGetValue(key, out ConcurrentBag<PoolEntry>? bag))
        {
            // Try to find an entry old enough to reuse
            ConcurrentBag<PoolEntry> deferred = new();
            while (bag.TryTake(out PoolEntry entry))
            {
                if (entry.Texture.IsNotValid())
                    continue; // discard disposed

                if (currentFrame - entry.ReturnedFrame >= MinFrameAge)
                    return entry.Texture;

                // Not old enough — put it back
                deferred.Add(entry);
            }

            // Re-add entries that were too young
            while (deferred.TryTake(out PoolEntry young))
                bag.Add(young);
        }

        return new Texture3DRT(w, h, d, fmt, mipmaps);
    }

    /// <summary>
    /// Returns a temporary 3D render texture to the pool for reuse.
    /// </summary>
    public static void ReleaseTemporary(Texture3DRT rt)
    {
        if (rt.IsNotValid())
            return;

        (uint, uint, uint, TextureImageFormat, bool) key = (rt.Width, rt.Height, rt.Depth, rt.Format, rt.HasMipmaps);
        ConcurrentBag<PoolEntry> bag = s_pool.GetOrAdd(key, _ => new ConcurrentBag<PoolEntry>());
        bag.Add(new PoolEntry { Texture = rt, ReturnedFrame = (uint)Time.FrameCount });
    }

    /// <summary>
    /// Clears the temporary pool and disposes all cached textures.
    /// </summary>
    public static void ClearPool()
    {
        foreach (System.Collections.Generic.KeyValuePair<(uint, uint, uint, TextureImageFormat, bool), ConcurrentBag<PoolEntry>> kvp in s_pool)
        {
            while (kvp.Value.TryTake(out PoolEntry entry))
                entry.Texture?.Dispose();
        }
        s_pool.Clear();
    }

    #endregion
}
