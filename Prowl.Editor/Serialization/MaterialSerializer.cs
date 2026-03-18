// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using System.Text.Json.Nodes;

using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Editor.Services;

/// <summary>
/// Serializes and deserializes <see cref="Material"/> assets to/from JSON (.mat) files.
/// The format stores a shader reference (asset path) and all property overrides so that
/// materials can be saved as project assets and re-loaded reliably.
/// </summary>
public static class MaterialSerializer
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Saves a <see cref="Material"/> to a .mat JSON file.
    /// </summary>
    public static void Save(Material material, string filePath)
    {
        var root = new JsonObject();

        // Shader reference
        string shaderPath = material.Shader?.AssetPath ?? string.Empty;
        root["shader"] = shaderPath;

        // Material name
        root["name"] = material.Name ?? "New Material";

        // Properties
        root["properties"] = SerializeProperties(material._properties);

        // Keywords
        if (material.LocalKeywords.Count > 0)
        {
            var kw = new JsonObject();
            foreach (var kvp in material.LocalKeywords)
                kw[kvp.Key] = kvp.Value;
            root["keywords"] = kw;
        }

        string json = root.ToJsonString(s_jsonOptions);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Loads a <see cref="Material"/> from a .mat JSON file.
    /// </summary>
    public static Material? Load(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            string json = File.ReadAllText(filePath);
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonObject obj)
                return null;

            // Resolve shader
            string shaderPath = obj["shader"]?.GetValue<string>() ?? string.Empty;
            Shader? shader = ResolveShader(shaderPath, filePath);

            if (shader == null)
            {
                Debug.LogWarning($"[MaterialSerializer] Could not resolve shader '{shaderPath}' for material '{filePath}'. Using default.");
                shader = Shader.LoadDefault(DefaultShader.Standard);
            }

            Material mat = new Material(shader);
            mat.Name = obj["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(filePath);
            mat.AssetPath = filePath;

            // Deserialize properties
            if (obj["properties"] is JsonObject propsObj)
                DeserializeProperties(mat, propsObj);

            // Deserialize keywords
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
        catch (Exception ex)
        {
            Debug.LogError($"[MaterialSerializer] Failed to load '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resolves a shader from a path string. Supports:
    /// - "$Default:ShaderName" for built-in shaders
    /// - Absolute or relative .shader file paths
    /// </summary>
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

        // Try as absolute path first
        if (Path.IsPathRooted(shaderPath) && File.Exists(shaderPath))
            return Shader.LoadFromFile(shaderPath);

        // Try relative to the material file's directory
        string? matDir = Path.GetDirectoryName(materialFilePath);
        if (matDir != null)
        {
            string resolved = Path.GetFullPath(Path.Combine(matDir, shaderPath));
            if (File.Exists(resolved))
                return Shader.LoadFromFile(resolved);
        }

        // Try as-is (may be relative to working directory or asset root)
        if (File.Exists(shaderPath))
            return Shader.LoadFromFile(shaderPath);

        return null;
    }

    private static JsonObject SerializeProperties(PropertyState props)
    {
        var obj = new JsonObject();

        // Colors
        var colors = new JsonObject();
        foreach (string name in props.GetColorNames())
        {
            Color c = props.GetColor(name);
            colors[name] = new JsonArray(c.R, c.G, c.B, c.A);
        }
        if (colors.Count > 0) obj["colors"] = colors;

        // Floats
        var floats = new JsonObject();
        foreach (string name in props.GetFloatNames())
            floats[name] = props.GetFloat(name);
        if (floats.Count > 0) obj["floats"] = floats;

        // Ints
        var ints = new JsonObject();
        foreach (string name in props.GetIntNames())
            ints[name] = props.GetInt(name);
        if (ints.Count > 0) obj["ints"] = ints;

        // Vector2s
        var vec2s = new JsonObject();
        foreach (string name in props.GetVector2Names())
        {
            Float2 v = props.GetVector2(name);
            vec2s[name] = new JsonArray(v.X, v.Y);
        }
        if (vec2s.Count > 0) obj["vectors2"] = vec2s;

        // Vector3s
        var vec3s = new JsonObject();
        foreach (string name in props.GetVector3Names())
        {
            Float3 v = props.GetVector3(name);
            vec3s[name] = new JsonArray(v.X, v.Y, v.Z);
        }
        if (vec3s.Count > 0) obj["vectors3"] = vec3s;

        // Vector4s
        var vec4s = new JsonObject();
        foreach (string name in props.GetVector4Names())
        {
            Float4 v = props.GetVector4(name);
            vec4s[name] = new JsonArray(v.X, v.Y, v.Z, v.W);
        }
        if (vec4s.Count > 0) obj["vectors4"] = vec4s;

        // Texture paths (store the AssetPath so they can be re-resolved)
        var textures = new JsonObject();
        foreach (string name in props.GetTextureNames())
        {
            Texture2D? tex = props.GetTexture(name);
            if (tex != null && !string.IsNullOrEmpty(tex.AssetPath))
                textures[name] = tex.AssetPath;
        }
        if (textures.Count > 0) obj["textures"] = textures;

        return obj;
    }

    private static void DeserializeProperties(Material mat, JsonObject propsObj)
    {
        // Colors
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

        // Floats
        if (propsObj["floats"] is JsonObject floats)
        {
            foreach (var kvp in floats)
            {
                if (kvp.Value is JsonValue jv && jv.TryGetValue<float>(out float val))
                    mat.SetFloat(kvp.Key, val);
            }
        }

        // Ints
        if (propsObj["ints"] is JsonObject ints)
        {
            foreach (var kvp in ints)
            {
                if (kvp.Value is JsonValue jv && jv.TryGetValue<int>(out int val))
                    mat.SetInt(kvp.Key, val);
            }
        }

        // Vector2s
        if (propsObj["vectors2"] is JsonObject vec2s)
        {
            foreach (var kvp in vec2s)
            {
                if (kvp.Value is JsonArray arr && arr.Count >= 2)
                    mat.SetVector(kvp.Key, new Float2(
                        arr[0]!.GetValue<float>(), arr[1]!.GetValue<float>()));
            }
        }

        // Vector3s
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

        // Vector4s
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
    }
}
