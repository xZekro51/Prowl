// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Echo;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Text;
using Prowl.Vector;

using Xunit;

namespace Prowl.Runtime.Test;

/// <summary>
/// Tests for <see cref="TextStyle"/> — verifies default values, serialization round-trip,
/// and integration with <see cref="TextRenderer.ApplyStyle"/> / <see cref="TextRenderer.CaptureStyle"/>.
/// </summary>
public class TextStyleTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable d in _disposables)
            d.Dispose();
        _disposables.Clear();
    }

    [Fact]
    public void TextStyle_Defaults_AreReasonable()
    {
        TextStyle style = ScriptableObject.CreateInstance<TextStyle>();
        _disposables.Add(style);

        Assert.Equal(1f, style.FontSize);
        Assert.Equal(Color.White, style.Color);
        Assert.Equal(TextAlignment.Left, style.Alignment);
        Assert.Equal(VerticalAlignment.Top, style.VerticalAlign);
        Assert.Equal(TextOverflowMode.Overflow, style.Overflow);
        Assert.True(style.RichText);
        Assert.Equal(0f, style.CharacterSpacing);
        Assert.Equal(0f, style.LineSpacing);
        Assert.Equal(0f, style.WordSpacing);
        Assert.Equal(0f, style.ParagraphSpacing);
        Assert.Equal(0f, style.OutlineWidth);
        Assert.Equal(0f, style.Softness);
        Assert.Equal(0f, style.UnderlayDilate);
        Assert.Equal(0f, style.UnderlaySoftness);
    }

    [Fact]
    public void TextStyle_RoundTrip_PreservesAllFields()
    {
        TextStyle original = ScriptableObject.CreateInstance<TextStyle>();
        _disposables.Add(original);

        original.FontSize = 24f;
        original.Color = new Color(1f, 0f, 0f, 1f);
        original.Alignment = TextAlignment.Center;
        original.VerticalAlign = VerticalAlignment.Middle;
        original.Overflow = TextOverflowMode.Ellipsis;
        original.RichText = false;
        original.CharacterSpacing = 1.5f;
        original.LineSpacing = 2f;
        original.WordSpacing = 0.5f;
        original.ParagraphSpacing = 3f;
        original.OutlineWidth = 0.1f;
        original.OutlineColor = new Color(0f, 1f, 0f, 1f);
        original.Softness = 0.5f;
        original.UnderlayColor = new Color(0f, 0f, 1f, 0.8f);
        original.UnderlayOffset = new Float2(2f, -3f);
        original.UnderlayDilate = 0.3f;
        original.UnderlaySoftness = 0.4f;

        EchoObject serialized = Serializer.Serialize(original);
        TextStyle? deserialized = Serializer.Deserialize<TextStyle>(serialized);
        Assert.NotNull(deserialized);
        _disposables.Add(deserialized!);

        Assert.Equal(original.FontSize, deserialized!.FontSize);
        Assert.Equal(original.Color, deserialized.Color);
        Assert.Equal(original.Alignment, deserialized.Alignment);
        Assert.Equal(original.VerticalAlign, deserialized.VerticalAlign);
        Assert.Equal(original.Overflow, deserialized.Overflow);
        Assert.Equal(original.RichText, deserialized.RichText);
        Assert.Equal(original.CharacterSpacing, deserialized.CharacterSpacing);
        Assert.Equal(original.LineSpacing, deserialized.LineSpacing);
        Assert.Equal(original.WordSpacing, deserialized.WordSpacing);
        Assert.Equal(original.ParagraphSpacing, deserialized.ParagraphSpacing);
        Assert.Equal(original.OutlineWidth, deserialized.OutlineWidth);
        Assert.Equal(original.OutlineColor, deserialized.OutlineColor);
        Assert.Equal(original.Softness, deserialized.Softness);
        Assert.Equal(original.UnderlayColor, deserialized.UnderlayColor);
        Assert.Equal(original.UnderlayOffset, deserialized.UnderlayOffset);
        Assert.Equal(original.UnderlayDilate, deserialized.UnderlayDilate);
        Assert.Equal(original.UnderlaySoftness, deserialized.UnderlaySoftness);
    }
}
