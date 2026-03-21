// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Vector;

namespace Prowl.Runtime.UI;

/// <summary>
/// Transition mode used by <see cref="UIButton"/> to indicate state changes.
/// </summary>
public enum ButtonTransition
{
    /// <summary>No visual transition.</summary>
    None,
    /// <summary>Tint the target graphic with a color.</summary>
    ColorTint,
}

/// <summary>
/// A clickable UI button that invokes an event when pressed.
/// Analogous to Unity's <c>Button</c> component.
/// </summary>
/// <remarks>
/// Requires a <see cref="RectTransform"/> on the same GameObject.
/// Optionally works with a sibling <see cref="UIImage"/> for visuals.
/// When <see cref="CanvasGroup.Interactable"/> is <c>false</c>,
/// the button is shown in its disabled state and does not fire click events.
/// </remarks>
[RequireComponent(typeof(RectTransform))]
public class UIButton : UIBehaviour
{
    /// <summary>
    /// Raised when the button is clicked and the element is interactable.
    /// </summary>
    public event Action? OnClick;

    /// <summary>
    /// Visual transition mode for state changes.
    /// </summary>
    public ButtonTransition Transition = ButtonTransition.ColorTint;

    /// <summary>
    /// Color applied in the normal state.
    /// </summary>
    public Color NormalColor = Color.White;

    /// <summary>
    /// Color applied when the pointer hovers over the button.
    /// </summary>
    public Color HighlightedColor = new(0.85f, 0.85f, 0.85f, 1f);

    /// <summary>
    /// Color applied while the button is being pressed.
    /// </summary>
    public Color PressedColor = new(0.7f, 0.7f, 0.7f, 1f);

    /// <summary>
    /// Color applied when the button is not interactable.
    /// </summary>
    public Color DisabledColor = new(0.5f, 0.5f, 0.5f, 0.5f);

    /// <summary>
    /// Corner radius for the button background (in pixels).
    /// </summary>
    public float CornerRadius = 4f;

    /// <summary>
    /// Optional label text displayed on the button.
    /// </summary>
    public string Text = string.Empty;

    /// <summary>
    /// Font size for the label text.
    /// </summary>
    public float FontSize = 16f;

    /// <summary>
    /// Text color for the label.
    /// </summary>
    public Color TextColor = Color.White;

    public override void BuildUI(Paper paper, UIContext context)
    {
        RectTransform? rt = GetComponent<RectTransform>();
        if (rt == null) return;

        Rect rect = rt.ComputedRect;
        float w = rect.Size.X;
        float h = rect.Size.Y;
        if (w <= 0 || h <= 0) return;

        bool interactable = context.Interactable;

        Color bgColor;
        if (!interactable)
            bgColor = DisabledColor;
        else
            bgColor = NormalColor;

        bgColor = new Color(
            bgColor.R,
            bgColor.G,
            bgColor.B,
            bgColor.A * context.Alpha
        );

        Color hoverColor = new(
            HighlightedColor.R,
            HighlightedColor.G,
            HighlightedColor.B,
            HighlightedColor.A * context.Alpha
        );

        var box = paper.Box($"btn_{InstanceID}")
            .PositionType(PositionType.SelfDirected)
            .Left(rect.Min.X)
            .Top(rect.Min.Y)
            .Width(w)
            .Height(h)
            .BackgroundColor(bgColor)
            .ChildLeft(UnitValue.Stretch(1f))
            .ChildRight(UnitValue.Stretch(1f))
            .ChildTop(UnitValue.Stretch(1f))
            .ChildBottom(UnitValue.Stretch(1f));

        if (CornerRadius > 0)
            box = box.Rounded(CornerRadius);

        if (interactable)
        {
            box = box.Hovered.BackgroundColor(hoverColor).End();
            box = box.OnClick(_ => OnClick?.Invoke());
        }

        using (box.Enter())
        {
            // Render label text if present
            if (!string.IsNullOrEmpty(Text))
            {
                var label = paper.Box($"btn_lbl_{InstanceID}")
                    .FontSize(FontSize)
                    .TextColor(new Color(TextColor.R, TextColor.G, TextColor.B, TextColor.A * context.Alpha));

                label.Enter().Dispose();
            }
        }
    }
}
