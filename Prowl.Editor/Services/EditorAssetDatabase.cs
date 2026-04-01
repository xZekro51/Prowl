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

            // Cache sub-resources for Models so Get(subGuid) resolves later
            if (obj is Model model)
                CacheModelSubResources(model);
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
                ".asset" => ScriptableObjectSerializer.Load(absolutePath),
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" =>
                    LoadTexture(absolutePath, relativePath),
                ".obj" or ".fbx" or ".gltf" or ".glb" or ".dae" or ".3ds" or ".blend" or ".ply" or ".stl" =>
                    Model.LoadFromFile(absolutePath),
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

        var tex = Texture2D.FromFile(absolutePath, generateMipmaps: true);
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

    /// <inheritdoc />
    public EngineObject? ResolveByPath(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
            return null;

        int hashIdx = assetPath.IndexOf('#');
        if (hashIdx < 0)
        {
            // Not a sub-resource path — try normal resolution
            Guid id = ResolveAssetId(assetPath);
            return id != Guid.Empty ? Get(id) : null;
        }

        string parentPath = assetPath[..hashIdx];
        string fragment = assetPath[(hashIdx + 1)..];

        Guid parentGuid = ResolveAssetId(parentPath);
        if (parentGuid == Guid.Empty)
            return null;

        // Load the parent model (this also caches sub-resources)
        var parent = Get(parentGuid);
        if (parent is not Model model)
            return null;

        return ResolveSubResource(model, fragment);
    }

    private void CacheModelSubResources(Model model)
    {
        model.StampSubResourceIds();

        foreach (var mm in model.Meshes)
        {
            if (mm.Mesh is { AssetID: var id } && id != Guid.Empty)
                _cache.TryAdd(id, mm.Mesh);
        }

        foreach (var mat in model.Materials)
        {
            if (mat is { AssetID: var id } && id != Guid.Empty)
                _cache.TryAdd(id, mat);
        }
    }

    private static EngineObject? ResolveSubResource(Model model, string fragment)
    {
        string[] parts = fragment.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int index))
            return null;

        return parts[0] switch
        {
            "Mesh" when index >= 0 && index < model.Meshes.Count => model.Meshes[index].Mesh,
            "Material" when index >= 0 && index < model.Materials.Count => model.Materials[index],
            _ => null
        };
    }
}
