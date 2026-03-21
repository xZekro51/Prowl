// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Vector;

namespace Prowl.Runtime.UI;

/// <summary>
/// Render mode for a <see cref="Canvas"/>.
/// </summary>
public enum RenderMode
{
    /// <summary>
    /// The Canvas is rendered as an overlay on top of the entire screen.
    /// UI elements are sized in screen-space pixels.
    /// </summary>
    ScreenSpaceOverlay,

    /// <summary>
    /// The Canvas is rendered as a screen-space overlay on a specific camera's
    /// output. Currently behaves identically to <see cref="ScreenSpaceOverlay"/>.
    /// </summary>
    ScreenSpaceCamera,

    /// <summary>
    /// The Canvas lives in world space. Use <see cref="WorldCanvas"/> for this mode.
    /// </summary>
    WorldSpace,
}

/// <summary>
/// Root component for a Paper-based UI hierarchy.
/// Analogous to Unity's <c>Canvas</c>.
/// </summary>
/// <remarks>
/// <para>
/// The Canvas is responsible for walking the child <see cref="GameObject"/>
/// hierarchy each frame during <see cref="MonoBehaviour.OnGui"/> and building
/// the Paper element tree. It computes <see cref="RectTransform"/> layout and
/// propagates <see cref="UIContext"/> (alpha, interactivity) from
/// <see cref="CanvasGroup"/> components.
/// </para>
/// <para>
/// <see cref="RenderMode.ScreenSpaceOverlay"/> and
/// <see cref="RenderMode.ScreenSpaceCamera"/> are rendered as screen-space
/// overlays via the Paper/OnGui pipeline.
/// For world-space canvases, use <see cref="WorldCanvas"/>.
/// </para>
/// </remarks>
[RequireComponent(typeof(RectTransform))]
public class Canvas : MonoBehaviour
{
    /// <summary>
    /// When set, Canvas uses this size instead of the window framebuffer size.
    /// Used by the editor to render into off-screen render textures.
    /// </summary>
    public static Float2? ScreenSizeOverride { get; set; }

    /// <summary>
    /// The rendering mode of this Canvas.
    /// </summary>
    public RenderMode RenderMode = RenderMode.ScreenSpaceOverlay;

    /// <summary>
    /// Global scale factor applied to all elements in the Canvas.
    /// </summary>
    public float ScaleFactor = 1f;

    /// <summary>
    /// Sort order relative to other screen-space canvases.
    /// Higher values render on top.
    /// </summary>
    public int SortOrder;

    /// <summary>
    /// Called every frame by the Scene's OnGui pipeline.
    /// Builds the full Paper UI tree from the child hierarchy.
    /// </summary>
    public override void OnGui(Paper paper)
    {
        // WorldSpace canvases are handled by WorldCanvas / IRenderable.
        if (RenderMode == RenderMode.WorldSpace)
            return;

        // Determine screen-space root rect.
        // When rendering into an off-screen RT (e.g. editor game view), the
        // override provides the correct dimensions instead of the window size.
        float rawW = ScreenSizeOverride?.X ?? Window.InternalWindow.FramebufferSize.X;
        float rawH = ScreenSizeOverride?.Y ?? Window.InternalWindow.FramebufferSize.Y;
        float screenW = rawW / ScaleFactor;
        float screenH = rawH / ScaleFactor;
        Rect rootRect = new(0, 0, screenW, screenH);

        // Compute layout for the root RectTransform
        RectTransform? rootRt = GetComponent<RectTransform>();
        if (rootRt != null)
        {
            rootRt.AnchorMin = Float2.Zero;
            rootRt.AnchorMax = Float2.One;
            rootRt.SizeDelta = Float2.Zero;
            rootRt.AnchoredPosition = Float2.Zero;
            rootRt.ComputedRect = rootRect;
        }

        UIContext context = UIContext.Default;

        // Create a root Paper container that covers the full screen
        var root = paper.Box($"canvas_{InstanceID}")
            .PositionType(PositionType.SelfDirected)
            .Left(0)
            .Top(0)
            .Width(screenW)
            .Height(screenH);

        using (root.Enter())
        {
            // Traverse children depth-first
            BuildChildren(paper, GameObject, rootRect, context);
        }
    }

    /// <summary>
    /// Recursively traverses child GameObjects, computes RectTransform layout,
    /// applies CanvasGroup contexts, and invokes BuildUI on UIBehaviours.
    /// </summary>
    private void BuildChildren(Paper paper, GameObject parent, Rect parentRect, UIContext context)
    {
        foreach (GameObject child in parent.Children)
        {
            if (!child.EnabledInHierarchy)
                continue;

            // Nested Canvas — skip; it will handle itself
            Canvas? nestedCanvas = child.GetComponent<Canvas>();
            if (nestedCanvas != null && nestedCanvas != this)
                continue;

            // Apply CanvasGroup if present
            UIContext childContext = context;
            CanvasGroup? group = child.GetComponent<CanvasGroup>();
            if (group != null && group.EnabledInHierarchy)
                childContext = group.ApplyTo(context);

            // Compute layout if a RectTransform is present
            Rect childRect = parentRect;
            RectTransform? rt = child.GetComponent<RectTransform>();
            if (rt != null)
                childRect = rt.ComputeRect(parentRect);

            // Build visual UI for all UIBehaviours on this object
            foreach (UIBehaviour uiBehaviour in child.GetComponents<UIBehaviour>())
            {
                if (uiBehaviour.EnabledInHierarchy)
                    uiBehaviour.BuildUI(paper, childContext);
            }

            // Recurse into grandchildren
            BuildChildren(paper, child, childRect, childContext);
        }
    }
}
