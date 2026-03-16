// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using System.Reflection;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.Runtime.Utils;
using Prowl.Editor.Docking;
using Prowl.Editor.Icons;
using Prowl.Editor.Services;
using Prowl.Editor.Undo;
using Prowl.Editor.Undo.Commands;

namespace Prowl.Editor.Panels;

/// <summary>
/// Inspector panel — displays and edits the properties of the currently
/// selected GameObject via Dear ImGui. Components are shown as collapsible
/// sections with per-field editors driven by reflection.
/// Uses a two-column table layout: label left (~35%) / value right (~65%).
/// </summary>
public sealed class InspectorPanel : EditorPanel
{
    // Cached list of MonoBehaviour-derived types for the Add Component popup
    private Type[]? _availableComponents;
    private string _componentFilter = string.Empty;

    // Asset picker state
    private string _assetPickerFilter = string.Empty;
    private string? _activePickerFieldId;

    // Internal field names that should never appear in the inspector
    private static readonly HashSet<string> InternalFields = new(StringComparer.Ordinal)
    {
        "_identifier", "_go", "_enabled", "_enabledInHierarchy",
        "_hasStarted", "_hasBeenEnabled", "HideFlags",
    };

    /// <summary> Label column width ratio (0–1). </summary>
    private const float LabelRatio = 0.35f;

    public InspectorPanel() : base("Inspector") { }

    protected override void DrawContent()
    {
        var sel = EditorServices.Get<ISelectionService>();

        // ── Asset inspection (from project selection) ──────────
        if (sel.SelectedAsset is AssetEntry asset)
        {
            DrawAssetInspector(asset);
            return;
        }

        if (sel.ActiveObject is not GameObject go)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "No object selected.");
            return;
        }

        // ── GameObject header ──────────────────────────────────
        // Draw a header row with the GO icon + name
        {
            string goIconName = IconManager.GetIconNameForGameObject(go);
            var icon = IconManager.GetIcon(goIconName);
            var cursorPos = ImGui.GetCursorScreenPos();
            float iconSz = ImGui.GetTextLineHeight();
            ImGui.Dummy(new Vector2(iconSz, iconSz));
            icon.Draw(cursorPos, iconSz);
            ImGui.SameLine();
        }
        DrawFieldRow("Name", () =>
        {
            ImGui.TextColored(new Vector4(0.90f, 0.90f, 0.90f, 1f), go.Name ?? "Unnamed");
        });

        bool enabled = go.Enabled;
        if (ImGui.Checkbox("Active", ref enabled))
            go.Enabled = enabled;

        ImGui.Separator();

        // ── Transform section ──────────────────────────────────
        if (ImGui.CollapsingHeader("     Transform", ImGuiTreeNodeFlags.DefaultOpen))
        {
            IconManager.DrawIconOverLastItem("Transform");
            DrawTransform(go.Transform);
        }
        else
        {
            IconManager.DrawIconOverLastItem("Transform");
        }

        // ── Components ─────────────────────────────────────────
        foreach (var comp in go.GetComponents())
        {
            if (comp == null) continue;

            string typeName = comp.GetType().Name;
            string compIconName = IconManager.GetIconNameForComponent(comp);
            ImGui.PushID(comp.GetHashCode());

            bool headerOpen = ImGui.CollapsingHeader($"     {typeName}", ImGuiTreeNodeFlags.DefaultOpen);

            // Overlay component icon on the header
            IconManager.DrawIconOverLastItem(compIconName);

            // Component context menu (right-click header)
            if (ImGui.BeginPopupContextItem())
            {
                if (ImGui.MenuItem("Remove Component"))
                {
                    if (EditorServices.TryGet<UndoRedoService>(out var undo))
                        undo!.Execute(new RemoveComponentCommand(go, comp));
                    else
                        go.RemoveComponent(comp);
                }
                ImGui.EndPopup();
            }

            if (headerOpen)
            {
                // Enabled checkbox
                bool compEnabled = comp.Enabled;
                if (ImGui.Checkbox("Enabled", ref compEnabled))
                    comp.Enabled = compEnabled;

                // Serializable fields via reflection
                DrawObjectFields(comp);
            }

            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Add Component button ───────────────────────────────
        float avail = ImGui.GetContentRegionAvail().X;
        float btnW = MathF.Min(220 * Game.DpiScale, avail);
        ImGui.SetCursorPosX((avail - btnW) * 0.5f + ImGui.GetCursorPosX());

        if (ImGui.Button("Add Component", new Vector2(btnW, 0)))
            ImGui.OpenPopup("##AddComponent");

        DrawAddComponentPopup(go);
    }

    // ────────────────────────────────────────────────────────────
    // Two-column layout helpers
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws a single label+value row using a two-column table.
    /// </summary>
    private static void DrawFieldRow(string label, Action drawValue)
    {
        if (ImGui.BeginTable("##fr_" + label, 2, ImGuiTableFlags.None))
        {
            float totalW = ImGui.GetContentRegionAvail().X;
            ImGui.TableSetupColumn("lbl", ImGuiTableColumnFlags.WidthFixed, totalW * LabelRatio);
            ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            // Label
            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);

            // Value
            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1);
            drawValue();

            ImGui.EndTable();
        }
    }

    // ────────────────────────────────────────────────────────────
    // Transform editing
    // ────────────────────────────────────────────────────────────

    private static void DrawTransform(Prowl.Vector.Transform t)
    {
        var pos = ToNumerics(t.LocalPosition);
        if (DrawLabeledFloat3("Position", ref pos, 0.05f))
            t.LocalPosition = FromNumerics(pos);

        var rot = ToNumerics(t.LocalEulerAngles);
        if (DrawLabeledFloat3("Rotation", ref rot, 0.5f))
            t.LocalEulerAngles = FromNumerics(rot);

        var scl = ToNumerics(t.LocalScale);
        if (DrawLabeledFloat3("Scale", ref scl, 0.05f))
            t.LocalScale = FromNumerics(scl);
    }

    /// <summary>
    /// Draws a Vector3 field with colored X/Y/Z labels in a two-column layout.
    /// </summary>
    private static bool DrawLabeledFloat3(string label, ref Vector3 value, float speed)
    {
        bool changed = false;

        if (ImGui.BeginTable("##v3_" + label, 2, ImGuiTableFlags.None))
        {
            float totalW = ImGui.GetContentRegionAvail().X;
            ImGui.TableSetupColumn("lbl", ImGuiTableColumnFlags.WidthFixed, totalW * LabelRatio);
            ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);

            ImGui.TableSetColumnIndex(1);
            ImGui.PushID(label);

            float fieldWidth = (ImGui.GetContentRegionAvail().X - 60 * Game.DpiScale) / 3f;
            if (fieldWidth < 30 * Game.DpiScale) fieldWidth = 30 * Game.DpiScale;

            // X
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.50f, 0.12f, 0.12f, 0.60f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.60f, 0.18f, 0.18f, 0.70f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.70f, 0.22f, 0.22f, 0.80f));
            ImGui.TextColored(new Vector4(0.95f, 0.30f, 0.30f, 1f), "X");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(fieldWidth);
            float x = value.X;
            if (ImGui.DragFloat("##X", ref x, speed)) { value.X = x; changed = true; }
            ImGui.PopStyleColor(3);

            // Y
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.12f, 0.40f, 0.12f, 0.60f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.18f, 0.50f, 0.18f, 0.70f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.22f, 0.60f, 0.22f, 0.80f));
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.30f, 0.90f, 0.30f, 1f), "Y");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(fieldWidth);
            float y = value.Y;
            if (ImGui.DragFloat("##Y", ref y, speed)) { value.Y = y; changed = true; }
            ImGui.PopStyleColor(3);

            // Z
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.12f, 0.18f, 0.50f, 0.60f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.18f, 0.24f, 0.60f, 0.70f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.22f, 0.30f, 0.70f, 0.80f));
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.30f, 0.50f, 0.95f, 1f), "Z");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(fieldWidth);
            float z = value.Z;
            if (ImGui.DragFloat("##Z", ref z, speed)) { value.Z = z; changed = true; }
            ImGui.PopStyleColor(3);

            ImGui.PopID();
            ImGui.EndTable();
        }

        return changed;
    }

    /// <summary>
    /// Draws a Vector2 field with colored X/Y labels in a two-column layout.
    /// </summary>
    private static bool DrawLabeledFloat2(string label, ref Vector2 value, float speed)
    {
        bool changed = false;

        if (ImGui.BeginTable("##v2_" + label, 2, ImGuiTableFlags.None))
        {
            float totalW = ImGui.GetContentRegionAvail().X;
            ImGui.TableSetupColumn("lbl", ImGuiTableColumnFlags.WidthFixed, totalW * LabelRatio);
            ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);

            ImGui.TableSetColumnIndex(1);
            ImGui.PushID(label);

            float fieldWidth = (ImGui.GetContentRegionAvail().X - 40 * Game.DpiScale) / 2f;
            if (fieldWidth < 40 * Game.DpiScale) fieldWidth = 40 * Game.DpiScale;

            // X
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.50f, 0.12f, 0.12f, 0.60f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.60f, 0.18f, 0.18f, 0.70f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.70f, 0.22f, 0.22f, 0.80f));
            ImGui.TextColored(new Vector4(0.95f, 0.30f, 0.30f, 1f), "X");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(fieldWidth);
            float x = value.X;
            if (ImGui.DragFloat("##X", ref x, speed)) { value.X = x; changed = true; }
            ImGui.PopStyleColor(3);

            // Y
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.12f, 0.40f, 0.12f, 0.60f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.18f, 0.50f, 0.18f, 0.70f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.22f, 0.60f, 0.22f, 0.80f));
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.30f, 0.90f, 0.30f, 1f), "Y");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(fieldWidth);
            float y = value.Y;
            if (ImGui.DragFloat("##Y", ref y, speed)) { value.Y = y; changed = true; }
            ImGui.PopStyleColor(3);

            ImGui.PopID();
            ImGui.EndTable();
        }

        return changed;
    }

    /// <summary>
    /// Draws a Vector4 field with colored X/Y/Z/W labels in a two-column layout.
    /// </summary>
    private static bool DrawLabeledFloat4(string label, ref Vector4 value, float speed)
    {
        bool changed = false;

        if (ImGui.BeginTable("##v4_" + label, 2, ImGuiTableFlags.None))
        {
            float totalW = ImGui.GetContentRegionAvail().X;
            ImGui.TableSetupColumn("lbl", ImGuiTableColumnFlags.WidthFixed, totalW * LabelRatio);
            ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);

            ImGui.TableSetColumnIndex(1);
            ImGui.PushID(label);

            float fieldWidth = (ImGui.GetContentRegionAvail().X - 80 * Game.DpiScale) / 4f;
            if (fieldWidth < 25 * Game.DpiScale) fieldWidth = 25 * Game.DpiScale;

            // X
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.50f, 0.12f, 0.12f, 0.60f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.60f, 0.18f, 0.18f, 0.70f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.70f, 0.22f, 0.22f, 0.80f));
            ImGui.TextColored(new Vector4(0.95f, 0.30f, 0.30f, 1f), "X");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(fieldWidth);
            float x = value.X;
            if (ImGui.DragFloat("##X", ref x, speed)) { value.X = x; changed = true; }
            ImGui.PopStyleColor(3);

            // Y
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.12f, 0.40f, 0.12f, 0.60f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.18f, 0.50f, 0.18f, 0.70f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.22f, 0.60f, 0.22f, 0.80f));
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.30f, 0.90f, 0.30f, 1f), "Y");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(fieldWidth);
            float y = value.Y;
            if (ImGui.DragFloat("##Y", ref y, speed)) { value.Y = y; changed = true; }
            ImGui.PopStyleColor(3);

            // Z
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.12f, 0.18f, 0.50f, 0.60f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.18f, 0.24f, 0.60f, 0.70f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.22f, 0.30f, 0.70f, 0.80f));
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.30f, 0.50f, 0.95f, 1f), "Z");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(fieldWidth);
            float z = value.Z;
            if (ImGui.DragFloat("##Z", ref z, speed)) { value.Z = z; changed = true; }
            ImGui.PopStyleColor(3);

            // W
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.40f, 0.30f, 0.12f, 0.60f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.50f, 0.38f, 0.18f, 0.70f));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.60f, 0.45f, 0.22f, 0.80f));
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.90f, 0.75f, 0.30f, 1f), "W");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(fieldWidth);
            float w = value.W;
            if (ImGui.DragFloat("##W", ref w, speed)) { value.W = w; changed = true; }
            ImGui.PopStyleColor(3);

            ImGui.PopID();
            ImGui.EndTable();
        }

        return changed;
    }

    // ────────────────────────────────────────────────────────────
    // Reflection-based field drawing
    // ────────────────────────────────────────────────────────────

    private void DrawObjectFields(object target)
    {
        FieldInfo[] fields = target.GetSerializableFields();

        foreach (var field in fields)
        {
            if (IsInternalField(field.Name)) continue;

            string label = FormatLabel(field.Name);
            object? value = field.GetValue(target);
            Type ft = field.FieldType;

            ImGui.PushID(field.Name);

            if (ft == typeof(float))
            {
                float v = (float)(value ?? 0f);
                DrawFieldRow(label, () =>
                {
                    if (ImGui.DragFloat("##val", ref v, 0.01f))
                        SetFieldWithUndo(target, field, value, v);
                });
            }
            else if (ft == typeof(double))
            {
                float v = (float)(double)(value ?? 0.0);
                DrawFieldRow(label, () =>
                {
                    if (ImGui.DragFloat("##val", ref v, 0.01f))
                        SetFieldWithUndo(target, field, value, (double)v);
                });
            }
            else if (ft == typeof(int))
            {
                int v = (int)(value ?? 0);
                DrawFieldRow(label, () =>
                {
                    if (ImGui.DragInt("##val", ref v))
                        SetFieldWithUndo(target, field, value, v);
                });
            }
            else if (ft == typeof(bool))
            {
                bool v = (bool)(value ?? false);
                DrawFieldRow(label, () =>
                {
                    if (ImGui.Checkbox("##val", ref v))
                        SetFieldWithUndo(target, field, value, v);
                });
            }
            else if (ft == typeof(string))
            {
                string v = (string)(value ?? string.Empty);
                DrawFieldRow(label, () =>
                {
                    if (ImGui.InputText("##val", ref v, 512))
                        SetFieldWithUndo(target, field, value, v);
                });
            }
            else if (ft == typeof(Prowl.Vector.Float2))
            {
                var pv = (Prowl.Vector.Float2)(value ?? Prowl.Vector.Float2.Zero);
                var nv = new Vector2(pv.X, pv.Y);
                if (DrawLabeledFloat2(label, ref nv, 0.05f))
                    SetFieldWithUndo(target, field, value, new Prowl.Vector.Float2(nv.X, nv.Y));
            }
            else if (ft == typeof(Prowl.Vector.Float3))
            {
                var pv = (Prowl.Vector.Float3)(value ?? Prowl.Vector.Float3.Zero);
                var nv = new Vector3(pv.X, pv.Y, pv.Z);
                if (DrawLabeledFloat3(label, ref nv, 0.05f))
                    SetFieldWithUndo(target, field, value, new Prowl.Vector.Float3(nv.X, nv.Y, nv.Z));
            }
            else if (ft == typeof(Prowl.Vector.Float4))
            {
                var pv = (Prowl.Vector.Float4)(value ?? Prowl.Vector.Float4.Zero);
                var nv = new Vector4(pv.X, pv.Y, pv.Z, pv.W);
                if (DrawLabeledFloat4(label, ref nv, 0.05f))
                    SetFieldWithUndo(target, field, value, new Prowl.Vector.Float4(nv.X, nv.Y, nv.Z, nv.W));
            }
            else if (ft == typeof(Prowl.Vector.Color))
            {
                var cv = (Prowl.Vector.Color)(value ?? new Prowl.Vector.Color(1, 1, 1, 1));
                var nv = new Vector4(cv.R, cv.G, cv.B, cv.A);
                DrawFieldRow(label, () =>
                {
                    if (ImGui.ColorEdit4("##val", ref nv))
                        SetFieldWithUndo(target, field, value, new Prowl.Vector.Color(nv.X, nv.Y, nv.Z, nv.W));
                });
            }
            else if (ft.IsEnum)
            {
                int current = (int)(value ?? 0);
                string[] names = Enum.GetNames(ft);
                DrawFieldRow(label, () =>
                {
                    if (ImGui.Combo("##val", ref current, names, names.Length))
                        SetFieldWithUndo(target, field, value, Enum.ToObject(ft, current));
                });
            }
            else if (ft.IsSubclassOf(typeof(EngineObject)) || ft == typeof(EngineObject) || ft == typeof(GameObject))
            {
                // Asset / object reference field with drag-drop support
                DrawAssetReferenceField(label, target, field, value as EngineObject);
            }
            else
            {
                // Unsupported type — show read-only text
                string text = value?.ToString() ?? "(null)";
                DrawFieldRow(label, () =>
                {
                    ImGui.TextDisabled(text);
                });
            }

            ImGui.PopID();
        }
    }

    // ────────────────────────────────────────────────────────────
    // Asset reference field (drag-drop, picker, clear)
    // ────────────────────────────────────────────────────────────

    private void DrawAssetReferenceField(string label, object target, FieldInfo field, EngineObject? current)
    {
        string fieldId = $"{target.GetHashCode()}_{field.Name}";
        string popupId = $"##AssetPicker_{fieldId}";

        if (ImGui.BeginTable("##ref_" + label, 2, ImGuiTableFlags.None))
        {
            float totalW = ImGui.GetContentRegionAvail().X;
            ImGui.TableSetupColumn("lbl", ImGuiTableColumnFlags.WidthFixed, totalW * LabelRatio);
            ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            // Label
            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);

            // Value area
            ImGui.TableSetColumnIndex(1);

            float availW = ImGui.GetContentRegionAvail().X;
            float clearBtnW = 20 * Game.DpiScale;
            float pickerBtnW = 20 * Game.DpiScale;
            float refBtnW = availW - clearBtnW - pickerBtnW - ImGui.GetStyle().ItemSpacing.X * 2;
            if (refBtnW < 40 * Game.DpiScale) refBtnW = 40 * Game.DpiScale;

            // Reference button — shows current asset name
            string displayName = current != null ? $"{current.Name} ({field.FieldType.Name})" : $"None ({field.FieldType.Name})";

            ImGui.PushStyleColor(ImGuiCol.Button, current != null
                ? new Vector4(0.22f, 0.30f, 0.22f, 1f)
                : new Vector4(0.20f, 0.20f, 0.20f, 1f));
            ImGui.Button(displayName, new Vector2(refBtnW, 0));
            ImGui.PopStyleColor();

            // Drag-drop target: accept EditorDragDrop payloads
            if (ImGui.IsItemHovered() && EditorDragDrop.IsDragging)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.28f, 0.56f, 1.0f, 0.40f));
                ImGui.Button(displayName, new Vector2(refBtnW, 0));
                ImGui.PopStyleColor();

                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    if (EditorDragDrop.PayloadType == "AssetEntry" && EditorDragDrop.Payload is AssetEntry entry)
                    {
                        // For now, store the asset path on the EngineObject — actual loading
                        // would go through a real asset loader. Set to null if incompatible.
                        EditorDragDrop.Clear();
                    }
                    else if (EditorDragDrop.PayloadType == "GameObject" && EditorDragDrop.Payload is GameObject droppedGo)
                    {
                        if (field.FieldType.IsAssignableFrom(typeof(GameObject)))
                        {
                            SetFieldWithUndo(target, field, current, droppedGo);
                            EditorDragDrop.Clear();
                        }
                    }
                }
            }

            // Context menu: clear reference
            if (ImGui.BeginPopupContextItem("##refctx"))
            {
                if (ImGui.MenuItem("Clear"))
                    SetFieldWithUndo(target, field, current, null);
                ImGui.EndPopup();
            }

            // Clear (X) button
            ImGui.SameLine();
            if (EditorIcons.ImageButton("##Clear" + fieldId, EditorIconType.Close, new Vector2(clearBtnW, clearBtnW)))
                SetFieldWithUndo(target, field, current, null);

            // Picker button — opens searchable asset popup
            ImGui.SameLine();
            if (EditorIcons.ImageButton("##Picker" + fieldId, EditorIconType.Dropdown, new Vector2(pickerBtnW, pickerBtnW)))
            {
                _activePickerFieldId = fieldId;
                _assetPickerFilter = string.Empty;
                ImGui.OpenPopup(popupId);
            }

            // Asset picker popup
            if (_activePickerFieldId == fieldId && ImGui.BeginPopup(popupId))
            {
                ImGui.Text($"Select {field.FieldType.Name}");
                ImGui.Separator();
                ImGui.InputText("##filter", ref _assetPickerFilter, 256);

                ImGui.BeginChild("##pickerList", new Vector2(280 * Game.DpiScale, 250 * Game.DpiScale));

                // "None" option
                if (ImGui.Selectable("None"))
                {
                    SetFieldWithUndo(target, field, current, null);
                    _activePickerFieldId = null;
                    ImGui.CloseCurrentPopup();
                }

                // If field is GameObject, list scene objects
                if (field.FieldType == typeof(GameObject) || field.FieldType.IsSubclassOf(typeof(GameObject)))
                {
                    if (EditorServices.TryGet<ISceneService>(out var sceneService))
                    {
                        foreach (var go in sceneService!.GetRootGameObjects())
                            DrawGameObjectPickerItem(go, target, field, current);
                    }
                }

                // List asset entries from the project
                if (EditorServices.TryGet<IAssetService>(out var assetDb) && assetDb!.HasProject)
                {
                    var entries = assetDb.GetAllEntriesRecursive();
                    foreach (var entry in entries)
                    {
                        if (entry.IsDirectory) continue;
                        if (!string.IsNullOrEmpty(_assetPickerFilter) &&
                            !entry.Name.Contains(_assetPickerFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (ImGui.Selectable(entry.Name))
                        {
                            // In a real implementation this would load the asset via an asset loader.
                            // For now we signal intent — the reference can be resolved later.
                            _activePickerFieldId = null;
                            ImGui.CloseCurrentPopup();
                        }
                    }
                }

                ImGui.EndChild();
                ImGui.EndPopup();
            }

            ImGui.EndTable();
        }
    }

    private void DrawGameObjectPickerItem(GameObject go, object target, FieldInfo field, EngineObject? current)
    {
        string name = go.Name ?? "Unnamed";
        if (!string.IsNullOrEmpty(_assetPickerFilter) &&
            !name.Contains(_assetPickerFilter, StringComparison.OrdinalIgnoreCase))
        {
            // Still recurse into children in case they match
            foreach (var child in go.Children)
                DrawGameObjectPickerItem(child, target, field, current);
            return;
        }

        if (ImGui.Selectable(name))
        {
            SetFieldWithUndo(target, field, current, go);
            _activePickerFieldId = null;
            ImGui.CloseCurrentPopup();
        }

        foreach (var child in go.Children)
            DrawGameObjectPickerItem(child, target, field, current);
    }

    // ────────────────────────────────────────────────────────────
    // Undo helper
    // ────────────────────────────────────────────────────────────

    private static void SetFieldWithUndo(object target, FieldInfo field, object? oldValue, object? newValue)
    {
        if (EditorServices.TryGet<UndoRedoService>(out var undo))
            undo!.Execute(new PropertyChangeCommand(target, field, oldValue, newValue));
        else
            field.SetValue(target, newValue);
    }

    // ────────────────────────────────────────────────────────────
    // Add Component popup
    // ────────────────────────────────────────────────────────────

    private void DrawAddComponentPopup(GameObject go)
    {
        if (!ImGui.BeginPopup("##AddComponent")) return;

        ImGui.Text("Add Component");
        ImGui.Separator();

        ImGui.InputText("##Filter", ref _componentFilter, 256);

        CacheAvailableComponents();

        ImGui.BeginChild("##CompList", new Vector2(280 * Game.DpiScale, 300 * Game.DpiScale));

        foreach (var type in _availableComponents!)
        {
            if (!string.IsNullOrEmpty(_componentFilter) &&
                !type.Name.Contains(_componentFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            // Draw icon + name for each component type
            string compIconName = IconManager.GetIconNameForComponent(type);
            var icon = IconManager.GetIcon(compIconName);
            var pos = ImGui.GetCursorScreenPos();
            float iconSz = ImGui.GetTextLineHeight();

            if (ImGui.Selectable($"     {type.Name}"))
            {
                if (EditorServices.TryGet<UndoRedoService>(out var undo))
                    undo!.Execute(new AddComponentCommand(go, type));
                else
                    go.AddComponent(type);

                _componentFilter = string.Empty;
                ImGui.CloseCurrentPopup();
            }

            // Overlay the icon on the selectable
            {
                var itemMin = ImGui.GetItemRectMin();
                float yOff = (ImGui.GetItemRectSize().Y - iconSz) * 0.5f;
                icon.Draw(new Vector2(itemMin.X + 4f, itemMin.Y + yOff), iconSz);
            }
        }

        ImGui.EndChild();
        ImGui.EndPopup();
    }

    private void CacheAvailableComponents()
    {
        if (_availableComponents != null) return;

        _availableComponents = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
            .Where(t => t.IsSubclassOf(typeof(MonoBehaviour)) && !t.IsAbstract)
            .OrderBy(t => t.Name)
            .ToArray();
    }

    // ────────────────────────────────────────────────────────────
    // Asset inspector
    // ────────────────────────────────────────────────────────────

    private static void DrawAssetInspector(AssetEntry asset)
    {
        // ── Asset header with icon ─────────────────────────────
        string iconName = IconManager.GetIconNameForExtension(asset.Extension);
        var icon = IconManager.GetIcon(iconName);

        // Reserve space for the icon and draw it
        var cursorPos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(32, 32));
        icon.Draw(cursorPos, 32f);
        ImGui.SameLine();

        ImGui.BeginGroup();
        ImGui.TextColored(new Vector4(0.90f, 0.90f, 0.90f, 1f), asset.Name);
        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), iconName);
        ImGui.EndGroup();

        ImGui.Separator();
        ImGui.Spacing();

        // ── File properties ────────────────────────────────────
        if (ImGui.CollapsingHeader("File Info", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawAssetFieldRow("Name", asset.Name);
            DrawAssetFieldRow("Extension", string.IsNullOrEmpty(asset.Extension) ? "(none)" : asset.Extension);
            DrawAssetFieldRow("Relative Path", asset.RelativePath);

            // File size and last modified (if file exists)
            if (File.Exists(asset.FullPath))
            {
                var info = new FileInfo(asset.FullPath);
                string sizeStr = info.Length < 1024
                    ? $"{info.Length} B"
                    : info.Length < 1024 * 1024
                        ? $"{info.Length / 1024.0:F1} KB"
                        : $"{info.Length / (1024.0 * 1024.0):F2} MB";
                DrawAssetFieldRow("Size", sizeStr);
                DrawAssetFieldRow("Modified", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
            }
        }

        // ── Type-specific sections ─────────────────────────────
        switch (asset.Extension.ToLowerInvariant())
        {
            case ".scene":
                DrawSceneAssetInfo(asset);
                break;
            case ".mat":
                DrawMaterialAssetInfo(asset);
                break;
            case ".cs":
                DrawScriptAssetInfo(asset);
                break;
            case ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".hdr":
                DrawTextureAssetInfo(asset);
                break;
        }
    }

    private static void DrawSceneAssetInfo(AssetEntry asset)
    {
        if (!ImGui.CollapsingHeader("Scene", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Double-click in Project to open.");

        if (ImGui.Button("Open Scene"))
        {
            EditorMenuBar.LoadSceneFromFile(asset.FullPath);
        }
    }

    private static void DrawMaterialAssetInfo(AssetEntry asset)
    {
        if (!ImGui.CollapsingHeader("Material", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // Show raw JSON content (read-only for now)
        if (File.Exists(asset.FullPath))
        {
            try
            {
                string content = File.ReadAllText(asset.FullPath);
                if (content.Length > 2048) content = content[..2048] + "\n... (truncated)";
                ImGui.TextWrapped(content);
            }
            catch
            {
                ImGui.TextDisabled("(unable to read file)");
            }
        }
    }

    private static void DrawScriptAssetInfo(AssetEntry asset)
    {
        if (!ImGui.CollapsingHeader("Script", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // Show first N lines of the script
        if (File.Exists(asset.FullPath))
        {
            try
            {
                string[] lines = File.ReadAllLines(asset.FullPath);
                int maxLines = Math.Min(lines.Length, 30);
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.70f, 0.80f, 0.70f, 1f));
                for (int i = 0; i < maxLines; i++)
                    ImGui.TextUnformatted(lines[i]);
                if (lines.Length > maxLines)
                    ImGui.TextDisabled($"... ({lines.Length - maxLines} more lines)");
                ImGui.PopStyleColor();
            }
            catch
            {
                ImGui.TextDisabled("(unable to read file)");
            }
        }
    }

    private static void DrawTextureAssetInfo(AssetEntry asset)
    {
        if (!ImGui.CollapsingHeader("Texture", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (File.Exists(asset.FullPath))
        {
            try
            {
                using var img = new ImageMagick.MagickImage(asset.FullPath);
                DrawAssetFieldRow("Dimensions", $"{img.Width} x {img.Height}");
                DrawAssetFieldRow("Format", img.Format.ToString());
                DrawAssetFieldRow("Color Space", img.ColorSpace.ToString());
            }
            catch
            {
                ImGui.TextDisabled("(unable to read image metadata)");
            }
        }
    }

    private static void DrawAssetFieldRow(string label, string value)
    {
        if (ImGui.BeginTable("##af_" + label, 2, ImGuiTableFlags.None))
        {
            float totalW = ImGui.GetContentRegionAvail().X;
            ImGui.TableSetupColumn("lbl", ImGuiTableColumnFlags.WidthFixed, totalW * LabelRatio);
            ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(value);

            ImGui.EndTable();
        }
    }

    // ────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────

    private static bool IsInternalField(string name) => InternalFields.Contains(name);

    /// <summary>
    /// Converts "_camelCase" or "camelCase" to "Camel Case".
    /// </summary>
    private static string FormatLabel(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        // Strip leading underscores
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

    private static Vector3 ToNumerics(Prowl.Vector.Float3 v) => new(v.X, v.Y, v.Z);
    private static Prowl.Vector.Float3 FromNumerics(Vector3 v) => new(v.X, v.Y, v.Z);
}
