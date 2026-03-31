// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;

namespace Prowl.Editor.Services;

/// <summary>
/// Default selection service that tracks the currently selected object in the editor.
/// Supports both scene objects (GameObjects) and project assets.
/// Setting one selection type automatically clears the other.
/// </summary>
public sealed class DefaultSelectionService : ISelectionService
{
    private EngineObject? _activeObject;
    private AssetEntry? _selectedAsset;

    public SelectionServiceEvents Events { get; } = new();

    public EngineObject? ActiveObject
    {
        get => _activeObject;
        set
        {
            if (ReferenceEquals(_activeObject, value) && _selectedAsset == null)
                return;

            _activeObject = value;
            _selectedAsset = null; // selecting a scene object clears asset selection
            Events.InvokeOnSelectionChanged();
        }
    }

    public AssetEntry? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (ReferenceEquals(_selectedAsset, value) && _activeObject == null)
                return;

            _selectedAsset = value;
            _activeObject = null; // selecting an asset clears scene object selection
            Events.InvokeOnSelectionChanged();
        }
    }
}
