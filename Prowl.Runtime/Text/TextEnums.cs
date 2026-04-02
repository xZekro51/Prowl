// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Runtime.Text;

/// <summary>
/// The type of atlas used for font rendering.
/// </summary>
public enum AtlasType : byte
{
    /// <summary> Single-channel signed distance field. </summary>
    SDF = 0,
    /// <summary> Multi-channel signed distance field (sharper corners). </summary>
    MSDF = 1,
    /// <summary> Standard rasterized bitmap atlas. </summary>
    Bitmap = 2
}

/// <summary>
/// Horizontal text alignment.
/// </summary>
public enum TextAlignment : byte
{
    Left = 0,
    Center = 1,
    Right = 2,
    Justified = 3
}

/// <summary>
/// Vertical text alignment.
/// </summary>
public enum VerticalAlignment : byte
{
    Top = 0,
    Middle = 1,
    Bottom = 2
}

/// <summary>
/// How text handles overflow when it exceeds the layout rect.
/// </summary>
public enum TextOverflowMode : byte
{
    /// <summary> Text extends beyond the rect (default). </summary>
    Overflow = 0,
    /// <summary> Break at word boundaries. </summary>
    WordWrap = 1,
    /// <summary> Break at character boundaries. </summary>
    CharacterWrap = 2,
    /// <summary> Cut off at boundary. </summary>
    Truncate = 3,
    /// <summary> Cut off and append ellipsis (…). </summary>
    Ellipsis = 4
}

/// <summary>
/// Style flags that can be combined for rich text runs.
/// </summary>
[System.Flags]
public enum TextStyleFlags
{
    None = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Underline = 1 << 2,
    Strikethrough = 1 << 3,
    Superscript = 1 << 4,
    Subscript = 1 << 5,
    Wave = 1 << 6,
    Shake = 1 << 7,
    Fade = 1 << 8,
    Typewriter = 1 << 9,
    Rainbow = 1 << 10,
    ScaleEffect = 1 << 11,
    RotateEffect = 1 << 12
}

/// <summary>
/// Mask covering all text effect style flags (animation effects, not formatting).
/// </summary>
public static class TextStyleFlagExtensions
{
    /// <summary> All effect flags combined. </summary>
    public const TextStyleFlags AllEffects =
        TextStyleFlags.Wave | TextStyleFlags.Shake | TextStyleFlags.Fade |
        TextStyleFlags.Typewriter | TextStyleFlags.Rainbow |
        TextStyleFlags.ScaleEffect | TextStyleFlags.RotateEffect;
}
