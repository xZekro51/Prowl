// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace Prowl.Editor.Icons;

/// <summary>
/// Unicode codepoints for Phosphor Icons (Regular weight).
/// These map to glyphs in the embedded <c>Phosphor.ttf</c> font file.
/// See https://phosphoricons.com/ for the full catalogue.
/// </summary>
public static class PhosphorIcons
{
    // ── Core objects ──
    public const string Cube                    = "\ue1da";
    public const string Camera                  = "\ue10e";
    public const string Sun                     = "\ue472";
    public const string Lightbulb               = "\ue2dc";
    public const string Flashlight              = "\ue246";
    public const string Folder                  = "\ue24a";
    public const string FolderOpen              = "\ue256";

    // ── Asset types ──
    public const string FileCode                = "\ue914";  // Script
    public const string Palette                 = "\ue6c8";  // Material
    public const string Image                   = "\ue2ca";  // Texture
    public const string GlobeHemisphereWest     = "\ue28c";  // Scene
    public const string Package                 = "\ue390";  // Mesh / Prefab
    public const string SpeakerHigh             = "\ue44a";  // Audio
    public const string File                    = "\ue230";  // Generic file
    public const string Code                    = "\ue1bc";  // Shader
    public const string FilmStrip               = "\ue792";  // Animation
    public const string TextAa                  = "\ue6ee";  // Font

    // ── Components ──
    public const string PuzzlePiece             = "\ue596";  // Generic component
    public const string ArrowsOutCardinal       = "\ue0a4";  // Transform
    public const string Atom                    = "\ue5e4";  // Rigidbody
    public const string BoundingBox             = "\ue6ce";  // Collider
    public const string Ear                     = "\ue70c";  // AudioListener
    public const string Sparkle                 = "\ue6a2";  // ParticleSystem
    public const string Mountains               = "\ue7ae";  // Terrain
    public const string LineSegments            = "\ue6d4";  // LineRenderer
    public const string Person                  = "\ue3a8";  // CharacterController

    // ── UI actions ──
    public const string GearSix                 = "\ue272";  // Settings
    public const string MagnifyingGlass         = "\ue30c";  // Search
    public const string FloppyDisk              = "\ue248";  // Save
    public const string Plus                    = "\ue3d4";
    public const string Trash                   = "\ue4a6";  // Delete
    public const string ArrowClockwise          = "\ue036";  // Refresh
    public const string Eye                     = "\ue220";
    public const string EyeSlash                = "\ue224";
    public const string Link                    = "\ue2e2";
    public const string Star                    = "\ue46a";
    public const string Copy                    = "\ue1ca";  // Duplicate
    public const string X                       = "\ue4f6";  // Close
    public const string CaretDown               = "\ue136";  // Dropdown
    public const string ArrowsOut               = "\ue09e";  // Scale (diagonal outward arrows)
    public const string ArrowCounterClockwise   = "\ue032";  // Rotate (single curved arrow)
    public const string CubeTransparent         = "\ue1dc";  // Gizmos (wireframe 3D)
    public const string Lightning               = "\ue2de";  // Compile / Build
    public const string CornersOut              = "\ue1d2";  // Maximize
    public const string CornersIn               = "\ue1d0";  // Restore

    // ── Playback ──
    public const string Play                    = "\ue3d0";
    public const string Stop                    = "\ue46c";
    public const string Pause                   = "\ue39e";
    public const string SkipForward             = "\ue5a6";  // StepForward

    // ── Status / log ──
    public const string Info                    = "\ue2ce";
    public const string Warning                 = "\ue4e0";
    public const string XCircle                 = "\ue4f8";  // Error
    public const string CheckCircle             = "\ue184";  // Success

    /// <summary>
    /// Minimum Unicode codepoint used by Phosphor glyphs (for font atlas glyph range).
    /// </summary>
    public const int GlyphRangeMin = 0xE002;

    /// <summary>
    /// Maximum Unicode codepoint used by Phosphor glyphs (for font atlas glyph range).
    /// </summary>
    public const int GlyphRangeMax = 0xED6E;
}
