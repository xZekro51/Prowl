// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using System.Reflection;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.ImGuiIntegration;
using Prowl.Runtime.ParticleSystem;
using Prowl.Runtime.Resources;

namespace Prowl.Editor;

/// <summary>
/// Icon types available in the editor UI.
/// </summary>
public enum EditorIconType
{
    // ── Core objects ──
    GameObject,
    Camera,
    Light,
    PointLight,
    SpotLight,
    Folder,
    FolderOpen,

    // ── Asset types ──
    Script,
    Material,
    Texture,
    Scene,
    Mesh,
    Prefab,
    Audio,
    File,
    Shader,
    Animation,
    Font,

    // ── Components ──
    Component,
    Transform,
    Rigidbody,
    Collider,
    AudioSource,
    AudioListener,
    ParticleSystem,
    Terrain,
    LineRenderer,
    CharacterController,

    // ── UI actions ──
    Settings,
    Search,
    Save,
    Plus,
    Delete,
    Refresh,
    Eye,
    EyeOff,
    Link,
    Star,
    Duplicate,
    Close,
    Dropdown,
    Translate,
    Rotate,
    Scale,

    // ── Playback ──
    Play,
    Stop,
    Pause,
    StepForward,

    // ── Status / log ──
    Info,
    Warning,
    Error,
    Success,
}

/// <summary>
/// Manages editor icons as GPU textures. Icons are loaded from embedded PNG resources,
/// cached as Texture2D objects. The resulting OpenGL texture handles are used with
/// ImGui.Image() for rendering.
/// </summary>
public static class EditorIcons
{
    private static readonly Dictionary<(EditorIconType, int), nint> _cache = new();
    private static readonly Dictionary<(EditorIconType, int), Texture2D> _textures = new();
    private static readonly Assembly _editorAssembly = typeof(EditorIcons).Assembly;
    private static bool _disposed;

    /// <summary>Default icon size in pixels.</summary>
    public const int DefaultSize = 32;

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

        texId = ImGuiTextureRegistry.GetOrRegister(tex.Handle?.GraphiteTexture);
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
        ".shader" or ".glsl" or ".hlsl" or ".vert" or ".frag" => EditorIconType.Shader,
        ".anim" or ".animation" => EditorIconType.Animation,
        ".ttf" or ".otf" or ".woff" => EditorIconType.Font,
        _ => EditorIconType.File,
    };

    /// <summary>
    /// Maps a GameObject to the best icon type based on its components.
    /// </summary>
    public static EditorIconType GetIconForGameObject(GameObject go)
    {
        if (go.GetComponent<Camera>() != null) return EditorIconType.Camera;
        if (go.GetComponent<DirectionalLight>() != null) return EditorIconType.Light;
        if (go.GetComponent<PointLight>() != null) return EditorIconType.PointLight;
        if (go.GetComponent<SpotLight>() != null) return EditorIconType.SpotLight;
        if (go.GetComponent<AudioSource>() != null) return EditorIconType.AudioSource;
        if (go.GetComponent<AudioListener>() != null) return EditorIconType.AudioListener;
        if (go.GetComponent<Rigidbody3D>() != null) return EditorIconType.Rigidbody;
        if (go.GetComponent<ParticleSystemComponent>() != null) return EditorIconType.ParticleSystem;
        if (go.GetComponent<MeshRenderer>() != null) return EditorIconType.Mesh;
        if (go.GetComponent<LineRenderer>() != null) return EditorIconType.LineRenderer;
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

    // ── ImGui helpers ──────────────────────────────────────────────

    /// <summary>
    /// Draws an <see cref="ImGui.ImageButton"/> with the given icon type.
    /// Returns true if the button was clicked.
    /// </summary>
    public static bool ImageButton(string id, EditorIconType type, Vector2 size)
    {
        nint texId = Get(type);
        if (texId == 0) return ImGui.Button(id, size);
        return ImGui.ImageButton(id, texId, size, new Vector2(0, 1), new Vector2(1, 0));
    }

    /// <summary>
    /// Draws an icon-only button sized to the current text line height.
    /// Returns true if the button was clicked.
    /// </summary>
    public static bool ImageButton(string id, EditorIconType type)
    {
        float h = ImGui.GetTextLineHeight();
        return ImageButton(id, type, new Vector2(h, h));
    }

    /// <summary>
    /// Draws an icon + text button. The icon is rendered inside the button
    /// area via the draw list, followed by the label text.
    /// Returns true if the button was clicked.
    /// </summary>
    public static bool ImageButtonWithLabel(string id, EditorIconType type, string label, Vector2 size = default)
    {
        nint texId = Get(type);
        if (texId == 0) return ImGui.Button($"{label}##{id}", size);

        float iconSz = ImGui.GetTextLineHeight();
        float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        Vector2 textSize = ImGui.CalcTextSize(label);
        if (size == default)
            size = new Vector2(iconSz + spacing + textSize.X + ImGui.GetStyle().FramePadding.X * 2,
                               iconSz + ImGui.GetStyle().FramePadding.Y * 2);

        Vector2 cursorPos = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.Button($"##{id}", size);

        var drawList = ImGui.GetWindowDrawList();
        var framePad = ImGui.GetStyle().FramePadding;
        float yOff = (size.Y - iconSz) * 0.5f;
        drawList.AddImage(texId,
            new Vector2(cursorPos.X + framePad.X, cursorPos.Y + yOff),
            new Vector2(cursorPos.X + framePad.X + iconSz, cursorPos.Y + yOff + iconSz),
            new Vector2(0, 1), new Vector2(1, 0));

        float textY = cursorPos.Y + (size.Y - textSize.Y) * 0.5f;
        drawList.AddText(new Vector2(cursorPos.X + framePad.X + iconSz + spacing, textY),
            ImGui.GetColorU32(ImGuiCol.Text), label);

        return clicked;
    }

    /// <summary>
    /// Draws a menu item with an icon prefix. Returns true if selected.
    /// </summary>
    public static bool IconMenuItem(EditorIconType type, string label)
    {
        nint texId = Get(type);
        float iconSz = ImGui.GetTextLineHeight();
        if (texId != 0)
        {
            ImGui.Image(texId, new Vector2(iconSz, iconSz), new Vector2(0, 1), new Vector2(1, 0));
            ImGui.SameLine();
        }
        return ImGui.Selectable(label);
    }

    /// <summary>
    /// Draws an inline icon image at the current cursor, sized to the text line height.
    /// </summary>
    public static void InlineIcon(EditorIconType type, Vector4? tint = null)
    {
        nint texId = Get(type);
        if (texId == 0) return;
        float sz = ImGui.GetTextLineHeight();
        ImGui.Image(texId, new Vector2(sz, sz), new Vector2(0, 1), new Vector2(1, 0),
            tint ?? new Vector4(1, 1, 1, 1));
    }

    // ── Icon creation ──────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="Texture2D"/> for the given icon type at the specified pixel size.
    /// Used internally by <see cref="Get"/> and exposed for <see cref="Icons.IconManager"/>.
    /// </summary>
    internal static Texture2D? CreateIconTextureRaw(EditorIconType type, int size)
        => CreateIconTexture(type, size);

    private static Texture2D? CreateIconTexture(EditorIconType type, int size)
    {
        try
        {
            string? resourceName = FindResourceName(type);
            if (resourceName == null)
                return CreateFallbackTexture(type, size);

            using var stream = _editorAssembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return CreateFallbackTexture(type, size);

            return Texture2D.FromStream(stream);
        }
        catch
        {
            return CreateFallbackTexture(type, size);
        }
    }

    private static string? FindResourceName(EditorIconType type)
    {
        string fileName = $"{type}.png";
        string[] resourceNames = _editorAssembly.GetManifestResourceNames();

        return Array.Find(resourceNames, r =>
            r.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
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

    // ── Programmatic fallback ──────────────────────────────────────
    // If PNG loading fails, generate simple colored rectangles.

    private static byte[] GenerateFallbackPixels(EditorIconType type, int size)
    {
        (byte r, byte g, byte b) = type switch
        {
            EditorIconType.GameObject         => ((byte)91, (byte)155, (byte)213),
            EditorIconType.Camera             => ((byte)155, (byte)89, (byte)182),
            EditorIconType.Light              => ((byte)241, (byte)196, (byte)15),
            EditorIconType.PointLight         => ((byte)243, (byte)156, (byte)18),
            EditorIconType.SpotLight          => ((byte)243, (byte)156, (byte)18),
            EditorIconType.Folder             => ((byte)232, (byte)168, (byte)56),
            EditorIconType.FolderOpen         => ((byte)240, (byte)192, (byte)96),
            EditorIconType.Script             => ((byte)46, (byte)204, (byte)113),
            EditorIconType.Material           => ((byte)231, (byte)76, (byte)139),
            EditorIconType.Texture            => ((byte)41, (byte)128, (byte)185),
            EditorIconType.Scene              => ((byte)230, (byte)126, (byte)34),
            EditorIconType.Mesh               => ((byte)26, (byte)188, (byte)156),
            EditorIconType.Prefab             => ((byte)52, (byte)152, (byte)219),
            EditorIconType.Audio              => ((byte)142, (byte)68, (byte)173),
            EditorIconType.File               => ((byte)127, (byte)140, (byte)141),
            EditorIconType.Shader             => ((byte)142, (byte)68, (byte)173),
            EditorIconType.Animation          => ((byte)231, (byte)76, (byte)60),
            EditorIconType.Font               => ((byte)52, (byte)73, (byte)94),
            EditorIconType.Component          => ((byte)52, (byte)152, (byte)219),
            EditorIconType.Transform          => ((byte)231, (byte)76, (byte)60),
            EditorIconType.Rigidbody          => ((byte)230, (byte)126, (byte)34),
            EditorIconType.Collider           => ((byte)46, (byte)204, (byte)113),
            EditorIconType.AudioSource        => ((byte)142, (byte)68, (byte)173),
            EditorIconType.AudioListener      => ((byte)142, (byte)68, (byte)173),
            EditorIconType.ParticleSystem     => ((byte)243, (byte)156, (byte)18),
            EditorIconType.Terrain            => ((byte)39, (byte)174, (byte)96),
            EditorIconType.LineRenderer       => ((byte)52, (byte)152, (byte)219),
            EditorIconType.CharacterController => ((byte)230, (byte)126, (byte)34),
            EditorIconType.Settings           => ((byte)189, (byte)195, (byte)199),
            EditorIconType.Search             => ((byte)189, (byte)195, (byte)199),
            EditorIconType.Save               => ((byte)52, (byte)152, (byte)219),
            EditorIconType.Plus               => ((byte)39, (byte)174, (byte)96),
            EditorIconType.Delete             => ((byte)231, (byte)76, (byte)60),
            EditorIconType.Refresh            => ((byte)52, (byte)152, (byte)219),
            EditorIconType.Eye                => ((byte)52, (byte)152, (byte)219),
            EditorIconType.EyeOff             => ((byte)127, (byte)140, (byte)141),
            EditorIconType.Link               => ((byte)52, (byte)152, (byte)219),
            EditorIconType.Star               => ((byte)241, (byte)196, (byte)15),
            EditorIconType.Duplicate          => ((byte)127, (byte)140, (byte)141),
            EditorIconType.Close              => ((byte)220, (byte)220, (byte)220),
            EditorIconType.Dropdown           => ((byte)189, (byte)189, (byte)189),
            EditorIconType.Translate          => ((byte)220, (byte)220, (byte)220),
            EditorIconType.Rotate             => ((byte)220, (byte)220, (byte)220),
            EditorIconType.Scale              => ((byte)220, (byte)220, (byte)220),
            EditorIconType.Play               => ((byte)76, (byte)175, (byte)80),
            EditorIconType.Stop               => ((byte)229, (byte)57, (byte)53),
            EditorIconType.Pause              => ((byte)255, (byte)183, (byte)77),
            EditorIconType.StepForward        => ((byte)189, (byte)189, (byte)189),
            EditorIconType.Info               => ((byte)52, (byte)152, (byte)219),
            EditorIconType.Warning            => ((byte)243, (byte)156, (byte)18),
            EditorIconType.Error              => ((byte)231, (byte)76, (byte)60),
            EditorIconType.Success            => ((byte)39, (byte)174, (byte)96),
            _                                 => ((byte)149, (byte)165, (byte)166),
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
