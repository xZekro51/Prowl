// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;

using ImGuiNET;

using Prowl.Editor.Docking;
using Prowl.Editor.Importing;
using Prowl.Editor.Services;
using Prowl.Runtime;
using Prowl.Runtime.EventSystem;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Text;

namespace Prowl.Editor.Panels;

/// <summary>
/// Dedicated editor window for creating <see cref="FontAsset"/> files from
/// .ttf / .otf source fonts. Provides an interactive UI for configuring
/// atlas generation settings, previewing the result, and saving to the project.
/// </summary>
public sealed class FontAssetCreatorPanel : EditorPanel
{
    // ── Source font ──────────────────────────────────────────
    private string _sourceFontPath = string.Empty;
    private string _sourceFontName = string.Empty;

    // ── Generation settings ─────────────────────────────────
    private int _atlasTypeIdx;               // 0=SDF, 1=MSDF, 2=Bitmap
    private int _pointSize = 48;
    private int _atlasResolutionIdx = 2;     // index into _resOptions
    private float _pxRange = 6f;
    private int _padding = 2;
    private int _charSetIdx;                 // 0=ASCII, 1=LatinExtended, 2=Custom
    private string _customChars = string.Empty;
    private int _sdfOversample = 4;
    private bool _generateMipmaps = false;

    // ── Output ──────────────────────────────────────────────
    private string _outputName = "NewFontAsset";
    private string _outputFolder = string.Empty;   // relative to Assets/

    // ── Preview / generation state ──────────────────────────
    private FontAsset? _previewAsset;
    private string? _previewError;
    private bool _isGenerating;
    private string _statusMessage = string.Empty;

    private static readonly string[] s_atlasTypes = ["SDF", "MSDF", "Bitmap"];
    private static readonly string[] s_resLabels = ["Auto", "256", "512", "1024", "2048", "4096"];
    private static readonly int[] s_resValues = [0, 256, 512, 1024, 2048, 4096];
    private static readonly string[] s_charSets = ["ASCII", "LatinExtended", "Custom"];

    /// <summary> Label column width ratio. </summary>
    private const float LabelRatio = 0.35f;

    public FontAssetCreatorPanel() : base("Font Asset Creator")
    {
        IsOpen = false;

        // Auto-refresh preview when assets are imported (e.g. font files added to project)
        AssetEvents.SubscribeOnAssetsImported(OnAssetsImported);
    }

    private void OnAssetsImported(AssetImportedArgs args)
    {
        // If a font file was imported, clear cached state so it can be picked up
        if (args.ImportedPaths == null) return;
        foreach (string path in args.ImportedPaths)
        {
            string ext = Path.GetExtension(path);
            if (FontAssetImporter.IsFontFile(ext))
            {
                _statusMessage = $"New font file detected: {Path.GetFileName(path)}";
                break;
            }
        }
    }

    protected override void DrawContent()
    {
        ImGui.TextColored(new Vector4(0.90f, 0.90f, 0.90f, 1f), "Font Asset Creator");
        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f),
            "Create SDF / MSDF / Bitmap font atlas assets from .ttf / .otf files.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Source Font ────────────────────────────────────────
        if (ImGui.CollapsingHeader("Source Font", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawSourceFontSection();
        }

        ImGui.Spacing();

        // ── Generation Settings ────────────────────────────────
        if (ImGui.CollapsingHeader("Generation Settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawGenerationSettings();
        }

        ImGui.Spacing();

        // ── Output ─────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Output", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawOutputSection();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Action buttons ─────────────────────────────────────
        DrawActionButtons();

        // ── Preview ────────────────────────────────────────────
        if (_previewAsset != null || _previewError != null)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.CollapsingHeader("Preview", ImGuiTreeNodeFlags.DefaultOpen))
            {
                DrawPreviewSection();
            }
        }

        // ── Status ─────────────────────────────────────────────
        if (!string.IsNullOrEmpty(_statusMessage))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.70f, 0.85f, 0.70f, 1f), _statusMessage);
        }

        // ── Popups (must be drawn every frame) ─────────────────
        DrawFontFilePicker();
        DrawOutputFolderPicker();

        // ── Drag-drop target for the whole window ──────────────
        HandleDragDrop();
    }

    // ────────────────────────────────────────────────────────────
    // Source Font
    // ────────────────────────────────────────────────────────────

    private void DrawSourceFontSection()
    {
        DrawRow("Font File", () =>
        {
            float avail = ImGui.GetContentRegionAvail().X;
            float btnW = 80 * Game.DpiScale;
            float fieldW = avail - btnW - ImGui.GetStyle().ItemSpacing.X;
            if (fieldW < 60) fieldW = 60;

            ImGui.SetNextItemWidth(fieldW);
            ImGui.InputText("##fontPath", ref _sourceFontPath, 1024, ImGuiInputTextFlags.ReadOnly);

            ImGui.SameLine();
            if (ImGui.Button("Browse...", new Vector2(btnW, 0)))
            {
                BrowseForFont();
            }
        });

        if (!string.IsNullOrEmpty(_sourceFontName))
        {
            DrawRow("Name", () =>
            {
                ImGui.TextUnformatted(_sourceFontName);
            });
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f),
            "Drag & drop a .ttf / .otf file onto this window to set the source font.");
    }

    // ────────────────────────────────────────────────────────────
    // Generation Settings
    // ────────────────────────────────────────────────────────────

    private void DrawGenerationSettings()
    {
        // Atlas Type
        DrawRow("Atlas Type", () =>
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.Combo("##atlasType", ref _atlasTypeIdx, s_atlasTypes, s_atlasTypes.Length);
        });

        // Point Size
        DrawRow("Point Size", () =>
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.DragInt("##pointSize", ref _pointSize, 1f, 8, 200);
        });

        // Atlas Resolution
        DrawRow("Atlas Resolution", () =>
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.Combo("##atlasRes", ref _atlasResolutionIdx, s_resLabels, s_resLabels.Length);
        });

        // Px Range
        DrawRow("SDF Px Range", () =>
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.DragFloat("##pxRange", ref _pxRange, 0.5f, 1f, 32f, "%.1f");
        });

        // Padding
        DrawRow("Padding", () =>
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.DragInt("##padding", ref _padding, 1f, 0, 16);
        });

        // Character Set
        DrawRow("Character Set", () =>
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.Combo("##charSet", ref _charSetIdx, s_charSets, s_charSets.Length);
        });

        // Custom characters
        if (_charSetIdx == 2)
        {
            DrawRow("Characters", () =>
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextMultiline("##customChars", ref _customChars, 8192,
                    new Vector2(-1, 60 * Game.DpiScale));
            });
        }

        // SDF Oversample (only for SDF mode)
        if (_atlasTypeIdx == 0)
        {
            DrawRow("SDF Oversample", () =>
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.DragInt("##sdfOversample", ref _sdfOversample, 1f, 1, 8);
            });
        }

        // Generate Mipmaps (mainly for Bitmap atlases)
        DrawRow("Mipmaps", () =>
        {
            ImGui.Checkbox("##mipmaps", ref _generateMipmaps);
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f),
                _atlasTypeIdx == 2 ? "(recommended for Bitmap)" : "(not recommended for SDF/MSDF)");
        });

        // Character count preview
        FontImportSettings tempSettings = BuildSettings();
        int codepointCount = tempSettings.GetCodepoints().Count;
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f),
            $"Character set contains {codepointCount} codepoints");
    }

    // ────────────────────────────────────────────────────────────
    // Output
    // ────────────────────────────────────────────────────────────

    private void DrawOutputSection()
    {
        DrawRow("Asset Name", () =>
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##outputName", ref _outputName, 256);
        });

        DrawRow("Output Folder", () =>
        {
            float avail = ImGui.GetContentRegionAvail().X;
            float btnW = 80 * Game.DpiScale;
            float fieldW = avail - btnW - ImGui.GetStyle().ItemSpacing.X;
            if (fieldW < 60) fieldW = 60;

            ImGui.SetNextItemWidth(fieldW);
            string display = string.IsNullOrEmpty(_outputFolder) ? "Assets/" : $"Assets/{_outputFolder}";
            ImGui.InputText("##outputFolder", ref display, 512, ImGuiInputTextFlags.ReadOnly);

            ImGui.SameLine();
            if (ImGui.Button("Select...", new Vector2(btnW, 0)))
            {
                BrowseForOutputFolder();
            }
        });
    }

    // ────────────────────────────────────────────────────────────
    // Action Buttons
    // ────────────────────────────────────────────────────────────

    private void DrawActionButtons()
    {
        bool hasSource = !string.IsNullOrEmpty(_sourceFontPath) && File.Exists(_sourceFontPath);

        float avail = ImGui.GetContentRegionAvail().X;
        float btnW = 140 * Game.DpiScale;
        float totalBtnW = btnW * 3 + ImGui.GetStyle().ItemSpacing.X * 2;
        float startX = (avail - totalBtnW) * 0.5f;
        if (startX < 0) startX = 0;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + startX);

        // Preview button
        if (!hasSource) ImGui.BeginDisabled();
        if (ImGui.Button("Preview", new Vector2(btnW, 0)))
        {
            GeneratePreview();
        }
        if (!hasSource) ImGui.EndDisabled();

        ImGui.SameLine();

        // Generate & Save button
        bool canSave = hasSource && !_isGenerating;
        if (!canSave) ImGui.BeginDisabled();
        if (ImGui.Button("Generate & Save", new Vector2(btnW, 0)))
        {
            GenerateAndSave();
        }
        if (!canSave) ImGui.EndDisabled();

        ImGui.SameLine();

        // Reset button
        if (ImGui.Button("Reset", new Vector2(btnW, 0)))
        {
            ResetState();
        }
    }

    // ────────────────────────────────────────────────────────────
    // Preview
    // ────────────────────────────────────────────────────────────

    private void DrawPreviewSection()
    {
        if (_previewError != null)
        {
            ImGui.TextColored(new Vector4(0.95f, 0.30f, 0.30f, 1f), $"Error: {_previewError}");
            return;
        }

        if (_previewAsset == null) return;

        DrawRow("Glyphs", () => ImGui.TextUnformatted(_previewAsset.GlyphTable.Count.ToString()));
        DrawRow("Atlas Type", () => ImGui.TextUnformatted(_previewAsset.AtlasType.ToString()));
        DrawRow("Atlas Size", () => ImGui.TextUnformatted($"{_previewAsset.AtlasWidth} x {_previewAsset.AtlasHeight}"));
        DrawRow("Point Size", () => ImGui.TextUnformatted(_previewAsset.PointSize.ToString("F0")));
        DrawRow("Line Height", () => ImGui.TextUnformatted(_previewAsset.LineHeight.ToString("F1")));
        DrawRow("Ascender", () => ImGui.TextUnformatted(_previewAsset.Ascender.ToString("F1")));
        DrawRow("Descender", () => ImGui.TextUnformatted(_previewAsset.Descender.ToString("F1")));
        DrawRow("Px Range", () => ImGui.TextUnformatted(_previewAsset.AtlasPxRange.ToString("F1")));

        // Glyph table preview
        if (_previewAsset.GlyphTable.Count > 0 && ImGui.TreeNodeEx("Glyph Table", ImGuiTreeNodeFlags.None))
        {
            int shown = 0;
            foreach (GlyphData glyph in _previewAsset.GlyphTable)
            {
                if (shown >= 100)
                {
                    ImGui.TextDisabled($"... ({_previewAsset.GlyphTable.Count - shown} more)");
                    break;
                }
                string ch = glyph.GlyphIndex < 0x10000 ? $"U+{glyph.GlyphIndex:X4}" : $"U+{glyph.GlyphIndex:X6}";
                string charStr = glyph.GlyphIndex < char.MaxValue && !char.IsControl((char)glyph.GlyphIndex)
                    ? $" '{(char)glyph.GlyphIndex}'"
                    : "";
                ImGui.BulletText($"{ch}{charStr}  W:{glyph.Width:F0} H:{glyph.Height:F0} Adv:{glyph.Advance:F1}");
                shown++;
            }
            ImGui.TreePop();
        }
    }

    // ────────────────────────────────────────────────────────────
    // Drag-drop
    // ────────────────────────────────────────────────────────────

    private void HandleDragDrop()
    {
        // Accept EditorDragDrop payloads (from ProjectPanel)
        if (EditorDragDrop.IsDragging &&
            EditorDragDrop.PayloadType == "AssetEntry" &&
            EditorDragDrop.Payload is AssetEntry entry &&
            FontAssetImporter.IsFontFile(entry.Extension))
        {
            // Draw a highlight over the window
            Vector2 winPos = ImGui.GetWindowPos();
            Vector2 winSize = ImGui.GetWindowSize();
            ImGui.GetWindowDrawList().AddRectFilled(
                winPos, winPos + winSize,
                ImGui.GetColorU32(new Vector4(0.28f, 0.56f, 1.0f, 0.15f)));

            ImGui.SetCursorPos(new Vector2(10, ImGui.GetWindowHeight() - 30));
            ImGui.TextColored(new Vector4(0.45f, 0.65f, 1.0f, 1.0f), "Drop font file here...");

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                SetSourceFont(entry.FullPath);
                EditorDragDrop.Clear();
            }
        }
    }

    // ────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────

    private FontImportSettings BuildSettings()
    {
        return new FontImportSettings
        {
            AtlasType = (AtlasType)_atlasTypeIdx,
            PointSize = _pointSize,
            AtlasResolution = s_resValues[_atlasResolutionIdx],
            PxRange = _pxRange,
            Padding = _padding,
            CharacterSet = s_charSets[_charSetIdx],
            CustomCharacters = _customChars,
            SdfOversample = _sdfOversample,
            GenerateMipmaps = _generateMipmaps,
        };
    }

    private void SetSourceFont(string absolutePath)
    {
        _sourceFontPath = absolutePath;
        _sourceFontName = Path.GetFileNameWithoutExtension(absolutePath);
        _outputName = _sourceFontName;
        _previewAsset?.Dispose();
        _previewAsset = null;
        _previewError = null;
        _statusMessage = $"Source font set: {_sourceFontName}";
    }

    private void BrowseForFont()
    {
        // Look for .ttf/.otf files in the project's Assets folder
        if (!EditorServices.TryGet<IAssetService>(out IAssetService? assetSvc) || !assetSvc!.HasProject)
        {
            _statusMessage = "No project open — cannot browse for fonts.";
            return;
        }

        IReadOnlyList<AssetEntry> entries = assetSvc.GetAllEntriesRecursive();
        List<string> fontPaths = [];
        foreach (AssetEntry entry in entries)
        {
            if (!entry.IsDirectory && FontAssetImporter.IsFontFile(entry.Extension))
                fontPaths.Add(entry.FullPath);
        }

        if (fontPaths.Count == 0)
        {
            _statusMessage = "No .ttf / .otf files found in Assets/.";
            return;
        }

        // Open a picker popup
        ImGui.OpenPopup("##FontFilePicker");
        _fontPickerPaths = fontPaths;
    }

    private List<string>? _fontPickerPaths;
    private string _fontPickerFilter = string.Empty;

    /// <summary>
    /// Draws the font file picker popup (called every frame when open).
    /// Must be called from <see cref="DrawContent"/> to be in the right ImGui scope.
    /// </summary>
    private void DrawFontFilePicker()
    {
        if (_fontPickerPaths == null) return;

        if (ImGui.BeginPopup("##FontFilePicker"))
        {
            ImGui.Text("Select Font File");
            ImGui.Separator();
            ImGui.InputText("##filter", ref _fontPickerFilter, 256);

            ImGui.BeginChild("##fontList", new Vector2(350 * Game.DpiScale, 300 * Game.DpiScale));
            foreach (string path in _fontPickerPaths)
            {
                string name = Path.GetFileName(path);
                if (!string.IsNullOrEmpty(_fontPickerFilter) &&
                    !name.Contains(_fontPickerFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ImGui.Selectable(name))
                {
                    SetSourceFont(path);
                    _fontPickerPaths = null;
                    _fontPickerFilter = string.Empty;
                    ImGui.CloseCurrentPopup();
                    break;
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(path);
                    ImGui.EndTooltip();
                }
            }
            ImGui.EndChild();
            ImGui.EndPopup();
        }
        else
        {
            _fontPickerPaths = null;
        }
    }

    private void BrowseForOutputFolder()
    {
        if (!EditorServices.TryGet<IAssetService>(out IAssetService? assetSvc) || !assetSvc!.HasProject)
        {
            _statusMessage = "No project open.";
            return;
        }

        // List available folders in the project
        IReadOnlyList<AssetEntry> entries = assetSvc.GetAllEntriesRecursive();
        List<string> folders = [""];
        foreach (AssetEntry entry in entries)
        {
            if (entry.IsDirectory)
                folders.Add(entry.RelativePath);
        }

        ImGui.OpenPopup("##OutputFolderPicker");
        _folderPickerPaths = folders;
    }

    private List<string>? _folderPickerPaths;

    private void DrawOutputFolderPicker()
    {
        if (_folderPickerPaths == null) return;

        if (ImGui.BeginPopup("##OutputFolderPicker"))
        {
            ImGui.Text("Select Output Folder");
            ImGui.Separator();

            ImGui.BeginChild("##folderList", new Vector2(300 * Game.DpiScale, 250 * Game.DpiScale));
            foreach (string folder in _folderPickerPaths)
            {
                string display = string.IsNullOrEmpty(folder) ? "Assets/ (root)" : $"Assets/{folder}";
                if (ImGui.Selectable(display))
                {
                    _outputFolder = folder;
                    _folderPickerPaths = null;
                    ImGui.CloseCurrentPopup();
                    break;
                }
            }
            ImGui.EndChild();
            ImGui.EndPopup();
        }
        else
        {
            _folderPickerPaths = null;
        }
    }

    private void GeneratePreview()
    {
        _previewAsset?.Dispose();
        _previewAsset = null;
        _previewError = null;

        FontImportSettings settings = BuildSettings();
        try
        {
            _previewAsset = FontAssetImporter.Import(_sourceFontPath, settings);
            if (_previewAsset == null)
                _previewError = "Import returned null — check the console for details.";
            else
                _statusMessage = $"Preview generated: {_previewAsset.GlyphTable.Count} glyphs, " +
                                 $"{_previewAsset.AtlasWidth}x{_previewAsset.AtlasHeight} {_previewAsset.AtlasType} atlas.";
        }
        catch (Exception ex)
        {
            _previewError = ex.Message;
        }
    }

    private void GenerateAndSave()
    {
        if (!EditorServices.TryGet<IAssetService>(out IAssetService? assetSvc) || !assetSvc!.HasProject)
        {
            _statusMessage = "No project open — cannot save.";
            return;
        }

        _isGenerating = true;
        _statusMessage = "Generating...";

        try
        {
            FontImportSettings settings = BuildSettings();
            FontAsset? asset = FontAssetImporter.Import(_sourceFontPath, settings);
            if (asset == null)
            {
                _statusMessage = "Generation failed — check the console.";
                _isGenerating = false;
                return;
            }

            // Save the FontAsset as a .fontasset file
            string outputDir = string.IsNullOrEmpty(_outputFolder)
                ? assetSvc.AssetRootPath
                : Path.Combine(assetSvc.AssetRootPath, _outputFolder);

            Directory.CreateDirectory(outputDir);

            string fileName = _outputName;
            if (!fileName.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                fileName += ".asset";

            string outputPath = Path.Combine(outputDir, fileName);

            // Save using ScriptableObject serialization
            ScriptableObjectSerializer.Save(asset, outputPath);

            // Also save the import settings to the source font's .meta
            FontAssetImporter.SaveSettings(_sourceFontPath, settings);

            _statusMessage = $"Saved: {outputPath}";
            Runtime.Debug.Log($"[FontAssetCreator] Generated and saved font asset: {outputPath}");

            // Refresh the asset database
            assetSvc.Refresh();
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error: {ex.Message}";
            Runtime.Debug.LogError($"[FontAssetCreator] {ex.Message}");
        }
        finally
        {
            _isGenerating = false;
        }
    }

    private void ResetState()
    {
        _sourceFontPath = string.Empty;
        _sourceFontName = string.Empty;
        _atlasTypeIdx = 0;
        _pointSize = 48;
        _atlasResolutionIdx = 2;
        _pxRange = 6f;
        _padding = 2;
        _charSetIdx = 0;
        _customChars = string.Empty;
        _sdfOversample = 4;
        _outputName = "NewFontAsset";
        _outputFolder = string.Empty;
        _previewAsset?.Dispose();
        _previewAsset = null;
        _previewError = null;
        _statusMessage = "Settings reset.";
    }

    // ────────────────────────────────────────────────────────────
    // Layout helper
    // ────────────────────────────────────────────────────────────

    private static void DrawRow(string label, Action drawValue)
    {
        if (ImGui.BeginTable("##fc_" + label, 2, ImGuiTableFlags.None))
        {
            float totalW = ImGui.GetContentRegionAvail().X;
            ImGui.TableSetupColumn("lbl", ImGuiTableColumnFlags.WidthFixed, totalW * LabelRatio);
            ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);

            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1);
            drawValue();

            ImGui.EndTable();
        }
    }
}
