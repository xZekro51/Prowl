// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Vector;

namespace Prowl.UI;

/// <summary>
/// Builder for hover-state styles. Call <see cref="End"/> to return
/// to the main <see cref="IElementBuilder"/> chain.
/// </summary>
public interface IHoverBuilder
{
    IHoverBuilder BackgroundColor(Color color);
    IElementBuilder End();
}
