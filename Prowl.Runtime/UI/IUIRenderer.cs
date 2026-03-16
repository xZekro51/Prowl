// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.UI;

/// <summary>
/// Backend-agnostic entry point for building a UI tree each frame.
/// Implementations forward calls to a concrete toolkit such as Paper or Dear ImGui.
/// </summary>
/// <remarks>
/// <para>
/// <b>Adding a new backend:</b><br/>
/// 1. Create a class that implements <see cref="IUIRenderer"/> (e.g. <c>ImGuiRenderer</c>).<br/>
/// 2. Implement <see cref="IElementBuilder"/> and <see cref="IHoverBuilder"/> for that backend.<br/>
/// 3. In your application's <c>BeginGui</c> override (or equivalent entry point),
///    construct the renderer and pass it to your UI code instead of the concrete toolkit object.
/// </para>
/// </remarks>
public interface IUIRenderer
{
    /// <summary>Creates a generic box element.</summary>
    IElementBuilder Box(string id);

    /// <summary>Creates a horizontal layout element (children placed in a row).</summary>
    IElementBuilder Row(string id);

    /// <summary>Creates a vertical layout element (children placed in a column).</summary>
    IElementBuilder Column(string id);

    /// <summary>Returns <c>true</c> if the pointer is inside the given screen-space rectangle.</summary>
    bool IsPointerOverRect(float x, float y, float width, float height);
}
