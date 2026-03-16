// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.UI;

/// <summary>
/// Describes how an element is positioned within its parent.
/// Backend-agnostic equivalent of layout-engine position types.
/// </summary>
public enum UIPositionType
{
    /// <summary>The element is laid out by its parent (default flow).</summary>
    ParentDirected,
    /// <summary>The element positions itself (absolute positioning).</summary>
    SelfDirected
}
