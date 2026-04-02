// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Echo;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Runtime.Text;

/// <summary>
/// A reusable style preset for text rendering.
/// Stores font, size, color, spacing, outline, and underlay settings
/// that can be applied to a <see cref="TextRenderer"/> component.
/// </summary>
[CreateAssetMenu(MenuName = "Text/Text Style", FileName = "NewTextStyle")]
public class TextStyle : ScriptableObject
{
    // ── Font & Size ───────────────────────────────────────────

    /// <summary> The font asset to use. </summary>
    [SerializeField] public FontAsset? Font;

    /// <summary> Font size in world units (or points for UI). </summary>
    [SerializeField] public float FontSize = 1f;

    /// <summary> Base face color. </summary>
    [SerializeField] public Color Color = Color.White;

    // ── Layout ────────────────────────────────────────────────

    /// <summary> Horizontal text alignment. </summary>
    [SerializeField] public TextAlignment Alignment = TextAlignment.Left;

    /// <summary> Vertical text alignment. </summary>
    [SerializeField] public VerticalAlignment VerticalAlign = VerticalAlignment.Top;

    /// <summary> How text handles overflow. </summary>
    [SerializeField] public TextOverflowMode Overflow = TextOverflowMode.Overflow;

    /// <summary> Enable/disable rich text tag parsing. </summary>
    [SerializeField] public bool RichText = true;

    // ── Spacing ───────────────────────────────────────────────

    /// <summary> Extra spacing between characters. </summary>
    [SerializeField] public float CharacterSpacing = 0f;

    /// <summary> Extra spacing between lines. </summary>
    [SerializeField] public float LineSpacing = 0f;

    /// <summary> Extra spacing between words. </summary>
    [SerializeField] public float WordSpacing = 0f;

    /// <summary> Extra spacing between paragraphs (on explicit newlines). </summary>
    [SerializeField] public float ParagraphSpacing = 0f;

    // ── Outline ───────────────────────────────────────────────

    /// <summary> SDF outline width (0 = no outline). </summary>
    [SerializeField] public float OutlineWidth = 0f;

    /// <summary> Outline color. </summary>
    [SerializeField] public Color OutlineColor = new(0f, 0f, 0f, 1f);

    /// <summary> Anti-aliasing softness. </summary>
    [SerializeField] public float Softness = 0f;

    // ── Underlay (drop shadow) ────────────────────────────────

    /// <summary> Drop shadow color. </summary>
    [SerializeField] public Color UnderlayColor = new(0f, 0f, 0f, 0.5f);

    /// <summary> Drop shadow offset. </summary>
    [SerializeField] public Float2 UnderlayOffset = Float2.Zero;

    /// <summary> Drop shadow dilation. </summary>
    [SerializeField] public float UnderlayDilate = 0f;

    /// <summary> Drop shadow softness. </summary>
    [SerializeField] public float UnderlaySoftness = 0f;
}
