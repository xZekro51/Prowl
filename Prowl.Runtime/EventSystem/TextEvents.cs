// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime.Resources;
using Prowl.Vector;

using Prowl.EventSystem;

namespace Prowl.Runtime.EventSystem;

/// <summary>
/// Events raised by the text rendering subsystem.
/// Subscribe via <c>TextEvents.SubscribeOnXxx(...)</c>.
/// </summary>
[EventDomain]
public static partial class TextEvents
{
    /// <summary>
    /// Raised after a <see cref="FontAsset"/>'s atlas has been rebuilt or expanded
    /// (e.g., dynamic atlas added new glyphs). All TextRenderers using this font
    /// should mark their meshes dirty.
    /// </summary>
    [EventArgs(typeof(FontAtlasChangedArgs))]
    private static readonly EventKey _OnFontAtlasChanged = new();

    /// <summary>
    /// Raised when a codepoint is requested but not found in any font in the
    /// fallback chain. Editor tooling can listen to this to surface missing
    /// glyph warnings in the console.
    /// </summary>
    [EventArgs(typeof(GlyphMissingArgs))]
    private static readonly EventKey _OnGlyphMissing = new();

    /// <summary>
    /// Raised after a TextRenderer's mesh has been rebuilt.
    /// Useful for editor inspectors that need to refresh a live preview, and
    /// for gameplay systems that react to text bounds changes (e.g., dialogue
    /// bubble sizing).
    /// </summary>
    [EventArgs(typeof(TextMeshRebuiltArgs))]
    private static readonly EventKey _OnTextMeshRebuilt = new();

    /// <summary>
    /// Raised when a <c>&lt;link="id"&gt;</c> region is clicked or hovered
    /// (input field / UI integration). Gameplay code subscribes to implement
    /// hyperlink behavior.
    /// </summary>
    [EventArgs(typeof(TextLinkInteractionArgs))]
    private static readonly EventKey _OnTextLinkInteraction = new();
}

/// <param name="FontAsset">The font whose atlas changed.</param>
/// <param name="AddedCodepoints">Codepoints that were newly added (empty for full rebuild).</param>
public readonly record struct FontAtlasChangedArgs(
    FontAsset FontAsset,
    uint[] AddedCodepoints);

/// <param name="Codepoint">The Unicode codepoint that could not be resolved.</param>
/// <param name="FontAsset">The primary font that was searched.</param>
/// <param name="SourceText">The full text string containing the missing codepoint (for diagnostics).</param>
public readonly record struct GlyphMissingArgs(
    uint Codepoint,
    FontAsset FontAsset,
    string SourceText);

/// <param name="Renderer">The MonoBehaviour whose mesh was rebuilt.</param>
/// <param name="TextBounds">The new bounding box of the rendered text.</param>
/// <param name="CharacterCount">Number of visible characters after layout.</param>
public readonly record struct TextMeshRebuiltArgs(
    MonoBehaviour Renderer,
    AABB TextBounds,
    int CharacterCount);

/// <param name="LinkId">The link ID from <c>&lt;link="id"&gt;</c>.</param>
/// <param name="InteractionType">Click, hover enter, hover exit.</param>
/// <param name="CharacterIndex">Start character index of the link region.</param>
public readonly record struct TextLinkInteractionArgs(
    string LinkId,
    TextLinkInteraction InteractionType,
    int CharacterIndex);

/// <summary>Type of interaction with a text link region.</summary>
public enum TextLinkInteraction
{
    Click,
    HoverEnter,
    HoverExit
}
