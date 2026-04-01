// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Echo;

namespace Prowl.Runtime;

/// <summary>
/// Interface for resolving engine objects by their asset ID.
/// Implement this to provide asset storage and retrieval (e.g., from disk, from a content pipeline, etc.).
/// </summary>
public interface IAssetDatabase
{
    /// <summary>
    /// Retrieves an <see cref="EngineObject"/> by its asset ID.
    /// Returns null if the asset is not found.
    /// </summary>
    EngineObject? Get(Guid assetId);

    /// <summary>
    /// Attempts to resolve an asset ID for an object given its asset path.
    /// Returns <see cref="Guid.Empty"/> if the path is not known to the database.
    /// </summary>
    Guid ResolveAssetId(string assetPath) => Guid.Empty;

    /// <summary>
    /// Attempts to resolve an <see cref="EngineObject"/> by its asset path.
    /// Supports sub-resource paths such as <c>"Models/cube.obj#Mesh:0"</c>.
    /// Returns null if the path cannot be resolved.
    /// </summary>
    EngineObject? ResolveByPath(string assetPath) => null;
}

/// <summary>
/// Provides a global asset database accessor and helpers for configuring
/// Echo serialization contexts to automatically serialize/deserialize asset references.
/// </summary>
public static class AssetDatabase
{
    /// <summary>
    /// The current asset database implementation. Set this before serializing/deserializing
    /// objects that contain asset references.
    /// Delegates to <see cref="EngineContext.Current"/>.<see cref="EngineContext.AssetDatabase"/>.
    /// </summary>
    public static IAssetDatabase? Current
    {
        get => EngineContext.Current.AssetDatabase;
        set => EngineContext.Current.AssetDatabase = value;
    }

    /// <summary>
    /// Resolves an <see cref="EngineObject"/> by asset ID from the current database.
    /// Returns null if no database is set or the asset is not found.
    /// </summary>
    public static EngineObject? Get(Guid assetId)
    {
        return Current?.Get(assetId);
    }

    /// <summary>
    /// Configures the given <see cref="SerializationContext"/> with OnSerialize/OnDeserialize
    /// callbacks that handle asset references via <c>$assetId</c> tags.
    /// <para>
    /// Any <see cref="EngineObject"/> that carries a non-empty
    /// <see cref="EngineObject.AssetID"/> (or whose GUID can be resolved
    /// from <see cref="EngineObject.AssetPath"/>) will be serialized as a
    /// compact <c>{$assetId, $assetPath}</c> reference.  Asset-type objects
    /// (Mesh, Material, Shader, Texture, Model, ScriptableObject) that
    /// cannot be resolved emit a diagnostic warning so that inline copies
    /// are caught during development.
    /// </para>
    /// </summary>
    public static void ConfigureContext(SerializationContext ctx)
    {
        ctx.OnSerialize = (obj, c) =>
        {
            if (obj is EngineObject eo)
            {
                Guid id = eo.AssetID;

                // If the object has no AssetID yet but does have a path,
                // ask the database to resolve the GUID from the meta system.
                if (id == Guid.Empty && !string.IsNullOrEmpty(eo.AssetPath) && Current != null)
                {
                    id = Current.ResolveAssetId(eo.AssetPath);
                    if (id != Guid.Empty)
                        eo.AssetID = id; // stamp for future use
                }

                if (id != Guid.Empty)
                {
                    var compound = EchoObject.NewCompound();
                    compound["$assetId"] = new EchoObject(id.ToString());
                    if (!string.IsNullOrEmpty(eo.AssetPath))
                        compound["$assetPath"] = new EchoObject(eo.AssetPath);
                    return compound; // serialize as just a reference
                }

                // Asset-type objects (Mesh, Material, Shader, Texture, etc.)
                // must NEVER be serialized inline.  If we reach this point
                // the object has no resolvable AssetID — emit a null marker
                // so the serialized data is a reference (not an inline copy)
                // and log a warning to help the developer fix the root cause.
                if (IsAssetType(eo.GetType()))
                {
                    Debug.LogWarning(
                        $"[AssetDatabase] {eo.GetType().Name} '{eo.Name}' has no AssetID and " +
                        $"cannot be serialized as a reference. The field will be null on " +
                        $"deserialization. Assign an AssetID or use AssetRef<T> to ensure " +
                        $"proper reference serialization. (path='{eo.AssetPath}')");

                    // Return an empty $assetRef marker so the deserializer
                    // recognizes this was an intentional reference (not inline data).
                    var nullRef = EchoObject.NewCompound();
                    nullRef["$assetId"] = new EchoObject(Guid.Empty.ToString());
                    if (!string.IsNullOrEmpty(eo.AssetPath))
                        nullRef["$assetPath"] = new EchoObject(eo.AssetPath);
                    return nullRef;
                }
            }
            return null; // normal serialization
        };

        ctx.OnDeserialize = (data, type, c) =>
        {
            if (typeof(EngineObject).IsAssignableFrom(type)
                && data.TryGet("$assetId", out var assetIdTag))
            {
                var assetId = Guid.Parse(assetIdTag.StringValue);
                var result = Get(assetId);

                // Fallback: path-based resolution (e.g., for sub-resources)
                if (result == null
                    && data.TryGet("$assetPath", out var pathTag)
                    && Current != null)
                {
                    result = Current.ResolveByPath(pathTag.StringValue);
                }

                return (true, result);
            }
            return (false, null); // normal deserialization
        };
    }

    /// <summary>
    /// Returns <c>true</c> if the given type is an asset type that should
    /// normally be serialized as a reference rather than inline.
    /// Scene-graph types (<see cref="GameObject"/>, <see cref="MonoBehaviour"/>)
    /// are NOT considered asset types because they are serialized inline
    /// as part of scene/prefab data.
    /// </summary>
    private static bool IsAssetType(Type type)
    {
        // Quick name-based checks cover the most common asset types without
        // requiring a hard dependency on every concrete class.
        // We avoid typeof() for types that live in Prowl.Runtime.Resources
        // and instead use namespace + name checks to keep the method lightweight.
        if (typeof(ScriptableObject).IsAssignableFrom(type))
            return true;

        string ns = type.Namespace ?? string.Empty;
        if (ns.StartsWith("Prowl.Runtime.Resources", StringComparison.Ordinal))
        {
            string name = type.Name;
            return name is "Mesh" or "Material" or "Shader" or "Texture2D"
                or "Texture3D" or "Model" or "AudioClip" or "AnimationClip"
                or "RenderTexture";
        }

        return false;
    }
}
