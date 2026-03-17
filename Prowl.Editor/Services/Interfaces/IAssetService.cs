// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Editor.Services;

/// <summary>
/// Represents a single entry in the asset database (file or folder).
/// </summary>
public sealed class AssetEntry
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public string Extension { get; init; } = string.Empty;

    public override string ToString() => Name;
}

/// <summary>
/// Abstracts the project asset database. Provides file-system scanning,
/// folder/file creation, and metadata for the Project browser.
/// </summary>
public interface IAssetService
{
    /// <summary> Root folder of the current project's assets. </summary>
    string AssetRootPath { get; }

    /// <summary> Returns true if a project is currently open. </summary>
    bool HasProject { get; }

    /// <summary> Initializes the asset root, creating the folder if needed. </summary>
    void SetAssetRoot(string path);

    /// <summary> Refreshes the asset database by re-scanning the asset folder. </summary>
    void Refresh();

    /// <summary> Returns all entries (files and folders) under the given relative directory. </summary>
    IReadOnlyList<AssetEntry> GetEntries(string relativeDir);

    /// <summary> Creates a new subfolder under the given relative directory. Returns the new entry. </summary>
    AssetEntry CreateFolder(string relativeDir, string folderName);

    /// <summary> Creates a new file with the given content. Returns the new entry. </summary>
    AssetEntry CreateFile(string relativeDir, string fileName, string content);

    /// <summary> Deletes an asset entry (file or empty folder). </summary>
    void Delete(AssetEntry entry);

    /// <summary> Returns the absolute path for a relative asset path. </summary>
    string GetAbsolutePath(string relativePath);

    /// <summary> Recursively returns all file entries under the asset root, optionally filtered by extension. </summary>
    IReadOnlyList<AssetEntry> GetAllEntriesRecursive(string? extensionFilter = null);

    // ── GUID / .meta support ──────────────────────────────────

    /// <summary> Returns the GUID for a relative asset path, or null if unknown. </summary>
    string? GetGuidByPath(string relativePath);

    /// <summary> Returns the relative asset path for a GUID, or null if unknown. </summary>
    string? GetAssetPathByGuid(string guid);

    /// <summary> Returns the <see cref="MetaFile"/> for the given GUID, or null. </summary>
    MetaFile? GetMeta(string guid);

    /// <summary> Returns the <see cref="AssetMetaManager"/> that backs the GUID system. </summary>
    AssetMetaManager MetaManager { get; }
}
