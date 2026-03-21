// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Echo;

namespace Prowl.Runtime;

/// <summary>
/// A data container that can be saved as a project asset.
/// Inspired by Unity's ScriptableObject — use this for configuration data,
/// game databases, AI settings, inventory definitions, and any reusable
/// data that should live as a file in your project rather than on a GameObject.
///
/// <para><b>Usage:</b></para>
/// <code>
/// [CreateAssetMenu(MenuName = "Game/Enemy Stats", FileName = "NewEnemyStats")]
/// public class EnemyStats : ScriptableObject
/// {
///     [SerializeField] public float MaxHealth = 100f;
///     [SerializeField] public float MoveSpeed = 5f;
///     [SerializeField] public string DisplayName = "Enemy";
/// }
/// </code>
///
/// In the editor, right-click in the Project panel → Create → Game → Enemy Stats
/// to create a new instance as a <c>.asset</c> file.
/// </summary>
public abstract class ScriptableObject : EngineObject, ISerializationCallbackReceiver
{
    /// <summary>
    /// Creates a new instance of a ScriptableObject-derived type at runtime.
    /// The returned object is not yet saved to disk — use the editor or
    /// serialization layer to persist it.
    /// </summary>
    public static T CreateInstance<T>() where T : ScriptableObject, new()
    {
        var instance = new T();
        instance.OnCreated();
        return instance;
    }

    /// <summary>
    /// Creates a new instance of a ScriptableObject-derived type at runtime.
    /// </summary>
    public static ScriptableObject CreateInstance(Type type)
    {
        if (!typeof(ScriptableObject).IsAssignableFrom(type))
            throw new ArgumentException($"Type '{type.FullName}' does not derive from ScriptableObject.", nameof(type));

        var instance = (ScriptableObject)Activator.CreateInstance(type)!;
        instance.OnCreated();
        return instance;
    }

    /// <summary>
    /// Called once after the object is created via <see cref="CreateInstance{T}"/>.
    /// Override to set initial values.
    /// </summary>
    protected virtual void OnCreated() { }

    /// <summary>
    /// Called after the object has been deserialized (loaded from disk).
    /// Override to rebuild runtime caches or validate data.
    /// </summary>
    protected virtual void OnAfterDeserialize() { }

    /// <summary>
    /// Called just before the object is serialized (saved to disk).
    /// Override to prepare data for serialization.
    /// </summary>
    protected virtual void OnBeforeSerialize() { }

    // ── ISerializationCallbackReceiver ─────────────────────────

    void ISerializationCallbackReceiver.OnBeforeSerialize() => OnBeforeSerialize();

    void ISerializationCallbackReceiver.OnAfterDeserialize() => OnAfterDeserialize();
}
