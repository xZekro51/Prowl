// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.Runtime.ParticleSystem;

namespace Prowl.Editor.Icons;

/// <summary>
/// Central registry for editor icons. Supports both texture-based icons (rasterised SVG / PNG)
/// and font-based icons (Unicode glyphs). Icons are loaded lazily on first access and cached
/// for the lifetime of the editor.
///
/// <para><b>Usage:</b></para>
/// <code>
/// IconManager.Load();                                     // call once at startup
/// IconManager.DrawIcon("Folder", position, 16f);          // draw by name
/// IIcon icon = IconManager.GetIcon("Script");             // retrieve for custom drawing
/// string name = IconManager.GetIconNameForExtension(".cs"); // extension mapping
/// string name = IconManager.GetIconNameForComponent(typeof(Camera)); // component mapping
/// </code>
/// </summary>
public static class IconManager
{
    // ── Storage ──────────────────────────────────────────────────

    private static readonly Dictionary<string, IIcon> _icons = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> _extensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Scripts
        [".cs"]     = "Script",
        // Scenes
        [".scene"]  = "Scene",
        // Materials
        [".mat"]    = "Material",
        // Textures / Images
        [".png"]    = "Texture",
        [".jpg"]    = "Texture",
        [".jpeg"]   = "Texture",
        [".bmp"]    = "Texture",
        [".tga"]    = "Texture",
        [".hdr"]    = "Texture",
        // 3D Models / Meshes
        [".fbx"]    = "Mesh",
        [".obj"]    = "Mesh",
        [".gltf"]   = "Mesh",
        [".glb"]    = "Mesh",
        // Prefabs
        [".prefab"] = "Prefab",
        // Audio
        [".wav"]    = "Audio",
        [".ogg"]    = "Audio",
        [".mp3"]    = "Audio",
        // Shaders
        [".shader"] = "Shader",
        [".glsl"]   = "Shader",
        [".hlsl"]   = "Shader",
        [".vert"]   = "Shader",
        [".frag"]   = "Shader",
        // Animation
        [".anim"]      = "Animation",
        [".animation"] = "Animation",
        // Fonts
        [".ttf"]    = "Font",
        [".otf"]    = "Font",
        [".woff"]   = "Font",
        // ScriptableObject assets
        [".asset"]  = "File",
    };

    /// <summary>
    /// Maps component type names to icon names.
    /// Keys are short type names (e.g. "Camera", not "Prowl.Runtime.Camera").
    /// </summary>
    private static readonly Dictionary<string, string> _componentIconMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Core
        ["Transform"]           = "Transform",
        ["Camera"]              = "Camera",
        // Lights
        ["DirectionalLight"]    = "Light",
        ["PointLight"]          = "PointLight",
        ["SpotLight"]           = "SpotLight",
        ["Light"]               = "Light",
        // Rendering
        ["MeshRenderer"]        = "Mesh",
        ["ModelRenderer"]       = "Mesh",
        ["SkinnedMeshRenderer"] = "Mesh",
        ["LineRenderer"]        = "LineRenderer",
        // Physics
        ["Rigidbody3D"]         = "Rigidbody",
        ["BoxCollider"]         = "Collider",
        ["SphereCollider"]      = "Collider",
        ["CapsuleCollider"]     = "Collider",
        ["MeshCollider"]        = "Collider",
        ["ConeCollider"]        = "Collider",
        ["CylinderCollider"]    = "Collider",
        ["ConvexHullCollider"]  = "Collider",
        ["ModelCollider"]       = "Collider",
        ["TerrainCollider"]     = "Collider",
        ["Collider"]            = "Collider",
        ["CharacterController"] = "CharacterController",
        // Audio
        ["AudioSource"]         = "AudioSource",
        ["AudioListener"]       = "AudioListener",
        // Particles
        ["ParticleSystemComponent"] = "ParticleSystem",
        // Terrain
        ["TerrainComponent"]    = "Terrain",
        ["TerrainChunk"]        = "Terrain",
    };

    /// <summary>The icon returned when a name is not found.</summary>
    private const string MissingIconName = "File";

    private static bool _loaded;

    // ── Initialisation ───────────────────────────────────────────

    /// <summary>
    /// Initialises the icon system by registering all built-in icons.
    /// Call once during editor startup (after the graphics context is ready).
    /// </summary>
    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;

        // Register every EditorIconType as a texture icon (backed by embedded PNGs).
        foreach (EditorIconType type in Enum.GetValues<EditorIconType>())
        {
            string name = type.ToString();
            // Use a lazy wrapper so the texture is only created when first drawn.
            _icons[name] = new LazyTextureIcon(type);
        }

        // All icons (including Play, Stop, Pause, StepForward, Close, Dropdown)
        // are now registered as texture icons via the EditorIconType enum above.
    }

    // ── Registration ─────────────────────────────────────────────

    /// <summary>Registers a texture-based icon under the given name.</summary>
    public static void RegisterTextureIcon(string name, Runtime.Resources.Texture2D texture)
    {
        _icons[name] = new TextureIcon(texture);
    }

    /// <summary>Registers a font glyph icon under the given name.</summary>
    public static void RegisterFontIcon(string name, string character, Vector4? defaultColor = null)
    {
        _icons[name] = new FontIcon(character, defaultColor);
    }

    /// <summary>Registers an arbitrary <see cref="IIcon"/> implementation.</summary>
    public static void Register(string name, IIcon icon)
    {
        _icons[name] = icon ?? throw new ArgumentNullException(nameof(icon));
    }

    // ── Retrieval ────────────────────────────────────────────────

    /// <summary>
    /// Returns the icon registered under <paramref name="name"/>,
    /// or a default "missing" icon if the name is not found.
    /// </summary>
    public static IIcon GetIcon(string name)
    {
        if (_icons.TryGetValue(name, out var icon))
            return icon;

        // Fallback to the generic "File" icon (always registered during Load)
        if (_icons.TryGetValue(MissingIconName, out var missing))
            return missing;

        // Should never happen after Load(), but guard against it
        return NullIcon.Instance;
    }

    /// <summary>
    /// Maps a file extension (e.g. ".cs") to an icon name (e.g. "Script").
    /// Returns the icon name, or <c>"File"</c> for unknown extensions.
    /// </summary>
    public static string GetIconNameForExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return MissingIconName;

        return _extensionMap.TryGetValue(extension.ToLowerInvariant(), out var name)
            ? name
            : MissingIconName;
    }

    /// <summary>
    /// Returns the best icon name for a <see cref="GameObject"/> based on its components.
    /// </summary>
    public static string GetIconNameForGameObject(GameObject go)
    {
        if (go.GetComponent<Camera>() != null)         return "Camera";
        if (go.GetComponent<DirectionalLight>() != null) return "Light";
        if (go.GetComponent<PointLight>() != null)     return "PointLight";
        if (go.GetComponent<SpotLight>() != null)      return "SpotLight";
        if (go.GetComponent<AudioSource>() != null)    return "AudioSource";
        if (go.GetComponent<AudioListener>() != null)  return "AudioListener";
        if (go.GetComponent<Rigidbody3D>() != null)    return "Rigidbody";
        if (go.GetComponent<ParticleSystemComponent>() != null) return "ParticleSystem";
        if (go.GetComponent<MeshRenderer>() != null)   return "Mesh";
        if (go.GetComponent<LineRenderer>() != null)   return "LineRenderer";
        return "GameObject";
    }

    /// <summary>
    /// Returns the icon name for a component type (e.g. <c>typeof(Camera)</c> → "Camera").
    /// Falls back to the generic "Component" icon for unmapped types.
    /// </summary>
    public static string GetIconNameForComponent(Type componentType)
    {
        if (componentType == null) return "Component";
        return _componentIconMap.TryGetValue(componentType.Name, out var name) ? name : "Component";
    }

    /// <summary>
    /// Returns the icon name for a component instance.
    /// </summary>
    public static string GetIconNameForComponent(MonoBehaviour component)
    {
        if (component == null) return "Component";
        return GetIconNameForComponent(component.GetType());
    }

    // ── Convenience drawing ──────────────────────────────────────

    /// <summary>
    /// Draws the named icon at the specified position and size.
    /// This is a shorthand for <c>GetIcon(name).Draw(position, size, tint)</c>.
    /// </summary>
    public static void DrawIcon(string name, Vector2 position, float size, Vector4? tint = null)
    {
        GetIcon(name).Draw(position, size, tint);
    }

    /// <summary>
    /// Helper that draws an icon overlaid on the most recently drawn ImGui item
    /// (typically a TreeNode). The icon is placed at the tree-node label start,
    /// vertically centred within the item rect.
    /// </summary>
    /// <param name="name">The icon name.</param>
    /// <param name="tint">Optional tint colour.</param>
    /// <param name="useTreeIndent">
    /// If true, positions the icon after the tree node indent (arrow + spacing).
    /// If false, positions it with a small fixed offset from the item rect.
    /// </param>
    public static void DrawIconOverLastItem(string name, Vector4? tint = null, bool useTreeIndent = true)
    {
        Vector2 itemMin = ImGui.GetItemRectMin();
        float iconSize = ImGui.GetTextLineHeight();
        float yOffset = (ImGui.GetItemRectSize().Y - iconSize) * 0.5f;

        float xOffset = useTreeIndent ? ImGui.GetTreeNodeToLabelSpacing() : 4f;
        var iconPos = new Vector2(itemMin.X + xOffset, itemMin.Y + yOffset);

        DrawIcon(name, iconPos, iconSize, tint);
    }

    // ── Cleanup ──────────────────────────────────────────────────

    /// <summary>
    /// Disposes all icon resources. Call once during editor shutdown.
    /// </summary>
    public static void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            if (icon is IDisposable disposable)
                disposable.Dispose();
        }
        _icons.Clear();
        _loaded = false;
    }

    // ── Internal types ───────────────────────────────────────────

    /// <summary>
    /// A texture icon that defers creation until the first draw call.
    /// This avoids loading all icon textures at startup.
    /// </summary>
    private sealed class LazyTextureIcon : IIcon, IDisposable
    {
        private readonly EditorIconType _type;
        private TextureIcon? _inner;
        private bool _disposed;

        public LazyTextureIcon(EditorIconType type) => _type = type;

        public void Draw(Vector2 position, float size, Vector4? tint = null)
        {
            if (_disposed) return;

            // Lazily resolve via the EditorIcons embedded PNG pipeline
            _inner ??= CreateFromEditorIcons(_type, (int)size);
            _inner?.Draw(position, size, tint);
        }

        private static TextureIcon? CreateFromEditorIcons(EditorIconType type, int size)
        {
            // Delegate to EditorIcons which loads from embedded PNG resources.
            // EditorIcons.CreateIconTextureRaw returns a Texture2D we can wrap.
            var texture = EditorIcons.CreateIconTextureRaw(type, Math.Max(size, EditorIcons.DefaultSize));
            return texture != null ? new TextureIcon(texture) : null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inner?.Dispose();
        }
    }

    /// <summary>
    /// A no-op icon returned when no icon can be found (defensive fallback).
    /// </summary>
    private sealed class NullIcon : IIcon
    {
        public static readonly NullIcon Instance = new();
        public void Draw(Vector2 position, float size, Vector4? tint = null) { }
    }
}
