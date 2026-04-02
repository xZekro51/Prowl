// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Text;
using Prowl.Runtime.Text.Effects;
using Prowl.Vector;

using Xunit;

namespace Prowl.Runtime.Test;

public class TextEffectTests
{
    private static TextLayout CreateSimpleLayout(int glyphCount)
    {
        GlyphPlacement[] glyphs = new GlyphPlacement[glyphCount];
        for (int i = 0; i < glyphCount; i++)
        {
            glyphs[i] = new GlyphPlacement
            {
                GlyphIndex = i,
                FontAssetIndex = 0,
                Position = new Float2(i * 14f, 0f),
                Scale = new Float2(1f, 1f),
                Color = Color.White,
                StyleFlags = TextStyleFlags.None,
                LineIndex = 0,
                CharacterIndex = i
            };
        }

        return new TextLayout
        {
            Glyphs = glyphs,
            Lines = [new LineInfo { StartGlyphIndex = 0, GlyphCount = glyphCount, Width = glyphCount * 14f, Ascender = 18f, Descender = -6f, Baseline = 0f }],
            TextBounds = new Float2(glyphCount * 14f, 24f),
            PreferredSize = new Float2(glyphCount * 14f, 24f)
        };
    }

    private static (Float3[] positions, Color[] colors, Float2[] uvs) CreateVertexData(int glyphCount)
    {
        Float3[] positions = new Float3[glyphCount * 4];
        Color[] colors = new Color[glyphCount * 4];
        Float2[] uvs = new Float2[glyphCount * 4];

        for (int g = 0; g < glyphCount; g++)
        {
            float x = g * 14f;
            int b = g * 4;
            positions[b + 0] = new Float3(x, 18f, 0);
            positions[b + 1] = new Float3(x + 12f, 18f, 0);
            positions[b + 2] = new Float3(x + 12f, -2f, 0);
            positions[b + 3] = new Float3(x, -2f, 0);

            for (int v = 0; v < 4; v++)
            {
                colors[b + v] = Color.White;
                uvs[b + v] = Float2.Zero;
            }
        }

        return (positions, colors, uvs);
    }

    [Fact]
    public void WaveEffect_ModifiesYPositions()
    {
        WaveEffect wave = new() { Amplitude = 1f, Frequency = 1f, Speed = 0f };
        TextLayout layout = CreateSimpleLayout(3);
        (Float3[] pos, Color[] col, Float2[] uv) = CreateVertexData(3);

        Float3[] original = (Float3[])pos.Clone();

        wave.Apply(pos, col, uv, layout, 1.0f);

        // At least some vertices should have moved in Y
        bool anyChanged = false;
        for (int i = 0; i < pos.Length; i++)
        {
            if (Math.Abs(pos[i].Y - original[i].Y) > 0.001f)
            {
                anyChanged = true;
                break;
            }
        }
        Assert.True(anyChanged, "WaveEffect should modify Y positions.");
    }

    [Fact]
    public void WaveEffect_Disabled_DoesNothing()
    {
        WaveEffect wave = new() { Amplitude = 1f, Enabled = false };
        TextLayout layout = CreateSimpleLayout(3);
        (Float3[] pos, Color[] col, Float2[] uv) = CreateVertexData(3);

        Float3[] original = (Float3[])pos.Clone();

        wave.Apply(pos, col, uv, layout, 1.0f);

        for (int i = 0; i < pos.Length; i++)
        {
            Assert.Equal(original[i].X, pos[i].X, 0.0001);
            Assert.Equal(original[i].Y, pos[i].Y, 0.0001);
        }
    }

    [Fact]
    public void ShakeEffect_ModifiesPositions()
    {
        ShakeEffect shake = new() { Amplitude = 1f, Speed = 10f };
        TextLayout layout = CreateSimpleLayout(3);
        (Float3[] pos, Color[] col, Float2[] uv) = CreateVertexData(3);

        Float3[] original = (Float3[])pos.Clone();

        shake.Apply(pos, col, uv, layout, 1.0f);

        bool anyChanged = false;
        for (int i = 0; i < pos.Length; i++)
        {
            if (Math.Abs(pos[i].X - original[i].X) > 0.0001f ||
                Math.Abs(pos[i].Y - original[i].Y) > 0.0001f)
            {
                anyChanged = true;
                break;
            }
        }
        Assert.True(anyChanged, "ShakeEffect should modify positions.");
    }

    [Fact]
    public void FadeInEffect_AtTimeZero_AllTransparent()
    {
        FadeInEffect fade = new() { CharacterDelay = 0.1f, FadeDuration = 0.2f, StartDelay = 0f };
        TextLayout layout = CreateSimpleLayout(3);
        (Float3[] pos, Color[] col, Float2[] uv) = CreateVertexData(3);

        // All start with alpha = 1
        fade.Apply(pos, col, uv, layout, -1.0f);

        // All characters should have alpha = 0 at negative time
        for (int i = 0; i < col.Length; i++)
        {
            Assert.Equal(0f, col[i].A, 0.001);
        }
    }

    [Fact]
    public void FadeInEffect_AfterAllRevealed_AllOpaque()
    {
        FadeInEffect fade = new() { CharacterDelay = 0.1f, FadeDuration = 0.2f, StartDelay = 0f };
        TextLayout layout = CreateSimpleLayout(3);
        (Float3[] pos, Color[] col, Float2[] uv) = CreateVertexData(3);

        // At time 10s, all chars should be fully revealed (delay 0.1*3 + fade 0.2 = 0.5s)
        fade.Apply(pos, col, uv, layout, 10.0f);

        for (int i = 0; i < col.Length; i++)
        {
            Assert.Equal(1f, col[i].A, 0.001);
        }
    }

    [Fact]
    public void TypewriterEffect_GetVisibleCount_Correct()
    {
        TypewriterEffect tw = new() { CharactersPerSecond = 10f, StartDelay = 0f };

        Assert.Equal(0, tw.GetVisibleCount(10, -1f));
        Assert.Equal(0, tw.GetVisibleCount(10, 0f));
        Assert.Equal(5, tw.GetVisibleCount(10, 0.5f));
        Assert.Equal(10, tw.GetVisibleCount(10, 1.0f));
        Assert.Equal(10, tw.GetVisibleCount(10, 2.0f)); // Clamped to total
    }

    [Fact]
    public void TypewriterEffect_HidesUnrevealedCharacters()
    {
        TypewriterEffect tw = new() { CharactersPerSecond = 10f, StartDelay = 0f };
        TextLayout layout = CreateSimpleLayout(5);
        (Float3[] pos, Color[] col, Float2[] uv) = CreateVertexData(5);

        // At 0.05s with 10 chars/sec:
        // glyph 0 reveal at 0.0s → visible (charTime=0.05)
        // glyph 1 reveal at 0.1s → hidden  (charTime=-0.05)
        // glyph 2 reveal at 0.2s → hidden  (charTime=-0.15)
        // glyph 3 reveal at 0.3s → hidden
        // glyph 4 reveal at 0.4s → hidden
        tw.Apply(pos, col, uv, layout, 0.05f);

        // First 1 glyph (4 verts) should be opaque
        for (int v = 0; v < 4; v++)
        {
            Assert.Equal(1f, col[v].A, 0.001);
        }

        // Remaining 4 glyphs (16 verts) should be transparent
        for (int v = 4; v < 20; v++)
        {
            Assert.Equal(0f, col[v].A, 0.001);
        }
    }

    [Fact]
    public void RainbowEffect_ChangesColors()
    {
        RainbowEffect rainbow = new() { Speed = 1f, CharacterOffset = 0.1f };
        TextLayout layout = CreateSimpleLayout(3);
        (Float3[] pos, Color[] col, Float2[] uv) = CreateVertexData(3);

        rainbow.Apply(pos, col, uv, layout, 1.0f);

        // Different characters should have different colors due to offset
        Color c0 = col[0]; // First vertex of glyph 0
        Color c1 = col[4]; // First vertex of glyph 1

        bool different = Math.Abs(c0.R - c1.R) > 0.01f ||
                         Math.Abs(c0.G - c1.G) > 0.01f ||
                         Math.Abs(c0.B - c1.B) > 0.01f;
        Assert.True(different, "RainbowEffect should produce different colors per character.");
    }

    [Fact]
    public void RainbowEffect_PreservesAlpha()
    {
        RainbowEffect rainbow = new();
        TextLayout layout = CreateSimpleLayout(1);
        (Float3[] pos, Color[] col, Float2[] uv) = CreateVertexData(1);

        // Set alpha to 0.5
        for (int i = 0; i < col.Length; i++)
            col[i] = new Color(1f, 1f, 1f, 0.5f);

        rainbow.Apply(pos, col, uv, layout, 1.0f);

        for (int i = 0; i < col.Length; i++)
        {
            Assert.Equal(0.5f, col[i].A, 0.01);
        }
    }

    [Fact]
    public void ScaleEffect_ModifiesPositions()
    {
        ScaleEffect scale = new() { BaseScale = 1f, Amplitude = 0.5f, Speed = 1f };
        TextLayout layout = CreateSimpleLayout(2);
        (Float3[] pos, Color[] col, Float2[] uv) = CreateVertexData(2);

        Float3[] original = (Float3[])pos.Clone();

        // At time = PI/2 / speed, sin = 1, scale = 1.5
        scale.Apply(pos, col, uv, layout, (float)(Math.PI / 2.0));

        // Vertices should have scaled from center
        bool anyChanged = false;
        for (int i = 0; i < pos.Length; i++)
        {
            if (Math.Abs(pos[i].X - original[i].X) > 0.001f ||
                Math.Abs(pos[i].Y - original[i].Y) > 0.001f)
            {
                anyChanged = true;
                break;
            }
        }
        Assert.True(anyChanged, "ScaleEffect should modify vertex positions.");
    }

    [Fact]
    public void RotateEffect_ModifiesPositions()
    {
        RotateEffect rotate = new() { AngleDegrees = 45f, Speed = 1f };
        TextLayout layout = CreateSimpleLayout(2);
        (Float3[] pos, Color[] col, Float2[] uv) = CreateVertexData(2);

        Float3[] original = (Float3[])pos.Clone();

        rotate.Apply(pos, col, uv, layout, (float)(Math.PI / 2.0));

        bool anyChanged = false;
        for (int i = 0; i < pos.Length; i++)
        {
            if (Math.Abs(pos[i].X - original[i].X) > 0.001f ||
                Math.Abs(pos[i].Y - original[i].Y) > 0.001f)
            {
                anyChanged = true;
                break;
            }
        }
        Assert.True(anyChanged, "RotateEffect should modify vertex positions.");
    }

    [Fact]
    public void RotateEffect_PreservesZ()
    {
        RotateEffect rotate = new() { AngleDegrees = 30f, Speed = 1f };
        TextLayout layout = CreateSimpleLayout(1);
        (Float3[] pos, Color[] col, Float2[] uv) = CreateVertexData(1);

        rotate.Apply(pos, col, uv, layout, 1.0f);

        // Z should stay 0 (rotation is in XY plane)
        for (int i = 0; i < pos.Length; i++)
        {
            Assert.Equal(0f, pos[i].Z, 0.001);
        }
    }
}
