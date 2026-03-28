// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Editor.Rendering;

/// <summary>
/// GPU-based selection outline effect with a soft faded edge.
///
/// <para><b>Algorithm overview:</b></para>
/// <list type="number">
/// <item>Render selected objects as flat white silhouettes into an off-screen
///        render texture using their existing meshes and transforms.</item>
/// <item>Apply a two-pass separable Gaussian blur to the silhouette to produce
///        a soft glow region around the object edges.</item>
/// <item>Composite the outline over the scene render texture by subtracting the
///        original silhouette from the blurred result, giving a faded edge that
///        only appears around the object's contour.</item>
/// </list>
/// </summary>
public sealed class OutlineEffect
{
    /// <summary> Outline color (RGBA). </summary>
    public Color OutlineColor { get; set; } = new(0.28f, 0.56f, 1.0f, 1.0f);

    /// <summary> Outline width in texels (controls Gaussian blur spread). </summary>
    public float OutlineWidth { get; set; } = 2.5f;

    private Material? _outlineMat;

    /// <summary>
    /// Renders a selection outline for the given objects directly onto
    /// <paramref name="sceneRT"/>. Call this after the scene has been rendered
    /// and while the camera's global uniforms are still active.
    /// </summary>
    public void Render(IReadOnlyList<GameObject> selectedObjects, RenderTexture sceneRT)
    {
        if (selectedObjects == null || selectedObjects.Count == 0) return;
        if (sceneRT == null) return;

        EnsureMaterial();
        if (_outlineMat == null) return;

        int w = sceneRT.Width;
        int h = sceneRT.Height;
        if (w <= 0 || h <= 0) return;

        // ── 1. Silhouette pass ─────────────────────────────────
        RenderTexture silhouetteRT = RenderTexture.GetTemporaryRT(w, h, false,
            [TextureImageFormat.Color4b]);

        RenderPipeline.BeginRenderToTarget(silhouetteRT, clear: true, clearColor: new Color(0, 0, 0, 0));

        foreach (var go in selectedObjects)
        {
            if (go == null) continue;
            DrawSilhouettes(go);
        }

        RenderPipeline.EndRenderToTarget(silhouetteRT);

        // ── 2. Horizontal blur ─────────────────────────────────
        RenderTexture blurH = RenderTexture.GetTemporaryRT(w, h, false,
            [TextureImageFormat.Color4b]);

        _outlineMat.SetTexture("_MainTex", silhouetteRT.MainTexture);
        _outlineMat.SetFloat("_OutlineWidth", OutlineWidth);
        _outlineMat.SetVector("_Direction", new Float2(1.0f / w, 0f));
        RenderPipeline.Blit(blurH, _outlineMat, 1, false, true);

        // ── 3. Vertical blur ───────────────────────────────────
        RenderTexture blurV = RenderTexture.GetTemporaryRT(w, h, false,
            [TextureImageFormat.Color4b]);

        _outlineMat.SetTexture("_MainTex", blurH.MainTexture);
        _outlineMat.SetVector("_Direction", new Float2(0f, 1.0f / h));
        RenderPipeline.Blit(blurV, _outlineMat, 1, false, true);

        // ── 4. Composite over scene RT ─────────────────────────
        _outlineMat.SetTexture("_MainTex", blurV.MainTexture);
        _outlineMat.SetTexture("_SilhouetteTex", silhouetteRT.MainTexture);
        _outlineMat.SetColor("_OutlineColor", OutlineColor);
        RenderPipeline.Blit(sceneRT, _outlineMat, 2);

        // ── Cleanup ────────────────────────────────────────────
        RenderTexture.ReleaseTemporaryRT(silhouetteRT);
        RenderTexture.ReleaseTemporaryRT(blurH);
        RenderTexture.ReleaseTemporaryRT(blurV);
    }

    /// <summary>
    /// Draws the silhouette (flat white) of a GameObject and all its children
    /// that have a <see cref="MeshRenderer"/>.
    /// </summary>
    private void DrawSilhouettes(GameObject go)
    {
        foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>())
        {
            if (renderer == null || !renderer.IsValid()) continue;

            Mesh? mesh = renderer.Mesh;
            if (mesh == null || !mesh.IsValid() || mesh.VertexCount <= 0) continue;

            Float4x4 model = renderer.Transform.LocalToWorldMatrix;
            PropertyState.SetGlobalMatrix("prowl_ObjectToWorld", model);
            PropertyState.SetGlobalMatrix("prowl_WorldToObject", model.Invert());

            RenderPipeline.DrawMeshNow(mesh, _outlineMat!, 0); // pass 0 = Silhouette
        }
    }

    private void EnsureMaterial()
    {
        if (_outlineMat == null || _outlineMat.Shader.IsNotValid())
        {
            _outlineMat = new Material(Shader.LoadDefault(DefaultShader.SelectionOutline));
        }
    }
}
