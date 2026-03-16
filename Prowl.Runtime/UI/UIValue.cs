// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.UI;

/// <summary>
/// Represents a dimension value that can be expressed in pixels or as a stretch factor.
/// Backend-agnostic equivalent of layout-engine unit values.
/// </summary>
public readonly struct UIValue
{
    public float Value { get; }
    public UIValueKind Kind { get; }

    private UIValue(float value, UIValueKind kind)
    {
        Value = value;
        Kind = kind;
    }

    /// <summary>Creates a stretch value with the given factor (like CSS flex-grow).</summary>
    public static UIValue Stretch(float factor = 1f) => new(factor, UIValueKind.Stretch);

    /// <summary>Implicit conversion from float (interpreted as pixels).</summary>
    public static implicit operator UIValue(float value) => new(value, UIValueKind.Pixels);

    /// <summary>Implicit conversion from int (interpreted as pixels).</summary>
    public static implicit operator UIValue(int value) => new(value, UIValueKind.Pixels);
}

/// <summary>
/// The kind of a <see cref="UIValue"/>.
/// </summary>
public enum UIValueKind
{
    Pixels,
    Stretch
}
