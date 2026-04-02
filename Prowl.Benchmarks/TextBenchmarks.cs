// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;

using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Text;
using Prowl.Vector;

namespace Prowl.Benchmarks;

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns(Column.Error, Column.StdDev)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class TextBenchmarks : IDisposable
{
    private FontAsset _font = null!;
    private string _shortText = null!;
    private string _longText = null!;
    private string _richText = null!;
    private string _complexMarkup = null!;
    private TextLayout _prebuiltLayout;
    private Mesh? _reuseMesh;

    [GlobalSetup]
    public void Setup()
    {
        _font = CreateTestFont();

        // ~50 chars
        _shortText = "Hello, World! This is a short text sample.";

        // ~1000 chars
        _longText = string.Create(1000, 0, (span, _) =>
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ";
            for (int i = 0; i < span.Length; i++)
                span[i] = alphabet[i % alphabet.Length];
        });

        // Rich text with various tags
        _richText = "<b>Bold</b> <i>Italic</i> <color=#FF0000>Red</color> <size=24>Big</size> " +
                    "<u>Underline</u> <s>Strike</s> Normal text here. " +
                    "<b><i><color=#00FF00>Nested bold italic green</color></i></b> " +
                    "<alpha=#80>Half alpha</alpha> <mark=#FFFF0040>Highlighted</mark>";

        // Complex markup with many tags for parser stress test
        _complexMarkup = string.Empty;
        for (int i = 0; i < 50; i++)
        {
            _complexMarkup += $"<b><color=#{i * 5:X2}FF00>Word{i}</color></b> ";
            if (i % 10 == 9)
                _complexMarkup += "<br>";
        }

        // Pre-built layout for mesh builder benchmark
        _prebuiltLayout = TextShaper.Shape(
            _longText, _font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        _reuseMesh = new Mesh();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _reuseMesh?.Dispose();
        _reuseMesh = null;
        _font?.Dispose();
    }

    public void Dispose()
    {
        Cleanup();
    }

    // ── TextShaper Benchmarks ─────────────────────────────────

    [Benchmark]
    [BenchmarkCategory("TextShaper")]
    public TextLayout TextShaper_Layout_ShortText()
    {
        return TextShaper.Shape(
            _shortText, _font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);
    }

    [Benchmark]
    [BenchmarkCategory("TextShaper")]
    public TextLayout TextShaper_Layout_1000Chars()
    {
        return TextShaper.Shape(
            _longText, _font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);
    }

    [Benchmark]
    [BenchmarkCategory("TextShaper")]
    public TextLayout TextShaper_Layout_1000Chars_WordWrap()
    {
        return TextShaper.Shape(
            _longText, _font, 32f, 300f,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.WordWrap, Color.White);
    }

    [Benchmark]
    [BenchmarkCategory("TextShaper")]
    public TextLayout TextShaper_Layout_1000Chars_Justified()
    {
        return TextShaper.Shape(
            _longText, _font, 32f, 300f,
            TextAlignment.Justified, VerticalAlignment.Top,
            TextOverflowMode.WordWrap, Color.White);
    }

    // ── TextMeshBuilder Benchmarks ────────────────────────────

    [Benchmark]
    [BenchmarkCategory("TextMeshBuilder")]
    public Mesh TextMeshBuilder_Build_1000Chars()
    {
        return TextMeshBuilder.Build(_prebuiltLayout, _font, _reuseMesh);
    }

    // ── RichTextParser Benchmarks ─────────────────────────────

    [Benchmark]
    [BenchmarkCategory("RichTextParser")]
    public ParsedText RichTextParser_Parse_SimpleMarkup()
    {
        return RichTextParser.Parse(_richText);
    }

    [Benchmark]
    [BenchmarkCategory("RichTextParser")]
    public ParsedText RichTextParser_Parse_ComplexMarkup()
    {
        return RichTextParser.Parse(_complexMarkup);
    }

    [Benchmark]
    [BenchmarkCategory("RichTextParser")]
    public ParsedText RichTextParser_Parse_NoMarkup()
    {
        return RichTextParser.Parse(_longText);
    }

    // ── FontAsset Glyph Lookup Benchmark ──────────────────────

    [Benchmark]
    [BenchmarkCategory("FontAtlas")]
    public int FontAtlasLookup_100K()
    {
        int hits = 0;
        for (int i = 0; i < 100_000; i++)
        {
            uint cp = (uint)(32 + (i % 95));
            if (_font.TryGetGlyph(cp, out _))
                hits++;
        }
        return hits;
    }

    // ── Full Pipeline Benchmark ───────────────────────────────

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FullPipeline")]
    public Mesh FullPipeline_ShapeAndBuild_1000Chars()
    {
        TextLayout layout = TextShaper.Shape(
            _longText, _font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);
        return TextMeshBuilder.Build(layout, _font, _reuseMesh);
    }

    [Benchmark]
    [BenchmarkCategory("FullPipeline")]
    public Mesh FullPipeline_ParseShapeBuild_RichText()
    {
        ParsedText parsed = RichTextParser.Parse(_richText);
        TextLayout layout = TextShaper.Shape(
            parsed.StrippedText, _font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White,
            styleRuns: parsed.Runs);
        return TextMeshBuilder.Build(layout, _font, _reuseMesh);
    }

    // ── Helper ────────────────────────────────────────────────

    private static FontAsset CreateTestFont()
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();

        GlyphData[] glyphs = new GlyphData[128];
        Dictionary<uint, int> charTable = [];

        for (uint c = 32; c < 128; c++)
        {
            int idx = (int)c;
            glyphs[idx] = new GlyphData(
                GlyphIndex: c,
                Width: 12f, Height: 20f,
                BearingX: 1f, BearingY: 18f,
                Advance: 14f,
                AtlasX: (c % 16) * 20f, AtlasY: (c / 16) * 24f,
                AtlasWidth: 14f, AtlasHeight: 22f,
                Scale: 1f);
            charTable[c] = idx;
        }

        glyphs[32] = new GlyphData(32, 0, 0, 0, 0, 14f, 0, 0, 0, 0, 1f);

        font.SetMetrics(32f, 40f, 30f, -10f, 0f);
        font.SetGlyphData(glyphs, charTable, []);
        font.SetAtlas(null!, 320, 192, AtlasType.MSDF, 4f, 1);

        return font;
    }
}
