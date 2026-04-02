// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.Vector;

namespace Prowl.Runtime.UI;

/// <summary>
/// Determines how a <see cref="CanvasScaler"/> computes the scale factor.
/// </summary>
public enum ScaleMode
{
    /// <summary>
    /// UI elements retain the same pixel size regardless of screen resolution.
    /// </summary>
    ConstantPixelSize,

    /// <summary>
    /// UI elements scale with the screen size based on a reference resolution.
    /// </summary>
    ScaleWithScreenSize,
}

/// <summary>
/// Controls how matching is performed when <see cref="ScaleMode.ScaleWithScreenSize"/> is used.
/// </summary>
public enum ScreenMatchMode
{
    /// <summary>
    /// Blend between matching the width and height of the reference resolution
    /// using <see cref="CanvasScaler.MatchWidthOrHeight"/>.
    /// </summary>
    MatchWidthOrHeight,

    /// <summary>
    /// Expand the canvas area so it never becomes smaller than the reference resolution.
    /// </summary>
    Expand,

    /// <summary>
    /// Shrink the canvas area so it never becomes larger than the reference resolution.
    /// </summary>
    Shrink,
}

/// <summary>
/// Manages the scale factor of a sibling <see cref="Canvas"/> to adapt UI layout
/// to different screen resolutions and DPI settings.
/// Analogous to Unity's <c>CanvasScaler</c>.
/// </summary>
/// <remarks>
/// <para>
/// Attach this component to the same <see cref="GameObject"/> as a <see cref="Canvas"/>.
/// Each frame it computes a scale factor and writes it to <see cref="Canvas.ScaleFactor"/>.
/// </para>
/// <para>
/// In <see cref="ScaleMode.ConstantPixelSize"/> mode, a fixed
/// <see cref="ScaleFactor"/> is applied directly.
/// </para>
/// <para>
/// In <see cref="ScaleMode.ScaleWithScreenSize"/> mode, the current screen size
/// is compared against a <see cref="ReferenceResolution"/> and the scale factor
/// is derived from the ratio, controlled by <see cref="ScreenMatchMode"/> and
/// <see cref="MatchWidthOrHeight"/>.
/// </para>
/// </remarks>
[RequireComponent(typeof(Canvas))]
public class CanvasScaler : MonoBehaviour
{
    /// <summary>
    /// How the scale factor is determined.
    /// </summary>
    public ScaleMode UIScaleMode = ScaleMode.ConstantPixelSize;

    /// <summary>
    /// Fixed scale factor used when <see cref="UIScaleMode"/> is
    /// <see cref="ScaleMode.ConstantPixelSize"/>.
    /// </summary>
    public float ScaleFactor = 1f;

    /// <summary>
    /// The reference resolution the UI is designed for.
    /// Only used when <see cref="UIScaleMode"/> is
    /// <see cref="ScaleMode.ScaleWithScreenSize"/>.
    /// </summary>
    public Float2 ReferenceResolution = new(1920f, 1080f);

    /// <summary>
    /// How width and height ratios are combined when
    /// <see cref="ScreenMatchMode"/> is <see cref="UI.ScreenMatchMode.MatchWidthOrHeight"/>.
    /// 0 = match width, 1 = match height, 0.5 = blend equally.
    /// </summary>
    public float MatchWidthOrHeight = 0.5f;

    /// <summary>
    /// The screen-match strategy used when <see cref="UIScaleMode"/> is
    /// <see cref="ScaleMode.ScaleWithScreenSize"/>.
    /// </summary>
    public ScreenMatchMode ScreenMatchMode = ScreenMatchMode.MatchWidthOrHeight;

    public override void Update()
    {
        Canvas? canvas = GetComponent<Canvas>();
        if (canvas == null)
            return;

        float newScale = ComputeScaleFactor();
        canvas.ScaleFactor = newScale;
    }

    private float ComputeScaleFactor()
    {
        switch (UIScaleMode)
        {
            case ScaleMode.ConstantPixelSize:
                return Maths.Max(ScaleFactor, 0.001f);

            case ScaleMode.ScaleWithScreenSize:
                return ComputeScreenSizeScale();

            default:
                return 1f;
        }
    }

    private float ComputeScreenSizeScale()
    {
        float screenW = Canvas.ScreenSizeOverride?.X ?? Window.InternalWindow.FramebufferSize.X;
        float screenH = Canvas.ScreenSizeOverride?.Y ?? Window.InternalWindow.FramebufferSize.Y;

        if (screenW <= 0 || screenH <= 0)
            return 1f;

        float refW = Maths.Max(ReferenceResolution.X, 1f);
        float refH = Maths.Max(ReferenceResolution.Y, 1f);

        float logWidth = MathF.Log2(screenW / refW);
        float logHeight = MathF.Log2(screenH / refH);

        float logScale;
        switch (ScreenMatchMode)
        {
            case ScreenMatchMode.MatchWidthOrHeight:
            {
                float match = Maths.Clamp(MatchWidthOrHeight, 0f, 1f);
                logScale = Maths.Lerp(logWidth, logHeight, match);
                break;
            }

            case ScreenMatchMode.Expand:
                logScale = Maths.Min(logWidth, logHeight);
                break;

            case ScreenMatchMode.Shrink:
                logScale = Maths.Max(logWidth, logHeight);
                break;

            default:
                logScale = 0f;
                break;
        }

        return Maths.Max(Maths.Pow(2f, logScale), 0.001f);
    }
}
