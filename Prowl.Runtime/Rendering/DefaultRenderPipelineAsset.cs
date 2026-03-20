// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.Rendering;

/// <summary>
/// Configurable asset for the default deferred/forward hybrid render pipeline.
/// Exposes settings that control GBuffer precision, shadow quality, and other
/// pipeline-wide rendering parameters.
/// <para>
/// Assign an instance of this asset to <see cref="RenderPipeline.ActivePipelineAsset"/>
/// to use it as the global default, or to <see cref="Camera.PipelineAsset"/>
/// for per-camera overrides.
/// </para>
/// </summary>
public class DefaultRenderPipelineAsset : RenderPipelineAsset
{
    #region GBuffer Settings

    /// <summary>
    /// Texture format for GBuffer A (Albedo + Alpha).
    /// Higher precision formats (e.g., <see cref="TextureImageFormat.Short4"/>)
    /// improve color accuracy for HDR workflows at the cost of GPU memory.
    /// </summary>
    public TextureImageFormat GBufferAlbedoFormat { get; set; } = TextureImageFormat.Short4;

    /// <summary>
    /// Texture format for GBuffer B (Normal + ShadingMode).
    /// </summary>
    public TextureImageFormat GBufferNormalFormat { get; set; } = TextureImageFormat.Color4b;

    /// <summary>
    /// Texture format for GBuffer C (Roughness, Metalness, Specular, AO).
    /// </summary>
    public TextureImageFormat GBufferPBRFormat { get; set; } = TextureImageFormat.Color4b;

    /// <summary>
    /// Texture format for GBuffer D (Custom data per shading mode, e.g. Emissive for Lit).
    /// </summary>
    public TextureImageFormat GBufferCustomFormat { get; set; } = TextureImageFormat.Color4b;

    #endregion

    #region Shadow Settings

    /// <summary>
    /// Resolution of the shadow atlas texture in pixels.
    /// Set to <c>0</c> for automatic selection based on GPU capabilities (4096 or 8192).
    /// Common explicit values: 1024, 2048, 4096, 8192.
    /// </summary>
    public int ShadowAtlasResolution { get; set; } = 0;

    #endregion

    /// <inheritdoc/>
    protected override RenderPipeline CreatePipeline()
    {
        return new DefaultRenderPipeline(this);
    }
}
