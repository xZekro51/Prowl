// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Graphite;

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Events raised during the main game loop lifecycle.
/// Subscribe via <c>Game.GameLoopEventManager.AddNewDelegate(...)</c> or
/// listen globally with <see cref="EventManager{T}.GlobalInvokeEvent"/>.
/// </summary>
public enum GameLoopEvents
{
    /// <summary>Raised once after the engine and window have been fully initialized.</summary>
    [EventArgs(typeof(InitializedArgs))]
    OnInitialized,

    /// <summary>Raised at the very beginning of each frame, before input processing.</summary>
    [EventArgs(typeof(FrameBeginArgs))]
    OnFrameBegin,

    /// <summary>Raised after all update logic has completed for the frame.</summary>
    [EventArgs(typeof(FrameEndArgs))]
    OnFrameEnd,

    /// <summary>Raised after rendering is complete (after Profiler.EndFrame).</summary>
    [EventArgs(typeof(RenderCompleteArgs))]
    OnRenderComplete,

    /// <summary>Raised when the application window is closing.</summary>
    [EventArgs(typeof(ClosingArgs))]
    OnClosing,
}

/// <summary>
/// Typed argument for <see cref="GameLoopEvents.OnInitialized"/>.
/// </summary>
/// <param name="Backend">The graphics backend that was initialized.</param>
/// <param name="BackendName">Human-readable backend name (e.g. "OpenGL 4.5", "Vulkan 1.3").</param>
/// <param name="WindowWidth">Initial window width in logical (DPI-scaled) pixels.</param>
/// <param name="WindowHeight">Initial window height in logical (DPI-scaled) pixels.</param>
public readonly record struct InitializedArgs(
    GraphicsBackendType Backend,
    string BackendName,
    int WindowWidth,
    int WindowHeight);

/// <summary>
/// Typed argument for <see cref="GameLoopEvents.OnFrameBegin"/>.
/// </summary>
/// <param name="FrameCount">The zero-based frame index (incremented each update tick).</param>
/// <param name="DeltaTime">Unscaled wall-clock time since the previous frame, in seconds.</param>
public readonly record struct FrameBeginArgs(
    long FrameCount,
    float DeltaTime);

/// <summary>
/// Typed argument for <see cref="GameLoopEvents.OnFrameEnd"/>.
/// </summary>
/// <param name="FrameCount">The zero-based frame index for the frame that just completed.</param>
/// <param name="DeltaTime">Time-scale-adjusted delta time for the completed frame, in seconds.</param>
/// <param name="UnscaledDeltaTime">Wall-clock delta time (ignoring time scale), in seconds.</param>
/// <param name="TotalTime">Accumulated scaled time since the game started, in seconds.</param>
public readonly record struct FrameEndArgs(
    long FrameCount,
    float DeltaTime,
    float UnscaledDeltaTime,
    float TotalTime);

/// <summary>
/// Typed argument for <see cref="GameLoopEvents.OnRenderComplete"/>.
/// </summary>
/// <param name="FrameCount">The frame index of the render pass that just finished.</param>
/// <param name="RenderDeltaTime">Wall-clock time allocated to this render tick, in seconds.</param>
public readonly record struct RenderCompleteArgs(
    long FrameCount,
    float RenderDeltaTime);

/// <summary>
/// Typed argument for <see cref="GameLoopEvents.OnClosing"/>.
/// </summary>
/// <param name="TotalRuntime">Total scaled time the application was running, in seconds.</param>
/// <param name="TotalFrames">Total number of update frames executed.</param>
public readonly record struct ClosingArgs(
    float TotalRuntime,
    long TotalFrames);
