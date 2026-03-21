// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace Prowl.Runtime;

/// <summary>
/// Marks a <see cref="ScriptableObject"/>-derived class so that it appears in
/// the editor's Project panel "Create" menu. The attribute specifies the menu
/// path, default file name, and sort order.
///
/// <para><b>Example:</b></para>
/// <code>
/// [CreateAssetMenu(MenuName = "Game/Enemy Stats", FileName = "NewEnemyStats")]
/// public class EnemyStats : ScriptableObject { ... }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CreateAssetMenuAttribute : Attribute
{
    /// <summary>
    /// The path in the Create menu (e.g. <c>"Game/Enemy Stats"</c>).
    /// Slashes create nested submenus.
    /// If empty, the class name is used.
    /// </summary>
    public string MenuName { get; set; } = string.Empty;

    /// <summary>
    /// The default file name (without extension) when creating a new asset.
    /// If empty, <c>"New"</c> + the class name is used.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Sort order within the Create menu. Lower values appear first.
    /// </summary>
    public int Order { get; set; }
}
