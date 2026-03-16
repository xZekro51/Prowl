// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;

namespace Prowl.Editor.Services;

/// <summary>
/// File-system-backed asset database. Scans a real directory tree
/// and supports creating folders/files.
/// </summary>
public sealed class FileSystemAssetDatabase : IAssetService
{
    private string _root = string.Empty;

    public string AssetRootPath => _root;
    public bool HasProject => !string.IsNullOrEmpty(_root) && Directory.Exists(_root);

    public void SetAssetRoot(string path)
    {
        _root = Path.GetFullPath(path);
        if (!Directory.Exists(_root))
            Directory.CreateDirectory(_root);

        Debug.Log($"[Assets] Root set to: {_root}");
    }

    public void Refresh()
    {
        // No-op for now — entries are read on demand.
        // A real implementation would track a FileSystemWatcher here.
    }

    public IReadOnlyList<AssetEntry> GetEntries(string relativeDir)
    {
        string absDir = GetAbsolutePath(relativeDir);
        if (!Directory.Exists(absDir))
            return Array.Empty<AssetEntry>();

        var entries = new List<AssetEntry>();

        // Folders first
        foreach (var dir in Directory.GetDirectories(absDir).OrderBy(Path.GetFileName))
        {
            string name = Path.GetFileName(dir);
            entries.Add(new AssetEntry
            {
                Name = name,
                FullPath = dir,
                RelativePath = Path.GetRelativePath(_root, dir).Replace('\\', '/'),
                IsDirectory = true,
                Extension = string.Empty,
            });
        }

        // Then files
        foreach (var file in Directory.GetFiles(absDir).OrderBy(Path.GetFileName))
        {
            string name = Path.GetFileName(file);
            entries.Add(new AssetEntry
            {
                Name = name,
                FullPath = file,
                RelativePath = Path.GetRelativePath(_root, file).Replace('\\', '/'),
                IsDirectory = false,
                Extension = Path.GetExtension(file).ToLowerInvariant(),
            });
        }

        return entries;
    }

    public AssetEntry CreateFolder(string relativeDir, string folderName)
    {
        string absDir = Path.Combine(GetAbsolutePath(relativeDir), folderName);
        Directory.CreateDirectory(absDir);
        Debug.Log($"[Assets] Created folder: {absDir}");
        return new AssetEntry
        {
            Name = folderName,
            FullPath = absDir,
            RelativePath = Path.GetRelativePath(_root, absDir).Replace('\\', '/'),
            IsDirectory = true,
        };
    }

    public AssetEntry CreateFile(string relativeDir, string fileName, string content)
    {
        string absPath = Path.Combine(GetAbsolutePath(relativeDir), fileName);
        File.WriteAllText(absPath, content);
        Debug.Log($"[Assets] Created file: {absPath}");
        return new AssetEntry
        {
            Name = fileName,
            FullPath = absPath,
            RelativePath = Path.GetRelativePath(_root, absPath).Replace('\\', '/'),
            IsDirectory = false,
            Extension = Path.GetExtension(fileName).ToLowerInvariant(),
        };
    }

    public void Delete(AssetEntry entry)
    {
        if (entry.IsDirectory && Directory.Exists(entry.FullPath))
        {
            Directory.Delete(entry.FullPath, false);
            Debug.Log($"[Assets] Deleted folder: {entry.FullPath}");
        }
        else if (!entry.IsDirectory && File.Exists(entry.FullPath))
        {
            File.Delete(entry.FullPath);
            Debug.Log($"[Assets] Deleted file: {entry.FullPath}");
        }
    }

    public string GetAbsolutePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath == ".")
            return _root;
        return Path.Combine(_root, relativePath);
    }

    public IReadOnlyList<AssetEntry> GetAllEntriesRecursive(string? extensionFilter = null)
    {
        if (!Directory.Exists(_root))
            return Array.Empty<AssetEntry>();

        var entries = new List<AssetEntry>();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            if (extensionFilter != null &&
                !Path.GetExtension(file).Equals(extensionFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            string name = Path.GetFileName(file);
            entries.Add(new AssetEntry
            {
                Name = name,
                FullPath = file,
                RelativePath = Path.GetRelativePath(_root, file).Replace('\\', '/'),
                IsDirectory = false,
                Extension = Path.GetExtension(file).ToLowerInvariant(),
            });
        }

        return entries;
    }
}
