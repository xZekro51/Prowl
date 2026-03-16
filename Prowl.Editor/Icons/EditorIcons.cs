// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text;
using ImageMagick;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Prowl.Editor;

/// <summary>
/// Icon types available in the editor UI.
/// </summary>
public enum EditorIconType
{
    GameObject,
    Camera,
    Light,
    Folder,
    FolderOpen,
    Script,
    Material,
    Texture,
    Scene,
    Mesh,
    Prefab,
    Audio,
    File,
    Component,
    Transform,
}

/// <summary>
/// Manages editor icons as GPU textures. Icons are defined as inline SVG data,
/// rasterized on first access via Magick.NET, and cached as Texture2D objects.
/// The resulting OpenGL texture handles are used with ImGui.Image() for rendering.
/// </summary>
public static class EditorIcons
{
    private static readonly Dictionary<(EditorIconType, int), nint> _cache = new();
    private static readonly Dictionary<(EditorIconType, int), Texture2D> _textures = new();
    private static bool _disposed;

    /// <summary>Default icon size in pixels.</summary>
    public const int DefaultSize = 16;

    /// <summary>
    /// Gets the ImGui texture ID for the given icon type at the specified size.
    /// Creates and caches the texture on first access.
    /// </summary>
    public static nint Get(EditorIconType type, int size = DefaultSize)
    {
        if (_disposed) return 0;

        var key = (type, size);
        if (_cache.TryGetValue(key, out nint texId))
            return texId;

        var tex = CreateIconTexture(type, size);
        if (tex == null)
            return 0;

        texId = (nint)tex.Handle.Handle;
        _cache[key] = texId;
        _textures[key] = tex;
        return texId;
    }

    /// <summary>
    /// Maps a file extension to the corresponding icon type.
    /// </summary>
    public static EditorIconType GetIconForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs" => EditorIconType.Script,
        ".scene" => EditorIconType.Scene,
        ".mat" => EditorIconType.Material,
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".hdr" => EditorIconType.Texture,
        ".fbx" or ".obj" or ".gltf" or ".glb" => EditorIconType.Mesh,
        ".prefab" => EditorIconType.Prefab,
        ".wav" or ".ogg" or ".mp3" => EditorIconType.Audio,
        _ => EditorIconType.File,
    };

    /// <summary>
    /// Maps a GameObject to the best icon type based on its components.
    /// </summary>
    public static EditorIconType GetIconForGameObject(GameObject go)
    {
        if (go.GetComponent<Camera>() != null) return EditorIconType.Camera;
        if (go.GetComponent<DirectionalLight>() != null) return EditorIconType.Light;
        if (go.GetComponent<MeshRenderer>() != null) return EditorIconType.Mesh;
        return EditorIconType.GameObject;
    }

    /// <summary>
    /// Disposes all cached icon textures.
    /// </summary>
    public static void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var tex in _textures.Values)
            tex?.Dispose();

        _textures.Clear();
        _cache.Clear();
    }

    // ── Icon creation ──────────────────────────────────────────────

    private static Texture2D? CreateIconTexture(EditorIconType type, int size)
    {
        try
        {
            string svg = GetSvg(type, size);
            byte[] svgBytes = Encoding.UTF8.GetBytes(svg);

            using var image = new MagickImage(svgBytes);
            image.Resize((uint)size, (uint)size);
            image.Alpha(AlphaOption.Set);

            // Extract RGBA pixel data
            byte[] pixels = image.ToByteArray(MagickFormat.Rgba);
            if (pixels.Length < size * size * 4)
                return CreateFallbackTexture(type, size);

            return UploadPixels(pixels, (uint)size, (uint)size);
        }
        catch
        {
            // SVG rendering failed — use fallback
            return CreateFallbackTexture(type, size);
        }
    }

    private static Texture2D? CreateFallbackTexture(EditorIconType type, int size)
    {
        try
        {
            byte[] pixels = GenerateFallbackPixels(type, size);
            return UploadPixels(pixels, (uint)size, (uint)size);
        }
        catch
        {
            return null;
        }
    }

    private static Texture2D UploadPixels(byte[] rgba, uint width, uint height)
    {
        var tex = new Texture2D(width, height, false, TextureImageFormat.Color4b);
        tex.SetTextureFilters(TextureMin.Linear, TextureMag.Linear);
        tex.SetData<byte>(rgba.AsMemory(), 0, 0, width, height);
        return tex;
    }

    // ── SVG definitions ────────────────────────────────────────────
    // Simple flat SVG icons loosely inspired by Unity's icon style.
    // Each produces a crisp icon at the requested size.

    private static string GetSvg(EditorIconType type, int size)
    {
        string s = size.ToString();
        return type switch
        {
            EditorIconType.GameObject => Svg(s,
                "<rect x='2' y='2' width='12' height='12' rx='1' fill='none' stroke='#5b9bd5' stroke-width='1.5'/>" +
                "<line x1='8' y1='2' x2='8' y2='14' stroke='#5b9bd5' stroke-width='0.8'/>" +
                "<line x1='2' y1='8' x2='14' y2='8' stroke='#5b9bd5' stroke-width='0.8'/>"),

            EditorIconType.Camera => Svg(s,
                "<rect x='1' y='4' width='9' height='8' rx='1' fill='#9b59b6'/>" +
                "<polygon points='10,5 15,2 15,14 10,11' fill='#9b59b6'/>"),

            EditorIconType.Light => Svg(s,
                "<circle cx='8' cy='8' r='4' fill='#f1c40f'/>" +
                "<line x1='8' y1='1' x2='8' y2='3' stroke='#f1c40f' stroke-width='1.5' stroke-linecap='round'/>" +
                "<line x1='8' y1='13' x2='8' y2='15' stroke='#f1c40f' stroke-width='1.5' stroke-linecap='round'/>" +
                "<line x1='1' y1='8' x2='3' y2='8' stroke='#f1c40f' stroke-width='1.5' stroke-linecap='round'/>" +
                "<line x1='13' y1='8' x2='15' y2='8' stroke='#f1c40f' stroke-width='1.5' stroke-linecap='round'/>"),

            EditorIconType.Folder => Svg(s,
                "<path d='M1,4 L1,13 L15,13 L15,5 L7,5 L6,3 L1,3 Z' fill='#e8a838' stroke='#c78c2e' stroke-width='0.5'/>"),

            EditorIconType.FolderOpen => Svg(s,
                "<path d='M1,4 L1,13 L13,13 L15,6 L7,6 L6,4 Z' fill='#e8a838' stroke='#c78c2e' stroke-width='0.5'/>" +
                "<path d='M3,6 L15,6 L13,13 L1,13 Z' fill='#f0c060'/>"),

            EditorIconType.Script => Svg(s,
                "<rect x='2' y='1' width='12' height='14' rx='1' fill='#2c3e50'/>" +
                "<line x1='5' y1='5' x2='11' y2='5' stroke='#2ecc71' stroke-width='1'/>" +
                "<line x1='5' y1='7.5' x2='9' y2='7.5' stroke='#2ecc71' stroke-width='1'/>" +
                "<line x1='5' y1='10' x2='11' y2='10' stroke='#27ae60' stroke-width='1'/>"),

            EditorIconType.Material => Svg(s,
                "<circle cx='8' cy='8' r='6' fill='#e74c8b'/>" +
                "<ellipse cx='6' cy='6' rx='2' ry='1.5' fill='#ffffff' fill-opacity='0.3'/>"),

            EditorIconType.Texture => Svg(s,
                "<rect x='1' y='1' width='14' height='14' rx='1' fill='#2980b9'/>" +
                "<circle cx='5' cy='5' r='2' fill='#f1c40f'/>" +
                "<polygon points='1,14 6,8 10,11 14,6 14,14' fill='#27ae60' fill-opacity='0.7'/>"),

            EditorIconType.Scene => Svg(s,
                "<rect x='1' y='2' width='14' height='12' rx='1' fill='#e67e22'/>" +
                "<rect x='1' y='2' width='14' height='3' fill='#d35400'/>" +
                "<rect x='3' y='1' width='3' height='4' fill='#d35400' stroke='#c0392b' stroke-width='0.5'/>"),

            EditorIconType.Mesh => Svg(s,
                "<polygon points='8,1 14,5 14,11 8,15 2,11 2,5' fill='none' stroke='#1abc9c' stroke-width='1.2'/>" +
                "<line x1='8' y1='1' x2='8' y2='15' stroke='#1abc9c' stroke-width='0.8'/>" +
                "<line x1='2' y1='5' x2='14' y2='11' stroke='#1abc9c' stroke-width='0.6'/>" +
                "<line x1='14' y1='5' x2='2' y2='11' stroke='#1abc9c' stroke-width='0.6'/>"),

            EditorIconType.Prefab => Svg(s,
                "<rect x='4' y='4' width='8' height='8' rx='1' fill='#3498db' transform='rotate(45 8 8)'/>" +
                "<circle cx='8' cy='8' r='2' fill='white' fill-opacity='0.6'/>"),

            EditorIconType.Audio => Svg(s,
                "<path d='M4,5 L4,11 L7,11 L11,14 L11,2 L7,5 Z' fill='#8e44ad'/>" +
                "<path d='M12,5 Q14,8 12,11' fill='none' stroke='#8e44ad' stroke-width='1'/>"),

            EditorIconType.File => Svg(s,
                "<path d='M3,1 L10,1 L13,4 L13,15 L3,15 Z' fill='#7f8c8d'/>" +
                "<path d='M10,1 L10,4 L13,4' fill='none' stroke='#95a5a6' stroke-width='0.8'/>"),

            EditorIconType.Component => Svg(s,
                "<circle cx='8' cy='8' r='5' fill='none' stroke='#3498db' stroke-width='1.2'/>" +
                "<circle cx='8' cy='8' r='2' fill='#3498db'/>"),

            EditorIconType.Transform => Svg(s,
                "<line x1='3' y1='8' x2='13' y2='8' stroke='#e74c3c' stroke-width='1.5' stroke-linecap='round'/>" +
                "<line x1='8' y1='3' x2='8' y2='13' stroke='#2ecc71' stroke-width='1.5' stroke-linecap='round'/>" +
                "<circle cx='8' cy='8' r='1.5' fill='#3498db'/>"),

            _ => Svg(s,
                "<rect x='3' y='3' width='10' height='10' rx='1' fill='#95a5a6'/>"),
        };
    }

    private static string Svg(string size, string content) =>
        $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16' width='{size}' height='{size}'>{content}</svg>";

    // ── Programmatic fallback ──────────────────────────────────────
    // If SVG rasterisation fails, generate simple colored rectangles.

    private static byte[] GenerateFallbackPixels(EditorIconType type, int size)
    {
        (byte r, byte g, byte b) = type switch
        {
            EditorIconType.GameObject => ((byte)91, (byte)155, (byte)213),
            EditorIconType.Camera     => ((byte)155, (byte)89, (byte)182),
            EditorIconType.Light      => ((byte)241, (byte)196, (byte)15),
            EditorIconType.Folder     => ((byte)232, (byte)168, (byte)56),
            EditorIconType.FolderOpen => ((byte)240, (byte)192, (byte)96),
            EditorIconType.Script     => ((byte)46, (byte)204, (byte)113),
            EditorIconType.Material   => ((byte)231, (byte)76, (byte)139),
            EditorIconType.Texture    => ((byte)41, (byte)128, (byte)185),
            EditorIconType.Scene      => ((byte)230, (byte)126, (byte)34),
            EditorIconType.Mesh       => ((byte)26, (byte)188, (byte)156),
            EditorIconType.Prefab     => ((byte)52, (byte)152, (byte)219),
            EditorIconType.Audio      => ((byte)142, (byte)68, (byte)173),
            EditorIconType.Component  => ((byte)52, (byte)152, (byte)219),
            EditorIconType.Transform  => ((byte)231, (byte)76, (byte)60),
            _                         => ((byte)149, (byte)165, (byte)166),
        };

        byte[] pixels = new byte[size * size * 4];
        int margin = Math.Max(1, size / 8);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                bool inShape = x >= margin && x < size - margin &&
                               y >= margin && y < size - margin;
                // Simple rounded-corner check
                if (inShape)
                {
                    int cornerDist = Math.Min(
                        Math.Min(x - margin, size - margin - 1 - x),
                        Math.Min(y - margin, size - margin - 1 - y));
                    inShape = cornerDist >= 0;
                }

                if (inShape)
                {
                    pixels[i]     = r;
                    pixels[i + 1] = g;
                    pixels[i + 2] = b;
                    pixels[i + 3] = 255;
                }
                else
                {
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 0;
                }
            }
        }

        return pixels;
    }
}
