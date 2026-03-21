// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using System.Text.Json.Nodes;
using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Serialization;

namespace Prowl.Editor.Services;

/// <summary>
/// Scene serializer that uses Prowl's Echo serializer to capture the full
/// scene graph (GameObjects, components, transforms, etc.) and persists it
/// as a JSON file via a bidirectional EchoObject ↔ JsonNode bridge.
/// </summary>
public sealed class JsonSceneSerializer : ISceneSerializer
{
    public string FileExtension => ".scene";

    public void Save(Scene scene, string filePath)
    {
        try
        {
            // Serialize the scene through Echo (handles ISerializationCallbackReceiver)
            var ctx = new SerializationContext();
            AssetDatabase.ConfigureContext(ctx);
            EchoObject echoData = Serializer.Serialize(typeof(Scene), scene, ctx);

            // Wrap in a versioned envelope
            var envelope = EchoObject.NewCompound();
            envelope.Add("version", new EchoObject(1));
            envelope.Add("scene", echoData);

            // Convert to JSON and write
            JsonNode? jsonNode = EchoToJson(envelope);
            var options = new JsonWriterOptions { Indented = true };
            string json = jsonNode?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";

            File.WriteAllText(filePath, json);
            Debug.Log($"[Scene] Saved to: {filePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Scene] Failed to save: {ex.Message}");
        }
    }

    public Scene? Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogError($"[Scene] File not found: {filePath}");
            return null;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            JsonNode? root = JsonNode.Parse(json);
            if (root == null) return null;

            EchoObject envelope = JsonToEcho(root);

            // Read scene data from envelope (supports both versioned and legacy formats)
            EchoObject? sceneData = null;
            if (envelope.TagType == EchoType.Compound && envelope.TryGet("scene", out EchoObject? sd))
                sceneData = sd;
            else
                sceneData = envelope;

            if (sceneData == null) return null;

            var ctx = new SerializationContext();
            AssetDatabase.ConfigureContext(ctx);

            Scene? scene = Serializer.Deserialize<Scene>(sceneData, ctx);

            if (scene != null)
                Debug.Log($"[Scene] Loaded from: {filePath}  ({scene.Count} objects)");

            return scene;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Scene] Failed to load: {ex.Message}");
            return null;
        }
    }

    // ── EchoObject → JsonNode bridge (delegates to shared utility) ──

    internal static JsonNode? EchoToJson(EchoObject echo) => EchoJsonBridge.EchoToJson(echo);

    internal static EchoObject JsonToEcho(JsonNode node) => EchoJsonBridge.JsonToEcho(node);

    internal static EchoObject ReconstructEcho(EchoType type, JsonNode? valueNode) => EchoJsonBridge.ReconstructEcho(type, valueNode);

    // ── GUID-based asset reference helpers ────────────────────

    /// <summary>
    /// Resolves an asset GUID to its current path using the asset service.
    /// Returns null if the GUID is unknown.
    /// </summary>
    public static string? ResolveGuid(string guid)
    {
        if (EditorServices.TryGet<IAssetService>(out var assets))
            return assets!.GetAssetPathByGuid(guid);
        return null;
    }

    /// <summary>
    /// Converts an asset path to its GUID using the asset service.
    /// Returns null if the path has no registered .meta.
    /// </summary>
    public static string? PathToGuid(string relativePath)
    {
        if (EditorServices.TryGet<IAssetService>(out var assets))
            return assets!.GetGuidByPath(relativePath);
        return null;
    }
}
