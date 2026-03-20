// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Echo;

namespace Prowl.Runtime.Prefabs;

/// <summary>
/// Lightweight data class stored on a <see cref="GameObject"/> to track its
/// relationship to a prefab asset. Presence of this link indicates the
/// object (and its hierarchy) was instantiated from a .prefab file.
/// </summary>
public sealed class PrefabLink
{
    /// <summary>
    /// The GUID of the prefab asset (from the .meta system).
    /// Empty when the link was established by path only.
    /// </summary>
    [SerializeField]
    public string PrefabAssetGuid = string.Empty;

    /// <summary>
    /// Relative path to the .prefab file inside the project Assets folder.
    /// Used as a fallback when the GUID cannot be resolved.
    /// </summary>
    [SerializeField]
    public string PrefabAssetPath = string.Empty;

    /// <summary>
    /// Whether this is the root of a prefab instance. Children inside
    /// the same prefab hierarchy have <c>IsRoot = false</c>.
    /// </summary>
    [SerializeField]
    public bool IsRoot = true;

    /// <summary>
    /// Creates a deep copy of this link.
    /// </summary>
    public PrefabLink Clone() => new()
    {
        PrefabAssetGuid = PrefabAssetGuid,
        PrefabAssetPath = PrefabAssetPath,
        IsRoot = IsRoot,
    };

    public override string ToString()
        => !string.IsNullOrEmpty(PrefabAssetPath)
            ? $"PrefabLink({PrefabAssetPath})"
            : $"PrefabLink(guid:{PrefabAssetGuid})";
}
