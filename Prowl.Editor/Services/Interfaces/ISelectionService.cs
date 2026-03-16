// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;

namespace Prowl.Editor.Services;

/// <summary>
/// Tracks the currently selected object(s) in the editor.
/// Inspector and other windows observe this to display context.
/// Supports both scene objects (GameObjects) and project assets.
/// </summary>
public interface ISelectionService
{
    /// <summary> The currently selected scene object, or null. Setting this clears <see cref="SelectedAsset"/>. </summary>
    EngineObject? ActiveObject { get; set; }

    /// <summary> The currently selected project asset, or null. Setting this clears <see cref="ActiveObject"/>. </summary>
    AssetEntry? SelectedAsset { get; set; }

    /// <summary> Fires whenever the selection changes (either ActiveObject or SelectedAsset). </summary>
    event Action? SelectionChanged;
}
