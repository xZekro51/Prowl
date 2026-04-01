// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Prowl.Echo;

namespace Prowl.Runtime;

/// <summary>
/// A lightweight, strongly-typed wrapper that holds a reference to an
/// asset-backed <see cref="EngineObject"/>.  It stores the
/// <see cref="AssetID"/> and <see cref="AssetPath"/> so that
/// serialization always emits a compact <c>{$assetId, $assetPath}</c>
/// reference rather than an inline copy of the object.
///
/// <para>
/// Usage is intentionally transparent:
/// <code>
/// public AssetRef&lt;Mesh&gt; Mesh;
///
/// // Assign like a normal field – the AssetID is captured automatically.
/// component.Mesh = someMesh;
///
/// // Read the resolved instance.
/// Mesh m = component.Mesh;
/// </code>
/// </para>
///
/// <para>
/// If the cached instance has been disposed or is null, accessing
/// <see cref="Instance"/> will try to re-resolve the asset through
/// <see cref="AssetDatabase.Current"/>.
/// </para>
/// </summary>
public struct AssetRef<T> : ISerializable, IEquatable<AssetRef<T>>
    where T : EngineObject
{
    /// <summary>
    /// The GUID that identifies this asset in the asset database / .meta system.
    /// </summary>
    public Guid AssetID;

    /// <summary>
    /// A human-readable path (relative to the asset root) stored alongside the
    /// GUID for debugging, logging, and fallback resolution.
    /// May contain a sub-resource fragment, e.g. <c>"Models/cube.obj#Mesh:0"</c>.
    /// </summary>
    public string AssetPath;

    /// <summary>
    /// The cached, resolved instance.  May be null if the reference has never
    /// been resolved, or if the asset was disposed.  Use <see cref="Instance"/>
    /// for auto-resolving access.
    /// </summary>
    private T? _instance;

    // ── Construction ────────────────────────────────────────────────

    /// <summary>
    /// Creates an <see cref="AssetRef{T}"/> that wraps an already-loaded
    /// <see cref="EngineObject"/>.  The object's <see cref="EngineObject.AssetID"/>
    /// and <see cref="EngineObject.AssetPath"/> are captured automatically.
    /// </summary>
    public AssetRef(T? instance)
    {
        _instance = instance;
        AssetID = instance?.AssetID ?? Guid.Empty;
        AssetPath = instance?.AssetPath ?? string.Empty;
    }

    /// <summary>
    /// Creates an <see cref="AssetRef{T}"/> from a known GUID and path
    /// without an instance.  The instance will be lazily resolved on first access.
    /// </summary>
    public AssetRef(Guid assetId, string assetPath = "")
    {
        _instance = null;
        AssetID = assetId;
        AssetPath = assetPath ?? string.Empty;
    }

    // ── Resolution ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the resolved asset instance, lazily loading from the
    /// <see cref="AssetDatabase"/> if needed.  Returns <c>null</c> if
    /// the reference is empty or the asset cannot be resolved.
    /// </summary>
    public T? Instance
    {
        get
        {
            if (_instance.IsValid())
                return _instance;

            // Try to resolve from the asset database
            _instance = null;
            if (AssetID != Guid.Empty)
            {
                _instance = AssetDatabase.Get(AssetID) as T;
            }

            // Fallback: path-based resolution (e.g., for sub-resources)
            if (_instance == null && !string.IsNullOrEmpty(AssetPath) && AssetDatabase.Current != null)
            {
                _instance = AssetDatabase.Current.ResolveByPath(AssetPath) as T;
            }

            return _instance;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when this reference has a valid, non-disposed instance
    /// or at least contains a non-empty <see cref="AssetID"/> that can be resolved.
    /// </summary>
    public readonly bool IsValid => _instance.IsValid() || AssetID != Guid.Empty;

    /// <summary>
    /// Returns <c>true</c> when this reference has no GUID and no cached instance.
    /// </summary>
    public readonly bool IsEmpty => AssetID == Guid.Empty && _instance.IsNotValid();

    // ── Implicit conversions ────────────────────────────────────────

    /// <summary>
    /// Implicitly converts an <see cref="EngineObject"/> instance to an
    /// <see cref="AssetRef{T}"/>, capturing its AssetID and AssetPath.
    /// </summary>
    public static implicit operator AssetRef<T>(T? instance) => new(instance);

    /// <summary>
    /// Implicitly converts an <see cref="AssetRef{T}"/> to its resolved instance
    /// (may be <c>null</c> if unresolved or disposed).
    /// </summary>
    [return: MaybeNull]
    public static implicit operator T?(AssetRef<T> assetRef) => assetRef.Instance;

    // ── ISerializable ───────────────────────────────────────────────

    /// <summary>
    /// Serializes this reference as a <c>{$assetId, $assetPath}</c> compound
    /// so that the serialized data never contains an inline copy of the asset.
    /// </summary>
    public void Serialize(ref EchoObject value, SerializationContext ctx)
    {
        // Sync ID/path from the instance in case they were updated after construction
        if (_instance.IsValid())
        {
            if (_instance!.AssetID != Guid.Empty)
                AssetID = _instance.AssetID;
            if (!string.IsNullOrEmpty(_instance.AssetPath))
                AssetPath = _instance.AssetPath;
        }

        // Last-resort: try to resolve the GUID via the database if we have a path but no ID
        if (AssetID == Guid.Empty && !string.IsNullOrEmpty(AssetPath) && AssetDatabase.Current != null)
        {
            Guid resolved = AssetDatabase.Current.ResolveAssetId(AssetPath);
            if (resolved != Guid.Empty)
                AssetID = resolved;
        }

        if (AssetID != Guid.Empty)
        {
            value["$assetId"] = new EchoObject(AssetID.ToString());
            if (!string.IsNullOrEmpty(AssetPath))
                value["$assetPath"] = new EchoObject(AssetPath);
        }
        else
        {
            // Emit an empty compound so deserialization knows this was an AssetRef
            // rather than accidentally serializing the full object tree.
            Debug.LogWarning(
                $"[AssetRef] Serializing AssetRef<{typeof(T).Name}> with no AssetID " +
                $"(path='{AssetPath}'). The reference will be null on deserialization.");
        }
    }

    /// <summary>
    /// Deserializes from a <c>{$assetId, $assetPath}</c> compound and lazily
    /// resolves the asset instance.
    /// </summary>
    public void Deserialize(EchoObject value, SerializationContext ctx)
    {
        _instance = null;
        AssetID = Guid.Empty;
        AssetPath = string.Empty;

        if (value.TryGet("$assetId", out EchoObject? idTag) && idTag != null)
        {
            if (Guid.TryParse(idTag.StringValue, out Guid id))
                AssetID = id;
        }

        if (value.TryGet("$assetPath", out EchoObject? pathTag) && pathTag != null)
        {
            AssetPath = pathTag.StringValue ?? string.Empty;
        }

        // Eagerly resolve now if the database is available — this avoids
        // deferred resolution surprises during gameplay.
        if (AssetID != Guid.Empty)
        {
            _instance = AssetDatabase.Get(AssetID) as T;
        }

        if (_instance == null && !string.IsNullOrEmpty(AssetPath) && AssetDatabase.Current != null)
        {
            _instance = AssetDatabase.Current.ResolveByPath(AssetPath) as T;
        }
    }

    // ── Equality ────────────────────────────────────────────────────

    public readonly bool Equals(AssetRef<T> other)
    {
        if (AssetID != Guid.Empty || other.AssetID != Guid.Empty)
            return AssetID == other.AssetID;
        return ReferenceEquals(_instance, other._instance);
    }

    public override readonly bool Equals(object? obj) => obj is AssetRef<T> other && Equals(other);
    public override readonly int GetHashCode() => AssetID != Guid.Empty ? AssetID.GetHashCode() : (_instance?.GetHashCode() ?? 0);

    public static bool operator ==(AssetRef<T> left, AssetRef<T> right) => left.Equals(right);
    public static bool operator !=(AssetRef<T> left, AssetRef<T> right) => !left.Equals(right);

    public override readonly string ToString()
    {
        if (_instance.IsValid())
            return $"AssetRef<{typeof(T).Name}>({_instance!.Name}, {AssetID:N})";
        if (AssetID != Guid.Empty)
            return $"AssetRef<{typeof(T).Name}>(unresolved, {AssetID:N})";
        return $"AssetRef<{typeof(T).Name}>(none)";
    }
}
