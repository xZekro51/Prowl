// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using Prowl.PaperUI;
using Prowl.PaperUI.LayoutEngine;
using Prowl.Scribe;
using Prowl.Vector;

namespace Prowl.UI;

/// <summary>
/// <see cref="IUIRenderer"/> implementation that forwards every call to
/// <see cref="Paper"/> (Prowl.Paper NuGet package).
/// </summary>
public sealed class PaperUIRenderer : IUIRenderer
{
    private readonly Paper _paper;

    public PaperUIRenderer(Paper paper) => _paper = paper;

    public IElementBuilder Box(string id) => new PaperElementBuilder(_paper.Box(id));
    public IElementBuilder Row(string id) => new PaperElementBuilder(_paper.Row(id));
    public IElementBuilder Column(string id) => new PaperElementBuilder(_paper.Column(id));

    public bool IsPointerOverRect(float x, float y, float width, float height)
        => _paper.IsPointerOverRect(x, y, width, height);
}

/// <summary>
/// Conversion helpers between UI abstraction types and Paper types.
/// Placed outside <see cref="PaperElementBuilder"/> to avoid method-name shadowing.
/// </summary>
internal static class PaperConvert
{
    internal static UnitValue ToUnit(UIValue v) => v.Kind switch
    {
        UIValueKind.Stretch => UnitValue.Stretch(v.Value),
        _ => v.Value // implicit float → UnitValue (pixels)
    };

    internal static Prowl.PaperUI.PositionType ToPos(UIPositionType t) => t switch
    {
        UIPositionType.SelfDirected => Prowl.PaperUI.PositionType.SelfDirected,
        _ => Prowl.PaperUI.PositionType.ParentDirected
    };
}

/// <summary>
/// Wraps <see cref="ElementBuilder"/> behind <see cref="IElementBuilder"/>.
/// </summary>
internal sealed class PaperElementBuilder : IElementBuilder
{
    internal ElementBuilder Inner;

    internal PaperElementBuilder(ElementBuilder inner) => Inner = inner;

    // ── Dimensions ─────────────────────────────────────────────────

    public IElementBuilder Width(UIValue value)  { var u = PaperConvert.ToUnit(value); Inner = Inner.Width(u);  return this; }
    public IElementBuilder Height(UIValue value) { var u = PaperConvert.ToUnit(value); Inner = Inner.Height(u); return this; }

    // ── Position ───────────────────────────────────────────────────

    public IElementBuilder PositionType(UIPositionType type) { Inner = Inner.PositionType(PaperConvert.ToPos(type)); return this; }
    public IElementBuilder Left(UIValue value)   { var u = PaperConvert.ToUnit(value); Inner = Inner.Left(u);   return this; }
    public IElementBuilder Top(UIValue value)    { var u = PaperConvert.ToUnit(value); Inner = Inner.Top(u);    return this; }
    public IElementBuilder Right(UIValue value)  { var u = PaperConvert.ToUnit(value); Inner = Inner.Right(u);  return this; }
    public IElementBuilder Bottom(UIValue value) { var u = PaperConvert.ToUnit(value); Inner = Inner.Bottom(u); return this; }

    // ── Child alignment / spacing ──────────────────────────────────

    public IElementBuilder ChildLeft(UIValue value)   { var u = PaperConvert.ToUnit(value); Inner = Inner.ChildLeft(u);   return this; }
    public IElementBuilder ChildRight(UIValue value)  { var u = PaperConvert.ToUnit(value); Inner = Inner.ChildRight(u);  return this; }
    public IElementBuilder ChildTop(UIValue value)    { var u = PaperConvert.ToUnit(value); Inner = Inner.ChildTop(u);    return this; }
    public IElementBuilder ChildBottom(UIValue value) { var u = PaperConvert.ToUnit(value); Inner = Inner.ChildBottom(u); return this; }
    public IElementBuilder RowBetween(UIValue value)  { var u = PaperConvert.ToUnit(value); Inner = Inner.RowBetween(u);  return this; }
    public IElementBuilder ColBetween(UIValue value)  { var u = PaperConvert.ToUnit(value); Inner = Inner.ColBetween(u);  return this; }

    // ── Visual styling ─────────────────────────────────────────────

    public IElementBuilder BackgroundColor(Color color) { Inner = Inner.BackgroundColor(color); return this; }
    public IElementBuilder Rounded(float radius)        { Inner = Inner.Rounded(radius);        return this; }

    // ── Text ───────────────────────────────────────────────────────

    public IElementBuilder Text(string text, FontFile font) { Inner = Inner.Text(text, font); return this; }
    public IElementBuilder FontSize(float size)             { Inner = Inner.FontSize(size);    return this; }
    public IElementBuilder TextColor(Color color)           { Inner = Inner.TextColor(color);  return this; }

    // ── Interaction ────────────────────────────────────────────────

    public IElementBuilder OnClick(Action handler)           { Inner = Inner.OnClick(_ => handler()); return this; }
    public IElementBuilder OnPostLayout(Action<Rect> handler) { Inner = Inner.OnPostLayout((_, rect) => handler(rect)); return this; }

    // ── Hover ──────────────────────────────────────────────────────

    public IHoverBuilder Hovered => new PaperHoverBuilder(Inner.Hovered, this);

    // ── Lifecycle ──────────────────────────────────────────────────

    public IDisposable Enter() => Inner.Enter();
}

/// <summary>
/// Wraps <see cref="StateDrivenStyle"/> behind <see cref="IHoverBuilder"/>.
/// </summary>
internal sealed class PaperHoverBuilder : IHoverBuilder
{
    private StateDrivenStyle _style;
    private readonly PaperElementBuilder _parent;

    internal PaperHoverBuilder(StateDrivenStyle style, PaperElementBuilder parent)
    {
        _style = style;
        _parent = parent;
    }

    public IHoverBuilder BackgroundColor(Color color)
    {
        _style = _style.BackgroundColor(color);
        return this;
    }

    public IElementBuilder End()
    {
        _parent.Inner = _style.End();
        return _parent;
    }
}
