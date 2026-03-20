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
    /// </summary>
    public static IAssetDatabase? Current { get; set; }

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
                    return compound; // serialize as just a reference
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
                return (true, Get(assetId)); // resolve from DB, may return null
            }
            return (false, null); // normal deserialization
        };
    }
}
