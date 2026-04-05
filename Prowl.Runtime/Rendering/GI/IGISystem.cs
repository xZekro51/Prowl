// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Runtime.Resources;

namespace Prowl.Runtime.Rendering.GI;

/// <summary>
/// Interface for pluggable Global Illumination systems.
/// Implementations handle a specific <see cref="Scene.GlobalIlluminationParams.GIMode"/>
/// and are managed by <see cref="GISystemManager"/>.
/// </summary>
public interface IGISystem : IDisposable
{
    /// <summary>The GI mode this system handles.</summary>
    Scene.GlobalIlluminationParams.GIMode SupportedMode { get; }

    /// <summary>
    /// Whether this system has produced valid indirect lighting data.
    /// Used by the composition pass to decide whether to suppress ambient lighting.
    /// </summary>
    bool HasValidData { get; }

    /// <summary>
    /// Allocates or reallocates GPU resources based on current GI parameters.
    /// Called every frame before <see cref="UpdateData"/> when this system's mode is active.
    /// </summary>
    void EnsureResources(Scene.GlobalIlluminationParams giParams);

    /// <summary>
    /// Performs the GI data update (e.g. voxelization, SDF update, probe update).
    /// Called during pipeline stage 5.2.
    /// </summary>
    void UpdateData(GIDataUpdateContext context);

    /// <summary>
    /// Performs the GI trace (e.g. cone trace, probe lookup) into the light accumulation buffer.
    /// Called during pipeline stage 7.1.
    /// </summary>
    void Trace(GITraceContext context);

    /// <summary>
    /// Renders debug visualization overlay if the given debug mode applies to this system.
    /// Called during pipeline stage 7.3.
    /// </summary>
    void RenderDebugVisualization(GIDebugMode debugMode, RenderTexture gBuffer,
        RenderTexture lightAccumulation, RenderPipeline.CameraSnapshot css);
}

/// <summary>
/// Context passed to <see cref="IGISystem.UpdateData"/> during the GI data update pass.
/// </summary>
public readonly record struct GIDataUpdateContext(
    System.Collections.Generic.IReadOnlyList<IRenderable> Renderables,
    System.Collections.Generic.HashSet<int> CulledRenderableIndices,
    RenderPipeline.CameraSnapshot CameraSnapshot,
    System.Collections.Generic.IReadOnlyList<IRenderableLight> Lights,
    Scene.GlobalIlluminationParams GIParams,
    float GIIntensity);

/// <summary>
/// Context passed to <see cref="IGISystem.Trace"/> during the GI trace pass.
/// </summary>
public readonly record struct GITraceContext(
    RenderTexture GBuffer,
    RenderTexture LightAccumulation,
    RenderPipeline.CameraSnapshot CameraSnapshot,
    float GIIntensity,
    int ConeCount);
