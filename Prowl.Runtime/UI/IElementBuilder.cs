// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using Prowl.Scribe;
using Prowl.Vector;

namespace Prowl.UI;

/// <summary>
/// Fluent builder for a single UI element. Mirrors the styling and layout
/// API of the underlying toolkit (Paper, Dear ImGui, etc.) without exposing
/// backend-specific types.
/// </summary>
public interface IElementBuilder
{
    // ── Dimensions ─────────────────────────────────────────────────
    IElementBuilder Width(UIValue value);
    IElementBuilder Height(UIValue value);

    // ── Position ───────────────────────────────────────────────────
    IElementBuilder PositionType(UIPositionType type);
    IElementBuilder Left(UIValue value);
    IElementBuilder Top(UIValue value);
    IElementBuilder Right(UIValue value);
    IElementBuilder Bottom(UIValue value);

    // ── Child alignment / spacing ──────────────────────────────────
    IElementBuilder ChildLeft(UIValue value);
    IElementBuilder ChildRight(UIValue value);
    IElementBuilder ChildTop(UIValue value);
    IElementBuilder ChildBottom(UIValue value);
    IElementBuilder RowBetween(UIValue value);
    IElementBuilder ColBetween(UIValue value);

    // ── Visual styling ─────────────────────────────────────────────
    IElementBuilder BackgroundColor(Color color);
    IElementBuilder Rounded(float radius);

    // ── Text ───────────────────────────────────────────────────────
    IElementBuilder Text(string text, FontFile font);
    IElementBuilder FontSize(float size);
    IElementBuilder TextColor(Color color);

    // ── Interaction ────────────────────────────────────────────────
    IElementBuilder OnClick(Action handler);
    IElementBuilder OnPostLayout(Action<Rect> handler);

    // ── State-driven styles ────────────────────────────────────────
    IHoverBuilder Hovered { get; }

    // ── Lifecycle ──────────────────────────────────────────────────
    /// <summary>
    /// Opens the element scope. The returned <see cref="IDisposable"/>
    /// must be disposed (typically via <c>using</c>) to close the element.
    /// </summary>
    IDisposable Enter();
}
