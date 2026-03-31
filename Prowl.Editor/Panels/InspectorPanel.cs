// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using System.Reflection;
using System.Text.Json;
using ImGuiNET;
using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Prefabs;
using Prowl.Runtime.Utils;
using Prowl.Runtime.Resources;
using Prowl.Editor.Core;
using Prowl.Editor.Docking;
using Prowl.Editor.Icons;
using Prowl.Editor.Inspector;
using Prowl.Editor.Prefabs;
using Prowl.Editor.Project;
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

    // Inspector name editing state
    private string _nameEditBuffer = string.Empty;
    private int _nameEditGoId;

    // Internal field names that should never appear in the normal inspector
    private static readonly HashSet<string> InternalFields = new(StringComparer.Ordinal)
    {
        "_identifier", "_go", "_enabled", "_enabledInHierarchy",
        "_hasStarted", "_hasBeenEnabled", "HideFlags", "_name",
    };

    /// <summary> When true the inspector shows every serializable field including
    /// those marked with <see cref="HideInInspectorAttribute"/> and internal fields. </summary>
    private bool _debugMode;

    // Component collapse state persistence (editor-only, not stored in scene)
    private readonly Dictionary<string, bool> _collapseState = new();
    private bool _collapseStateLoaded;

    /// <summary> Label column width ratio (0–1). </summary>
    private const float LabelRatio = 0.3f;

    public InspectorPanel() : base("Inspector")
    {
        // Invalidate cached component list when user scripts are recompiled
        EditorEvents.SubscribeOnAssemblyChanged(InvalidateComponentCache);
    }

    /// <summary>
    /// Clears the cached list of available MonoBehaviour types so it is
    /// rebuilt on the next frame, picking up newly compiled user scripts.
    /// </summary>
    public void InvalidateComponentCache()
    {
        _availableComponents = null;
    }

    protected override void DrawContent()
    {
        var sel = EditorServices.Get<ISelectionService>();

        if (sel.ActiveObject is GameObject gameObject)
        {
            bool enabled = gameObject.Enabled;
            if (ImGui.Checkbox("Active", ref enabled))
                gameObject.Enabled = enabled;
            ImGui.SameLine();
        }

        // ── Debug mode toggle (top-right corner) ──────────────
        {
            float toggleAvail = ImGui.GetContentRegionAvail().X;
            float toggleW = 60 * Game.DpiScale;
            ImGui.SameLine(toggleAvail - toggleW);
            if (_debugMode)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.35f, 0.15f, 1f));
            if (ImGui.SmallButton(_debugMode ? "Debug" : "Normal"))
                _debugMode = !_debugMode;
            if (_debugMode)
                ImGui.PopStyleColor();
        }


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
            ImGui.Dummy(new Vector2(iconSz * 2, iconSz*2));
            icon.Draw(cursorPos - new Vector2(0,10), iconSz * 2);
            ImGui.SameLine();
        }
        DrawFieldRow("Name", () =>
        {
            // Keep the edit buffer in sync with the currently selected GO
            if (_nameEditGoId != go.InstanceID)
            {
                _nameEditBuffer = go.Name ?? "Unnamed";
                _nameEditGoId = go.InstanceID;
            }

            if (ImGui.InputText("##GOName", ref _nameEditBuffer, 256,
                ImGuiInputTextFlags.EnterReturnsTrue))
            {
                string newName = _nameEditBuffer.Trim();
                if (string.IsNullOrEmpty(newName))
                {
                    // Revert to previous name if empty
                    _nameEditBuffer = go.Name ?? "Unnamed";
                }
                else if (newName != go.Name)
                {
                    string oldName = go.Name ?? "Unnamed";
                    if (EditorServices.TryGet<UndoRedoService>(out var undo))
                        undo!.Execute(new RenameCommand(go, oldName, newName));
                    else
                        go.Name = newName;
                }
            }

            // Also apply on deactivation (focus lost) to match typical UX
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                string newName = _nameEditBuffer.Trim();
                if (string.IsNullOrEmpty(newName))
                {
                    _nameEditBuffer = go.Name ?? "Unnamed";
                }
                else if (newName != go.Name)
                {
                    string oldName = go.Name ?? "Unnamed";
                    if (EditorServices.TryGet<UndoRedoService>(out var undo))
                        undo!.Execute(new RenameCommand(go, oldName, newName));
                    else
                        go.Name = newName;
                }
            }
        });


        ImGui.Separator();

        // ── Prefab info bar ────────────────────────────────────
        DrawPrefabBar(go);

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

        var compArray = go.GetComponents().ToArray();

        // ── Components ─────────────────────────────────────────
        foreach (var comp in compArray)
        {
            if (comp == null) continue;

            string typeName = comp.GetType().Name;
            string compIconName = IconManager.GetIconNameForComponent(comp);
            ImGui.PushID(comp.Identifier.ToString());

            // Enabled checkbox on the left, before the foldout header
            bool compEnabled = comp.Enabled;
            if (ImGui.Checkbox($"##enabled", ref compEnabled))
                comp.Enabled = compEnabled;

            ImGui.SameLine();

            // Restore persisted collapse state for this component
            EnsureCollapseStateLoaded();
            string collapseKey = comp.Identifier.ToString();
            bool storedOpen = _collapseState.TryGetValue(collapseKey, out bool savedOpen) ? savedOpen : true;
            ImGui.SetNextItemOpen(storedOpen, ImGuiCond.Once);

            bool headerOpen = ImGui.CollapsingHeader($"     {typeName}");

            // Persist collapse state changes
            if (headerOpen != storedOpen)
            {
                _collapseState[collapseKey] = headerOpen;
                SaveCollapseState();
            }

            // Overlay component icon on the header
            IconManager.DrawIconOverLastItem(compIconName);

            // Drag source — allows dragging components to reference fields
            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
            {
                EditorDragDrop.BeginDrag("Component", comp);
                unsafe
                {
                    int dummy = 0;
                    ImGui.SetDragDropPayload("COMPONENT", (nint)(&dummy), sizeof(int));
                }
                ImGui.Text($"{typeName} ({go.Name ?? "?"})");
                ImGui.EndDragDropSource();
            }

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
                // Serializable fields via reflection
                DrawObjectFields(comp);

                // Material sub-inspector for renderers with materials
                if (comp is MeshRenderer meshRenderer && meshRenderer.Material != null)
                {
                    ImGui.Spacing();
                    if (ImGui.TreeNodeEx("Material", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        MaterialInspector.DrawMaterial(meshRenderer.Material);
                        ImGui.TreePop();
                    }
                }
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
        {
            ImGui.OpenPopup("##AddComponent");
        }

        DrawAddComponentPopup(go);
    }

    // ────────────────────────────────────────────────────────────
    // Component collapse state persistence
    // ────────────────────────────────────────────────────────────

    private void EnsureCollapseStateLoaded()
    {
        if (_collapseStateLoaded) return;
        _collapseStateLoaded = true;

        string? path = GetCollapseStatePath();
        if (path == null || !File.Exists(path)) return;

        try
        {
            string json = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
            if (dict != null)
                foreach (var kv in dict)
                    _collapseState[kv.Key] = kv.Value;
        }
        catch { /* corrupted or inaccessible — start fresh */ }
    }

    private void SaveCollapseState()
    {
        string? path = GetCollapseStatePath();
        if (path == null) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(_collapseState,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { /* best-effort persistence */ }
    }

    private static string? GetCollapseStatePath()
    {
        if (EditorApplication.ProjectPath == null) return null;
        return Path.Combine(EditorApplication.ProjectPath, "ProjectSettings", "InspectorState.json");
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


    // Per-component drag state for the letter-label drag interaction
    private static string? _dragLetter;
    private static float _dragStartValue;
    private static Vector2 _dragStartPos;
    private static bool _isDraggingLabel;

    /// <summary>
    /// Draws a single vector component in the Stride / S&amp;box style: a small colored
    /// label that can be dragged horizontally to scrub the value, flush with an
    /// <see cref="ImGui.InputFloat"/> text field that is immediately editable.
    /// Click the label to reset to zero.
    /// </summary>
    private static bool DrawVectorComponent(string letter, ref float value, float speed,
        float fieldWidth, float buttonW,
        Vector4 btnColor, Vector4 btnHover, Vector4 btnActive)
    {
        bool changed = false;
        var style = ImGui.GetStyle();
        string id = "##lbl_" + letter;

        // ── Colored label (draggable, click-to-reset) ──────────
        ImGui.PushStyleColor(ImGuiCol.Button, btnColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, btnHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, btnActive);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, style.ItemSpacing.Y));

        ImGui.Button(letter, new Vector2(buttonW, ImGui.GetFrameHeight()));
        bool labelHovered = ImGui.IsItemHovered();
        bool labelActive = ImGui.IsItemActive();

        // Show a horizontal-resize cursor when hovering the label
        if (labelHovered || (_isDraggingLabel && _dragLetter == id))
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);

        // Begin drag: record starting value and mouse position
        if (labelHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _dragLetter = id;
            _dragStartValue = value;
            _dragStartPos = ImGui.GetMousePos();
            _isDraggingLabel = false; // not dragging yet — could be a click
        }

        // Continue drag
        if (_dragLetter == id && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            Vector2 delta = ImGui.GetMousePos() - _dragStartPos;
            if (!_isDraggingLabel && MathF.Abs(delta.X) > 2f)
                _isDraggingLabel = true; // crossed the dead-zone → real drag

            if (_isDraggingLabel)
            {
                value = _dragStartValue + delta.X * speed;
                changed = true;
            }
        }

        // End drag / click
        if (_dragLetter == id && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (!_isDraggingLabel)
            {
                // Pure click (no drag) → reset to zero
                value = 0;
                changed = true;
            }
            _dragLetter = null;
            _isDraggingLabel = false;
        }

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(4);

        // ── InputFloat flush against the label ─────────────────
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, style.ItemSpacing.Y));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(fieldWidth);
        if (ImGui.InputFloat("##" + letter, ref value, 0f, 0f, "%.3f"))
            changed = true;
        ImGui.PopStyleVar();

        return changed;
    }

    /// <summary>
    /// Draws a Vector3 field with Stride-style colored indicator buttons
    /// flush against DragFloat inputs in a two-column layout.
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

            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float buttonW = ImGui.GetFrameHeight();
            float avail = ImGui.GetContentRegionAvail().X;
            float fieldWidth = (avail - buttonW * 3 - spacing * 2) / 3f;
            if (fieldWidth < 20 * Game.DpiScale) fieldWidth = 20 * Game.DpiScale;

            float x = value.X, y = value.Y, z = value.Z;

            // X — Red
            if (DrawVectorComponent("X", ref x, speed, fieldWidth, buttonW,
                new Vector4(0.80f, 0.15f, 0.15f, 1f),
                new Vector4(0.90f, 0.20f, 0.20f, 1f),
                new Vector4(1.00f, 0.25f, 0.25f, 1f)))
            { value.X = x; changed = true; }

            ImGui.SameLine(0, spacing);

            // Y — Green
            if (DrawVectorComponent("Y", ref y, speed, fieldWidth, buttonW,
                new Vector4(0.20f, 0.60f, 0.20f, 1f),
                new Vector4(0.25f, 0.70f, 0.25f, 1f),
                new Vector4(0.30f, 0.80f, 0.30f, 1f)))
            { value.Y = y; changed = true; }

            ImGui.SameLine(0, spacing);

            // Z — Blue
            if (DrawVectorComponent("Z", ref z, speed, fieldWidth, buttonW,
                new Vector4(0.15f, 0.25f, 0.80f, 1f),
                new Vector4(0.20f, 0.30f, 0.90f, 1f),
                new Vector4(0.25f, 0.35f, 1.00f, 1f)))
            { value.Z = z; changed = true; }

            ImGui.PopID();
            ImGui.EndTable();
        }

        return changed;
    }

    /// <summary>
    /// Draws a Vector2 field with Stride-style colored indicator buttons
    /// flush against DragFloat inputs in a two-column layout.
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

            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float buttonW = ImGui.GetFrameHeight();
            float avail = ImGui.GetContentRegionAvail().X;
            float fieldWidth = (avail - buttonW * 2 - spacing) / 2f;
            if (fieldWidth < 30 * Game.DpiScale) fieldWidth = 30 * Game.DpiScale;

            float x = value.X, y = value.Y;

            // X — Red
            if (DrawVectorComponent("X", ref x, speed, fieldWidth, buttonW,
                new Vector4(0.80f, 0.15f, 0.15f, 1f),
                new Vector4(0.90f, 0.20f, 0.20f, 1f),
                new Vector4(1.00f, 0.25f, 0.25f, 1f)))
            { value.X = x; changed = true; }

            ImGui.SameLine(0, spacing);

            // Y — Green
            if (DrawVectorComponent("Y", ref y, speed, fieldWidth, buttonW,
                new Vector4(0.20f, 0.60f, 0.20f, 1f),
                new Vector4(0.25f, 0.70f, 0.25f, 1f),
                new Vector4(0.30f, 0.80f, 0.30f, 1f)))
            { value.Y = y; changed = true; }

            ImGui.PopID();
            ImGui.EndTable();
        }

        return changed;
    }

    /// <summary>
    /// Draws a Vector4 field with Stride-style colored indicator buttons
    /// flush against DragFloat inputs in a two-column layout.
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

            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float buttonW = ImGui.GetFrameHeight();
            float avail = ImGui.GetContentRegionAvail().X;
            float fieldWidth = (avail - buttonW * 4 - spacing * 3) / 4f;
            if (fieldWidth < 16 * Game.DpiScale) fieldWidth = 16 * Game.DpiScale;

            float x = value.X, y = value.Y, z = value.Z, w = value.W;

            // X — Red
            if (DrawVectorComponent("X", ref x, speed, fieldWidth, buttonW,
                new Vector4(0.80f, 0.15f, 0.15f, 1f),
                new Vector4(0.90f, 0.20f, 0.20f, 1f),
                new Vector4(1.00f, 0.25f, 0.25f, 1f)))
            { value.X = x; changed = true; }

            ImGui.SameLine(0, spacing);

            // Y — Green
            if (DrawVectorComponent("Y", ref y, speed, fieldWidth, buttonW,
                new Vector4(0.20f, 0.60f, 0.20f, 1f),
                new Vector4(0.25f, 0.70f, 0.25f, 1f),
                new Vector4(0.30f, 0.80f, 0.30f, 1f)))
            { value.Y = y; changed = true; }

            ImGui.SameLine(0, spacing);

            // Z — Blue
            if (DrawVectorComponent("Z", ref z, speed, fieldWidth, buttonW,
                new Vector4(0.15f, 0.25f, 0.80f, 1f),
                new Vector4(0.20f, 0.30f, 0.90f, 1f),
                new Vector4(0.25f, 0.35f, 1.00f, 1f)))
            { value.Z = z; changed = true; }

            ImGui.SameLine(0, spacing);

            // W — Purple
            if (DrawVectorComponent("W", ref w, speed, fieldWidth, buttonW,
                new Vector4(0.55f, 0.25f, 0.70f, 1f),
                new Vector4(0.65f, 0.30f, 0.80f, 1f),
                new Vector4(0.75f, 0.35f, 0.90f, 1f)))
            { value.W = w; changed = true; }

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
            // In normal mode, skip internal fields and [HideInInspector] fields
            if (!_debugMode)
            {
                if (IsInternalField(field.Name)) continue;
                if (field.GetCustomAttribute<HideInInspectorAttribute>() != null) continue;
            }

            // In debug mode, dim fields that are normally hidden
            bool isHiddenField = _debugMode &&
                (IsInternalField(field.Name) || field.GetCustomAttribute<HideInInspectorAttribute>() != null);
            if (isHiddenField)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.55f, 0.70f));

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
                string[] names = Enum.GetNames(ft);
                Array values = Enum.GetValues(ft);
                int current = Array.IndexOf(values, value ?? values.GetValue(0));
                if (current < 0) current = 0;
                DrawFieldRow(label, () =>
                {
                    if (ImGui.Combo("##val", ref current, names, names.Length))
                        SetFieldWithUndo(target, field, value, values.GetValue(current));
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

            if (isHiddenField)
                ImGui.PopStyleColor();

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
            ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthFixed, totalW * (1-LabelRatio));
            ImGui.TableNextRow();

            // Label
            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);

            // Value area
            ImGui.TableSetColumnIndex(1);

            float availW = ImGui.GetContentRegionAvail().X;
            float clearBtnW = 15 * Game.DpiScale;
            float pickerBtnW = 15 * Game.DpiScale;
            float refBtnW = availW - clearBtnW - pickerBtnW - ImGui.GetStyle().ItemSpacing.X * 2 - 20;
            if (refBtnW < 30 * Game.DpiScale) refBtnW = 30 * Game.DpiScale;

            // Reference button — shows current asset name
            string displayName = current != null ? $"{current.Name} ({field.FieldType.Name})" : $"None ({field.FieldType.Name})";

            ImGui.PushStyleColor(ImGuiCol.Button, current != null
                ? new Vector4(0.22f, 0.30f, 0.22f, 1f)
                : new Vector4(0.20f, 0.20f, 0.20f, 1f));
            ImGui.Button(displayName, new Vector2(refBtnW, 0));
            ImGui.PopStyleColor();

            // Single-click on reference button → ping asset in project view
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !EditorDragDrop.IsDragging && current != null)
            {
                PingReferencedAsset(current);
            }

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
                        EngineObject? loaded = TryLoadAssetForField(entry, field.FieldType);
                        if (loaded != null)
                            SetFieldWithUndo(target, field, current, loaded);
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
                    else if (EditorDragDrop.PayloadType == "Component" && EditorDragDrop.Payload is MonoBehaviour droppedComp)
                    {
                        if (field.FieldType.IsInstanceOfType(droppedComp))
                        {
                            SetFieldWithUndo(target, field, current, droppedComp);
                            EditorDragDrop.Clear();
                        }
                    }
                }
            }

            // ImGui native drag-drop target for cross-panel drops
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
                            // Resolve GUID to path if needed
                            string? resolvedPath = assetSvc!.GetAssetPathByGuid(data);
                            string relativePath = resolvedPath ?? data;
                            string absPath = assetSvc.GetAbsolutePath(relativePath);

                            var fakeEntry = new AssetEntry
                            {
                                Name = Path.GetFileName(absPath),
                                FullPath = absPath,
                                RelativePath = relativePath,
                                Extension = Path.GetExtension(absPath),
                            };

                            EngineObject? loaded = TryLoadAssetForField(fakeEntry, field.FieldType);
                            if (loaded != null)
                                SetFieldWithUndo(target, field, current, loaded);
                        }
                    }
                }
                ImGui.EndDragDropTarget();
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

                // Built-in primitive meshes for Mesh fields
                if (field.FieldType == typeof(Prowl.Runtime.Resources.Mesh) || field.FieldType.IsSubclassOf(typeof(Prowl.Runtime.Resources.Mesh)))
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "Primitives:");

                    foreach ((string name, Prowl.Runtime.Resources.Mesh mesh) in Prowl.Runtime.Resources.PrimitiveMeshes.All)
                    {
                        if (!string.IsNullOrEmpty(_assetPickerFilter) &&
                            !name.Contains(_assetPickerFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (ImGui.Selectable($"  {name}"))
                        {
                            SetFieldWithUndo(target, field, current, mesh);
                            _activePickerFieldId = null;
                            ImGui.CloseCurrentPopup();
                        }
                    }
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
                    // Only show project files section when there are compatible extensions
                    var entries = assetDb.GetAllEntriesRecursive();
                    bool headerDrawn = false;
                    foreach (var entry in entries)
                    {
                        if (entry.IsDirectory) continue;
                        if (!string.IsNullOrEmpty(_assetPickerFilter) &&
                            !entry.Name.Contains(_assetPickerFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Filter by compatible extension for the field type
                        if (!IsAssetCompatible(entry, field.FieldType))
                            continue;

                        if (!headerDrawn)
                        {
                            ImGui.Spacing();
                            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "Project:");
                            headerDrawn = true;
                        }

                        if (ImGui.Selectable(entry.Name))
                        {
                            EngineObject? loaded = TryLoadAssetForField(entry, field.FieldType);
                            if (loaded != null)
                                SetFieldWithUndo(target, field, current, loaded);
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
    // Asset loading helpers for drag-drop & picker
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Mesh-compatible model file extensions.
    /// </summary>
    private static readonly HashSet<string> MeshExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".obj", ".fbx", ".gltf", ".glb", ".dae", ".blend", ".3ds", ".ply", ".stl"
    };

    private static readonly HashSet<string> MaterialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mat"
    };

    private static readonly HashSet<string> ShaderExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".shader"
    };

    private static readonly HashSet<string> TextureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".hdr"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".ogg", ".flac"
    };

    /// <summary>
    /// Returns true if the given asset entry is compatible with the target field type.
    /// Only shows assets whose file extension matches the expected type.
    /// </summary>
    private static bool IsAssetCompatible(AssetEntry entry, Type fieldType)
    {
        if (fieldType == typeof(Prowl.Runtime.Resources.Mesh) || fieldType.IsSubclassOf(typeof(Prowl.Runtime.Resources.Mesh)))
            return MeshExtensions.Contains(entry.Extension);
        if (fieldType == typeof(Prowl.Runtime.Resources.Material) || fieldType.IsSubclassOf(typeof(Prowl.Runtime.Resources.Material)))
            return MaterialExtensions.Contains(entry.Extension);
        if (fieldType == typeof(Prowl.Runtime.Resources.Shader) || fieldType.IsSubclassOf(typeof(Prowl.Runtime.Resources.Shader)))
            return ShaderExtensions.Contains(entry.Extension);
        if (fieldType == typeof(Prowl.Runtime.Resources.Model) || fieldType.IsSubclassOf(typeof(Prowl.Runtime.Resources.Model)))
            return MeshExtensions.Contains(entry.Extension);
        if (fieldType == typeof(Prowl.Runtime.Resources.Texture2D) || fieldType.IsSubclassOf(typeof(Prowl.Runtime.Resources.Texture2D)))
            return TextureExtensions.Contains(entry.Extension);
        if (fieldType == typeof(Prowl.Runtime.Resources.AudioClip) || fieldType.IsSubclassOf(typeof(Prowl.Runtime.Resources.AudioClip)))
            return AudioExtensions.Contains(entry.Extension);
        if (fieldType == typeof(Prowl.Runtime.AnimationClip) || fieldType.IsSubclassOf(typeof(Prowl.Runtime.AnimationClip)))
            return false; // Animations are loaded from model files, not standalone
        if (fieldType == typeof(Prowl.Runtime.Resources.Scene) || fieldType.IsSubclassOf(typeof(Prowl.Runtime.Resources.Scene)))
            return entry.Extension.Equals(".scene", StringComparison.OrdinalIgnoreCase);
        if (fieldType == typeof(GameObject) || fieldType.IsSubclassOf(typeof(GameObject)))
            return false; // GameObjects come from the scene, not asset files

        // Unknown EngineObject types: don't show file assets (avoids cluttering the list)
        return false;
    }

    /// <summary>
    /// Attempts to load an asset from disk and return an EngineObject of the appropriate type.
    /// </summary>
    private static EngineObject? TryLoadAssetForField(AssetEntry entry, Type fieldType)
    {
        try
        {
            string path = entry.FullPath;
            string ext = entry.Extension.ToLowerInvariant();

            // Texture2D field: load via TextureImporter with import settings
            if (fieldType == typeof(Texture2D))
            {
                if (Importing.TextureImporter.IsTextureFile(ext) && File.Exists(path))
                {
                    return Importing.TextureImporter.Import(path);
                }
            }

            // Mesh field: load via ModelImporter, return the first mesh
            if (fieldType == typeof(Prowl.Runtime.Resources.Mesh))
            {
                if (MeshExtensions.Contains(ext) && File.Exists(path))
                {
                    var model = Prowl.Runtime.Resources.Model.LoadFromFile(path);
                    if (model.Meshes.Count > 0)
                    {
                        var mesh = model.Meshes[0].Mesh;
                        mesh.AssetPath = path;
                        mesh.Name = Path.GetFileNameWithoutExtension(path);
                        return mesh;
                    }
                }
            }

            // Model field: load the full model
            if (fieldType == typeof(Prowl.Runtime.Resources.Model))
            {
                if (MeshExtensions.Contains(ext) && File.Exists(path))
                {
                    return Prowl.Runtime.Resources.Model.LoadFromFile(path);
                }
            }

            // Material field: load from .mat JSON file
            if (fieldType == typeof(Prowl.Runtime.Resources.Material))
            {
                if (ext == ".mat" && File.Exists(path))
                {
                    var mat = MaterialSerializer.Load(path);
                    if (mat != null)
                        return mat;
                }
            }

            // Shader field: load from .shader file
            if (fieldType == typeof(Prowl.Runtime.Resources.Shader))
            {
                if (ext == ".shader" && File.Exists(path))
                {
                    return Prowl.Runtime.Resources.Shader.LoadFromFile(path);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Inspector] Failed to load asset: {ex.Message}");
        }

        return null;
    }

    // ────────────────────────────────────────────────────────────
    // Undo helper
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to find the referenced asset in the project and ping it in the ProjectPanel.
    /// Navigates to the asset's folder and highlights it.
    /// </summary>
    private static void PingReferencedAsset(EngineObject obj)
    {
        if (obj == null) return;

        // Try to find the asset by its AssetPath property or name
        if (!EditorServices.TryGet<IAssetService>(out var assets) || !assets!.HasProject)
            return;

        string? assetRelPath = null;

        // Check if the object has an AssetPath property
        var assetPathProp = obj.GetType().GetProperty("AssetPath");
        if (assetPathProp != null)
        {
            string? absPath = assetPathProp.GetValue(obj) as string;
            if (!string.IsNullOrEmpty(absPath) && absPath.StartsWith(assets.AssetRootPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    assetRelPath = Path.GetRelativePath(assets.AssetRootPath, absPath).Replace('\\', '/');
                }
                catch { /* not a project path */ }
            }
        }

        // Fall back to searching by name in the asset database
        if (assetRelPath == null && !string.IsNullOrEmpty(obj.Name))
        {
            var allEntries = assets.GetAllEntriesRecursive();
            foreach (var entry in allEntries)
            {
                if (entry.IsDirectory) continue;
                string nameNoExt = Path.GetFileNameWithoutExtension(entry.Name);
                if (nameNoExt.Equals(obj.Name, StringComparison.OrdinalIgnoreCase))
                {
                    assetRelPath = entry.RelativePath;
                    break;
                }
            }
        }

        if (assetRelPath != null)
        {
            // Find the ProjectPanel and ping the asset
            if (EditorServices.TryGet<ProjectPanel>(out var projectPanel))
            {
                projectPanel!.PingAsset(assetRelPath);
            }
        }
    }

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

        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !ImGui.IsAnyItemActive() && !ImGui.IsMouseClicked(0))
            ImGui.SetKeyboardFocusHere(0);

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
        ImGui.Dummy(new Vector2(40, 32));
        icon.Draw(cursorPos - new Vector2(0, 10), 40f);
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
            case ".shader":
                DrawShaderAssetInfo(asset);
                break;
            case ".cs":
                DrawScriptAssetInfo(asset);
                break;
            case ".asset":
                DrawScriptableObjectAssetInfo(asset);
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

        if (!File.Exists(asset.FullPath))
        {
            ImGui.TextDisabled("(file not found)");
            return;
        }

        // Load the material using the serializer
        var mat = MaterialSerializer.Load(asset.FullPath);
        if (mat == null)
        {
            ImGui.TextDisabled("(unable to parse material)");
            return;
        }

        // Draw material properties via the MaterialInspector
        MaterialInspector.DrawMaterial(mat, asset.FullPath);
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

    private static void DrawShaderAssetInfo(AssetEntry asset)
    {
        if (!ImGui.CollapsingHeader("Shader", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!File.Exists(asset.FullPath))
        {
            ImGui.TextDisabled("(file not found)");
            return;
        }

        try
        {
            var shader = Prowl.Runtime.Resources.Shader.LoadFromFile(asset.FullPath);
            if (shader == null)
            {
                ImGui.TextDisabled("(failed to parse shader)");
                return;
            }

            ImGui.TextColored(new Vector4(0.70f, 0.80f, 0.90f, 1f), shader.Name ?? "Unnamed Shader");
            ImGui.Spacing();

            // Properties
            bool anyProps = false;
            foreach (var prop in shader.Properties)
            {
                if (!anyProps)
                {
                    ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "Properties:");
                    ImGui.Indent();
                    anyProps = true;
                }
                string typeName = prop.PropertyType.ToString();
                string display = !string.IsNullOrEmpty(prop.DisplayName) ? prop.DisplayName : prop.Name;
                ImGui.BulletText($"{display}  ({typeName})");
            }
            if (anyProps) ImGui.Unindent();
            else ImGui.TextDisabled("No properties defined.");

            ImGui.Spacing();

            // Passes
            int passCount = 0;
            foreach (var pass in shader.Passes)
                passCount++;

            DrawAssetFieldRow("Passes", passCount.ToString());

            int idx = 0;
            foreach (var pass in shader.Passes)
            {
                string passLabel = !string.IsNullOrEmpty(pass.Name) ? pass.Name : $"Pass {idx}";
                ImGui.BulletText(passLabel);
                idx++;
            }
        }
        catch (Exception ex)
        {
            ImGui.TextDisabled($"(error: {ex.Message})");
        }
    }

    // ── Texture import settings editing state ────────────────
    private static Importing.TextureImportSettings? _texImportSettings;
    private static string? _texImportSettingsPath;

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

        // ── Import Settings ──
        if (!ImGui.CollapsingHeader("Import Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // Load settings lazily or when the asset path changes
        if (_texImportSettings == null || _texImportSettingsPath != asset.FullPath)
        {
            _texImportSettings = Importing.TextureImporter.LoadSettings(asset.FullPath);
            _texImportSettingsPath = asset.FullPath;
        }

        bool changed = false;

        // Wrap Mode
        if (ImGui.BeginTable("##texWrap", 2, ImGuiTableFlags.None))
        {
            float totalW = ImGui.GetContentRegionAvail().X;
            ImGui.TableSetupColumn("lbl", ImGuiTableColumnFlags.WidthFixed, totalW * LabelRatio);
            ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Wrap Mode");
            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1);
            int wrapIdx = (int)_texImportSettings.WrapMode;
            if (ImGui.Combo("##wrapMode", ref wrapIdx,
                ["Repeat", "ClampToBorder", "ClampToEdge", "MirroredRepeat"], 4))
            {
                _texImportSettings.WrapMode = (TextureWrap)wrapIdx;
                changed = true;
            }
            ImGui.EndTable();
        }

        // Min Filter
        if (ImGui.BeginTable("##texMin", 2, ImGuiTableFlags.None))
        {
            float totalW = ImGui.GetContentRegionAvail().X;
            ImGui.TableSetupColumn("lbl", ImGuiTableColumnFlags.WidthFixed, totalW * LabelRatio);
            ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Min Filter");
            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1);
            var minNames = Enum.GetNames<TextureMin>();
            int minIdx = (int)_texImportSettings.MinFilter;
            if (ImGui.Combo("##minFilter", ref minIdx, minNames, minNames.Length))
            {
                _texImportSettings.MinFilter = (TextureMin)minIdx;
                changed = true;
            }
            ImGui.EndTable();
        }

        // Mag Filter
        if (ImGui.BeginTable("##texMag", 2, ImGuiTableFlags.None))
        {
            float totalW = ImGui.GetContentRegionAvail().X;
            ImGui.TableSetupColumn("lbl", ImGuiTableColumnFlags.WidthFixed, totalW * LabelRatio);
            ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Mag Filter");
            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1);
            var magNames = Enum.GetNames<TextureMag>();
            int magIdx = (int)_texImportSettings.MagFilter;
            if (ImGui.Combo("##magFilter", ref magIdx, magNames, magNames.Length))
            {
                _texImportSettings.MagFilter = (TextureMag)magIdx;
                changed = true;
            }
            ImGui.EndTable();
        }

        // Generate Mipmaps
        if (ImGui.BeginTable("##texMip", 2, ImGuiTableFlags.None))
        {
            float totalW = ImGui.GetContentRegionAvail().X;
            ImGui.TableSetupColumn("lbl", ImGuiTableColumnFlags.WidthFixed, totalW * LabelRatio);
            ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Generate Mipmaps");
            ImGui.TableSetColumnIndex(1);
            bool mip = _texImportSettings.GenerateMipmaps;
            if (ImGui.Checkbox("##genMipmaps", ref mip))
            {
                _texImportSettings.GenerateMipmaps = mip;
                changed = true;
            }
            ImGui.EndTable();
        }

        // Apply button
        ImGui.Spacing();
        bool canApply = changed || _texImportSettings != null;
        if (ImGui.Button("Apply Import Settings"))
        {
            Importing.TextureImporter.SaveSettings(asset.FullPath, _texImportSettings!);
            Runtime.Debug.Log($"[Inspector] Saved import settings for: {asset.Name}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Revert"))
        {
            _texImportSettings = Importing.TextureImporter.LoadSettings(asset.FullPath);
        }
    }

    private static void DrawScriptableObjectAssetInfo(AssetEntry asset)
    {
        ScriptableObject? so = null;
        try
        {
            so = ScriptableObjectSerializer.Load(asset.FullPath);
        }
        catch
        {
            // handled below
        }

        if (so == null)
        {
            ImGui.TextDisabled("(unable to load ScriptableObject)");
            return;
        }

        string typeName = so.GetType().Name;
        if (!ImGui.CollapsingHeader($"     {typeName}", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        IconManager.DrawIconOverLastItem("File");

        // Draw all serializable public fields on the ScriptableObject
        var fields = so.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
        bool changed = false;

        foreach (var field in fields)
        {
            // Skip EngineObject internals
            if (field.DeclaringType == typeof(EngineObject))
                continue;

            if (Attribute.IsDefined(field, typeof(HideInInspectorAttribute)))
                continue;

            string label = field.Name;
            object? value = field.GetValue(so);
            string valueStr = value?.ToString() ?? "(null)";

            if (field.FieldType == typeof(float) && value is float fv)
            {
                if (DrawDragFloat(label, ref fv))
                {
                    field.SetValue(so, fv);
                    changed = true;
                }
            }
            else if (field.FieldType == typeof(int) && value is int iv)
            {
                if (DrawDragInt(label, ref iv))
                {
                    field.SetValue(so, iv);
                    changed = true;
                }
            }
            else if (field.FieldType == typeof(bool) && value is bool bv)
            {
                DrawFieldRow(label, () =>
                {
                    if (ImGui.Checkbox($"##{label}", ref bv))
                    {
                        field.SetValue(so, bv);
                        changed = true;
                    }
                });
            }
            else if (field.FieldType == typeof(string))
            {
                string sv = (value as string) ?? string.Empty;
                DrawFieldRow(label, () =>
                {
                    if (ImGui.InputText($"##{label}", ref sv, 1024))
                    {
                        field.SetValue(so, sv);
                        changed = true;
                    }
                });
            }
            else
            {
                DrawAssetFieldRow(label, valueStr);
            }
        }

        // Also draw fields with [SerializeField] attribute (private fields)
        var privateFields = so.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var field in privateFields)
        {
            if (!Attribute.IsDefined(field, typeof(SerializeFieldAttribute)))
                continue;
            if (field.DeclaringType == typeof(EngineObject))
                continue;
            if (Attribute.IsDefined(field, typeof(HideInInspectorAttribute)))
                continue;

            string label = field.Name.TrimStart('_');
            object? value = field.GetValue(so);
            string valueStr = value?.ToString() ?? "(null)";
            DrawAssetFieldRow(label, valueStr);
        }

        ImGui.Spacing();

        if (changed)
        {
            try
            {
                ScriptableObjectSerializer.Save(so, asset.FullPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to save ScriptableObject: {ex.Message}");
            }
        }
    }

    private static bool DrawDragFloat(string label, ref float value)
    {
        bool changed = false;
        float local = value;
        DrawFieldRow(label, () =>
        {
            if (ImGui.DragFloat($"##{label}", ref local, 0.1f))
                changed = true;
        });
        if (changed) value = local;
        return changed;
    }

    private static bool DrawDragInt(string label, ref int value)
    {
        bool changed = false;
        int local = value;
        DrawFieldRow(label, () =>
        {
            if (ImGui.DragInt($"##{label}", ref local))
                changed = true;
        });
        if (changed) value = local;
        return changed;
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

    // ────────────────────────────────────────────────────────────
    // Prefab info bar
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws a compact toolbar for prefab instances showing the prefab
    /// source name and quick-access buttons: Select, Revert, Apply, Unpack.
    /// </summary>
    private static void DrawPrefabBar(GameObject go)
    {
        var prefabRoot = go.GetPrefabRoot();
        if (prefabRoot?.PrefabLink == null) return;

        var link = prefabRoot.PrefabLink;
        string prefabName = PrefabManager.GetPrefabName(link);

        // Background tint for the prefab bar
        var drawList = ImGui.GetWindowDrawList();
        var cursorPos = ImGui.GetCursorScreenPos();
        float barW = ImGui.GetContentRegionAvail().X;
        float barH = ImGui.GetTextLineHeightWithSpacing() + 8 * Game.DpiScale;
        drawList.AddRectFilled(cursorPos, new Vector2(cursorPos.X + barW, cursorPos.Y + barH),
            ImGui.GetColorU32(new Vector4(0.18f, 0.30f, 0.55f, 0.35f)), 4f);
        drawList.AddRect(cursorPos, new Vector2(cursorPos.X + barW, cursorPos.Y + barH),
            ImGui.GetColorU32(new Vector4(0.30f, 0.50f, 0.85f, 0.50f)), 4f, ImDrawFlags.None, 1f);

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3 * Game.DpiScale);
        ImGui.Indent(6 * Game.DpiScale);

        // Prefab icon + name
        EditorIcons.InlineIcon(EditorIconType.Prefab, new Vector4(0.45f, 0.65f, 1.0f, 1.0f));
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.45f, 0.65f, 1.0f, 1.0f), prefabName);

        // Buttons
        ImGui.SameLine();
        float btnStart = ImGui.GetContentRegionAvail().X - 260 * Game.DpiScale;
        if (btnStart > 0)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + btnStart);

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4 * Game.DpiScale, 2 * Game.DpiScale));

        if (ImGui.SmallButton("Select"))
        {
            // Navigate to prefab asset in project browser
            SelectPrefabAssetInProject(link);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Open"))
        {
            // Open the prefab in prefab edit mode
            string absPath = PrefabManager.ResolvePrefabAbsolutePath(link);
            if (!string.IsNullOrEmpty(absPath) && File.Exists(absPath))
            {
                if (EditorServices.TryGet<PrefabEditMode>(out var prefabMode))
                    prefabMode!.Enter(absPath);
            }
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Revert"))
        {
            if (EditorServices.TryGet<UndoRedoService>(out var undo))
                undo!.Execute(new RevertPrefabCommand(prefabRoot));
            else
                new PrefabManager().RevertInstance(prefabRoot);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Apply"))
        {
            if (EditorServices.TryGet<UndoRedoService>(out var undo))
                undo!.Execute(new ApplyPrefabCommand(prefabRoot));
            else
                new PrefabManager().ApplyInstance(prefabRoot);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Unpack"))
        {
            if (EditorServices.TryGet<UndoRedoService>(out var undo))
                undo!.Execute(new UnpackPrefabCommand(prefabRoot));
            else
                PrefabManager.UnpackInstance(prefabRoot);
        }

        ImGui.PopStyleVar();
        ImGui.Unindent(6 * Game.DpiScale);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3 * Game.DpiScale);
        ImGui.Separator();
    }

    /// <summary>
    /// Selects the prefab asset in the Project panel.
    /// </summary>
    private static void SelectPrefabAssetInProject(PrefabLink link)
    {
        if (!EditorServices.TryGet<IAssetService>(out var assets)) return;

        string? relativePath = null;
        if (!string.IsNullOrEmpty(link.PrefabAssetGuid))
            relativePath = assets!.GetAssetPathByGuid(link.PrefabAssetGuid);
        if (string.IsNullOrEmpty(relativePath))
            relativePath = link.PrefabAssetPath;

        if (string.IsNullOrEmpty(relativePath)) return;

        string fullPath = assets!.GetAbsolutePath(relativePath);
        string name = Path.GetFileName(relativePath);
        string ext = Path.GetExtension(relativePath);

        var entry = new AssetEntry
        {
            Name = name,
            FullPath = fullPath,
            RelativePath = relativePath,
            IsDirectory = false,
            Extension = ext,
        };

        if (EditorServices.TryGet<ISelectionService>(out var sel))
            sel!.SelectedAsset = entry;
    }
}
