// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using System.Numerics;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Rendering.Shaders;
using Prowl.Runtime.Resources;
using Prowl.Editor.Icons;
using Prowl.Editor.Services;

namespace Prowl.Editor.Inspector;

/// <summary>
/// Draws an inline material editor inside the Inspector panel.
/// Shows shader properties (colors, floats, textures) defined by the
/// material's shader, or a set of common properties as a fallback.
/// Supports assigning shaders from project .shader assets and saving
/// material changes back to .mat files.
/// </summary>
public static class MaterialInspector
{
    /// <summary> Label column width ratio (0–1). </summary>
    private const float LabelRatio = 0.35f;

    private static string _shaderPickerFilter = string.Empty;
    private static bool _shaderPickerOpen = false;

    /// <summary>
    /// Draws the material editor UI for a single <see cref="Material"/>.
    /// Should be called within an ImGui context (e.g. inside a collapsing header).
    /// </summary>
    public static void DrawMaterial(Material material) => DrawMaterial(material, null);

    /// <summary>
    /// Draws the material editor UI with optional save support.
    /// When <paramref name="materialFilePath"/> is provided, a Save button is shown.
    /// </summary>
    public static void DrawMaterial(Material material, string? materialFilePath)
    {
        if (material == null)
        {
            ImGui.TextDisabled("(no material)");
            return;
        }

        // Material name / info
        ImGui.TextColored(new Vector4(0.70f, 0.80f, 0.90f, 1f), material.Name ?? "Unnamed Material");

        Shader? shader = material.Shader;
        if (shader != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"[{shader.Name ?? "Shader"}]");
        }

        ImGui.Spacing();

        // ── Shader picker ──────────────────────────────────────
        DrawShaderField(material);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Draw properties from shader definition if available
        shader = material.Shader; // Re-read in case shader was changed above
        if (shader != null)
        {
            DrawShaderProperties(material, shader);
        }
        else
        {
            // Fallback: draw common properties
            DrawCommonProperties(material);
        }

        // ── Save button ────────────────────────────────────────
        if (materialFilePath != null)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            float avail = ImGui.GetContentRegionAvail().X;
            float btnW = MathF.Min(160 * Game.DpiScale, avail);
            ImGui.SetCursorPosX((avail - btnW) * 0.5f + ImGui.GetCursorPosX());

            if (ImGui.Button("Save Material", new Vector2(btnW, 0)))
            {
                try
                {
                    MaterialSerializer.Save(material, materialFilePath);
                    Debug.Log($"[Material] Saved: {materialFilePath}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Material] Failed to save: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Draws a shader reference field with a picker popup.
    /// Allows assigning .shader assets from the project or built-in default shaders.
    /// </summary>
    private static void DrawShaderField(Material material)
    {
        Shader? currentShader = material.Shader;
        string displayName = currentShader != null
            ? (currentShader.Name ?? currentShader.AssetPath ?? "Shader")
            : "None (Shader)";

        DrawRow("Shader", () =>
        {
            float availW = ImGui.GetContentRegionAvail().X;

            ImGui.PushStyleColor(ImGuiCol.Button, currentShader != null
                ? new Vector4(0.22f, 0.28f, 0.35f, 1f)
                : new Vector4(0.20f, 0.20f, 0.20f, 1f));

            if (ImGui.Button(displayName, new Vector2(availW, 0)))
            {
                _shaderPickerOpen = true;
                _shaderPickerFilter = string.Empty;
                ImGui.OpenPopup("##ShaderPicker");
            }

            ImGui.PopStyleColor();

            // Drag-drop: accept .shader assets
            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload("ASSET_ENTRY");
                unsafe
                {
                    if (payload.NativePtr != null && payload.DataSize > 0)
                    {
                        string data = System.Text.Encoding.UTF8.GetString(
                            (byte*)payload.Data, payload.DataSize).TrimEnd('\0');

                        TryAssignShaderFromDrop(material, data);
                    }
                }
                ImGui.EndDragDropTarget();
            }

            // Also accept EditorDragDrop
            if (ImGui.IsItemHovered() && EditorDragDrop.IsDragging &&
                EditorDragDrop.PayloadType == "AssetEntry" &&
                EditorDragDrop.Payload is AssetEntry dragEntry &&
                dragEntry.Extension.Equals(".shader", StringComparison.OrdinalIgnoreCase))
            {
                ImGui.SetTooltip("Drop shader here");
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    TryAssignShaderFromFile(material, dragEntry.FullPath);
                    EditorDragDrop.Clear();
                }
            }

            // Shader picker popup
            if (_shaderPickerOpen && ImGui.BeginPopup("##ShaderPicker"))
            {
                ImGui.Text("Select Shader");
                ImGui.Separator();
                ImGui.InputText("##filter", ref _shaderPickerFilter, 256);

                ImGui.BeginChild("##shaderList", new Vector2(280 * Game.DpiScale, 250 * Game.DpiScale));

                // Built-in default shaders
                ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "Built-in:");
                foreach (DefaultShader ds in Enum.GetValues<DefaultShader>())
                {
                    string name = ds.ToString();
                    if (!string.IsNullOrEmpty(_shaderPickerFilter) &&
                        !name.Contains(_shaderPickerFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (ImGui.Selectable($"  {name}"))
                    {
                        try
                        {
                            material.Shader = Shader.LoadDefault(ds);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[MaterialInspector] Failed to load default shader '{name}': {ex.Message}");
                        }
                        _shaderPickerOpen = false;
                        ImGui.CloseCurrentPopup();
                    }
                }

                // Project .shader files
                if (EditorServices.TryGet<IAssetService>(out var assetSvc) && assetSvc!.HasProject)
                {
                    var shaderEntries = assetSvc.GetAllEntriesRecursive(".shader");
                    if (shaderEntries.Count > 0)
                    {
                        ImGui.Spacing();
                        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "Project:");
                        foreach (var entry in shaderEntries)
                        {
                            if (!string.IsNullOrEmpty(_shaderPickerFilter) &&
                                !entry.Name.Contains(_shaderPickerFilter, StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (ImGui.Selectable($"  {entry.Name}"))
                            {
                                TryAssignShaderFromFile(material, entry.FullPath);
                                _shaderPickerOpen = false;
                                ImGui.CloseCurrentPopup();
                            }
                        }
                    }
                }

                ImGui.EndChild();
                ImGui.EndPopup();
            }
        });
    }

    private static void TryAssignShaderFromDrop(Material material, string data)
    {
        if (EditorServices.TryGet<IAssetService>(out var assetSvc))
        {
            string? resolvedPath = assetSvc!.GetAssetPathByGuid(data);
            string relativePath = resolvedPath ?? data;
            string absPath = assetSvc.GetAbsolutePath(relativePath);

            if (absPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                TryAssignShaderFromFile(material, absPath);
        }
    }

    private static void TryAssignShaderFromFile(Material material, string filePath)
    {
        try
        {
            var shader = Shader.LoadFromFile(filePath);
            if (shader != null)
                material.Shader = shader;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MaterialInspector] Failed to load shader: {ex.Message}");
        }
    }

    /// <summary>
    /// Draws editable properties defined by the material's shader.
    /// </summary>
    private static void DrawShaderProperties(Material material, Shader shader)
    {
        bool anyDrawn = false;

        foreach (ShaderProperty prop in shader.Properties)
        {
            if (string.IsNullOrEmpty(prop.Name)) continue;

            string label = !string.IsNullOrEmpty(prop.DisplayName) ? prop.DisplayName : FormatPropertyName(prop.Name);
            ImGui.PushID(prop.Name);

            switch (prop.PropertyType)
            {
                case ShaderPropertyType.Color:
                    DrawColorProperty(material, prop.Name, label);
                    anyDrawn = true;
                    break;

                case ShaderPropertyType.Float:
                    DrawFloatProperty(material, prop.Name, label);
                    anyDrawn = true;
                    break;

                case ShaderPropertyType.Vector2:
                    DrawVector2Property(material, prop.Name, label);
                    anyDrawn = true;
                    break;

                case ShaderPropertyType.Vector3:
                    DrawVector3Property(material, prop.Name, label);
                    anyDrawn = true;
                    break;

                case ShaderPropertyType.Vector4:
                    DrawVector4Property(material, prop.Name, label);
                    anyDrawn = true;
                    break;

                case ShaderPropertyType.Texture2D:
                    DrawTextureProperty(material, prop.Name, label);
                    anyDrawn = true;
                    break;

                case ShaderPropertyType.Matrix:
                    // Matrices are typically not user-editable; show read-only
                    DrawRow(label, () => ImGui.TextDisabled("(matrix)"));
                    anyDrawn = true;
                    break;
            }

            ImGui.PopID();
        }

        if (!anyDrawn)
        {
            ImGui.TextDisabled("No editable properties.");
        }
    }

    /// <summary>
    /// Fallback: draw common material properties that most shaders use.
    /// </summary>
    private static void DrawCommonProperties(Material material)
    {
        ImGui.PushID("##CommonMat");

        DrawColorProperty(material, "_MainColor", "Main Color");
        DrawFloatProperty(material, "_Glossiness", "Glossiness");
        DrawFloatProperty(material, "_Metallic", "Metallic");
        DrawTextureProperty(material, "_MainTex", "Main Texture");

        ImGui.PopID();
    }

    // ────────────────────────────────────────────────────────────
    // Individual property editors
    // ────────────────────────────────────────────────────────────

    private static void DrawColorProperty(Material material, string propName, string label)
    {
        Prowl.Vector.Color color = material._properties.GetColor(propName);
        var nv = new Vector4(color.R, color.G, color.B, color.A);

        DrawRow(label, () =>
        {
            // Small color swatch preview
            var swatchPos = ImGui.GetCursorScreenPos();
            float swatchSz = ImGui.GetFrameHeight();
            var drawList = ImGui.GetWindowDrawList();
            uint swatchCol = ImGui.GetColorU32(nv);
            drawList.AddRectFilled(swatchPos, new Vector2(swatchPos.X + swatchSz, swatchPos.Y + swatchSz), swatchCol);
            drawList.AddRect(swatchPos, new Vector2(swatchPos.X + swatchSz, swatchPos.Y + swatchSz),
                ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));
            ImGui.Dummy(new Vector2(swatchSz, swatchSz));
            ImGui.SameLine();

            ImGui.SetNextItemWidth(-1);
            if (ImGui.ColorEdit4("##col", ref nv, ImGuiColorEditFlags.AlphaBar))
            {
                material.SetColor(propName, new Prowl.Vector.Color(nv.X, nv.Y, nv.Z, nv.W));
            }
        });
    }

    private static void DrawFloatProperty(Material material, string propName, string label)
    {
        float value = material._properties.GetFloat(propName);

        DrawRow(label, () =>
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.DragFloat("##flt", ref value, 0.01f))
            {
                material.SetFloat(propName, value);
            }
        });
    }

    private static void DrawVector2Property(Material material, string propName, string label)
    {
        Prowl.Vector.Float2 pv = material._properties.GetVector2(propName);
        var nv = new Vector2(pv.X, pv.Y);

        DrawRow(label, () =>
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.DragFloat2("##v2", ref nv, 0.01f))
            {
                material.SetVector(propName, new Prowl.Vector.Float2(nv.X, nv.Y));
            }
        });
    }

    private static void DrawVector3Property(Material material, string propName, string label)
    {
        Prowl.Vector.Float3 pv = material._properties.GetVector3(propName);
        var nv = new Vector3(pv.X, pv.Y, pv.Z);

        DrawRow(label, () =>
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.DragFloat3("##v3", ref nv, 0.01f))
            {
                material.SetVector(propName, new Prowl.Vector.Float3(nv.X, nv.Y, nv.Z));
            }
        });
    }

    private static void DrawVector4Property(Material material, string propName, string label)
    {
        Prowl.Vector.Float4 pv = material._properties.GetVector4(propName);
        var nv = new Vector4(pv.X, pv.Y, pv.Z, pv.W);

        DrawRow(label, () =>
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.DragFloat4("##v4", ref nv, 0.01f))
            {
                material.SetVector(propName, new Prowl.Vector.Float4(nv.X, nv.Y, nv.Z, nv.W));
            }
        });
    }

    private static readonly string[] TextureExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".hdr"];

    private static void DrawTextureProperty(Material material, string propName, string label)
    {
        Texture2D? tex = material._properties.GetTexture(propName);

        DrawRow(label, () =>
        {
            float thumbSz = 48f * Game.DpiScale;
            float availW = ImGui.GetContentRegionAvail().X;
            float clearBtnW = 15 * Game.DpiScale;
            float refBtnW = availW - clearBtnW - thumbSz - ImGui.GetStyle().ItemSpacing.X * 3;
            if (refBtnW < 30 * Game.DpiScale) refBtnW = 30 * Game.DpiScale;

            // Thumbnail preview
            if (tex != null && tex.IsValid())
            {
                nint texId = (nint)tex.Handle.Handle;
                ImGui.Image(texId, new Vector2(thumbSz, thumbSz));
            }
            else
            {
                // Placeholder rectangle
                var pos = ImGui.GetCursorScreenPos();
                var drawList = ImGui.GetWindowDrawList();
                drawList.AddRectFilled(pos, new Vector2(pos.X + thumbSz, pos.Y + thumbSz),
                    ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f)));
                drawList.AddRect(pos, new Vector2(pos.X + thumbSz, pos.Y + thumbSz),
                    ImGui.GetColorU32(new Vector4(0.35f, 0.35f, 0.35f, 1f)));
                ImGui.Dummy(new Vector2(thumbSz, thumbSz));
            }

            ImGui.SameLine();

            ImGui.BeginGroup();

            // Asset reference button — shows current texture name
            string displayName = tex != null ? $"{tex.Name ?? "(texture)"} (Texture2D)" : "None (Texture2D)";

            ImGui.PushStyleColor(ImGuiCol.Button, tex != null
                ? new Vector4(0.22f, 0.30f, 0.22f, 1f)
                : new Vector4(0.20f, 0.20f, 0.20f, 1f));
            ImGui.Button(displayName, new Vector2(refBtnW, 0));
            ImGui.PopStyleColor();

            // Single-click on reference button → ping texture asset in project view
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && tex != null && !string.IsNullOrEmpty(tex.AssetPath))
            {
                PingTextureAsset(tex);
            }

            // Drag-drop target on the reference button: accept texture assets from the Project panel
            AcceptTextureDrop(material, propName);

            // Also accept EditorDragDrop
            if (ImGui.IsItemHovered() && EditorDragDrop.IsDragging &&
                EditorDragDrop.PayloadType == "AssetEntry" &&
                EditorDragDrop.Payload is AssetEntry dragEntry &&
                IsTextureFile(dragEntry.Extension))
            {
                ImGui.SetTooltip("Drop texture here");
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    TryAssignTextureFromFile(material, propName, dragEntry.FullPath);
                    EditorDragDrop.Clear();
                }
            }

            // Clear button (X)
            ImGui.SameLine();
            if (ImGui.Button("\u2716##clr", new Vector2(clearBtnW, 0)))
            {
                material.SetTexture(propName, null);
            }

            // Picker button: open texture picker popup
            string pickerId = $"##TexPicker_{propName}";
            if (ImGui.Button("Pick...##pick", new Vector2(refBtnW + clearBtnW + ImGui.GetStyle().ItemSpacing.X, 0)))
            {
                ImGui.OpenPopup(pickerId);
            }

            // Texture picker popup
            if (ImGui.BeginPopup(pickerId))
            {
                ImGui.Text("Select Texture");
                ImGui.Separator();

                // Built-in defaults
                ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "Built-in:");
                foreach (DefaultTexture dt in Enum.GetValues<DefaultTexture>())
                {
                    if (ImGui.Selectable($"  {dt}"))
                    {
                        material.SetTexture(propName, Texture2D.LoadDefault(dt));
                        ImGui.CloseCurrentPopup();
                    }
                }

                // Project texture files
                if (EditorServices.TryGet<IAssetService>(out var assetSvc) && assetSvc!.HasProject)
                {
                    var allEntries = assetSvc.GetAllEntriesRecursive();
                    bool headerShown = false;
                    foreach (var entry in allEntries)
                    {
                        if (entry.IsDirectory) continue;
                        if (!IsTextureFile(entry.Extension)) continue;

                        if (!headerShown)
                        {
                            ImGui.Spacing();
                            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "Project:");
                            headerShown = true;
                        }

                        if (ImGui.Selectable($"  {entry.Name}"))
                        {
                            TryAssignTextureFromFile(material, propName, entry.FullPath);
                            ImGui.CloseCurrentPopup();
                        }
                    }
                }

                ImGui.EndPopup();
            }

            ImGui.EndGroup();
        });
    }

    private static bool IsTextureFile(string extension)
    {
        string ext = extension.ToLowerInvariant();
        foreach (string texExt in TextureExtensions)
            if (ext == texExt) return true;
        return false;
    }

    private static void AcceptTextureDrop(Material material, string propName)
    {
        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload("ASSET_ENTRY");
            unsafe
            {
                if (payload.NativePtr != null && payload.DataSize > 0)
                {
                    string data = System.Text.Encoding.UTF8.GetString(
                        (byte*)payload.Data, payload.DataSize).TrimEnd('\0');

                    if (EditorServices.TryGet<IAssetService>(out var assetSvc))
                    {
                        string? resolvedPath = assetSvc!.GetAssetPathByGuid(data);
                        string relativePath = resolvedPath ?? data;
                        string absPath = assetSvc.GetAbsolutePath(relativePath);

                        if (IsTextureFile(Path.GetExtension(absPath)))
                            TryAssignTextureFromFile(material, propName, absPath);
                    }
                }
            }
            ImGui.EndDragDropTarget();
        }
    }

    private static void TryAssignTextureFromFile(Material material, string propName, string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var newTex = Texture2D.LoadFromFile(filePath, generateMipmaps: true);
                newTex.Name = Path.GetFileNameWithoutExtension(filePath);
                newTex.AssetPath = filePath;
                material.SetTexture(propName, newTex);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MaterialInspector] Failed to load texture: {ex.Message}");
        }
    }

    private static void PingTextureAsset(Texture2D tex)
    {
        if (!EditorServices.TryGet<IAssetService>(out var assets) || !assets!.HasProject)
            return;

        string? relPath = null;
        string? absPath = tex.AssetPath;

        if (!string.IsNullOrEmpty(absPath) && absPath.StartsWith(assets.AssetRootPath, StringComparison.OrdinalIgnoreCase))
        {
            try { relPath = Path.GetRelativePath(assets.AssetRootPath, absPath).Replace('\\', '/'); }
            catch { /* ignore */ }
        }

        // Fallback: search by name
        if (relPath == null && !string.IsNullOrEmpty(tex.Name))
        {
            var allEntries = assets.GetAllEntriesRecursive();
            foreach (var entry in allEntries)
            {
                if (entry.IsDirectory) continue;
                string nameNoExt = Path.GetFileNameWithoutExtension(entry.Name);
                if (nameNoExt.Equals(tex.Name, StringComparison.OrdinalIgnoreCase) && IsTextureFile(entry.Extension))
                {
                    relPath = entry.RelativePath;
                    break;
                }
            }
        }

        if (relPath != null && EditorServices.TryGet<Panels.ProjectPanel>(out var projectPanel))
        {
            projectPanel!.PingAsset(relPath);
        }
    }

    // ────────────────────────────────────────────────────────────
    // Layout helper
    // ────────────────────────────────────────────────────────────

    private static void DrawRow(string label, Action drawValue)
    {
        if (ImGui.BeginTable("##mr_" + label, 2, ImGuiTableFlags.None))
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

    /// <summary>
    /// Converts "_propertyName" to "Property Name".
    /// </summary>
    private static string FormatPropertyName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        int start = 0;
        while (start < name.Length && name[start] == '_') start++;
        if (start >= name.Length) return name;

        var sb = new System.Text.StringBuilder(name.Length + 4);
        sb.Append(char.ToUpper(name[start]));

        for (int i = start + 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > start + 1 && !char.IsUpper(name[i - 1]))
                sb.Append(' ');
            sb.Append(name[i]);
        }

        return sb.ToString();
    }
}
