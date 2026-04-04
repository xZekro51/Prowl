// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Runtime.Resources;

using Material = Prowl.Runtime.Resources.Material;
using Shader = Prowl.Runtime.Resources.Shader;

namespace Prowl.Runtime.Rendering.GI;

/// <summary>
/// Applies temporal accumulation filtering for GI results.
/// Both VoxelGI and SDFGI produce noisy single-frame results; temporal
/// filtering greatly improves quality by blending with previous frames.
/// </summary>
public sealed class GITemporalFilter : IDisposable
{
    private RenderTexture? _previousGI;
    private Material? _blendMat;
    private bool _disposed;

    /// <summary>
    /// Applies temporal blending between the current GI contribution and
    /// the previous frame's result.
    /// <para>
    /// On the first frame (no history), the current GI is copied to the
    /// history buffer and returned unchanged. On subsequent frames, the
    /// history is blended with the current GI using an adaptive luminance-
    /// difference rejection to avoid ghosting on camera cuts or fast motion.
    /// </para>
    /// </summary>
    /// <param name="currentGI">Render texture containing this frame's GI contribution.</param>
    /// <param name="gBuffer">GBuffer (unused currently; reserved for depth-based rejection).</param>
    /// <param name="css">Camera snapshot for the current frame.</param>
    /// <param name="blendFactor">Temporal blend weight. 0 = no history, 1 = full history. Default 0.9.</param>
    public void Apply(RenderTexture currentGI, RenderTexture gBuffer,
                      RenderPipeline.CameraSnapshot css, float blendFactor = 0.9f)
    {
        if (_disposed)
            return;

        // Ensure the blend material exists
        if (_blendMat.IsNotValid())
        {
            _blendMat?.Dispose();
            _blendMat = new Material(Shader.LoadDefault(DefaultShader.GI_TemporalBlend));
        }

        // First frame — no history available, just store current and return
        if (_previousGI == null || _previousGI.IsDisposed)
        {
            _previousGI = RenderTexture.GetTemporaryRT(
                currentGI.Width, currentGI.Height, false,
                [TextureImageFormat.Short4]);

            // Copy current GI into history for next frame
            RenderPipeline.Blit(currentGI, _previousGI);
            return;
        }

        // Reallocate history if dimensions changed
        if (_previousGI.Width != currentGI.Width || _previousGI.Height != currentGI.Height)
        {
            RenderTexture.ReleaseTemporaryRT(_previousGI);
            _previousGI = RenderTexture.GetTemporaryRT(
                currentGI.Width, currentGI.Height, false,
                [TextureImageFormat.Short4]);

            RenderPipeline.Blit(currentGI, _previousGI);
            return;
        }

        // Blend current GI with previous frame's result
        _blendMat!.SetTexture("_PreviousGI", _previousGI.MainTexture);
        _blendMat.SetFloat("_BlendFactor", blendFactor);

        // Create a temporary RT for the blended result
        RenderTexture blended = RenderTexture.GetTemporaryRT(
            currentGI.Width, currentGI.Height, false,
            [TextureImageFormat.Short4]);

        RenderPipeline.Blit(currentGI, blended, _blendMat, 0);

        // Copy blended result back to currentGI
        RenderPipeline.Blit(blended, currentGI);

        // Store blended result as history for next frame
        RenderPipeline.Blit(blended, _previousGI);

        RenderTexture.ReleaseTemporaryRT(blended);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _blendMat?.Dispose();
        _blendMat = null;

        if (_previousGI != null)
        {
            RenderTexture.ReleaseTemporaryRT(_previousGI);
            _previousGI = null;
        }
    }
}
