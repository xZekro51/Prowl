// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;

using Prowl.Runtime.Text;

namespace Prowl.Editor.Importing;

/// <summary>
/// Import settings for font assets (.ttf / .otf). Stored in the .meta file's
/// ImportSettings dictionary and applied when font atlas assets are generated.
/// </summary>
public sealed class FontImportSettings
{
    /// <summary> Atlas type to generate. </summary>
    public AtlasType AtlasType { get; set; } = AtlasType.SDF;

    /// <summary> Font size in pixels used during atlas generation. </summary>
    public int PointSize { get; set; } = 48;

    /// <summary> Atlas texture resolution. 0 = auto-fit. </summary>
    public int AtlasResolution { get; set; } = 1024;

    /// <summary> SDF/MSDF distance range in atlas pixels. </summary>
    public float PxRange { get; set; } = 6f;

    /// <summary> Padding in pixels around each glyph in the atlas. </summary>
    public int Padding { get; set; } = 2;

    /// <summary>
    /// Character set preset. Common presets are "ASCII", "LatinExtended", or "Custom".
    /// </summary>
    public string CharacterSet { get; set; } = "ASCII";

    /// <summary>
    /// When <see cref="CharacterSet"/> is "Custom", this string contains the
    /// exact characters to include in the atlas.
    /// </summary>
    public string CustomCharacters { get; set; } = string.Empty;

    /// <summary>
    /// Oversampling multiplier for bitmap-based SDF generation.
    /// Higher values produce more accurate distance fields but take longer.
    /// Only used when <see cref="AtlasType"/> is <see cref="AtlasType.SDF"/>.
    /// </summary>
    public int SdfOversample { get; set; } = 4;

    /// <summary>
    /// Whether to generate mipmaps for the atlas texture.
    /// Defaults to <c>false</c> for SDF/MSDF (the shader's fwidth-based anti-aliasing
    /// provides resolution-independent rendering without mipmaps). Set to <c>true</c>
    /// for Bitmap atlases where standard bilinear mipmaps improve quality at small sizes.
    /// </summary>
    public bool GenerateMipmaps { get; set; } = false;

    /// <summary> Reads settings from a MetaFile's ImportSettings dictionary. </summary>
    public static FontImportSettings FromMeta(Dictionary<string, object?> importSettings)
    {
        FontImportSettings s = new();

        if (importSettings.TryGetValue("atlasType", out object? at) && at != null)
            s.AtlasType = ParseEnum<AtlasType>(at);
        if (importSettings.TryGetValue("pointSize", out object? ps) && ps != null)
            s.PointSize = ParseInt(ps, s.PointSize);
        if (importSettings.TryGetValue("atlasResolution", out object? ar) && ar != null)
            s.AtlasResolution = ParseInt(ar, s.AtlasResolution);
        if (importSettings.TryGetValue("pxRange", out object? pr) && pr != null)
            s.PxRange = ParseFloat(pr, s.PxRange);
        if (importSettings.TryGetValue("padding", out object? pad) && pad != null)
            s.Padding = ParseInt(pad, s.Padding);
        if (importSettings.TryGetValue("characterSet", out object? cs) && cs is string csStr)
            s.CharacterSet = csStr;
        if (importSettings.TryGetValue("customCharacters", out object? cc) && cc is string ccStr)
            s.CustomCharacters = ccStr;
        if (importSettings.TryGetValue("sdfOversample", out object? so) && so != null)
            s.SdfOversample = ParseInt(so, s.SdfOversample);
        if (importSettings.TryGetValue("generateMipmaps", out object? gm) && gm != null)
            s.GenerateMipmaps = ParseBool(gm, s.GenerateMipmaps);

        // Handle JsonElement strings
        if (importSettings.TryGetValue("characterSet", out object? csObj) && csObj is JsonElement csEl && csEl.ValueKind == JsonValueKind.String)
            s.CharacterSet = csEl.GetString() ?? s.CharacterSet;
        if (importSettings.TryGetValue("customCharacters", out object? ccObj) && ccObj is JsonElement ccEl && ccEl.ValueKind == JsonValueKind.String)
            s.CustomCharacters = ccEl.GetString() ?? s.CustomCharacters;

        return s;
    }

    /// <summary> Writes settings into a MetaFile's ImportSettings dictionary. </summary>
    public void WriteTo(Dictionary<string, object?> importSettings)
    {
        importSettings["atlasType"] = (int)AtlasType;
        importSettings["pointSize"] = PointSize;
        importSettings["atlasResolution"] = AtlasResolution;
        importSettings["pxRange"] = PxRange;
        importSettings["padding"] = Padding;
        importSettings["characterSet"] = CharacterSet;
        importSettings["customCharacters"] = CustomCharacters;
        importSettings["sdfOversample"] = SdfOversample;
        importSettings["generateMipmaps"] = GenerateMipmaps;
    }

    /// <summary> Creates a copy of these settings. </summary>
    public FontImportSettings Clone() => new()
    {
        AtlasType = AtlasType,
        PointSize = PointSize,
        AtlasResolution = AtlasResolution,
        PxRange = PxRange,
        Padding = Padding,
        CharacterSet = CharacterSet,
        CustomCharacters = CustomCharacters,
        SdfOversample = SdfOversample,
        GenerateMipmaps = GenerateMipmaps,
    };

    /// <summary>
    /// Returns the set of Unicode codepoints to include in the atlas based
    /// on the current <see cref="CharacterSet"/> setting.
    /// </summary>
    public HashSet<uint> GetCodepoints()
    {
        return CharacterSet switch
        {
            "LatinExtended" => GetLatinExtendedCodepoints(),
            "Custom" => GetCustomCodepoints(),
            _ => GetAsciiCodepoints(), // "ASCII" or any unrecognized value
        };
    }

    private static HashSet<uint> GetAsciiCodepoints()
    {
        HashSet<uint> codepoints = new(128);
        for (uint c = 32; c < 127; c++) // printable ASCII
            codepoints.Add(c);
        return codepoints;
    }

    private static HashSet<uint> GetLatinExtendedCodepoints()
    {
        HashSet<uint> codepoints = GetAsciiCodepoints();
        // Latin-1 Supplement (U+00A0 – U+00FF)
        for (uint c = 0x00A0; c <= 0x00FF; c++)
            codepoints.Add(c);
        // Latin Extended-A (U+0100 – U+017F)
        for (uint c = 0x0100; c <= 0x017F; c++)
            codepoints.Add(c);
        // Latin Extended-B subset (U+0180 – U+024F)
        for (uint c = 0x0180; c <= 0x024F; c++)
            codepoints.Add(c);
        return codepoints;
    }

    private HashSet<uint> GetCustomCodepoints()
    {
        HashSet<uint> codepoints = new();
        if (string.IsNullOrEmpty(CustomCharacters))
            return GetAsciiCodepoints(); // fallback to ASCII if no custom set

        for (int i = 0; i < CustomCharacters.Length; i++)
        {
            if (char.IsHighSurrogate(CustomCharacters[i]) && i + 1 < CustomCharacters.Length)
            {
                codepoints.Add((uint)char.ConvertToUtf32(CustomCharacters[i], CustomCharacters[i + 1]));
                i++;
            }
            else
            {
                codepoints.Add(CustomCharacters[i]);
            }
        }
        return codepoints;
    }

    private static T ParseEnum<T>(object value) where T : struct, Enum
    {
        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number)
                return (T)Enum.ToObject(typeof(T), je.GetInt32());
            if (je.ValueKind == JsonValueKind.String && Enum.TryParse<T>(je.GetString(), true, out T r))
                return r;
        }
        if (value is int i) return (T)Enum.ToObject(typeof(T), i);
        if (value is long l) return (T)Enum.ToObject(typeof(T), (int)l);
        if (value is string str && Enum.TryParse<T>(str, true, out T parsed)) return parsed;
        return default;
    }

    private static int ParseInt(object value, int fallback)
    {
        if (value is int i) return i;
        if (value is long l) return (int)l;
        if (value is double d) return (int)d;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Number) return je.GetInt32();
        return fallback;
    }

    private static float ParseFloat(object value, float fallback)
    {
        if (value is float f) return f;
        if (value is double d) return (float)d;
        if (value is int i) return i;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Number) return (float)je.GetDouble();
        return fallback;
    }

    private static bool ParseBool(object value, bool fallback)
    {
        if (value is bool b) return b;
        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.True) return true;
            if (je.ValueKind == JsonValueKind.False) return false;
        }
        return fallback;
    }
}
