// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;

namespace Prowl.Editor.Importing;

/// <summary>
/// Import settings for texture assets. Stored in the .meta file's
/// ImportSettings dictionary and applied when textures are loaded.
/// </summary>
public sealed class TextureImportSettings
{
    public TextureWrap WrapMode { get; set; } = TextureWrap.Repeat;
    public TextureMin MinFilter { get; set; } = TextureMin.NearestMipmapLinear;
    public TextureMag MagFilter { get; set; } = TextureMag.Nearest;
    public bool GenerateMipmaps { get; set; } = true;

    /// <summary> Reads settings from a MetaFile's ImportSettings dictionary. </summary>
    public static TextureImportSettings FromMeta(Dictionary<string, object?> importSettings)
    {
        var s = new TextureImportSettings();

        if (importSettings.TryGetValue("wrapMode", out var wm) && wm != null)
            s.WrapMode = ParseEnum<TextureWrap>(wm);
        if (importSettings.TryGetValue("minFilter", out var mn) && mn != null)
            s.MinFilter = ParseEnum<TextureMin>(mn);
        if (importSettings.TryGetValue("magFilter", out var mg) && mg != null)
            s.MagFilter = ParseEnum<TextureMag>(mg);
        if (importSettings.TryGetValue("generateMipmaps", out var gm) && gm != null)
            s.GenerateMipmaps = ParseBool(gm);

        return s;
    }

    /// <summary> Writes settings into a MetaFile's ImportSettings dictionary. </summary>
    public void WriteTo(Dictionary<string, object?> importSettings)
    {
        importSettings["wrapMode"] = (int)WrapMode;
        importSettings["minFilter"] = (int)MinFilter;
        importSettings["magFilter"] = (int)MagFilter;
        importSettings["generateMipmaps"] = GenerateMipmaps;
    }

    /// <summary> Creates a copy of these settings. </summary>
    public TextureImportSettings Clone() => new()
    {
        WrapMode = WrapMode,
        MinFilter = MinFilter,
        MagFilter = MagFilter,
        GenerateMipmaps = GenerateMipmaps,
    };

    private static T ParseEnum<T>(object value) where T : struct, Enum
    {
        if (value is System.Text.Json.JsonElement je)
        {
            if (je.ValueKind == System.Text.Json.JsonValueKind.Number)
                return (T)Enum.ToObject(typeof(T), je.GetInt32());
            if (je.ValueKind == System.Text.Json.JsonValueKind.String && Enum.TryParse<T>(je.GetString(), true, out var r))
                return r;
        }

        if (value is int i) return (T)Enum.ToObject(typeof(T), i);
        if (value is long l) return (T)Enum.ToObject(typeof(T), (int)l);
        if (value is string str && Enum.TryParse<T>(str, true, out var parsed)) return parsed;
        return default;
    }

    private static bool ParseBool(object value)
    {
        if (value is bool b) return b;
        if (value is System.Text.Json.JsonElement je)
        {
            if (je.ValueKind == System.Text.Json.JsonValueKind.True) return true;
            if (je.ValueKind == System.Text.Json.JsonValueKind.False) return false;
        }
        return true;
    }
}
