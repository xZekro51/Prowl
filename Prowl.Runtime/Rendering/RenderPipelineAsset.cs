// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.Rendering;

/// <summary>
/// Base class for render pipeline configuration assets.
/// A <see cref="RenderPipelineAsset"/> defines settings and creates a configured
/// <see cref="RenderPipeline"/> instance at runtime.
/// <para>
/// This pattern follows Unity's Scriptable Render Pipeline (SRP) approach:
/// the <b>asset</b> is the serializable, inspector-editable configuration, while
/// the <b>pipeline</b> is the transient runtime object that does the actual rendering.
/// </para>
/// <para>
/// Assign a <see cref="RenderPipelineAsset"/> to
/// <see cref="RenderPipeline.ActivePipelineAsset"/> to set the global default, or to
/// <see cref="Camera.PipelineAsset"/> to override per-camera.
/// </para>
/// </summary>
public abstract class RenderPipelineAsset : EngineObject
{
    private RenderPipeline? _cachedPipeline;

    /// <summary>
    /// Creates a new <see cref="RenderPipeline"/> instance configured by this asset.
    /// Called once and cached until <see cref="InvalidatePipeline"/> is invoked.
    /// </summary>
    protected abstract RenderPipeline CreatePipeline();

    /// <summary>
    /// Gets the pipeline instance for this asset, creating it on first access.
    /// The result is cached so that repeated calls return the same instance.
    /// </summary>
    public RenderPipeline Pipeline
    {
        get
        {
            _cachedPipeline ??= CreatePipeline();
            return _cachedPipeline;
        }
    }

    /// <summary>
    /// Marks the cached pipeline as stale so that the next access to
    /// <see cref="Pipeline"/> will create a fresh instance.
    /// Call this when asset settings change at runtime.
    /// </summary>
    protected void InvalidatePipeline()
    {
        _cachedPipeline = null;
    }

    /// <inheritdoc/>
    public override void OnDispose()
    {
        _cachedPipeline = null;
        base.OnDispose();
    }
}
