// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prowl.Editor.Services;

/// <summary>
/// Represents the contents of an asset .meta file. Contains a unique GUID
/// and optional import settings so that assets can be renamed/moved without
/// breaking references.
/// </summary>
public sealed class MetaFile
{
    /// <summary> Unique identifier for this asset. </summary>
    [JsonPropertyName("guid")]
    public string Guid { get; set; } = string.Empty;

    /// <summary>
    /// Extensible import settings dictionary. Keys are importer-specific
    /// setting names; values are JSON-compatible objects (string, number, bool).
    /// </summary>
    [JsonPropertyName("importSettings")]
    public Dictionary<string, object?> ImportSettings { get; set; } = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary> Generates a new MetaFile with a freshly created GUID. </summary>
    public static MetaFile CreateNew()
    {
        return new MetaFile
        {
            Guid = System.Guid.NewGuid().ToString("N"), // 32-char hex string
        };
    }

    /// <summary> Writes this meta file to disk. </summary>
    public void Save(string metaFilePath)
    {
        string json = JsonSerializer.Serialize(this, _jsonOptions);
        File.WriteAllText(metaFilePath, json);
    }

    /// <summary> Reads a meta file from disk. Returns null if the file is missing or corrupt. </summary>
    public static MetaFile? Load(string metaFilePath)
    {
        if (!File.Exists(metaFilePath))
            return null;

        try
        {
            string json = File.ReadAllText(metaFilePath);
            return JsonSerializer.Deserialize<MetaFile>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary> Returns the .meta path for a given asset path. </summary>
    public static string GetMetaPath(string assetPath) => assetPath + ".meta";
}
