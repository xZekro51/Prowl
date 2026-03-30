// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Events raised by the asset system when assets change on disk.
/// These are only available in the editor (or tools that run the asset pipeline).
/// </summary>
public enum AssetEvents
{
    /// <summary>Raised after the asset database is refreshed (files re-scanned).</summary>
    [EventArgs(typeof(Unit))]
    OnAssetsRefreshed,

    /// <summary>Raised when one or more files are imported into the project (e.g. from OS drag-drop).</summary>
    [EventArgs(typeof(AssetImportedArgs))]
    OnAssetsImported,

    /// <summary>Raised when an asset is deleted from the project.</summary>
    [EventArgs(typeof(AssetDeletedArgs))]
    OnAssetDeleted,
}

/// <summary>
/// Typed argument for <see cref="AssetEvents.OnAssetsImported"/>.
/// </summary>
/// <param name="ImportedPaths">The relative paths of the imported assets.</param>
public readonly record struct AssetImportedArgs(string[] ImportedPaths);

/// <summary>
/// Typed argument for <see cref="AssetEvents.OnAssetDeleted"/>.
/// </summary>
/// <param name="RelativePath">The relative path of the deleted asset.</param>
public readonly record struct AssetDeletedArgs(string RelativePath);
