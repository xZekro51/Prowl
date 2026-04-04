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

    /// <summary>Raised after voxelization or SDF update completes for the frame.</summary>
    [EventArgs(typeof(GIUpdateArgs))]
    private static readonly EventKey _OnGIDataUpdated = new();

    /// <summary>Raised after GI probes have been updated (SDFGI only).</summary>
    [EventArgs(typeof(GIUpdateArgs))]
    private static readonly EventKey _OnGIProbesUpdated = new();
}

/// <summary>
/// Arguments for GI update events.
/// </summary>
public readonly record struct GIUpdateArgs(
    Runtime.Resources.Scene.GlobalIlluminationParams.GIMode Mode,
    float UpdateTimeMs);
