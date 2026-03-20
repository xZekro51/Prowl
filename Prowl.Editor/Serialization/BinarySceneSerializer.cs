// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Prowl.Editor.Services;

/// <summary>
/// Scene serializer that uses Prowl's Echo serializer to capture the full
/// scene graph and persists it in Echo's compact binary format.
/// <para>
/// This is intended for standalone builds where parse speed and file size
/// matter more than human readability.  The editor should continue to use
/// <see cref="JsonSceneSerializer"/> for its diff-friendly JSON output.
/// </para>
/// </summary>
public sealed class BinarySceneSerializer : ISceneSerializer
{
    /// <summary>
    /// The binary encoding mode used when writing scene files.
    /// <see cref="BinaryEncodingMode.Size"/> produces smaller files;
    /// <see cref="BinaryEncodingMode.Performance"/> is faster to write/read.
    /// Defaults to <see cref="BinaryEncodingMode.Size"/> for build output.
    /// </summary>
    public BinaryEncodingMode EncodingMode { get; set; } = BinaryEncodingMode.Size;

    public string FileExtension => ".bscene";

    public void Save(Scene scene, string filePath)
    {
        try
        {
            // Serialize the scene through Echo (handles ISerializationCallbackReceiver)
            var ctx = new SerializationContext();
            AssetDatabase.ConfigureContext(ctx);
            EchoObject echoData = Serializer.Serialize(typeof(Scene), scene, ctx);

            // Wrap in a versioned envelope (same structure as JsonSceneSerializer)
            var envelope = EchoObject.NewCompound();
            envelope.Add("version", new EchoObject(1));
            envelope.Add("scene", echoData);

            // Write binary
            var format = new EchoBinaryFormat
            {
                Options = new BinarySerializationOptions { EncodingMode = EncodingMode }
            };

            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var stream = File.Create(filePath);
            format.WriteTo(envelope, stream);

            Debug.Log($"[Scene] Binary-saved to: {filePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Scene] Failed to binary-save: {ex.Message}");
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
            var format = new EchoBinaryFormat
            {
                Options = new BinarySerializationOptions { EncodingMode = EncodingMode }
            };

            EchoObject envelope;
            using (var stream = File.OpenRead(filePath))
            {
                envelope = format.ReadFrom(stream);
            }

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
                Debug.Log($"[Scene] Binary-loaded from: {filePath}  ({scene.Count} objects)");

            return scene;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Scene] Failed to binary-load: {ex.Message}");
            return null;
        }
    }
}
