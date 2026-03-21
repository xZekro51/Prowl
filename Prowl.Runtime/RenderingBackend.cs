// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime;

/// <summary>
/// Supported rendering backends for window creation and graphics
/// initialisation. The engine currently implements <see cref="OpenGL"/>
/// only; other values are reserved for future use.
/// </summary>
public enum RenderingBackend
{
    /// <summary>OpenGL 4.1 Core profile (default).</summary>
    OpenGL = 0,

    /// <summary>Vulkan (reserved — not yet implemented).</summary>
    Vulkan = 1,

    /// <summary>OpenGL ES 3.0 (reserved — not yet implemented).</summary>
    OpenGLES = 2,
}
