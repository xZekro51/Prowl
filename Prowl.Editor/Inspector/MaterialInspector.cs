// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

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
/// </summary>
public static class MaterialInspector
{
    /// <summary> Label column width ratio (0–1). </summary>
    private const float LabelRatio = 0.35f;

    /// <summary>
    /// Draws the material editor UI for a single <see cref="Material"/>.
    /// Should be called within an ImGui context (e.g. inside a collapsing header).
    /// </summary>
    public static void DrawMaterial(Material material)
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

        // Draw properties from shader definition if available
        if (shader != null)
        {
            DrawShaderProperties(material, shader);
        }
        else
        {
            // Fallback: draw common properties
            DrawCommonProperties(material);
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

    private static void DrawTextureProperty(Material material, string propName, string label)
    {
        Texture2D? tex = material._properties.GetTexture(propName);

        DrawRow(label, () =>
        {
            float thumbSz = 48f * Game.DpiScale;

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

            // Drag-drop target: accept texture assets from the Project panel
            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload("ASSET_ENTRY");
                unsafe
                {
                    if (payload.NativePtr != null && payload.DataSize > 0)
                    {
                        // In a full implementation, resolve the asset path and load
                        // the Texture2D. For now log the intent.
                        string data = System.Text.Encoding.UTF8.GetString(
                            (byte*)payload.Data, payload.DataSize).TrimEnd('\0');
                        Debug.Log($"[MaterialInspector] Texture drop on '{propName}': {data}");
                    }
                }
                ImGui.EndDragDropTarget();
            }

            // Also accept EditorDragDrop
            if (ImGui.IsItemHovered() && EditorDragDrop.IsDragging &&
                EditorDragDrop.PayloadType == "AssetEntry")
            {
                ImGui.SetTooltip("Drop texture here");
            }

            ImGui.SameLine();
            string texName = tex != null ? tex.Name ?? "(texture)" : "None";
            ImGui.TextUnformatted(texName);
        });
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
