// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.UI;

namespace Prowl.Runtime;

/// <summary>
/// Abstracts the lifecycle of an overlay UI system (e.g. Dear ImGui) so that
/// <see cref="Game"/> can drive the per-frame update/render loop without
/// depending on a concrete implementation.
/// <para>
/// Editor and Launcher projects provide an <see cref="IOverlayManager"/>
/// backed by Dear ImGui; standalone game builds can omit it entirely.
/// </para>
/// </summary>
public interface IOverlayManager : IDisposable
{
    /// <summary>Whether the overlay has been initialised and is ready for use.</summary>
    bool IsReady { get; }

    /// <summary>The <see cref="IUIRenderer"/> created during initialisation (may be null).</summary>
    IUIRenderer? Renderer { get; }

    /// <summary>
    /// Initialises the overlay (context, renderer, input handler, fonts, etc.).
    /// Called during the window Load event.
    /// </summary>
    void Initialize();

    /// <summary>Updates input and begins a new frame.</summary>
    void Update(float delta);

    /// <summary>Begins the overlay frame via the renderer.</summary>
    void BeginFrame();

    /// <summary>Finalises and renders the overlay draw data.</summary>
    void Render();

    /// <summary>
    /// Called when the DPI scale changes at runtime so the overlay can
    /// adjust font rendering or rebuild its atlas.
    /// </summary>
    void OnDpiChanged(float newScale);
}
