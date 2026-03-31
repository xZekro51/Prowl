// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Services;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Prowl.Editor.Importing;

/// <summary>
/// Imports texture files applying settings from the .meta file.
/// This is the central place to load texture assets with correct
/// import settings (wrap mode, filters, mipmaps).
/// </summary>
public static class TextureImporter
{
    private static readonly HashSet<string> TextureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".hdr"
    };

    /// <summary> Returns true if the file extension represents a texture file. </summary>
    public static bool IsTextureFile(string extension)
        => TextureExtensions.Contains(extension);

    /// <summary>
    /// Loads a texture from disk, applying import settings from the
    /// associated .meta file if available.
    /// </summary>
    public static Texture2D? Import(string absolutePath)
    {
        if (!File.Exists(absolutePath))
            return null;

        try
        {
            var settings = LoadSettings(absolutePath);
            var texture = Texture2D.FromFile(absolutePath, settings.GenerateMipmaps);
            texture.Name = Path.GetFileNameWithoutExtension(absolutePath);
            texture.AssetPath = absolutePath;

            texture.SetTextureFilters(settings.MinFilter, settings.MagFilter);
            texture.SetWrapModes(settings.WrapMode, settings.WrapMode);

            return texture;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[TextureImporter] Failed to import '{Path.GetFileName(absolutePath)}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads import settings for a texture from its .meta file.
    /// Returns default settings if no .meta file or no import settings exist.
    /// </summary>
    public static TextureImportSettings LoadSettings(string absolutePath)
    {
        string metaPath = MetaFile.GetMetaPath(absolutePath);
        MetaFile? meta = MetaFile.Load(metaPath);
        if (meta != null && meta.ImportSettings.Count > 0)
            return TextureImportSettings.FromMeta(meta.ImportSettings);
        return new TextureImportSettings();
    }

    /// <summary>
    /// Saves import settings for a texture into its .meta file and
    /// optionally reimports the texture.
    /// </summary>
    public static void SaveSettings(string absolutePath, TextureImportSettings settings)
    {
        string metaPath = MetaFile.GetMetaPath(absolutePath);
        MetaFile? meta = MetaFile.Load(metaPath);
        if (meta == null)
        {
            meta = MetaFile.CreateNew();
        }

        settings.WriteTo(meta.ImportSettings);
        meta.Save(metaPath);
    }
}
