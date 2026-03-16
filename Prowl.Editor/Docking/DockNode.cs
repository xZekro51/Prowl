// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Editor.Docking;

/// <summary> Orientation of a dock split. </summary>
public enum SplitDirection
{
    Horizontal,
    Vertical
}

/// <summary>
/// A node in the docking layout tree.
/// Either a leaf (holds a single panel) or a split (divides space between two children).
/// </summary>
public sealed class DockNode
{
    // --- Leaf properties ---
    public EditorPanel? Panel { get; set; }

    // --- Split properties ---
    public SplitDirection Direction { get; set; }
    public float SplitRatio { get; set; } = 0.5f;
    public DockNode? First { get; set; }
    public DockNode? Second { get; set; }

    public bool IsLeaf => Panel != null;

    /// <summary> Creates a leaf node holding a single panel. </summary>
    public static DockNode Leaf(EditorPanel panel)
        => new() { Panel = panel };

    /// <summary> Creates a split node dividing space between two children. </summary>
    public static DockNode Split(SplitDirection dir, float ratio, DockNode first, DockNode second)
        => new() { Direction = dir, SplitRatio = ratio, First = first, Second = second };
}
