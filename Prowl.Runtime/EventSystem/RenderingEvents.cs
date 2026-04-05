// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Prowl.Runtime.Rendering;
using Prowl.Runtime.Rendering.GI;
using Prowl.Runtime.Resources;

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

    // ── Per-stage pipeline events ──────────────────────────────────────

    /// <summary>Raised at the start of a camera's render pass (after setup, before GBuffer).</summary>
    [EventArgs(typeof(CameraRenderBeginArgs))]
    private static readonly EventKey _OnCameraRenderBegin = new();

    /// <summary>Raised after a camera's render pass completes (after blit, before cleanup).</summary>
    [EventArgs(typeof(CameraRenderEndArgs))]
    private static readonly EventKey _OnCameraRenderEnd = new();

    /// <summary>Raised when the GBuffer pass begins.</summary>
    [EventArgs(typeof(GBufferPassArgs))]
    private static readonly EventKey _OnGBufferPassBegin = new();

    /// <summary>Raised when the GBuffer pass ends.</summary>
    [EventArgs(typeof(GBufferPassArgs))]
    private static readonly EventKey _OnGBufferPassEnd = new();

    /// <summary>Raised when the deferred lighting pass begins.</summary>
    [EventArgs(typeof(LightingPassArgs))]
    private static readonly EventKey _OnLightingPassBegin = new();

    /// <summary>Raised when the deferred lighting pass ends.</summary>
    [EventArgs(typeof(LightingPassArgs))]
    private static readonly EventKey _OnLightingPassEnd = new();

    /// <summary>Raised when the forward transparent pass begins.</summary>
    [EventArgs(typeof(TransparentPassArgs))]
    private static readonly EventKey _OnTransparentPassBegin = new();

    /// <summary>Raised after the deferred composition pass completes.</summary>
    [EventArgs(typeof(CompositionCompleteArgs))]
    private static readonly EventKey _OnCompositionComplete = new();

    /// <summary>Raised after render stats are swapped at the end of a frame.</summary>
    [EventArgs(typeof(RenderStatsReadyArgs))]
    private static readonly EventKey _OnRenderStatsReady = new();

    // \u2500\u2500 GI system events \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    /// <summary>Raised when the GI data update pass begins (voxelization/SDF update).</summary>
    [EventArgs(typeof(GIPassBeginArgs))]
    private static readonly EventKey _OnGIPassBegin = new();

    /// <summary>Raised when the GI data update pass ends.</summary>
    [EventArgs(typeof(GIPassEndArgs))]
    private static readonly EventKey _OnGIPassEnd = new();

    /// <summary>Raised during lighting for GI trace (cone trace / probe lookup).</summary>
    [EventArgs(typeof(GITracePassArgs))]
    private static readonly EventKey _OnGITracePass = new();

    /// <summary>Raised after GI trace for debug visualization overlay.</summary>
    [EventArgs(typeof(GIDebugVisualizeArgs))]
    private static readonly EventKey _OnGIDebugVisualize = new();
}

/// <summary>
/// Arguments for GI update events.
/// </summary>
public readonly record struct GIUpdateArgs(
    Runtime.Resources.Scene.GlobalIlluminationParams.GIMode Mode,
    float UpdateTimeMs);

/// <summary>Arguments for the start of a camera render pass.</summary>
public readonly record struct CameraRenderBeginArgs(uint PixelWidth, uint PixelHeight);

/// <summary>Arguments for the end of a camera render pass.</summary>
public readonly record struct CameraRenderEndArgs(bool RenderedToSwapchain);

/// <summary>Arguments for GBuffer pass begin/end events.</summary>
public readonly record struct GBufferPassArgs(RenderTexture GBuffer);

/// <summary>Arguments for lighting pass begin/end events.</summary>
public readonly record struct LightingPassArgs(RenderTexture GBuffer, RenderTexture LightAccumulation, int LightCount);

/// <summary>Arguments for transparent pass begin event.</summary>
public readonly record struct TransparentPassArgs(RenderTexture ComposedOutput);

/// <summary>Arguments for the composition complete event.</summary>
public readonly record struct CompositionCompleteArgs(RenderTexture FinalOutput, RenderTexture GBuffer);

/// <summary>Arguments for render stats ready event.</summary>
public readonly record struct RenderStatsReadyArgs(int DrawCalls, int Triangles, int Vertices, float GpuTimeMs);

/// <summary>Arguments for the GI data update pass begin event.</summary>
public readonly record struct GIPassBeginArgs(
    Runtime.Resources.Scene.GlobalIlluminationParams.GIMode Mode,
    float GIIntensity,
    Runtime.Resources.Scene.GlobalIlluminationParams GIParams,
    IReadOnlyList<IRenderable> Renderables,
    HashSet<int> CulledRenderableIndices,
    RenderPipeline.CameraSnapshot CameraSnapshot,
    IReadOnlyList<IRenderableLight> Lights);

/// <summary>Arguments for the GI data update pass end event.</summary>
public readonly record struct GIPassEndArgs(
    Runtime.Resources.Scene.GlobalIlluminationParams.GIMode Mode);

/// <summary>Arguments for the GI trace pass (cone trace / probe lookup during lighting).</summary>
public readonly record struct GITracePassArgs(
    Runtime.Resources.Scene.GlobalIlluminationParams.GIMode Mode,
    float GIIntensity,
    int ConeCount,
    RenderTexture GBuffer,
    RenderTexture LightAccumulation,
    RenderPipeline.CameraSnapshot CameraSnapshot);

/// <summary>Arguments for GI debug visualization overlay.</summary>
public readonly record struct GIDebugVisualizeArgs(
    Runtime.Resources.Scene.GlobalIlluminationParams.GIMode Mode,
    GIDebugMode DebugMode,
    RenderTexture GBuffer,
    RenderTexture LightAccumulation,
    RenderPipeline.CameraSnapshot CameraSnapshot);
