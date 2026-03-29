// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Numerics;

using ImGuiNET;

using Prowl.Scribe;
using Prowl.Vector;

namespace Prowl.ImGuiIntegration;

/// <summary>
/// <see cref="Prowl.UI.IUIRenderer"/> implementation that forwards calls to Dear ImGui.
/// </summary>
public sealed class ImGuiUIRenderer : Prowl.UI.IUIRenderer
{
    internal static readonly Stack<ImGuiLayoutMode> LayoutStack = new();
    internal static readonly Stack<int> ChildCountStack = new();
    public static readonly Dictionary<int, ImFontPtr> Fonts = new();

    /// <summary>
    /// Loads fonts at common sizes used by the Editor / Launcher UI.
    /// Must be called during ImGui initialisation, before the font atlas is built
    /// (e.g. inside the <c>onConfigureIO</c> callback of the Silk.NET ImGuiController).
    /// </summary>
    /// <param name="fontPath">Path to a TrueType font file.</param>
    /// <param name="dpiScale">Monitor DPI scale factor (1.0 = 96 DPI). Font pixel sizes are multiplied by this.</param>
    /// <param name="iconFontPath">Optional path to an icon font (e.g. Phosphor Icons) to merge into each size.</param>
    /// <param name="iconGlyphMin">First Unicode codepoint in the icon font glyph range.</param>
    /// <param name="iconGlyphMax">Last Unicode codepoint in the icon font glyph range.</param>
    public static unsafe void LoadFonts(string fontPath, float dpiScale = 1.0f,
        string? iconFontPath = null, int iconGlyphMin = 0, int iconGlyphMax = 0)
    {
        bool mergeIcons = !string.IsNullOrEmpty(iconFontPath) && iconGlyphMin > 0 && iconGlyphMax > 0;
        var io = ImGui.GetIO();
        foreach (int size in new[] { 12, 13, 14, 15, 16, 17, 19, 21 })
        {
            int scaledSize = (int)MathF.Round(size * dpiScale);
            Fonts[size] = io.Fonts.AddFontFromFileTTF(fontPath, scaledSize);

            if (mergeIcons)
            {
                ImFontConfigPtr config = ImGuiNative.ImFontConfig_ImFontConfig();
                config.MergeMode = true;
                config.PixelSnapH = true;
                config.GlyphMinAdvanceX = scaledSize;
                ushort[] ranges = [(ushort)iconGlyphMin, (ushort)iconGlyphMax, 0];
                fixed (ushort* pRanges = ranges)
                {
                    io.Fonts.AddFontFromFileTTF(iconFontPath!, scaledSize, config, (nint)pRanges);
                }
                config.Destroy();
            }
        }
    }

    /// <summary>Clears internal layout-tracking state. Call once at the start of each frame.</summary>
    public void BeginFrame()
    {
        LayoutStack.Clear();
        ChildCountStack.Clear();
    }

    public Prowl.UI.IElementBuilder Box(string id)    => new ImGuiElementBuilder(id, ImGuiLayoutMode.Box);
    public Prowl.UI.IElementBuilder Row(string id)    => new ImGuiElementBuilder(id, ImGuiLayoutMode.Row);
    public Prowl.UI.IElementBuilder Column(string id) => new ImGuiElementBuilder(id, ImGuiLayoutMode.Column);

    public bool IsPointerOverRect(float x, float y, float w, float h)
    {
        var mp = ImGui.GetIO().MousePos;
        return mp.X >= x && mp.X < x + w && mp.Y >= y && mp.Y < y + h;
    }
}

// ─────────────────────────────────────────────────────────────────────────────

internal enum ImGuiLayoutMode { Box, Row, Column }

// ─────────────────────────────────────────────────────────────────────────────

internal sealed class ImGuiElementBuilder : Prowl.UI.IElementBuilder
{
    private readonly string _id;
    private readonly ImGuiLayoutMode _mode;

    // Size
    private float _width;
    private float _height;
    private bool  _widthStretch;
    private bool  _heightStretch;
    private float _widthFactor  = 1f;
    private float _heightFactor = 1f;

    // Position
    private Prowl.UI.UIPositionType _posType = Prowl.UI.UIPositionType.ParentDirected;
    private float _left, _top;

    // Visuals
    private Color? _bgColor;
    private Color? _hoverBgColor;
    private float  _rounding;

    // Text
    private string? _text;
    private float   _fontSize;
    private Color?  _textColor;

    // Interaction
    private Action?       _onClick;
    private Action<Rect>? _onPostLayout;

    // Padding / spacing
    private float _childLeft, _childTop;
    private float _rowBetween, _colBetween;
    private bool  _paddingSet;
    private bool  _spacingSet;

    internal ImGuiElementBuilder(string id, ImGuiLayoutMode mode)
    {
        _id   = id;
        _mode = mode;
    }

    // ── Dimensions ─────────────────────────────────────────────────

    public Prowl.UI.IElementBuilder Width(Prowl.UI.UIValue value)
    {
        if (value.Kind == Prowl.UI.UIValueKind.Stretch) { _widthStretch = true;  _widthFactor = value.Value; }
        else                                            { _width = value.Value;  _widthStretch = false; }
        return this;
    }

    public Prowl.UI.IElementBuilder Height(Prowl.UI.UIValue value)
    {
        if (value.Kind == Prowl.UI.UIValueKind.Stretch) { _heightStretch = true;  _heightFactor = value.Value; }
        else                                            { _height = value.Value;  _heightStretch = false; }
        return this;
    }

    // ── Position ───────────────────────────────────────────────────

    public Prowl.UI.IElementBuilder PositionType(Prowl.UI.UIPositionType type) { _posType = type; return this; }
    public Prowl.UI.IElementBuilder Left(Prowl.UI.UIValue value)   { _left = value.Value;  return this; }
    public Prowl.UI.IElementBuilder Top(Prowl.UI.UIValue value)    { _top  = value.Value;  return this; }
    public Prowl.UI.IElementBuilder Right(Prowl.UI.UIValue value)  { return this; }
    public Prowl.UI.IElementBuilder Bottom(Prowl.UI.UIValue value) { return this; }

    // ── Child alignment / spacing ──────────────────────────────────

    public Prowl.UI.IElementBuilder ChildLeft(Prowl.UI.UIValue value)   { _childLeft = value.Value; _paddingSet = true; return this; }
    public Prowl.UI.IElementBuilder ChildRight(Prowl.UI.UIValue value)  { _paddingSet = true; return this; }
    public Prowl.UI.IElementBuilder ChildTop(Prowl.UI.UIValue value)    { _childTop = value.Value;  _paddingSet = true; return this; }
    public Prowl.UI.IElementBuilder ChildBottom(Prowl.UI.UIValue value) { _paddingSet = true; return this; }
    public Prowl.UI.IElementBuilder RowBetween(Prowl.UI.UIValue value)  { _rowBetween = value.Value; _spacingSet = true; return this; }
    public Prowl.UI.IElementBuilder ColBetween(Prowl.UI.UIValue value)  { _colBetween = value.Value; _spacingSet = true; return this; }

    // ── Visual styling ─────────────────────────────────────────────

    public Prowl.UI.IElementBuilder BackgroundColor(Color color) { _bgColor  = color;  return this; }
    public Prowl.UI.IElementBuilder Rounded(float radius)        { _rounding = radius; return this; }

    // ── Text ───────────────────────────────────────────────────────

    public Prowl.UI.IElementBuilder Text(string text, FontFile font) { _text = text; return this; }
    public Prowl.UI.IElementBuilder FontSize(float size)             { _fontSize = size; return this; }
    public Prowl.UI.IElementBuilder TextColor(Color color)           { _textColor = color; return this; }

    // ── Interaction ────────────────────────────────────────────────

    public Prowl.UI.IElementBuilder OnClick(Action handler)           { _onClick = handler;       return this; }
    public Prowl.UI.IElementBuilder OnPostLayout(Action<Rect> handler) { _onPostLayout = handler; return this; }

    // ── Hover ──────────────────────────────────────────────────────

    public Prowl.UI.IHoverBuilder Hovered => new ImGuiHoverBuilder(this);
    internal void SetHoverBg(Color c) => _hoverBgColor = c;

    // ── Lifecycle ──────────────────────────────────────────────────

    public IDisposable Enter()
    {
        int colorsPushed = 0;
        int varsPushed   = 0;
        bool fontPushed  = false;
        bool usedWindow  = false;

        // Resolve sizes
        var avail = ImGui.GetContentRegionAvail();
        float w = _widthStretch  ? avail.X * _widthFactor  : _width;
        float h = _heightStretch ? avail.Y * _heightFactor : _height;
        if (w < 0) w = 0;
        if (h < 0) h = 0;

        // Row parent ⇒ insert SameLine before siblings after the first
        if (ImGuiUIRenderer.LayoutStack.Count > 0 &&
            ImGuiUIRenderer.LayoutStack.Peek() == ImGuiLayoutMode.Row)
        {
            int idx = ImGuiUIRenderer.ChildCountStack.Pop();
            if (idx > 0) ImGui.SameLine();
            ImGuiUIRenderer.ChildCountStack.Push(idx + 1);
        }
        else if (ImGuiUIRenderer.ChildCountStack.Count > 0)
        {
            int idx = ImGuiUIRenderer.ChildCountStack.Pop();
            ImGuiUIRenderer.ChildCountStack.Push(idx + 1);
        }

        // Hover detection (before pushing styles)
        bool hovered = false;
        if (_hoverBgColor.HasValue && _posType != Prowl.UI.UIPositionType.SelfDirected)
        {
            var sp = ImGui.GetCursorScreenPos();
            hovered = ImGui.IsMouseHoveringRect(sp, new Vector2(sp.X + w, sp.Y + h));
        }

        Color? effectiveBg = hovered && _hoverBgColor.HasValue ? _hoverBgColor : _bgColor;

        // ── Push styles ────────────────────────────────────────────

        if (effectiveBg.HasValue)
        {
            var c   = effectiveBg.Value;
            var col = _posType == Prowl.UI.UIPositionType.SelfDirected ? ImGuiCol.WindowBg : ImGuiCol.ChildBg;
            ImGui.PushStyleColor(col, new Vector4(c.R, c.G, c.B, c.A));
            colorsPushed++;
        }

        if (_rounding > 0)
        {
            ImGui.PushStyleVar(
                _posType == Prowl.UI.UIPositionType.SelfDirected
                    ? ImGuiStyleVar.WindowRounding
                    : ImGuiStyleVar.ChildRounding,
                _rounding);
            varsPushed++;
        }

        if (_paddingSet)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(_childLeft, _childTop));
            varsPushed++;
        }

        if (_spacingSet)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(_colBetween, _rowBetween));
            varsPushed++;
        }

        // ── Begin element ──────────────────────────────────────────

        if (_posType == Prowl.UI.UIPositionType.SelfDirected)
        {
            ImGui.SetNextWindowPos(new Vector2(_left, _top));
            ImGui.SetNextWindowSize(new Vector2(w, h));
            ImGui.Begin("##" + _id,
                ImGuiWindowFlags.NoTitleBar  | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoMove      | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoBringToFrontOnFocus |
                ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoCollapse  | ImGuiWindowFlags.NoDecoration);
            usedWindow = true;
        }
        else
        {
            ImGui.BeginChild(_id, new Vector2(w, h),
                ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar);
        }

        // ── Font ───────────────────────────────────────────────────

        if (_fontSize > 0)
        {
            int rounded = (int)MathF.Round(_fontSize);
            if (ImGuiUIRenderer.Fonts.TryGetValue(rounded, out var font))
            {
                ImGui.PushFont(font);
                fontPushed = true;
            }
        }

        // ── Inline text ────────────────────────────────────────────

        if (_text != null)
        {
            if (_textColor.HasValue)
            {
                var tc = _textColor.Value;
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(tc.R, tc.G, tc.B, tc.A));
            }

            ImGui.TextUnformatted(_text);

            if (_textColor.HasValue)
                ImGui.PopStyleColor();
        }

        // ── Track layout mode ──────────────────────────────────────

        ImGuiUIRenderer.LayoutStack.Push(_mode);
        ImGuiUIRenderer.ChildCountStack.Push(0);

        return new ImGuiElementScope(usedWindow, colorsPushed, varsPushed,
            fontPushed, _onClick, _onPostLayout);
    }
}

// ─────────────────────────────────────────────────────────────────────────────

internal sealed class ImGuiHoverBuilder : Prowl.UI.IHoverBuilder
{
    private readonly ImGuiElementBuilder _parent;
    internal ImGuiHoverBuilder(ImGuiElementBuilder parent) => _parent = parent;

    public Prowl.UI.IHoverBuilder BackgroundColor(Color color) { _parent.SetHoverBg(color); return this; }
    public Prowl.UI.IElementBuilder End() => _parent;
}

// ─────────────────────────────────────────────────────────────────────────────

internal sealed class ImGuiElementScope : IDisposable
{
    private readonly bool          _usedWindow;
    private readonly int           _colorsPushed;
    private readonly int           _varsPushed;
    private readonly bool          _fontPushed;
    private readonly Action?       _onClick;
    private readonly Action<Rect>? _onPostLayout;

    internal ImGuiElementScope(
        bool usedWindow, int colorsPushed, int varsPushed,
        bool fontPushed, Action? onClick, Action<Rect>? onPostLayout)
    {
        _usedWindow    = usedWindow;
        _colorsPushed  = colorsPushed;
        _varsPushed    = varsPushed;
        _fontPushed    = fontPushed;
        _onClick       = onClick;
        _onPostLayout  = onPostLayout;
    }

    public void Dispose()
    {
        // Pop layout tracking
        ImGuiUIRenderer.LayoutStack.Pop();
        ImGuiUIRenderer.ChildCountStack.Pop();

        // Capture layout rect before closing the scope
        var pos  = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();

        // Detect click while still inside the scope
        bool clicked = _onClick != null &&
            ImGui.IsWindowHovered(ImGuiHoveredFlags.None) &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left);

        if (_fontPushed) ImGui.PopFont();

        // Close the element
        if (_usedWindow) ImGui.End();
        else             ImGui.EndChild();

        // For child elements, also check the more reliable IsItemClicked
        if (!_usedWindow && _onClick != null && ImGui.IsItemClicked())
            clicked = true;

        if (clicked)
            _onClick?.Invoke();

        // Post-layout callback (Rect stores min / max corners)
        _onPostLayout?.Invoke(
            new Rect(pos.X, pos.Y, pos.X + size.X, pos.Y + size.Y));

        // Pop styles (must happen AFTER EndChild / End)
        if (_colorsPushed > 0) ImGui.PopStyleColor(_colorsPushed);
        if (_varsPushed   > 0) ImGui.PopStyleVar(_varsPushed);
    }
}
