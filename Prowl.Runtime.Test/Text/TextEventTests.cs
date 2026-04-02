// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Text;
using Prowl.Vector;

using Xunit;

namespace Prowl.Runtime.Test;

/// <summary>
/// Tests for <see cref="TextEvents"/> — verifies OnFontAtlasChanged, OnGlyphMissing,
/// OnTextMeshRebuilt, and OnTextLinkInteraction fire/subscribe/unsubscribe contracts.
/// </summary>
public class TextEventTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable d in _disposables)
            d.Dispose();
        _disposables.Clear();
    }

    private FontAsset CreateTestFont()
    {
        FontAsset font = ScriptableObject.CreateInstance<FontAsset>();
        _disposables.Add(font);

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

    #region OnFontAtlasChanged

    [Fact]
    public void OnFontAtlasChanged_SubscribeAndInvoke_ReceivesArgs()
    {
        FontAsset font = CreateTestFont();
        FontAtlasChangedArgs? received = null;

        IDisposable sub = TextEvents.SubscribeOnFontAtlasChanged(args => received = args);
        _disposables.Add(sub);

        uint[] codepoints = [65, 66, 67];
        TextEvents.InvokeOnFontAtlasChanged(new FontAtlasChangedArgs(font, codepoints));

        Assert.NotNull(received);
        Assert.Equal(font, received!.Value.FontAsset);
        Assert.Equal(codepoints, received.Value.AddedCodepoints);
    }

    [Fact]
    public void OnFontAtlasChanged_Dispose_StopsReceiving()
    {
        int callCount = 0;
        IDisposable sub = TextEvents.SubscribeOnFontAtlasChanged(_ => callCount++);

        FontAsset font = CreateTestFont();
        TextEvents.InvokeOnFontAtlasChanged(new FontAtlasChangedArgs(font, []));
        Assert.Equal(1, callCount);

        sub.Dispose();

        TextEvents.InvokeOnFontAtlasChanged(new FontAtlasChangedArgs(font, []));
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void OnFontAtlasChanged_MultipleSubscribers_AllReceive()
    {
        int count1 = 0;
        int count2 = 0;
        IDisposable sub1 = TextEvents.SubscribeOnFontAtlasChanged(_ => count1++);
        IDisposable sub2 = TextEvents.SubscribeOnFontAtlasChanged(_ => count2++);
        _disposables.Add(sub1);
        _disposables.Add(sub2);

        FontAsset font = CreateTestFont();
        TextEvents.InvokeOnFontAtlasChanged(new FontAtlasChangedArgs(font, []));

        Assert.Equal(1, count1);
        Assert.Equal(1, count2);
    }

    #endregion

    #region OnGlyphMissing

    [Fact]
    public void OnGlyphMissing_SubscribeAndInvoke_ReceivesArgs()
    {
        FontAsset font = CreateTestFont();
        GlyphMissingArgs? received = null;

        IDisposable sub = TextEvents.SubscribeOnGlyphMissing(args => received = args);
        _disposables.Add(sub);

        TextEvents.InvokeOnGlyphMissing(new GlyphMissingArgs(0x4E2D, font, "中文测试"));

        Assert.NotNull(received);
        Assert.Equal(0x4E2Du, received!.Value.Codepoint);
        Assert.Equal(font, received.Value.FontAsset);
        Assert.Equal("中文测试", received.Value.SourceText);
    }

    [Fact]
    public void OnGlyphMissing_Dispose_StopsReceiving()
    {
        int callCount = 0;
        IDisposable sub = TextEvents.SubscribeOnGlyphMissing(_ => callCount++);

        FontAsset font = CreateTestFont();
        TextEvents.InvokeOnGlyphMissing(new GlyphMissingArgs(0x1234, font, "test"));
        Assert.Equal(1, callCount);

        sub.Dispose();

        TextEvents.InvokeOnGlyphMissing(new GlyphMissingArgs(0x1234, font, "test"));
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void OnGlyphMissing_Priority_LowerRunsFirst()
    {
        List<int> order = [];
        IDisposable sub1 = TextEvents.SubscribeOnGlyphMissing(_ => order.Add(2), priority: 2);
        IDisposable sub2 = TextEvents.SubscribeOnGlyphMissing(_ => order.Add(0), priority: 0);
        IDisposable sub3 = TextEvents.SubscribeOnGlyphMissing(_ => order.Add(1), priority: 1);
        _disposables.Add(sub1);
        _disposables.Add(sub2);
        _disposables.Add(sub3);

        FontAsset font = CreateTestFont();
        TextEvents.InvokeOnGlyphMissing(new GlyphMissingArgs(0x1234, font, "test"));

        Assert.Equal([0, 1, 2], order);
    }

    [Fact]
    public void OnGlyphMissing_FiredByTextShaper_ForUnknownCodepoint()
    {
        // Create a font with only ASCII glyphs (32-127)
        FontAsset font = CreateTestFont();
        GlyphMissingArgs? received = null;

        IDisposable sub = TextEvents.SubscribeOnGlyphMissing(args => received = args);
        _disposables.Add(sub);

        // Shape a string containing a codepoint not in our test font (non-ASCII)
        // Character 0x00 (NUL) won't be in our char table
        string testText = "\x01"; // SOH control char — not in 32-127 range
        TextShaper.Shape(
            testText, font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        Assert.NotNull(received);
        Assert.Equal(1u, received!.Value.Codepoint);
        Assert.Equal(font, received.Value.FontAsset);
        Assert.Equal(testText, received.Value.SourceText);
    }

    [Fact]
    public void OnGlyphMissing_Deduplicated_SameCodepointOnlyFiresOnce()
    {
        FontAsset font = CreateTestFont();
        int callCount = 0;

        IDisposable sub = TextEvents.SubscribeOnGlyphMissing(_ => callCount++);
        _disposables.Add(sub);

        // Shape text with repeated missing codepoints
        string testText = "\x01\x01\x01";
        TextShaper.Shape(
            testText, font, 32f, float.MaxValue,
            TextAlignment.Left, VerticalAlignment.Top,
            TextOverflowMode.Overflow, Color.White);

        // Should only fire once due to deduplication in TextShaper
        Assert.Equal(1, callCount);
    }

    #endregion

    #region OnTextMeshRebuilt

    [Fact]
    public void OnTextMeshRebuilt_SubscribeAndInvoke_ReceivesArgs()
    {
        TextMeshRebuiltArgs? received = null;

        IDisposable sub = TextEvents.SubscribeOnTextMeshRebuilt(args => received = args);
        _disposables.Add(sub);

        AABB bounds = new AABB(Float3.Zero, new Float3(10f, 5f, 0f));
        TextEvents.InvokeOnTextMeshRebuilt(new TextMeshRebuiltArgs(null!, bounds, 42));

        Assert.NotNull(received);
        Assert.Equal(42, received!.Value.CharacterCount);
    }

    [Fact]
    public void OnTextMeshRebuilt_Dispose_StopsReceiving()
    {
        int callCount = 0;
        IDisposable sub = TextEvents.SubscribeOnTextMeshRebuilt(_ => callCount++);

        AABB bounds = default;
        TextEvents.InvokeOnTextMeshRebuilt(new TextMeshRebuiltArgs(null!, bounds, 0));
        Assert.Equal(1, callCount);

        sub.Dispose();

        TextEvents.InvokeOnTextMeshRebuilt(new TextMeshRebuiltArgs(null!, bounds, 0));
        Assert.Equal(1, callCount);
    }

    #endregion

    #region OnTextLinkInteraction

    [Fact]
    public void OnTextLinkInteraction_SubscribeAndInvoke_ReceivesArgs()
    {
        TextLinkInteractionArgs? received = null;

        IDisposable sub = TextEvents.SubscribeOnTextLinkInteraction(args => received = args);
        _disposables.Add(sub);

        TextEvents.InvokeOnTextLinkInteraction(new TextLinkInteractionArgs(
            "my-link", TextLinkInteraction.Click, 5));

        Assert.NotNull(received);
        Assert.Equal("my-link", received!.Value.LinkId);
        Assert.Equal(TextLinkInteraction.Click, received.Value.InteractionType);
        Assert.Equal(5, received.Value.CharacterIndex);
    }

    [Fact]
    public void OnTextLinkInteraction_AllInteractionTypes_Delivered()
    {
        List<TextLinkInteraction> interactions = [];

        IDisposable sub = TextEvents.SubscribeOnTextLinkInteraction(args =>
            interactions.Add(args.InteractionType));
        _disposables.Add(sub);

        TextEvents.InvokeOnTextLinkInteraction(new TextLinkInteractionArgs("link", TextLinkInteraction.HoverEnter, 0));
        TextEvents.InvokeOnTextLinkInteraction(new TextLinkInteractionArgs("link", TextLinkInteraction.Click, 0));
        TextEvents.InvokeOnTextLinkInteraction(new TextLinkInteractionArgs("link", TextLinkInteraction.HoverExit, 0));

        Assert.Equal(3, interactions.Count);
        Assert.Equal(TextLinkInteraction.HoverEnter, interactions[0]);
        Assert.Equal(TextLinkInteraction.Click, interactions[1]);
        Assert.Equal(TextLinkInteraction.HoverExit, interactions[2]);
    }

    #endregion

    #region IDisposable Subscription Cleanup

    [Fact]
    public void Subscription_DisposePreventsLeak_FontAtlasChanged()
    {
        int callCount = 0;

        // Create and immediately dispose — simulates component OnDisable
        IDisposable sub = TextEvents.SubscribeOnFontAtlasChanged(_ => callCount++);
        sub.Dispose();

        FontAsset font = CreateTestFont();
        TextEvents.InvokeOnFontAtlasChanged(new FontAtlasChangedArgs(font, []));

        Assert.Equal(0, callCount);
    }

    [Fact]
    public void Subscription_DisposePreventsLeak_GlyphMissing()
    {
        int callCount = 0;

        IDisposable sub = TextEvents.SubscribeOnGlyphMissing(_ => callCount++);
        sub.Dispose();

        FontAsset font = CreateTestFont();
        TextEvents.InvokeOnGlyphMissing(new GlyphMissingArgs(0x1234, font, "test"));

        Assert.Equal(0, callCount);
    }

    [Fact]
    public void Subscription_DisposePreventsLeak_TextMeshRebuilt()
    {
        int callCount = 0;

        IDisposable sub = TextEvents.SubscribeOnTextMeshRebuilt(_ => callCount++);
        sub.Dispose();

        TextEvents.InvokeOnTextMeshRebuilt(new TextMeshRebuiltArgs(null!, default, 0));

        Assert.Equal(0, callCount);
    }

    [Fact]
    public void Subscription_DisposePreventsLeak_TextLinkInteraction()
    {
        int callCount = 0;

        IDisposable sub = TextEvents.SubscribeOnTextLinkInteraction(_ => callCount++);
        sub.Dispose();

        TextEvents.InvokeOnTextLinkInteraction(new TextLinkInteractionArgs("link", TextLinkInteraction.Click, 0));

        Assert.Equal(0, callCount);
    }

    [Fact]
    public void Subscription_DoubleDispose_DoesNotThrow()
    {
        IDisposable sub = TextEvents.SubscribeOnFontAtlasChanged(_ => { });

        Exception? ex = Record.Exception(() =>
        {
            sub.Dispose();
            sub.Dispose();
        });

        Assert.Null(ex);
    }

    #endregion

    #region PlusEquals and MinusEquals Subscription

    [Fact]
    public void PlusEquals_Subscribe_ReceivesEvents()
    {
        GlyphMissingArgs? received = null;
        Action<GlyphMissingArgs> handler = args => received = args;

        TextEvents.OnGlyphMissing += handler;

        FontAsset font = CreateTestFont();
        TextEvents.InvokeOnGlyphMissing(new GlyphMissingArgs(0x1234, font, "test"));

        Assert.NotNull(received);

        // Cleanup
        TextEvents.OnGlyphMissing -= handler;
    }

    [Fact]
    public void MinusEquals_Unsubscribe_StopsReceiving()
    {
        int callCount = 0;
        Action<GlyphMissingArgs> handler = _ => callCount++;

        TextEvents.OnGlyphMissing += handler;

        FontAsset font = CreateTestFont();
        TextEvents.InvokeOnGlyphMissing(new GlyphMissingArgs(0x1234, font, "test"));
        Assert.Equal(1, callCount);

        TextEvents.OnGlyphMissing -= handler;

        TextEvents.InvokeOnGlyphMissing(new GlyphMissingArgs(0x1234, font, "test"));
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void OnGlyphMissing_NegativePriority_ProcessesBeforeDefault()
    {
        List<string> order = [];
        FontAsset font = CreateTestFont();

        // Default priority handler (0)
        IDisposable defaultSub = TextEvents.SubscribeOnGlyphMissing(_ => order.Add("default"), priority: 0);
        // High-priority handler (-10), simulating DynamicFontAtlas
        IDisposable prioritySub = TextEvents.SubscribeOnGlyphMissing(_ => order.Add("priority"), priority: -10);

        TextEvents.InvokeOnGlyphMissing(new GlyphMissingArgs(0xABCD, font, "test"));

        Assert.Equal(2, order.Count);
        Assert.Equal("priority", order[0]);
        Assert.Equal("default", order[1]);

        prioritySub.Dispose();
        defaultSub.Dispose();
    }

    #endregion
}
