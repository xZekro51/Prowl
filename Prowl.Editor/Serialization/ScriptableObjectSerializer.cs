// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using System.Text.Json.Nodes;

using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Serialization;

namespace Prowl.Editor.Services;

/// <summary>
/// Serializes and deserializes <see cref="ScriptableObject"/> assets to/from
/// JSON <c>.asset</c> files. The format stores the concrete CLR type name and
/// all serializable fields via the Echo serialization layer.
/// </summary>
public static class ScriptableObjectSerializer
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Saves a <see cref="ScriptableObject"/> to a <c>.asset</c> JSON file.
    /// </summary>
    public static void Save(ScriptableObject so, string filePath)
    {
        var ctx = new SerializationContext();
        AssetDatabase.ConfigureContext(ctx);

        EchoObject echoData = Serializer.Serialize(so.GetType(), so, ctx);

        var envelope = EchoObject.NewCompound();
        envelope.Add("version", new EchoObject(1));
        envelope.Add("type", new EchoObject(so.GetType().AssemblyQualifiedName ?? so.GetType().FullName ?? so.GetType().Name));
        envelope.Add("data", echoData);

        JsonNode? jsonNode = EchoToJson(envelope);
        string json = jsonNode?.ToJsonString(s_jsonOptions) ?? "{}";
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Loads a <see cref="ScriptableObject"/> from a <c>.asset</c> JSON file.
    /// Returns null if the file is missing, the type cannot be resolved, or
    /// deserialization fails.
    /// </summary>
    public static ScriptableObject? Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogError($"[ScriptableObject] File not found: {filePath}");
            return null;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            JsonNode? root = JsonNode.Parse(json);
            if (root == null) return null;

            EchoObject envelope = JsonToEcho(root);

            // Resolve the concrete type
            string? typeName = envelope.TryGet("type", out EchoObject? typeTag)
                ? typeTag?.StringValue
                : null;

            if (string.IsNullOrEmpty(typeName))
            {
                Debug.LogError($"[ScriptableObject] Missing type in: {filePath}");
                return null;
            }

            Type? soType = Type.GetType(typeName);

            // Fallback: search all loaded assemblies by full name
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
            {
                Debug.LogError($"[ScriptableObject] Cannot resolve type '{typeName}' in: {filePath}");
                return null;
            }

            // Deserialize the data payload
            EchoObject? data = envelope.TryGet("data", out EchoObject? d) ? d : envelope;
            if (data == null) return null;

            var ctx = new SerializationContext();
            AssetDatabase.ConfigureContext(ctx);

            var result = Serializer.Deserialize(data, soType, ctx) as ScriptableObject;

            if (result != null)
                Debug.Log($"[ScriptableObject] Loaded {soType.Name} from: {filePath}");

            return result;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ScriptableObject] Failed to load: {ex.Message}");
            return null;
        }
    }

    // ── EchoObject ↔ JsonNode bridge (delegates to shared utility) ──

    internal static JsonNode? EchoToJson(EchoObject echo) => EchoJsonBridge.EchoToJson(echo);

    internal static EchoObject JsonToEcho(JsonNode node) => EchoJsonBridge.JsonToEcho(node);
}
