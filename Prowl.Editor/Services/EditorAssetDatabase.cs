// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Prowl.Editor.Services;

/// <summary>
/// Editor-side implementation of <see cref="IAssetDatabase"/> that resolves
/// <see cref="EngineObject"/> instances by their asset GUID.
/// Bridges the editor's <see cref="IAssetService"/> / <see cref="AssetMetaManager"/>
/// to the runtime's <see cref="AssetDatabase"/> interface so that
/// <see cref="AssetDatabase.ConfigureContext"/> can serialize asset references
/// as compact <c>$assetId</c> tags and resolve them back on deserialization.
/// </summary>
public sealed class EditorAssetDatabase : IAssetDatabase
{
    private readonly IAssetService _assetService;

    // Cache of loaded assets by GUID to avoid repeated disk loads
    private readonly Dictionary<Guid, EngineObject?> _cache = [];

    public EditorAssetDatabase(IAssetService assetService)
    {
        _assetService = assetService;
    }

    /// <inheritdoc />
    public EngineObject? Get(Guid assetId)
    {
        if (assetId == Guid.Empty)
            return null;

        // Check cache first
        if (_cache.TryGetValue(assetId, out var cached))
        {
            if (cached != null && !cached.IsDisposed)
                return cached;

            // Stale entry — remove and re-resolve
            _cache.Remove(assetId);
        }

        // Resolve the GUID to a relative asset path via the meta system
        string guidStr = assetId.ToString("N");
        string? relativePath = _assetService.GetAssetPathByGuid(guidStr);
        if (relativePath == null)
            return null;

        string absolutePath = _assetService.GetAbsolutePath(relativePath);
        if (!File.Exists(absolutePath))
            return null;

        // Try to load based on file extension
        EngineObject? obj = LoadAsset(absolutePath, relativePath);

        if (obj != null)
        {
            obj.AssetID = assetId;
            obj.AssetPath = relativePath;
            _cache[assetId] = obj;
        }

        return obj;
    }

    /// <summary>
    /// Loads an asset from disk based on its file extension.
    /// Returns null for unsupported formats.
    /// </summary>
    private static EngineObject? LoadAsset(string absolutePath, string relativePath)
    {
        string ext = Path.GetExtension(absolutePath).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".shader" => Shader.LoadFromFile(absolutePath),
                ".mat" => MaterialSerializer.Load(absolutePath),
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" =>
                    LoadTexture(absolutePath, relativePath),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[EditorAssetDatabase] Failed to load '{relativePath}': {ex.Message}");
            return null;
        }
    }

    private static Texture2D? LoadTexture(string absolutePath, string relativePath)
    {
        if (!File.Exists(absolutePath))
            return null;

        var tex = Texture2D.FromFile(absolutePath);
        if (tex != null)
        {
            tex.Name = Path.GetFileNameWithoutExtension(absolutePath);
            tex.AssetPath = relativePath;
        }
        return tex;
    }

    /// <summary>
    /// Invalidates the cached entry for a specific GUID so the next
    /// <see cref="Get"/> call will reload it from disk.
    /// </summary>
    public void Invalidate(Guid assetId) => _cache.Remove(assetId);

    /// <summary>
    /// Clears the entire asset cache, forcing all subsequent lookups
    /// to reload from disk.
    /// </summary>
    public void ClearCache() => _cache.Clear();

    /// <inheritdoc />
    public Guid ResolveAssetId(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
            return Guid.Empty;

        string relativePath = assetPath;

        // Convert absolute paths to relative
        if (Path.IsPathRooted(relativePath) && !string.IsNullOrEmpty(_assetService.AssetRootPath))
            relativePath = Path.GetRelativePath(_assetService.AssetRootPath, relativePath).Replace('\\', '/');

        string? guid = _assetService.GetGuidByPath(relativePath);
        if (guid != null && Guid.TryParse(guid, out Guid assetId))
            return assetId;

        return Guid.Empty;
    }
}
