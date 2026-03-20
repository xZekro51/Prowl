// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Prowl.Runtime.Resources;

namespace Prowl.Runtime;

/// <summary>
/// Centralises the mutable runtime state that was previously spread across
/// multiple static fields (<see cref="Scene"/>.Current, <see cref="Time"/>.TimeStack,
/// <see cref="AssetDatabase"/>.Current, etc.).
/// <para>
/// A single <see cref="EngineContext"/> instance is created by <see cref="Game.Run"/>
/// and set as <see cref="Current"/>. The existing static accessors on
/// <c>Scene</c>, <c>Time</c>, and <c>AssetDatabase</c> are now thin convenience
/// wrappers that delegate to <c>EngineContext.Current</c>, so all call-sites
/// continue to work unchanged.
/// </para>
/// <para>
/// For advanced scenarios (headless servers, multi-scene testing, editor isolation)
/// you can create additional <see cref="EngineContext"/> instances and swap
/// <see cref="Current"/> as needed.
/// </para>
/// </summary>
public sealed class EngineContext
{
    // ── Singleton accessor ─────────────────────────────────────
    private static EngineContext? s_current;
    private static readonly EngineContext s_default = new();

    /// <summary>
    /// The currently active engine context.
    /// Returns a shared default instance when no explicit context has been set.
    /// </summary>
    public static EngineContext Current
    {
        get => s_current ?? s_default;
        set => s_current = value;
    }

    // ── Scene state ────────────────────────────────────────────

    /// <summary>
    /// The currently active scene.
    /// Equivalent to the former <c>Scene.Current</c>.
    /// </summary>
    public Scene? ActiveScene { get; set; }

    /// <summary>
    /// When <c>true</c>, all component lifecycle methods execute normally.
    /// When <c>false</c> (edit mode), only components marked with
    /// <see cref="ExecuteInEditModeAttribute"/> or implementing
    /// <see cref="Rendering.IRenderable"/>/<see cref="Rendering.IRenderableLight"/>
    /// will execute.
    /// Defaults to <c>true</c> so standalone (non-editor) games run without changes.
    /// </summary>
    public bool IsPlayMode { get; set; } = true;

    /// <summary>
    /// When <c>false</c>, <see cref="Scene.FixedUpdate"/> skips the physics step.
    /// The editor sets this to <c>false</c> during edit mode and <c>true</c> during play mode.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool SimulatePhysics { get; set; } = true;

    // ── Time state ─────────────────────────────────────────────

    /// <summary>
    /// Stack of <see cref="TimeData"/> objects.
    /// The top of the stack is the active time source for the current frame.
    /// </summary>
    public Stack<TimeData> TimeStack { get; } = new();

    // ── Asset database ─────────────────────────────────────────

    /// <summary>
    /// The current asset database implementation.
    /// Equivalent to the former <c>AssetDatabase.Current</c>.
    /// </summary>
    public IAssetDatabase? AssetDatabase { get; set; }
}
