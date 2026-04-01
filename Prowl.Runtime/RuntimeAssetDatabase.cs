// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

using Prowl.Echo;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Serialization;
using Prowl.Vector;

namespace Prowl.Runtime;

/// <summary>
/// Runtime implementation of <see cref="IAssetDatabase"/> used by built
/// players.  Scans an <c>Assets/</c> directory for <c>.meta</c> files to
/// build GUID → path mappings, then loads assets on demand from disk.
/// <para>
/// This class mirrors the capabilities of the editor's
/// <c>EditorAssetDatabase</c> without taking a dependency on editor-only
/// types such as <c>IAssetService</c> or <c>AssetMetaManager</c>.
/// </para>
/// </summary>
public sealed class RuntimeAssetDatabase : IAssetDatabase
{
    private readonly string _assetsRoot;

    // GUID (32-char hex) → relative path (from assets root)
    private readonly Dictionary<string, string> _guidToPath = new(StringComparer.OrdinalIgnoreCase);
    // relative path → GUID
    private readonly Dictionary<string, string> _pathToGuid = new(StringComparer.OrdinalIgnoreCase);

    // Loaded asset cache
    private readonly Dictionary<Guid, EngineObject?> _cache = [];

    public RuntimeAssetDatabase(string assetsRoot)
    {
        _assetsRoot = Path.GetFullPath(assetsRoot);
        ScanMetaFiles();
    }

    // ── IAssetDatabase ──────────────────────────────────────────

    public EngineObject? Get(Guid assetId)
    {
        if (assetId == Guid.Empty)
            return null;

        if (_cache.TryGetValue(assetId, out var cached))
        {
            if (cached != null && !cached.IsDisposed)
                return cached;
            _cache.Remove(assetId);
        }

        string guidStr = assetId.ToString("N");
        if (!_guidToPath.TryGetValue(guidStr, out string? relativePath))
            return null;

        string absolutePath = Path.Combine(_assetsRoot, relativePath);
        if (!File.Exists(absolutePath))
            return null;

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

    public Guid ResolveAssetId(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
            return Guid.Empty;

        string relativePath = assetPath;
        if (Path.IsPathRooted(relativePath))
            relativePath = Path.GetRelativePath(_assetsRoot, relativePath).Replace('\\', '/');

        if (_pathToGuid.TryGetValue(relativePath, out string? guid) && Guid.TryParse(guid, out Guid id))
            return id;

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

    // ── Meta file scanning ──────────────────────────────────────

    private void ScanMetaFiles()
    {
        if (!Directory.Exists(_assetsRoot))
        {
            Debug.LogWarning($"[RuntimeAssetDB] Assets root not found: {_assetsRoot}");
            return;
        }

        foreach (string file in Directory.EnumerateFiles(_assetsRoot, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                continue;

            string metaPath = file + ".meta";
            if (!File.Exists(metaPath))
                continue;

            string? guid = ReadGuidFromMeta(metaPath);
            if (string.IsNullOrEmpty(guid))
                continue;

            string relativePath = Path.GetRelativePath(_assetsRoot, file).Replace('\\', '/');
            _guidToPath[guid] = relativePath;
            _pathToGuid[relativePath] = guid;
        }

        Debug.Log($"[RuntimeAssetDB] Scanned {_guidToPath.Count} assets in: {_assetsRoot}");
    }

    private static string? ReadGuidFromMeta(string metaPath)
    {
        try
        {
            string json = File.ReadAllText(metaPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("guid", out var guidProp))
                return guidProp.GetString();
        }
        catch { /* best effort */ }
        return null;
    }

    // ── Asset loading by extension ──────────────────────────────

    private EngineObject? LoadAsset(string absolutePath, string relativePath)
    {
        string ext = Path.GetExtension(absolutePath).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".shader" => Shader.LoadFromFile(absolutePath),
                ".mat" => LoadMaterial(absolutePath),
                ".asset" => LoadScriptableObject(absolutePath),
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" => LoadTexture(absolutePath, relativePath),
                ".obj" or ".fbx" or ".gltf" or ".glb" or ".dae" or ".3ds" or ".blend" or ".ply" or ".stl" =>
                    Model.LoadFromFile(absolutePath),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RuntimeAssetDB] Failed to load '{relativePath}': {ex.Message}");
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

    // ── Material loading (mirrors MaterialSerializer.Load) ──────

    private Material? LoadMaterial(string filePath)
    {
        string json = File.ReadAllText(filePath);
        JsonNode? root = JsonNode.Parse(json);
        if (root is not JsonObject obj)
            return null;

        // Resolve shader — try GUID first, fall back to path
        Shader? shader = null;
        string shaderPathStr = obj["shader"]?.GetValue<string>() ?? string.Empty;
        string? shaderGuid = obj["shaderGuid"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(shaderGuid) && Guid.TryParse(shaderGuid, out Guid shaderAssetId))
        {
            shader = AssetDatabase.Get(shaderAssetId) as Shader;
        }
        if (shader == null)
        {
            shader = ResolveShader(shaderPathStr, filePath);
        }
        if (shader == null)
        {
            Debug.LogWarning($"[RuntimeAssetDB] Could not resolve shader '{shaderPathStr}' for material '{filePath}'. Using default.");
            shader = Shader.LoadDefault(DefaultShader.Standard);
        }

        Material mat = new Material(shader);
        mat.Name = obj["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(filePath);
        mat.AssetPath = filePath;

        // Properties
        if (obj["properties"] is JsonObject propsObj)
            DeserializeMaterialProperties(mat, propsObj);

        // Keywords
        if (obj["keywords"] is JsonObject kwObj)
        {
            foreach (var kvp in kwObj)
            {
                if (kvp.Value is JsonValue jv && jv.TryGetValue<bool>(out bool val))
                    mat.SetKeyword(kvp.Key, val);
            }
        }

        return mat;
    }

    private static Shader? ResolveShader(string shaderPath, string materialFilePath)
    {
        if (string.IsNullOrEmpty(shaderPath))
            return null;

        // Built-in shader reference
        if (shaderPath.StartsWith("$Default:"))
        {
            string name = shaderPath["$Default:".Length..];
            if (Enum.TryParse<DefaultShader>(name, out var defaultShader))
                return Shader.LoadDefault(defaultShader);
            return null;
        }

        if (Path.IsPathRooted(shaderPath) && File.Exists(shaderPath))
            return Shader.LoadFromFile(shaderPath);

        string? matDir = Path.GetDirectoryName(materialFilePath);
        if (matDir != null)
        {
            string resolved = Path.GetFullPath(Path.Combine(matDir, shaderPath));
            if (File.Exists(resolved))
                return Shader.LoadFromFile(resolved);
        }

        if (File.Exists(shaderPath))
            return Shader.LoadFromFile(shaderPath);

        return null;
    }

    private void DeserializeMaterialProperties(Material mat, JsonObject propsObj)
    {
        if (propsObj["colors"] is JsonObject colors)
        {
            foreach (var kvp in colors)
            {
                if (kvp.Value is JsonArray arr && arr.Count >= 4)
                    mat.SetColor(kvp.Key, new Color(
                        arr[0]!.GetValue<float>(), arr[1]!.GetValue<float>(),
                        arr[2]!.GetValue<float>(), arr[3]!.GetValue<float>()));
            }
        }

        if (propsObj["floats"] is JsonObject floats)
        {
            foreach (var kvp in floats)
            {
                if (kvp.Value is JsonValue jv && jv.TryGetValue<float>(out float val))
                    mat.SetFloat(kvp.Key, val);
            }
        }

        if (propsObj["ints"] is JsonObject ints)
        {
            foreach (var kvp in ints)
            {
                if (kvp.Value is JsonValue jv && jv.TryGetValue<int>(out int val))
                    mat.SetInt(kvp.Key, val);
            }
        }

        if (propsObj["vectors2"] is JsonObject vec2s)
        {
            foreach (var kvp in vec2s)
            {
                if (kvp.Value is JsonArray arr && arr.Count >= 2)
                    mat.SetVector(kvp.Key, new Float2(
                        arr[0]!.GetValue<float>(), arr[1]!.GetValue<float>()));
            }
        }

        if (propsObj["vectors3"] is JsonObject vec3s)
        {
            foreach (var kvp in vec3s)
            {
                if (kvp.Value is JsonArray arr && arr.Count >= 3)
                    mat.SetVector(kvp.Key, new Float3(
                        arr[0]!.GetValue<float>(), arr[1]!.GetValue<float>(),
                        arr[2]!.GetValue<float>()));
            }
        }

        if (propsObj["vectors4"] is JsonObject vec4s)
        {
            foreach (var kvp in vec4s)
            {
                if (kvp.Value is JsonArray arr && arr.Count >= 4)
                    mat.SetVector(kvp.Key, new Float4(
                        arr[0]!.GetValue<float>(), arr[1]!.GetValue<float>(),
                        arr[2]!.GetValue<float>(), arr[3]!.GetValue<float>()));
            }
        }

        if (propsObj["textures"] is JsonObject textures)
        {
            foreach (var kvp in textures)
            {
                Texture2D? tex = null;

                if (kvp.Value is JsonObject texRef)
                {
                    string? guid = texRef["guid"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(guid) && Guid.TryParse(guid, out Guid texAssetId))
                        tex = AssetDatabase.Get(texAssetId) as Texture2D;

                    if (tex == null)
                    {
                        string? path = texRef["path"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                            tex = Texture2D.FromFile(path);
                    }
                }
                else if (kvp.Value is JsonValue jv && jv.TryGetValue<string>(out string? legacyPath))
                {
                    if (!string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath))
                        tex = Texture2D.FromFile(legacyPath);
                }

                if (tex != null)
                    mat.SetTexture(kvp.Key, tex);
            }
        }
    }

    // ── ScriptableObject loading ────────────────────────────────

    private static ScriptableObject? LoadScriptableObject(string filePath)
    {
        string json = File.ReadAllText(filePath);
        JsonNode? root = JsonNode.Parse(json);
        if (root == null) return null;

        EchoObject envelope = EchoJsonBridge.JsonToEcho(root);

        string? typeName = envelope.TryGet("type", out EchoObject? typeTag)
            ? typeTag?.StringValue
            : null;

        if (string.IsNullOrEmpty(typeName))
            return null;

        Type? soType = Type.GetType(typeName);

        if (soType == null)
        {
            string fullName = typeName.Contains(',')
                ? typeName[..typeName.IndexOf(',')].Trim()
                : typeName;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                soType = asm.GetType(fullName);
                if (soType != null) break;
            }
        }

        if (soType == null || !typeof(ScriptableObject).IsAssignableFrom(soType))
            return null;

        EchoObject? data = envelope.TryGet("data", out EchoObject? d) ? d : envelope;
        if (data == null) return null;

        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        return Serializer.Deserialize(data, soType, ctx) as ScriptableObject;
    }

    // ── Scene loading ───────────────────────────────────────────

    /// <summary>
    /// Loads a scene file from disk and returns the deserialized
    /// <see cref="Scene"/>. Supports both the compact binary format
    /// (<c>.bscene</c>) used by standalone builds and the JSON format
    /// (<c>.scene</c>) used by the editor. Returns null if the file is
    /// missing or corrupt.
    /// </summary>
    public static Scene? LoadScene(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogError($"[RuntimeAssetDB] Scene file not found: {filePath}");
            return null;
        }

        try
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            EchoObject envelope = ext switch
            {
                ".bscene" => LoadBinaryEnvelope(filePath),
                _ => LoadJsonEnvelope(filePath),
            };

            // Read scene data from envelope (supports both versioned and legacy formats)
            EchoObject? sceneData;
            if (envelope.TagType == EchoType.Compound && envelope.TryGet("scene", out EchoObject? sd))
                sceneData = sd;
            else
                sceneData = envelope;

            if (sceneData == null) return null;

            var ctx = new SerializationContext();
            AssetDatabase.ConfigureContext(ctx);

            Scene? scene = Serializer.Deserialize<Scene>(sceneData, ctx);

            if (scene != null)
                Debug.Log($"[RuntimeAssetDB] Loaded scene: {filePath}  ({scene.Count} objects)");

            return scene;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RuntimeAssetDB] Failed to load scene: {ex.Message}");
            return null;
        }
    }

    private static EchoObject LoadBinaryEnvelope(string filePath)
    {
        var format = new EchoBinaryFormat
        {
            Options = new BinarySerializationOptions { EncodingMode = BinaryEncodingMode.Size }
        };

        using var stream = File.OpenRead(filePath);
        return format.ReadFrom(stream);
    }

    private static EchoObject LoadJsonEnvelope(string filePath)
    {
        string json = File.ReadAllText(filePath);
        JsonNode? root = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("Scene JSON parsed to null.");
        return EchoJsonBridge.JsonToEcho(root);
    }
}
