// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;

namespace Prowl.Editor.Icons;

/// <summary>
/// Represents a drawable icon that can be rendered in the editor UI.
/// Implementations may be texture-based (raster/SVG) or font-based (glyph).
/// </summary>
public interface IIcon
{
    /// <summary>
    /// Draws the icon at the specified position with the given size.
    /// </summary>
    /// <param name="position">Top-left screen position (in ImGui screen coordinates).</param>
    /// <param name="size">Width and height in pixels (icons are square).</param>
    /// <param name="tint">Optional RGBA tint color. Defaults to opaque white.</param>
    void Draw(Vector2 position, float size, Vector4? tint = null);
}
