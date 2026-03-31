// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Events raised during the rendering pipeline.
/// </summary>
[EventDomain(Global = true)]
public static partial class RenderingEvents
{
    /// <summary>Raised before any scene rendering begins for the frame.</summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnBeginRender = new();

    /// <summary>Raised after all scene rendering and post-processing is complete.</summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnEndRender = new();

    /// <summary>Raised after the shadow atlas has been initialized and cleared.</summary>
    [EventArgs(typeof(Unit))]
    private static readonly EventKey _OnShadowsReady = new();
}
