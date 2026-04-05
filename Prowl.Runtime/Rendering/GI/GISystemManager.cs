// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Resources;

namespace Prowl.Runtime.Rendering.GI;

/// <summary>
/// Static manager that orchestrates <see cref="IGISystem"/> instances via the event system.
/// <para>
/// Subscribes to <see cref="RenderingEvents.OnGIPassBegin"/>,
/// <see cref="RenderingEvents.OnGITracePass"/>, and
/// <see cref="RenderingEvents.OnGIDebugVisualize"/> to route work to the active GI system.
/// Also subscribes to <see cref="GraphiteDeviceEvents.OnDeviceDisposing"/> to clean up.
/// </para>
/// <para>
/// By default, <see cref="VoxelGISystem"/> and <see cref="SDFGISystem"/> are lazily created.
/// Custom GI systems can be registered via <see cref="Register"/>.
/// </para>
/// </summary>
internal static class GISystemManager
{
    private static readonly Dictionary<Scene.GlobalIlluminationParams.GIMode, IGISystem> s_systems = [];
    private static readonly Dictionary<Scene.GlobalIlluminationParams.GIMode, Func<IGISystem>> s_factories = [];
    private static GITemporalFilter? s_temporalFilter;

    /// <summary>
    /// Queries whether the active GI system for the given mode has produced valid data.
    /// Used by the composition pass to decide whether to suppress ambient lighting.
    /// </summary>
    public static bool HasValidData(Scene.GlobalIlluminationParams.GIMode mode)
    {
        return s_systems.TryGetValue(mode, out IGISystem? system) && system.HasValidData;
    }

    /// <summary>
    /// Registers a custom GI system instance for the given mode.
    /// Replaces any existing system for that mode.
    /// </summary>
    public static void Register(IGISystem system)
    {
        if (s_systems.TryGetValue(system.SupportedMode, out IGISystem? existing) && existing != system)
            existing.Dispose();

        s_systems[system.SupportedMode] = system;
    }

    /// <summary>
    /// Unregisters a GI system. Does not dispose it.
    /// </summary>
    public static void Unregister(IGISystem system)
    {
        if (s_systems.TryGetValue(system.SupportedMode, out IGISystem? existing) && existing == system)
            s_systems.Remove(system.SupportedMode);
    }

    /// <summary>
    /// Registers a factory for lazy creation of a GI system for the given mode.
    /// The factory is called on the first <see cref="RenderingEvents.OnGIPassBegin"/>
    /// for that mode.
    /// </summary>
    public static void RegisterFactory(Scene.GlobalIlluminationParams.GIMode mode, Func<IGISystem> factory)
    {
        s_factories[mode] = factory;
    }

    /// <summary>
    /// Called once from <see cref="Graphics.InitializeEventSubscriptions"/>.
    /// Sets up event subscriptions and registers default GI system factories.
    /// </summary>
    public static void InitializeEventSubscriptions()
    {
        // Register default factories for built-in GI systems
        RegisterFactory(Scene.GlobalIlluminationParams.GIMode.VoxelGI, () => new VoxelGISystem());
        RegisterFactory(Scene.GlobalIlluminationParams.GIMode.SDFGI, () => new SDFGISystem());

        // GI data update pass (stage 5.2) — priority 0 (default)
        RenderingEvents.SubscribeOnGIPassBegin(OnGIPassBegin, priority: 0);

        // GI trace pass (stage 7.1) — priority 0 for trace, priority 10 for temporal filter
        RenderingEvents.SubscribeOnGITracePass(OnGITracePass, priority: 0);
        RenderingEvents.SubscribeOnGITracePass(OnGITemporalFilter, priority: 10);

        // GI debug visualization (stage 7.3)
        RenderingEvents.SubscribeOnGIDebugVisualize(OnGIDebugVisualize, priority: 0);

        // Device teardown — dispose all systems
        GraphiteDeviceEvents.SubscribeOnDeviceDisposing(DisposeAll, priority: -100);
    }

    private static void OnGIPassBegin(GIPassBeginArgs args)
    {
        if (args.Mode == Scene.GlobalIlluminationParams.GIMode.None)
            return;

        IGISystem system = GetOrCreateSystem(args.Mode);

        system.EnsureResources(args.GIParams);
        system.UpdateData(new GIDataUpdateContext(
            args.Renderables,
            args.CulledRenderableIndices,
            args.CameraSnapshot,
            args.Lights,
            args.GIParams,
            args.GIIntensity));
    }

    private static void OnGITracePass(GITracePassArgs args)
    {
        if (args.Mode == Scene.GlobalIlluminationParams.GIMode.None)
            return;

        if (!s_systems.TryGetValue(args.Mode, out IGISystem? system))
            return;

        system.Trace(new GITraceContext(
            args.GBuffer,
            args.LightAccumulation,
            args.CameraSnapshot,
            args.GIIntensity,
            args.ConeCount));
    }

    private static void OnGITemporalFilter(GITracePassArgs args)
    {
        if (args.Mode == Scene.GlobalIlluminationParams.GIMode.None)
            return;

        s_temporalFilter ??= new GITemporalFilter();
        s_temporalFilter.Apply(args.LightAccumulation, args.GBuffer, args.CameraSnapshot);
    }

    private static void OnGIDebugVisualize(GIDebugVisualizeArgs args)
    {
        if (args.DebugMode == GIDebugMode.None)
            return;

        if (!s_systems.TryGetValue(args.Mode, out IGISystem? system))
            return;

        system.RenderDebugVisualization(args.DebugMode, args.GBuffer,
            args.LightAccumulation, args.CameraSnapshot);
    }

    private static IGISystem GetOrCreateSystem(Scene.GlobalIlluminationParams.GIMode mode)
    {
        if (s_systems.TryGetValue(mode, out IGISystem? system))
            return system;

        if (s_factories.TryGetValue(mode, out Func<IGISystem>? factory))
        {
            system = factory();
            s_systems[mode] = system;
            return system;
        }

        throw new InvalidOperationException($"No GI system registered for mode {mode}");
    }

    private static void DisposeAll()
    {
        foreach (IGISystem system in s_systems.Values)
            system.Dispose();
        s_systems.Clear();

        s_temporalFilter?.Dispose();
        s_temporalFilter = null;
    }
}
