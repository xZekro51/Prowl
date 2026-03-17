// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;

namespace Prowl.Editor.Services;

/// <summary>
/// Manages .meta files alongside assets, maintaining bidirectional
/// GUID ↔ asset-path mappings. When the asset database is scanned,
/// each file gets a .meta; if no .meta exists, one is created with
/// a fresh GUID.
/// </summary>
public sealed class AssetMetaManager
{
    // GUID → relative asset path
    private readonly Dictionary<string, string> _guidToPath = new(StringComparer.OrdinalIgnoreCase);
    // relative asset path → GUID
    private readonly Dictionary<string, string> _pathToGuid = new(StringComparer.OrdinalIgnoreCase);
    // GUID → full MetaFile (so import settings are accessible)
    private readonly Dictionary<string, MetaFile> _guidToMeta = new(StringComparer.OrdinalIgnoreCase);

    private string _root = string.Empty;

    /// <summary> Sets the asset root and performs a full scan. </summary>
    public void Initialize(string assetRoot)
    {
        _root = assetRoot;
        Refresh();
    }

    /// <summary>
    /// Re-scans the asset root, ensuring every file has a .meta and
    /// rebuilding the in-memory GUID maps.
    /// </summary>
    public void Refresh()
    {
        _guidToPath.Clear();
        _pathToGuid.Clear();
        _guidToMeta.Clear();

        if (!Directory.Exists(_root))
            return;

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            // Skip .meta files themselves
            if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                continue;

            EnsureMeta(file);
        }
    }

    /// <summary>
    /// Ensures a .meta file exists for the given asset. If not, creates one.
    /// Registers the GUID mapping in memory.
    /// </summary>
    public MetaFile EnsureMeta(string absoluteAssetPath)
    {
        string metaPath = MetaFile.GetMetaPath(absoluteAssetPath);
        MetaFile? meta = MetaFile.Load(metaPath);

        if (meta == null || string.IsNullOrEmpty(meta.Guid))
        {
            meta = MetaFile.CreateNew();
            meta.Save(metaPath);
            Debug.Log($"[Meta] Created .meta for: {Path.GetFileName(absoluteAssetPath)}  GUID={meta.Guid}");
        }

        string relativePath = Path.GetRelativePath(_root, absoluteAssetPath).Replace('\\', '/');

        // Handle GUID collisions (shouldn't happen, but guard against it)
        if (_guidToPath.TryGetValue(meta.Guid, out var existingPath) && existingPath != relativePath)
        {
            Debug.LogWarning($"[Meta] GUID collision for {meta.Guid} — regenerating for {relativePath}");
            meta = MetaFile.CreateNew();
            meta.Save(metaPath);
        }

        _guidToPath[meta.Guid] = relativePath;
        _pathToGuid[relativePath] = meta.Guid;
        _guidToMeta[meta.Guid] = meta;

        return meta;
    }

    /// <summary> Returns the GUID for a relative asset path, or null if unknown. </summary>
    public string? GetGuid(string relativePath)
    {
        return _pathToGuid.TryGetValue(relativePath, out var guid) ? guid : null;
    }

    /// <summary> Returns the relative asset path for a GUID, or null if unknown. </summary>
    public string? GetAssetPath(string guid)
    {
        return _guidToPath.TryGetValue(guid, out var path) ? path : null;
    }

    /// <summary> Returns the MetaFile for a GUID, or null if unknown. </summary>
    public MetaFile? GetMeta(string guid)
    {
        return _guidToMeta.TryGetValue(guid, out var meta) ? meta : null;
    }

    /// <summary>
    /// Called when an asset is deleted. Removes the .meta file and clears mappings.
    /// </summary>
    public void RemoveMeta(string absoluteAssetPath)
    {
        string relativePath = Path.GetRelativePath(_root, absoluteAssetPath).Replace('\\', '/');

        if (_pathToGuid.TryGetValue(relativePath, out var guid))
        {
            _guidToPath.Remove(guid);
            _guidToMeta.Remove(guid);
            _pathToGuid.Remove(relativePath);
        }

        string metaPath = MetaFile.GetMetaPath(absoluteAssetPath);
        if (File.Exists(metaPath))
        {
            try { File.Delete(metaPath); }
            catch { /* best effort */ }
        }
    }

    /// <summary> Returns all known GUIDs. </summary>
    public IReadOnlyDictionary<string, string> GuidToPathMap => _guidToPath;
}
