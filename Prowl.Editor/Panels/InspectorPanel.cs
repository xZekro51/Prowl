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
using Prowl.Runtime.Text;
using Prowl.Runtime.EventSystem;
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

        // Refresh font inspector when a font atlas is rebuilt
        TextEvents.SubscribeOnFontAtlasChanged(args =>
        {
            if (_cachedFontAsset != null && args.FontAsset == _cachedFontAsset)
            {
                _cachedFontAsset = null;
                _cachedFontAssetPath = null;
                _glyphIndexToCodepoint = null;
            }
        });

        // Refresh text renderer inspector layout info when a mesh is rebuilt
        TextEvents.SubscribeOnTextMeshRebuilt(_ =>
        {
            // The layout info in DrawTextRendererInspector reads directly
            // from the TextRenderer each frame, so no cache invalidation needed.
            // This subscription exists as the documented integration point.
        });
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
        bool isRect = go.Transform is Prowl.Vector.RectTransform;
        string headerLabel = isRect ? "     Rect Transform" : "     Transform";
        if (ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.DefaultOpen))
        {
            IconManager.DrawIconOverLastItem("Transform");
            if (isRect)
                DrawRectTransform((Prowl.Vector.RectTransform)go.Transform);
            else
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
                if (comp is MeshRenderer meshRenderer && meshRenderer.Materials is { Length: > 0 })
                {
                    ImGui.Spacing();
                    for (int mi = 0; mi < meshRenderer.Materials.Length; mi++)
                    {
                        var mat = meshRenderer.Materials[mi];
                        if (mat == null) continue;
                        string matLabel = meshRenderer.Materials.Length == 1
                            ? "Material"
                            : $"Material [{mi}]";
                        if (ImGui.TreeNodeEx(matLabel, ImGuiTreeNodeFlags.DefaultOpen))
                        {
                            MaterialInspector.DrawMaterial(mat);
                            ImGui.TreePop();
                        }
                    }
                }

                // TextRenderer custom inspector section
                if (comp is TextRenderer textRenderer)
                {
                    DrawTextRendererInspector(textRenderer);
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

    // ────────────────────────────────────────────────────────────
    // RectTransform editing (Unity-like inspector)
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Anchor preset definition for the anchor preset picker grid.
    /// </summary>
    private readonly record struct AnchorPreset(
        string Name,
        Prowl.Vector.Float2 AnchorMin,
        Prowl.Vector.Float2 AnchorMax,
        Prowl.Vector.Float2 Pivot);

    private static readonly AnchorPreset[] s_anchorPresets =
    [
        // Row 0 — point anchors
        new("Top-Left",       new(0, 0),   new(0, 0),   new(0, 0)),
        new("Top-Center",     new(0.5f, 0), new(0.5f, 0), new(0.5f, 0)),
        new("Top-Right",      new(1, 0),   new(1, 0),   new(1, 0)),
        new("Top-Stretch",    new(0, 0),   new(1, 0),   new(0.5f, 0)),

        // Row 1 — middle
        new("Mid-Left",       new(0, 0.5f), new(0, 0.5f), new(0, 0.5f)),
        new("Center",         new(0.5f, 0.5f), new(0.5f, 0.5f), new(0.5f, 0.5f)),
        new("Mid-Right",      new(1, 0.5f), new(1, 0.5f), new(1, 0.5f)),
        new("Mid-Stretch-H",  new(0, 0.5f), new(1, 0.5f), new(0.5f, 0.5f)),

        // Row 2 — bottom
        new("Bot-Left",       new(0, 1),   new(0, 1),   new(0, 1)),
        new("Bot-Center",     new(0.5f, 1), new(0.5f, 1), new(0.5f, 1)),
        new("Bot-Right",      new(1, 1),   new(1, 1),   new(1, 1)),
        new("Bot-Stretch",    new(0, 1),   new(1, 1),   new(0.5f, 1)),

        // Row 3 — vertical stretch
        new("Stretch-Top",    new(0, 0),   new(0, 1),   new(0, 0.5f)),
        new("Stretch-Center", new(0.5f, 0), new(0.5f, 1), new(0.5f, 0.5f)),
        new("Stretch-Right",  new(1, 0),   new(1, 1),   new(1, 0.5f)),
        new("Stretch-All",    new(0, 0),   new(1, 1),   new(0.5f, 0.5f)),
    ];

    private static void DrawRectTransform(Prowl.Vector.RectTransform rt)
    {
        float totalW = ImGui.GetContentRegionAvail().X;
        float anchorBtnSize = 48 * Game.DpiScale;
        float spacing = ImGui.GetStyle().ItemSpacing.X;

        // ── Row: Anchor preset button + Position/Size fields ───
        if (ImGui.BeginTable("##rt_top", 2, ImGuiTableFlags.None))
        {
            ImGui.TableSetupColumn("anchor", ImGuiTableColumnFlags.WidthFixed, anchorBtnSize + spacing);
            ImGui.TableSetupColumn("fields", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            // Anchor preset button (visual icon)
            ImGui.TableSetColumnIndex(0);
            DrawAnchorPresetButton(rt, anchorBtnSize);

            // Position / Size fields on the right
            ImGui.TableSetColumnIndex(1);
            DrawRectPositionAndSize(rt);

            ImGui.EndTable();
        }

        // ── Anchors (collapsible) ──────────────────────────────
        if (ImGui.TreeNodeEx("Anchors", ImGuiTreeNodeFlags.None))
        {
            DrawLabeledFloat2("Min", ref rt.AnchorMin, 0.01f);
            DrawLabeledFloat2("Max", ref rt.AnchorMax, 0.01f);
            ImGui.TreePop();
        }

        // ── Pivot ──────────────────────────────────────────────
        DrawLabeledFloat2("Pivot", ref rt.Pivot, 0.01f);

        // ── Rotation ───────────────────────────────────────────
        var rot = ToNumerics(rt.LocalEulerAngles);
        if (DrawLabeledFloat3("Rotation", ref rot, 0.5f))
            rt.LocalEulerAngles = FromNumerics(rot);

        // ── Scale ──────────────────────────────────────────────
        var scl = ToNumerics(rt.LocalScale);
        if (DrawLabeledFloat3("Scale", ref scl, 0.05f))
            rt.LocalScale = FromNumerics(scl);
    }

    /// <summary>
    /// Draws the position / size area on the right side of the anchor icon.
    /// Adapts labels based on whether anchors are together or apart on each axis.
    /// </summary>
    private static void DrawRectPositionAndSize(Prowl.Vector.RectTransform rt)
    {
        bool stretchH = MathF.Abs(rt.AnchorMin.X - rt.AnchorMax.X) > 1e-6f;
        bool stretchV = MathF.Abs(rt.AnchorMin.Y - rt.AnchorMax.Y) > 1e-6f;

        // Row 1: Position
        {
            string labelX = stretchH ? "Left" : "Pos X";
            string labelY = stretchV ? "Top" : "Pos Y";

            float posX = rt.AnchoredPosition.X;
            float posY = rt.AnchoredPosition.Y;
            float posZ = rt.LocalPosition.Z;

            float avail = ImGui.GetContentRegionAvail().X;
            float fieldW = (avail - ImGui.GetStyle().ItemSpacing.X * 2) / 3f;

            bool changed = false;

            ImGui.PushID("rt_pos");
            ImGui.BeginGroup();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), labelX);
            ImGui.SameLine(0, 0);
            ImGui.SetNextItemWidth(fieldW - ImGui.CalcTextSize(labelX).X - 4 * Game.DpiScale);
            ImGui.SameLine();
            if (ImGui.DragFloat("##px", ref posX, 0.5f)) changed = true;
            ImGui.SameLine();

            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), labelY);
            ImGui.SameLine(0, 0);
            ImGui.SetNextItemWidth(fieldW - ImGui.CalcTextSize(labelY).X - 4 * Game.DpiScale);
            ImGui.SameLine();
            if (ImGui.DragFloat("##py", ref posY, 0.5f)) changed = true;
            ImGui.SameLine();

            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Pos Z");
            ImGui.SameLine(0, 0);
            ImGui.SetNextItemWidth(MathF.Max(20 * Game.DpiScale, ImGui.GetContentRegionAvail().X));
            ImGui.SameLine();
            if (ImGui.DragFloat("##pz", ref posZ, 0.05f)) changed = true;
            ImGui.EndGroup();
            ImGui.PopID();

            if (changed)
            {
                rt.AnchoredPosition = new Prowl.Vector.Float2(posX, posY);
                Prowl.Vector.Float3 lp = rt.LocalPosition;
                lp.Z = posZ;
                rt.LocalPosition = lp;
            }
        }

        // Row 2: Width / Height (or Right / Bottom for stretch)
        {
            string labelW = stretchH ? "Right" : "Width";
            string labelH = stretchV ? "Bottom" : "Height";

            float valW = rt.SizeDelta.X;
            float valH = rt.SizeDelta.Y;

            float avail = ImGui.GetContentRegionAvail().X;
            float fieldW = (avail - ImGui.GetStyle().ItemSpacing.X) / 2f;

            bool changed = false;

            ImGui.PushID("rt_size");
            ImGui.BeginGroup();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), labelW);
            ImGui.SameLine(0, 0);
            ImGui.SetNextItemWidth(fieldW - ImGui.CalcTextSize(labelW).X - 4 * Game.DpiScale);
            ImGui.SameLine();
            if (ImGui.DragFloat("##sw", ref valW, 0.5f)) changed = true;
            ImGui.SameLine();

            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), labelH);
            ImGui.SameLine(0, 0);
            ImGui.SetNextItemWidth(MathF.Max(20 * Game.DpiScale, ImGui.GetContentRegionAvail().X));
            ImGui.SameLine();
            if (ImGui.DragFloat("##sh", ref valH, 0.5f)) changed = true;
            ImGui.EndGroup();
            ImGui.PopID();

            if (changed)
                rt.SizeDelta = new Prowl.Vector.Float2(valW, valH);
        }
    }

    /// <summary>
    /// Draws the anchor preset button. Clicking opens the anchor preset popup grid.
    /// The button displays a visual representation of the current anchor configuration.
    /// </summary>
    private static void DrawAnchorPresetButton(Prowl.Vector.RectTransform rt, float size)
    {
        Vector2 pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        // Button background
        if (ImGui.InvisibleButton("##anchorPreset", new Vector2(size, size)))
            ImGui.OpenPopup("##AnchorPresetPopup");

        // Draw button background
        uint bgColor = ImGui.IsItemHovered()
            ? ImGui.GetColorU32(ImGuiCol.ButtonHovered)
            : ImGui.GetColorU32(ImGuiCol.Button);
        drawList.AddRectFilled(pos, pos + new Vector2(size, size), bgColor, 4f);

        // Draw border
        drawList.AddRect(pos, pos + new Vector2(size, size),
            ImGui.GetColorU32(ImGuiCol.Border), 4f);

        // Draw anchor visualization inside the button
        float pad = 6 * Game.DpiScale;
        float innerW = size - pad * 2;
        float innerH = size - pad * 2;
        Vector2 innerMin = pos + new Vector2(pad, pad);

        // Draw a rect representing the parent
        drawList.AddRect(innerMin, innerMin + new Vector2(innerW, innerH),
            ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 0.6f)));

        // Anchor min/max points
        float ax0 = innerMin.X + rt.AnchorMin.X * innerW;
        float ay0 = innerMin.Y + rt.AnchorMin.Y * innerH;
        float ax1 = innerMin.X + rt.AnchorMax.X * innerW;
        float ay1 = innerMin.Y + rt.AnchorMax.Y * innerH;

        uint anchorColor = ImGui.GetColorU32(new Vector4(1f, 0.55f, 0.15f, 1f));

        if (MathF.Abs(ax1 - ax0) < 2 && MathF.Abs(ay1 - ay0) < 2)
        {
            // Point anchor — draw crosshair
            float cx = (ax0 + ax1) * 0.5f;
            float cy = (ay0 + ay1) * 0.5f;
            float armLen = 5 * Game.DpiScale;
            drawList.AddLine(new Vector2(cx - armLen, cy), new Vector2(cx + armLen, cy), anchorColor, 2f);
            drawList.AddLine(new Vector2(cx, cy - armLen), new Vector2(cx, cy + armLen), anchorColor, 2f);
        }
        else
        {
            // Stretch anchor — draw anchor rect area
            drawList.AddRectFilled(
                new Vector2(ax0, ay0), new Vector2(ax1, ay1),
                ImGui.GetColorU32(new Vector4(1f, 0.55f, 0.15f, 0.2f)));
            drawList.AddRect(
                new Vector2(ax0, ay0), new Vector2(ax1, ay1),
                anchorColor, 0f, ImDrawFlags.None, 1.5f);

            // Draw corner triangles at the four anchor corners
            float triSize = 3 * Game.DpiScale;
            DrawAnchorTriangle(drawList, new Vector2(ax0, ay0), triSize, anchorColor, 0);
            DrawAnchorTriangle(drawList, new Vector2(ax1, ay0), triSize, anchorColor, 1);
            DrawAnchorTriangle(drawList, new Vector2(ax0, ay1), triSize, anchorColor, 2);
            DrawAnchorTriangle(drawList, new Vector2(ax1, ay1), triSize, anchorColor, 3);
        }

        // Anchor preset popup
        DrawAnchorPresetPopup(rt);
    }

    /// <summary>
    /// Draws a small triangle at an anchor corner.
    /// </summary>
    private static void DrawAnchorTriangle(ImDrawListPtr drawList, Vector2 center,
        float triSize, uint color, int corner)
    {
        // corner: 0=TL, 1=TR, 2=BL, 3=BR
        float dx = (corner % 2 == 0) ? 1 : -1;
        float dy = (corner < 2) ? 1 : -1;
        Vector2 a = center;
        Vector2 b = center + new Vector2(dx * triSize, 0);
        Vector2 c = center + new Vector2(0, dy * triSize);
        drawList.AddTriangleFilled(a, b, c, color);
    }

    /// <summary>
    /// Draws the anchor preset popup grid (4×4 of common presets).
    /// </summary>
    private static void DrawAnchorPresetPopup(Prowl.Vector.RectTransform rt)
    {
        if (!ImGui.BeginPopup("##AnchorPresetPopup"))
            return;

        ImGui.TextUnformatted("Anchor Presets");
        ImGui.Separator();

        float btnSize = 32 * Game.DpiScale;
        float spacing = 4 * Game.DpiScale;

        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                int idx = row * 4 + col;
                AnchorPreset preset = s_anchorPresets[idx];

                if (col > 0) ImGui.SameLine(0, spacing);

                ImGui.PushID(idx);
                Vector2 btnPos = ImGui.GetCursorScreenPos();

                // Check if this preset matches the current state
                bool isActive =
                    MathF.Abs(rt.AnchorMin.X - preset.AnchorMin.X) < 0.01f &&
                    MathF.Abs(rt.AnchorMin.Y - preset.AnchorMin.Y) < 0.01f &&
                    MathF.Abs(rt.AnchorMax.X - preset.AnchorMax.X) < 0.01f &&
                    MathF.Abs(rt.AnchorMax.Y - preset.AnchorMax.Y) < 0.01f;

                if (isActive)
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.5f, 0.8f, 0.6f));

                if (ImGui.Button("##preset", new Vector2(btnSize, btnSize)))
                {
                    rt.AnchorMin = preset.AnchorMin;
                    rt.AnchorMax = preset.AnchorMax;
                    rt.Pivot = preset.Pivot;
                    ImGui.CloseCurrentPopup();
                }

                if (isActive)
                    ImGui.PopStyleColor();

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(preset.Name);

                // Draw preset visualization inside the button
                var dl = ImGui.GetWindowDrawList();
                float pad = 4 * Game.DpiScale;
                float innerW = btnSize - pad * 2;
                float innerH = btnSize - pad * 2;
                Vector2 innerMin = btnPos + new Vector2(pad, pad);

                // Parent rect outline
                dl.AddRect(innerMin, innerMin + new Vector2(innerW, innerH),
                    ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 0.5f)));

                // Anchor area
                float pax0 = innerMin.X + preset.AnchorMin.X * innerW;
                float pay0 = innerMin.Y + preset.AnchorMin.Y * innerH;
                float pax1 = innerMin.X + preset.AnchorMax.X * innerW;
                float pay1 = innerMin.Y + preset.AnchorMax.Y * innerH;

                uint presetColor = ImGui.GetColorU32(new Vector4(1f, 0.55f, 0.15f, 1f));

                if (MathF.Abs(pax1 - pax0) < 2 && MathF.Abs(pay1 - pay0) < 2)
                {
                    // Point anchor — crosshair
                    float cx = (pax0 + pax1) * 0.5f;
                    float cy = (pay0 + pay1) * 0.5f;
                    float arm = 3 * Game.DpiScale;
                    dl.AddLine(new Vector2(cx - arm, cy), new Vector2(cx + arm, cy), presetColor, 1.5f);
                    dl.AddLine(new Vector2(cx, cy - arm), new Vector2(cx, cy + arm), presetColor, 1.5f);
                }
                else
                {
                    // Stretch — draw area
                    float lineW = MathF.Max(pax1 - pax0, 1);
                    float lineH = MathF.Max(pay1 - pay0, 1);
                    dl.AddRectFilled(
                        new Vector2(pax0, pay0),
                        new Vector2(pax0 + lineW, pay0 + lineH),
                        ImGui.GetColorU32(new Vector4(1f, 0.55f, 0.15f, 0.3f)));
                    dl.AddRect(
                        new Vector2(pax0, pay0),
                        new Vector2(pax0 + lineW, pay0 + lineH),
                        presetColor);
                }

                ImGui.PopID();
            }
        }

        ImGui.Separator();

        // Manual anchor input
        ImGui.TextUnformatted("Custom:");
        float inputW = 60 * Game.DpiScale;
        ImGui.SetNextItemWidth(inputW);
        ImGui.DragFloat("Min X", ref rt.AnchorMin.X, 0.01f, 0f, 1f, "%.2f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(inputW);
        ImGui.DragFloat("Min Y", ref rt.AnchorMin.Y, 0.01f, 0f, 1f, "%.2f");
        ImGui.SetNextItemWidth(inputW);
        ImGui.DragFloat("Max X", ref rt.AnchorMax.X, 0.01f, 0f, 1f, "%.2f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(inputW);
        ImGui.DragFloat("Max Y", ref rt.AnchorMax.Y, 0.01f, 0f, 1f, "%.2f");

        ImGui.EndPopup();
    }

    /// <summary>
    /// Draws a two-component Float2 row (X, Y) with a label.
    /// </summary>
    private static bool DrawLabeledFloat2(string label, ref Prowl.Vector.Float2 value, float speed)
    {
        bool changed = false;

        if (ImGui.BeginTable("##v2_" + label, 2, ImGuiTableFlags.None))
        {
            float w = ImGui.GetContentRegionAvail().X;
            ImGui.TableSetupColumn("lbl", ImGuiTableColumnFlags.WidthFixed, w * LabelRatio);
            ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);

            ImGui.TableSetColumnIndex(1);
            ImGui.PushID(label);

            float itemSpacing = ImGui.GetStyle().ItemSpacing.X;
            float buttonW = ImGui.GetFrameHeight();
            float avail = ImGui.GetContentRegionAvail().X;
            float fieldWidth = (avail - buttonW * 2 - itemSpacing) / 2f;
            if (fieldWidth < 20 * Game.DpiScale) fieldWidth = 20 * Game.DpiScale;

            float x = value.X, y = value.Y;

            // X
            if (DrawVectorComponent("X", ref x, speed, fieldWidth, buttonW,
                new Vector4(0.19f, 0.16f, 0.16f, 1f),
                new Vector4(0.34f, 0.29f, 0.29f, 1f),
                new Vector4(0.49f, 0.42f, 0.42f, 1f),
                new Vector4(0.86f, 0.42f, 0.41f, 1f)))
            { value.X = x; changed = true; }

            ImGui.SameLine(0, itemSpacing);

            // Y
            if (DrawVectorComponent("Y", ref y, speed, fieldWidth, buttonW,
                new Vector4(0.16f, 0.17f, 0.15f, 1f),
                new Vector4(0.31f, 0.32f, 0.28f, 1f),
                new Vector4(0.45f, 0.47f, 0.41f, 1f),
                new Vector4(0.56f, 0.72f, 0.35f, 1f)))
            { value.Y = y; changed = true; }

            ImGui.PopID();
            ImGui.EndTable();
        }

        return changed;
    }

    // Per-component drag state for the letter-label drag interaction
    private static uint _dragId;
    private static float _dragStartValue;
    private static Vector2 _dragStartPos;
    private static bool _isDraggingLabel;

    private static bool DrawVectorComponent(string letter, ref float value, float speed,
        float fieldWidth, float buttonW,
        Vector4 btnColor, Vector4 btnHover, Vector4 btnActive)
    {
        return DrawVectorComponent(letter, ref value, speed, fieldWidth, buttonW, btnColor, btnHover, btnActive, Vector4.One);
    }

    /// <summary>
    /// Draws a single vector component in the Stride / S&amp;box style: a small colored
    /// label that can be dragged horizontally to scrub the value, flush with an
    /// <see cref="ImGui.InputFloat"/> text field that is immediately editable.
    /// Click the label to reset to zero.
    /// </summary>
    private static bool DrawVectorComponent(string letter, ref float value, float speed,
        float fieldWidth, float buttonW,
        Vector4 btnColor, Vector4 btnHover, Vector4 btnActive, Vector4 labelColor)
    {
        bool changed = false;
        var style = ImGui.GetStyle();
        // Use ImGui.GetID to incorporate the full ID stack (including parent PushID),
        // so each property+component pair gets a truly unique tracking ID.
        uint id = ImGui.GetID("##lbl_" + letter);

        // ── Colored label (draggable, click-to-reset) ──────────
        ImGui.PushStyleColor(ImGuiCol.Button, btnColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, btnHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, btnActive);
        ImGui.PushStyleColor(ImGuiCol.Text, labelColor);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, style.ItemSpacing.Y));

        ImGui.Button(letter, new Vector2(buttonW, ImGui.GetFrameHeight()));
        bool labelHovered = ImGui.IsItemHovered();
        bool labelActive = ImGui.IsItemActive();

        // Show a horizontal-resize cursor when hovering the label
        if (labelHovered || (_isDraggingLabel && _dragId == id))
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);

        // Begin drag: record starting value and mouse position
        if (labelHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _dragId = id;
            _dragStartValue = value;
            _dragStartPos = ImGui.GetMousePos();
            _isDraggingLabel = false; // not dragging yet — could be a click
        }

        // Continue drag
        if (_dragId == id && ImGui.IsMouseDown(ImGuiMouseButton.Left))
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
        if (_dragId == id && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (!_isDraggingLabel)
            {
                // Pure click (no drag) → reset to zero
                value = 0;
                changed = true;
            }
            _dragId = 0;
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
    /// Integer variant of <see cref="DrawVectorComponent"/>. Draws a colored
    /// label button (draggable, click-to-reset) flush with an <see cref="ImGui.InputInt"/>
    /// text field.
    /// </summary>
    private static bool DrawVectorComponentInt(string letter, ref int value, float speed,
        float fieldWidth, float buttonW,
        Vector4 btnColor, Vector4 btnHover, Vector4 btnActive, Vector4 labelColor)
    {
        bool changed = false;
        var style = ImGui.GetStyle();
        uint id = ImGui.GetID("##lbl_" + letter);

        // ── Colored label (draggable, click-to-reset) ──────────
        ImGui.PushStyleColor(ImGuiCol.Button, btnColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, btnHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, btnActive);
        ImGui.PushStyleColor(ImGuiCol.Text, labelColor);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, style.ItemSpacing.Y));

        ImGui.Button(letter, new Vector2(buttonW, ImGui.GetFrameHeight()));
        bool labelHovered = ImGui.IsItemHovered();

        if (labelHovered || (_isDraggingLabel && _dragId == id))
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);

        if (labelHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _dragId = id;
            _dragStartValue = value;
            _dragStartPos = ImGui.GetMousePos();
            _isDraggingLabel = false;
        }

        if (_dragId == id && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            Vector2 delta = ImGui.GetMousePos() - _dragStartPos;
            if (!_isDraggingLabel && MathF.Abs(delta.X) > 2f)
                _isDraggingLabel = true;

            if (_isDraggingLabel)
            {
                value = (int)MathF.Round(_dragStartValue + delta.X * speed);
                changed = true;
            }
        }

        if (_dragId == id && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (!_isDraggingLabel)
            {
                value = 0;
                changed = true;
            }
            _dragId = 0;
            _isDraggingLabel = false;
        }

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(4);

        // ── InputInt flush against the label ───────────────────
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, style.ItemSpacing.Y));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(fieldWidth);
        if (ImGui.InputInt("##" + letter, ref value, 0, 0))
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

            // Right-click label to copy / paste the whole vector
            if (ImGui.BeginPopupContextItem("##v3ctx"))
            {
                if (ImGui.MenuItem("Copy Vector"))
                    ImGui.SetClipboardText($"{value.X}, {value.Y}, {value.Z}");
                if (ImGui.MenuItem("Paste Vector"))
                {
                    string cb = ImGui.GetClipboardText();
                    if (!string.IsNullOrWhiteSpace(cb))
                    {
                        var parts = cb.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px)) value.X = px;
                            if (float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float py)) value.Y = py;
                            if (float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pz)) value.Z = pz;
                            changed = true;
                        }
                    }
                }
                ImGui.EndPopup();
            }

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
                new Vector4(0.19f, 0.16f, 0.16f, 1f),
                new Vector4(0.34f, 0.29f, 0.29f, 1f),
                new Vector4(0.49f, 0.42f, 0.42f, 1f),
                new Vector4(0.86f, 0.42f, 0.41f, 1f)))
            { value.X = x; changed = true; }
            bool activeX = ImGui.IsItemFocused();


            ImGui.SameLine(0, spacing);

            // Y — Green
            if (DrawVectorComponent("Y", ref y, speed, fieldWidth, buttonW,
                new Vector4(0.16f, 0.17f, 0.15f, 1f),
                new Vector4(0.31f, 0.32f, 0.28f, 1f),
                new Vector4(0.45f, 0.47f, 0.41f, 1f),
                new Vector4(0.56f, 0.72f, 0.35f, 1f)))
            { value.Y = y; changed = true; }
            bool activeY = ImGui.IsItemFocused();

            ImGui.SameLine(0, spacing);

            // Z — Blue
            if (DrawVectorComponent("Z", ref z, speed, fieldWidth, buttonW,
                new Vector4(0.17f, 0.18f, 0.2f, 1f),
                new Vector4(0.3f, 0.32f, 0.35f, 1f),
                new Vector4(0.42f, 0.46f, 0.5f, 1f),
                new Vector4(0.34f, 0.51f, 0.71f, 1f)))
            { value.Z = z; changed = true; }
            bool activeZ = ImGui.IsItemFocused();

            if ((activeX || activeY || activeZ) && ImGui.GetIO().KeyCtrl && !ImGui.GetIO().WantTextInput)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.C))
                {
                    ImGui.SetClipboardText($"{value.X}, {value.Y}, {value.Z}");
                }
                else if (ImGui.IsKeyPressed(ImGuiKey.V))
                {
                    string cb = ImGui.GetClipboardText();
                    if (!string.IsNullOrWhiteSpace(cb))
                    {
                        var parts = cb.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px)) value.X = px;
                            if (float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float py)) value.Y = py;
                            if (float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pz)) value.Z = pz;
                            changed = true;
                        }
                    }
                }
            }

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

            // Right-click label to copy / paste the whole vector
            if (ImGui.BeginPopupContextItem("##v2ctx"))
            {
                if (ImGui.MenuItem("Copy Vector"))
                    ImGui.SetClipboardText($"{value.X}, {value.Y}");
                if (ImGui.MenuItem("Paste Vector"))
                {
                    string cb = ImGui.GetClipboardText();
                    if (!string.IsNullOrWhiteSpace(cb))
                    {
                        var parts = cb.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px)) value.X = px;
                            if (float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float py)) value.Y = py;
                            changed = true;
                        }
                    }
                }
                ImGui.EndPopup();
            }

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
                new Vector4(0.19f, 0.16f, 0.16f, 1f),
                new Vector4(0.34f, 0.29f, 0.29f, 1f),
                new Vector4(0.49f, 0.42f, 0.42f, 1f),
                new Vector4(0.86f, 0.42f, 0.41f, 1f)))
            { value.X = x; changed = true; }
            bool activeX = ImGui.IsItemFocused();

            ImGui.SameLine(0, spacing);

            // Y — Green
            if (DrawVectorComponent("Y", ref y, speed, fieldWidth, buttonW,
                new Vector4(0.16f, 0.17f, 0.15f, 1f),
                new Vector4(0.31f, 0.32f, 0.28f, 1f),
                new Vector4(0.45f, 0.47f, 0.41f, 1f),
                new Vector4(0.56f, 0.72f, 0.35f, 1f)))
            { value.Y = y; changed = true; }
            bool activeY = ImGui.IsItemFocused();

            if ((activeX || activeY) && ImGui.GetIO().KeyCtrl && !ImGui.GetIO().WantTextInput)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.C))
                {
                    ImGui.SetClipboardText($"{value.X}, {value.Y}");
                }
                else if (ImGui.IsKeyPressed(ImGuiKey.V))
                {
                    string cb = ImGui.GetClipboardText();
                    if (!string.IsNullOrWhiteSpace(cb))
                    {
                        var parts = cb.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px)) value.X = px;
                            if (float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float py)) value.Y = py;
                            changed = true;
                        }
                    }
                }
            }

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

            // Right-click label to copy / paste the whole vector
            if (ImGui.BeginPopupContextItem("##v4ctx"))
            {
                if (ImGui.MenuItem("Copy Vector"))
                    ImGui.SetClipboardText($"{value.X}, {value.Y}, {value.Z}, {value.W}");
                if (ImGui.MenuItem("Paste Vector"))
                {
                    string cb = ImGui.GetClipboardText();
                    if (!string.IsNullOrWhiteSpace(cb))
                    {
                        var parts = cb.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4)
                        {
                            if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px)) value.X = px;
                            if (float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float py)) value.Y = py;
                            if (float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pz)) value.Z = pz;
                            if (float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pw)) value.W = pw;
                            changed = true;
                        }
                    }
                }
                ImGui.EndPopup();
            }

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
                new Vector4(0.19f, 0.16f, 0.16f, 1f),
                new Vector4(0.34f, 0.29f, 0.29f, 1f),
                new Vector4(0.49f, 0.42f, 0.42f, 1f),
                new Vector4(0.86f, 0.42f, 0.41f, 1f)))
            { value.X = x; changed = true; }
            bool activeX = ImGui.IsItemFocused();

            ImGui.SameLine(0, spacing);

            // Y — Green
            if (DrawVectorComponent("Y", ref y, speed, fieldWidth, buttonW,
                new Vector4(0.16f, 0.17f, 0.15f, 1f),
                new Vector4(0.31f, 0.32f, 0.28f, 1f),
                new Vector4(0.45f, 0.47f, 0.41f, 1f),
                new Vector4(0.56f, 0.72f, 0.35f, 1f)))
            { value.Y = y; changed = true; }
            bool activeY = ImGui.IsItemFocused();

            ImGui.SameLine(0, spacing);

            // Z — Blue
            if (DrawVectorComponent("Z", ref z, speed, fieldWidth, buttonW,
                new Vector4(0.17f, 0.18f, 0.2f, 1f),
                new Vector4(0.3f, 0.32f, 0.35f, 1f),
                new Vector4(0.42f, 0.46f, 0.5f, 1f),
                new Vector4(0.34f, 0.51f, 0.71f, 1f)))
            { value.Z = z; changed = true; }
            bool activeZ = ImGui.IsItemFocused();

            ImGui.SameLine(0, spacing);

            // W — Purple
            if (DrawVectorComponent("W", ref w, speed, fieldWidth, buttonW,
                new Vector4(0.17f, 0.16f, 0.19f, 1f),
                new Vector4(0.31f, 0.29f, 0.34f, 1f),
                new Vector4(0.45f, 0.41f, 0.49f, 1f),
                new Vector4(0.62f, 0.4f, 0.84f, 1f)))
            { value.W = w; changed = true; }
            bool activeW = ImGui.IsItemFocused();

            if ((activeX || activeY || activeZ || activeW) && ImGui.GetIO().KeyCtrl && !ImGui.GetIO().WantTextInput)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.C))
                {
                    ImGui.SetClipboardText($"{value.X}, {value.Y}, {value.Z}, {value.W}");
                }
                else if (ImGui.IsKeyPressed(ImGuiKey.V))
                {
                    string cb = ImGui.GetClipboardText();
                    if (!string.IsNullOrWhiteSpace(cb))
                    {
                        var parts = cb.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4)
                        {
                            if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px)) value.X = px;
                            if (float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float py)) value.Y = py;
                            if (float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pz)) value.Z = pz;
                            if (float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pw)) value.W = pw;
                            changed = true;
                        }
                    }
                }
            }

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
                    float buttonW = ImGui.GetFrameHeight();
                    float fieldWidth = ImGui.GetContentRegionAvail().X - buttonW;
                    if (DrawVectorComponent("f", ref v, 0.01f, fieldWidth, buttonW,
                        new Vector4(0.17f, 0.18f, 0.2f, 1f),
                        new Vector4(0.3f, 0.32f, 0.35f, 1f),
                        new Vector4(0.42f, 0.46f, 0.5f, 1f),
                        new Vector4(0.34f, 0.51f, 0.71f, 1f)))
                        SetFieldWithUndo(target, field, value, v);
                });
            }
            else if (ft == typeof(double))
            {
                float v = (float)(double)(value ?? 0.0);
                DrawFieldRow(label, () =>
                {
                    float buttonW = ImGui.GetFrameHeight();
                    float fieldWidth = ImGui.GetContentRegionAvail().X - buttonW;
                    if (DrawVectorComponent("f", ref v, 0.01f, fieldWidth, buttonW,
                        new Vector4(0.17f, 0.18f, 0.2f, 1f),
                        new Vector4(0.3f, 0.32f, 0.35f, 1f),
                        new Vector4(0.42f, 0.46f, 0.5f, 1f),
                        new Vector4(0.34f, 0.51f, 0.71f, 1f)))
                        SetFieldWithUndo(target, field, value, (double)v);
                });
            }
            else if (ft == typeof(int))
            {
                int v = (int)(value ?? 0);
                DrawFieldRow(label, () =>
                {
                    float buttonW = ImGui.GetFrameHeight();
                    float fieldWidth = ImGui.GetContentRegionAvail().X - buttonW;
                    if (DrawVectorComponentInt("i", ref v, 0.15f, fieldWidth, buttonW,
                        new Vector4(0.17f, 0.18f, 0.2f, 1f),
                        new Vector4(0.3f, 0.32f, 0.35f, 1f),
                        new Vector4(0.42f, 0.46f, 0.5f, 1f),
                        new Vector4(0.34f, 0.51f, 0.71f, 1f)))
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
            else if (ft.IsArray || (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(List<>)))
            {
                // Array or List<T> editor
                DrawListField(label, target, field, value, ft);
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
    // Array / List editor
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws an inline editor for arrays and <see cref="List{T}"/> fields.
    /// Supports add/remove and delegates per-element drawing to the
    /// appropriate editor (primitives, enums, EngineObject references, etc.).
    /// </summary>
    private void DrawListField(string label, object target, FieldInfo field, object? value, Type fieldType)
    {
        Type elementType = fieldType.IsArray
            ? fieldType.GetElementType()!
            : fieldType.GetGenericArguments()[0];

        // Convert current value to a working List<object?> so we can mutate it uniformly
        var items = new List<object?>();
        int count = 0;

        if (value is Array arr)
        {
            count = arr.Length;
            for (int i = 0; i < count; i++)
                items.Add(arr.GetValue(i));
        }
        else if (value is System.Collections.IList list)
        {
            count = list.Count;
            for (int i = 0; i < count; i++)
                items.Add(list[i]);
        }

        bool changed = false;

        // Header with element count and +/- buttons
        bool open = ImGui.TreeNodeEx($"{label}  [{count}]", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap);

        // "+" button on the same line
        {
            float btnSz = ImGui.GetFrameHeight();
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - btnSz * 2 - ImGui.GetStyle().ItemSpacing.X + ImGui.GetCursorPosX());
            if (ImGui.SmallButton($"+##{label}"))
            {
                items.Add(CreateDefault(elementType));
                changed = true;
            }
            ImGui.SameLine();
            bool canRemove = items.Count > 0;
            if (!canRemove) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"-##{label}"))
            {
                items.RemoveAt(items.Count - 1);
                changed = true;
            }
            if (!canRemove) ImGui.EndDisabled();
        }

        if (open)
        {
            int removeIdx = -1;

            for (int i = 0; i < items.Count; i++)
            {
                ImGui.PushID(i);
                string elemLabel = $"Element {i}";
                object? elem = items[i];

                if (elementType == typeof(float))
                {
                    float v = (float)(elem ?? 0f);
                    DrawFieldRow(elemLabel, () =>
                    {
                        float buttonW = ImGui.GetFrameHeight();
                        float fieldWidth = ImGui.GetContentRegionAvail().X - buttonW;
                        if (DrawVectorComponent("f", ref v, 0.01f, fieldWidth, buttonW,
                            new Vector4(0.19f, 0.16f, 0.16f, 1f),
                            new Vector4(0.34f, 0.29f, 0.29f, 1f),
                            new Vector4(0.49f, 0.42f, 0.42f, 1f),
                            new Vector4(0.86f, 0.42f, 0.41f, 1f)))
                        { items[i] = v; changed = true; }
                    });
                }
                else if (elementType == typeof(double))
                {
                    float v = (float)(double)(elem ?? 0.0);
                    DrawFieldRow(elemLabel, () =>
                    {
                        float buttonW = ImGui.GetFrameHeight();
                        float fieldWidth = ImGui.GetContentRegionAvail().X - buttonW;
                        if (DrawVectorComponent("f", ref v, 0.01f, fieldWidth, buttonW,
                            new Vector4(0.19f, 0.16f, 0.16f, 1f),
                            new Vector4(0.34f, 0.29f, 0.29f, 1f),
                            new Vector4(0.49f, 0.42f, 0.42f, 1f),
                            new Vector4(0.86f, 0.42f, 0.41f, 1f)))
                        { items[i] = (double)v; changed = true; }
                    });
                }
                else if (elementType == typeof(int))
                {
                    int v = (int)(elem ?? 0);
                    DrawFieldRow(elemLabel, () =>
                    {
                        float buttonW = ImGui.GetFrameHeight();
                        float fieldWidth = ImGui.GetContentRegionAvail().X - buttonW;
                        if (DrawVectorComponentInt("i", ref v, 0.15f, fieldWidth, buttonW,
                            new Vector4(0.19f, 0.16f, 0.16f, 1f),
                            new Vector4(0.34f, 0.29f, 0.29f, 1f),
                            new Vector4(0.49f, 0.42f, 0.42f, 1f),
                            new Vector4(0.86f, 0.42f, 0.41f, 1f)))
                        { items[i] = v; changed = true; }
                    });
                }
                else if (elementType == typeof(bool))
                {
                    bool v = (bool)(elem ?? false);
                    DrawFieldRow(elemLabel, () =>
                    {
                        if (ImGui.Checkbox("##val", ref v))
                        { items[i] = v; changed = true; }
                    });
                }
                else if (elementType == typeof(string))
                {
                    string v = (string)(elem ?? string.Empty);
                    DrawFieldRow(elemLabel, () =>
                    {
                        if (ImGui.InputText("##val", ref v, 512))
                        { items[i] = v; changed = true; }
                    });
                }
                else if (elementType.IsEnum)
                {
                    string[] names = Enum.GetNames(elementType);
                    Array vals = Enum.GetValues(elementType);
                    int cur = Array.IndexOf(vals, elem ?? vals.GetValue(0));
                    if (cur < 0) cur = 0;
                    DrawFieldRow(elemLabel, () =>
                    {
                        if (ImGui.Combo("##val", ref cur, names, names.Length))
                        { items[i] = vals.GetValue(cur); changed = true; }
                    });
                }
                else if (elementType.IsSubclassOf(typeof(EngineObject)) || elementType == typeof(EngineObject) || elementType == typeof(GameObject))
                {
                    // Draw a mini asset-reference field for each element
                    DrawListElementAssetReference(elemLabel, items, i, elementType, ref changed);
                }
                else
                {
                    string text = elem?.ToString() ?? "(null)";
                    DrawFieldRow(elemLabel, () => ImGui.TextDisabled(text));
                }

                ImGui.PopID();
            }

            if (removeIdx >= 0)
            {
                items.RemoveAt(removeIdx);
                changed = true;
            }

            ImGui.TreePop();
        }

        if (changed)
        {
            object newValue;
            if (fieldType.IsArray)
            {
                var newArr = Array.CreateInstance(elementType, items.Count);
                for (int i = 0; i < items.Count; i++)
                    newArr.SetValue(items[i], i);
                newValue = newArr;
            }
            else
            {
                var newList = (System.Collections.IList)Activator.CreateInstance(fieldType)!;
                foreach (var item in items)
                    newList.Add(item);
                newValue = newList;
            }
            SetFieldWithUndo(target, field, value, newValue);
        }
    }

    /// <summary>
    /// Draws a compact asset-reference field for a single list element.
    /// </summary>
    private void DrawListElementAssetReference(string elemLabel, List<object?> items, int index, Type elementType, ref bool changed)
    {
        EngineObject? current = items[index] as EngineObject;
        string fieldId = $"listelem_{index}";
        string popupId = $"##ListElemPicker_{index}";

        if (ImGui.BeginTable("##le_" + index, 2, ImGuiTableFlags.None))
        {
            float totalW = ImGui.GetContentRegionAvail().X;
            ImGui.TableSetupColumn("lbl", ImGuiTableColumnFlags.WidthFixed, totalW * LabelRatio);
            ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthFixed, totalW * (1 - LabelRatio));
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(elemLabel);

            ImGui.TableSetColumnIndex(1);

            float availW = ImGui.GetContentRegionAvail().X;
            float clearBtnW = 15 * Game.DpiScale;
            float refBtnW = availW - clearBtnW - ImGui.GetStyle().ItemSpacing.X - 10;
            if (refBtnW < 30 * Game.DpiScale) refBtnW = 30 * Game.DpiScale;

            string displayName = current != null ? $"{current.Name} ({elementType.Name})" : $"None ({elementType.Name})";

            ImGui.PushStyleColor(ImGuiCol.Button, current != null
                ? new Vector4(0.22f, 0.30f, 0.22f, 1f)
                : new Vector4(0.20f, 0.20f, 0.20f, 1f));
            ImGui.Button(displayName, new Vector2(refBtnW, 0));
            ImGui.PopStyleColor();

            // Drag-drop target
            if (ImGui.IsItemHovered() && EditorDragDrop.IsDragging)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.28f, 0.56f, 1.0f, 0.40f));
                ImGui.Button(displayName, new Vector2(refBtnW, 0));
                ImGui.PopStyleColor();

                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    if (EditorDragDrop.PayloadType == "AssetEntry" && EditorDragDrop.Payload is AssetEntry entry)
                    {
                        EngineObject? loaded = TryLoadAssetForField(entry, elementType);
                        if (loaded != null)
                        { items[index] = loaded; changed = true; }
                        EditorDragDrop.Clear();
                    }
                }
            }

            // ImGui native drag-drop target
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

                            var fakeEntry = new AssetEntry
                            {
                                Name = Path.GetFileName(absPath),
                                FullPath = absPath,
                                RelativePath = relativePath,
                                Extension = Path.GetExtension(absPath),
                            };

                            EngineObject? loaded = TryLoadAssetForField(fakeEntry, elementType);
                            if (loaded != null)
                            { items[index] = loaded; changed = true; }
                        }
                    }
                }
                ImGui.EndDragDropTarget();
            }

            // Context menu: clear
            if (ImGui.BeginPopupContextItem("##elemctx"))
            {
                if (ImGui.MenuItem("Clear"))
                { items[index] = null; changed = true; }
                ImGui.EndPopup();
            }

            // Clear button
            ImGui.SameLine();
            if (EditorIcons.ImageButton("##ElemClear" + index, EditorIconType.Close, new Vector2(clearBtnW, clearBtnW)))
            { items[index] = null; changed = true; }

            ImGui.EndTable();
        }
    }

    /// <summary>
    /// Creates a default instance for an element type.
    /// </summary>
    private static object? CreateDefault(Type elementType)
    {
        if (elementType == typeof(string)) return string.Empty;
        if (elementType.IsValueType) return Activator.CreateInstance(elementType);
        return null; // reference types default to null
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

            // Show AssetID and AssetPath in a tooltip when in debug mode
            if (_debugMode && ImGui.IsItemHovered() && current != null)
            {
                ImGui.BeginTooltip();
                ImGui.Text($"AssetID: {(current.AssetID != Guid.Empty ? current.AssetID.ToString("N") : "(none)")}");
                ImGui.Text($"AssetPath: {(!string.IsNullOrEmpty(current.AssetPath) ? current.AssetPath : "(none)")}");
                ImGui.EndTooltip();
            }

            // Single-click on reference button → open asset picker
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !EditorDragDrop.IsDragging)
            {
                _activePickerFieldId = fieldId;
                _assetPickerFilter = string.Empty;
                ImGui.OpenPopup(popupId);
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

            // Context menu: ping in project, clear reference
            if (ImGui.BeginPopupContextItem("##refctx"))
            {
                if (current != null && ImGui.MenuItem("Ping in Project"))
                    PingReferencedAsset(current);
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

                    // Sub-assets from model files (meshes and materials embedded in .fbx/.gltf/etc.)
                    DrawModelSubAssetPickerItems(assetDb, field, target, current);
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
    // Model sub-asset picker (meshes & materials from model files)
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Cached sub-asset metadata per model file path, shared across picker invocations.
    /// Cleared whenever the asset service refreshes.
    /// </summary>
    private readonly Dictionary<string, List<PickerSubAsset>?> _pickerSubAssetCache = new();

    private sealed record PickerSubAsset(string Name, string Category, int Index);

    /// <summary>
    /// For Mesh and Material fields, lists sub-assets embedded in model files
    /// (e.g. meshes and materials inside .fbx/.gltf files).
    /// </summary>
    private void DrawModelSubAssetPickerItems(IAssetService assets, FieldInfo field, object target, EngineObject? current)
    {
        bool wantsMesh = field.FieldType == typeof(Prowl.Runtime.Resources.Mesh) ||
                         field.FieldType.IsSubclassOf(typeof(Prowl.Runtime.Resources.Mesh));
        bool wantsMaterial = field.FieldType == typeof(Prowl.Runtime.Resources.Material) ||
                             field.FieldType.IsSubclassOf(typeof(Prowl.Runtime.Resources.Material));

        if (!wantsMesh && !wantsMaterial)
            return;

        var allEntries = assets.GetAllEntriesRecursive();
        bool headerDrawn = false;

        foreach (AssetEntry entry in allEntries)
        {
            if (entry.IsDirectory) continue;
            if (!MeshExtensions.Contains(entry.Extension)) continue;

            List<PickerSubAsset>? subs = GetOrLoadPickerSubAssets(entry, assets);
            if (subs == null) continue;

            foreach (PickerSubAsset sub in subs)
            {
                if (wantsMesh && sub.Category != "Mesh") continue;
                if (wantsMaterial && sub.Category != "Material") continue;

                string displayName = $"{entry.Name} \u25B8 {sub.Name}";
                if (!string.IsNullOrEmpty(_assetPickerFilter) &&
                    !displayName.Contains(_assetPickerFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!headerDrawn)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "Model Sub-Assets:");
                    headerDrawn = true;
                }

                if (ImGui.Selectable(displayName))
                {
                    EngineObject? loaded = LoadModelSubAsset(entry, sub, assets);
                    if (loaded != null)
                        SetFieldWithUndo(target, field, current, loaded);
                    _activePickerFieldId = null;
                    ImGui.CloseCurrentPopup();
                }
            }
        }
    }

    private List<PickerSubAsset>? GetOrLoadPickerSubAssets(AssetEntry entry, IAssetService assets)
    {
        if (_pickerSubAssetCache.TryGetValue(entry.RelativePath, out List<PickerSubAsset>? cached))
            return cached;

        try
        {
            string? guid = assets.GetGuidByPath(entry.RelativePath);
            if (guid == null || !Guid.TryParse(guid, out Guid parentGuid))
            {
                _pickerSubAssetCache[entry.RelativePath] = null;
                return null;
            }

            EngineObject? parentObj = AssetDatabase.Get(parentGuid);
            if (parentObj is not Prowl.Runtime.Resources.Model model)
            {
                _pickerSubAssetCache[entry.RelativePath] = null;
                return null;
            }

            List<PickerSubAsset> subAssets = [];

            for (int i = 0; i < model.Meshes.Count; i++)
                subAssets.Add(new PickerSubAsset(model.Meshes[i].Name ?? $"Mesh_{i}", "Mesh", i));

            for (int i = 0; i < model.Materials.Count; i++)
                subAssets.Add(new PickerSubAsset(model.Materials[i]?.Name ?? $"Material_{i}", "Material", i));

            cached = subAssets.Count > 0 ? subAssets : null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Inspector] Failed to read model sub-assets for '{entry.Name}': {ex.Message}");
            cached = null;
        }

        _pickerSubAssetCache[entry.RelativePath] = cached;
        return cached;
    }

    private static EngineObject? LoadModelSubAsset(AssetEntry parentEntry, PickerSubAsset sub, IAssetService assets)
    {
        string? guid = assets.GetGuidByPath(parentEntry.RelativePath);
        if (guid == null || !Guid.TryParse(guid, out Guid parentGuid))
            return null;

        EngineObject? parentObj = AssetDatabase.Get(parentGuid);
        if (parentObj is not Prowl.Runtime.Resources.Model model)
            return null;

        model.StampSubResourceIds();

        return sub.Category switch
        {
            "Mesh" when sub.Index >= 0 && sub.Index < model.Meshes.Count => model.Meshes[sub.Index].Mesh,
            "Material" when sub.Index >= 0 && sub.Index < model.Materials.Count => model.Materials[sub.Index],
            _ => null
        };
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
    /// After loading, stamps <see cref="EngineObject.AssetID"/> and
    /// <see cref="EngineObject.AssetPath"/> from the .meta system so that
    /// serialization emits a compact <c>$assetId</c> reference instead of an
    /// inline copy.
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
                    EngineObject? tex = Importing.TextureImporter.Import(path);
                    if (tex != null)
                        StampAssetId(tex, entry);
                    return tex;
                }
            }

            // Mesh field: load via ModelImporter, return the first mesh
            if (fieldType == typeof(Prowl.Runtime.Resources.Mesh))
            {
                if (MeshExtensions.Contains(ext) && File.Exists(path))
                {
                    var model = Prowl.Runtime.Resources.Model.LoadFromFile(path);
                    StampAssetId(model, entry);
                    model.StampSubResourceIds();
                    if (model.Meshes.Count > 0)
                    {
                        var mesh = model.Meshes[0].Mesh;
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
                    var model = Prowl.Runtime.Resources.Model.LoadFromFile(path);
                    StampAssetId(model, entry);
                    model.StampSubResourceIds();
                    return model;
                }
            }

            // Material field: load from .mat JSON file
            if (fieldType == typeof(Prowl.Runtime.Resources.Material))
            {
                if (ext == ".mat" && File.Exists(path))
                {
                    var mat = MaterialSerializer.Load(path);
                    if (mat != null)
                    {
                        StampAssetId(mat, entry);
                        return mat;
                    }
                }
            }

            // Shader field: load from .shader file
            if (fieldType == typeof(Prowl.Runtime.Resources.Shader))
            {
                if (ext == ".shader" && File.Exists(path))
                {
                    var shader = Prowl.Runtime.Resources.Shader.LoadFromFile(path);
                    if (shader != null)
                        StampAssetId(shader, entry);
                    return shader;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Inspector] Failed to load asset: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Resolves the GUID for the given asset entry from the .meta system and
    /// stamps <see cref="EngineObject.AssetID"/> and
    /// <see cref="EngineObject.AssetPath"/> on the loaded object.
    /// </summary>
    private static void StampAssetId(EngineObject obj, AssetEntry entry)
    {
        if (!EditorServices.TryGet<IAssetService>(out var assetSvc) || !assetSvc!.HasProject)
            return;

        string relativePath = entry.RelativePath;
        if (string.IsNullOrEmpty(relativePath))
        {
            // Derive a relative path from the absolute path if not provided
            string absPath = entry.FullPath;
            if (!string.IsNullOrEmpty(absPath) && !string.IsNullOrEmpty(assetSvc.AssetRootPath))
            {
                try
                {
                    relativePath = Path.GetRelativePath(assetSvc.AssetRootPath, absPath).Replace('\\', '/');
                }
                catch { /* not under asset root */ }
            }
        }

        if (string.IsNullOrEmpty(relativePath))
            return;

        string? guidStr = assetSvc.GetGuidByPath(relativePath);
        if (guidStr != null && Guid.TryParse(guidStr, out Guid assetId))
        {
            obj.AssetID = assetId;
            obj.AssetPath = relativePath;
        }
        else
        {
            // No .meta yet — still set the path so ping/navigation works
            obj.AssetPath = relativePath;
        }
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

        if (!EditorServices.TryGet<IAssetService>(out var assets) || !assets!.HasProject)
            return;

        string? assetRelPath = null;

        // AssetPath is a public field on EngineObject — use it directly.
        // It may be a relative path or an absolute path depending on how the
        // object was loaded.
        string storedPath = obj.AssetPath;
        if (!string.IsNullOrEmpty(storedPath))
        {
            // Strip any sub-resource fragment (e.g. "Models/cube.obj#Mesh:0" → "Models/cube.obj")
            int hashIdx = storedPath.IndexOf('#');
            string basePath = hashIdx >= 0 ? storedPath[..hashIdx] : storedPath;

            if (Path.IsPathRooted(basePath) &&
                basePath.StartsWith(assets.AssetRootPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    assetRelPath = Path.GetRelativePath(assets.AssetRootPath, basePath).Replace('\\', '/');
                }
                catch { /* not a project path */ }
            }
            else if (!Path.IsPathRooted(basePath))
            {
                // Already a relative path
                assetRelPath = basePath.Replace('\\', '/');
            }
        }

        // If AssetID is set, try resolving the path through the meta manager
        if (assetRelPath == null && obj.AssetID != Guid.Empty)
        {
            string? resolvedPath = assets.GetAssetPathByGuid(obj.AssetID.ToString("N"));
            if (resolvedPath != null)
                assetRelPath = resolvedPath;
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
            case ".obj" or ".fbx" or ".gltf" or ".glb" or ".dae" or ".blend" or ".3ds" or ".ply" or ".stl":
                DrawModelAssetInfo(asset);
                break;
            case ".ttf" or ".otf":
                DrawFontAssetInfo(asset);
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

    // ── Font import settings editing state ────────────────────
    private static Importing.FontImportSettings? _fontImportSettings;
    private static string? _fontImportSettingsPath;
    private static Runtime.Resources.FontAsset? _cachedFontAsset;
    private static string? _cachedFontAssetPath;
    private static string? _cachedFontAssetError;
    private static string _fontCustomCharsBuffer = string.Empty;
    private static string _kernLeftBuffer = string.Empty;
    private static string _kernRightBuffer = string.Empty;
    private static float _kernNewValue = 0f;
    private static Dictionary<uint, uint>? _glyphIndexToCodepoint;

    // ── TextRenderer inspector state ──────────────────────────
    private static string _textRendererTextBuffer = string.Empty;
    private static int _textRendererTextOwnerId;

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

    // ────────────────────────────────────────────────────────────
    // TextRenderer custom inspector
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws additional inspector UI for <see cref="TextRenderer"/> components:
    /// a multiline text editor, layout info, and quick style controls.
    /// </summary>
    private static void DrawTextRendererInspector(TextRenderer textRenderer)
    {
        ImGui.Spacing();

        // ── Multiline text editor ──────────────────────────────
        if (ImGui.TreeNodeEx("Text Editor", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // Keep the buffer in sync with the component
            if (_textRendererTextOwnerId != textRenderer.InstanceID)
            {
                _textRendererTextBuffer = textRenderer.Text ?? string.Empty;
                _textRendererTextOwnerId = textRenderer.InstanceID;
            }

            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f),
                "Multiline text input (rich text tags supported):");

            float height = Math.Max(80f * Game.DpiScale, ImGui.GetTextLineHeight() * 5);
            if (ImGui.InputTextMultiline("##TextEditorInput", ref _textRendererTextBuffer, 16384,
                new Vector2(-1, height)))
            {
                textRenderer.Text = _textRendererTextBuffer;
            }

            // Also apply on deactivation
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                textRenderer.Text = _textRendererTextBuffer;
            }

            ImGui.TreePop();
        }

        // ── Layout Info ────────────────────────────────────────
        TextLayout layout = textRenderer.GetTextLayout();
        if (layout.Glyphs != null && layout.Glyphs.Length > 0)
        {
            if (ImGui.TreeNodeEx("Layout Info", ImGuiTreeNodeFlags.None))
            {
                DrawAssetFieldRow("Characters", layout.Glyphs.Length.ToString());
                DrawAssetFieldRow("Lines", (layout.Lines?.Length ?? 0).ToString());
                DrawAssetFieldRow("Text Bounds",
                    $"{layout.TextBounds.X:F1} x {layout.TextBounds.Y:F1}");
                DrawAssetFieldRow("Preferred Size",
                    $"{layout.PreferredSize.X:F1} x {layout.PreferredSize.Y:F1}");

                // Per-line info
                if (layout.Lines != null && layout.Lines.Length > 0 &&
                    ImGui.TreeNodeEx("Lines", ImGuiTreeNodeFlags.None))
                {
                    for (int i = 0; i < layout.Lines.Length; i++)
                    {
                        LineInfo line = layout.Lines[i];
                        ImGui.BulletText(
                            $"Line {i}: {line.GlyphCount} glyphs, W:{line.Width:F1}");
                    }
                    ImGui.TreePop();
                }

                ImGui.TreePop();
            }
        }

        // ── Mesh Info ──────────────────────────────────────────
        Prowl.Runtime.Resources.Mesh? mesh = textRenderer.GetMesh();
        if (mesh != null && mesh.IsValid())
        {
            if (ImGui.TreeNodeEx("Mesh Info", ImGuiTreeNodeFlags.None))
            {
                DrawAssetFieldRow("Vertices", mesh.VertexCount.ToString());
                DrawAssetFieldRow("Indices", mesh.IndexCount.ToString());
                DrawAssetFieldRow("Sub-Meshes", mesh.SubMeshCount.ToString());
                ImGui.TreePop();
            }
        }

        // ── Quick Actions ──────────────────────────────────────
        ImGui.Spacing();
        float avail = ImGui.GetContentRegionAvail().X;
        float btnW = 120 * Game.DpiScale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - btnW) * 0.5f);
        if (ImGui.Button("Force Rebuild", new Vector2(btnW, 0)))
        {
            textRenderer.ForceMeshUpdate();
        }
    }

    private static void DrawFontAssetInfo(AssetEntry asset)
    {
        if (!ImGui.CollapsingHeader("Font", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!File.Exists(asset.FullPath))
        {
            ImGui.TextDisabled("(file not found)");
            return;
        }

        // Show basic file info
        DrawAssetFieldRow("Type", asset.Extension.ToUpperInvariant().TrimStart('.') + " Font");

        // Load the font asset once and cache it
        if (_cachedFontAsset == null || _cachedFontAssetPath != asset.FullPath)
        {
            _cachedFontAsset = null;
            _cachedFontAssetError = null;
            _cachedFontAssetPath = asset.FullPath;
            _glyphIndexToCodepoint = null;
            try
            {
                _cachedFontAsset = Importing.FontAssetImporter.Import(asset.FullPath);
            }
            catch (Exception ex)
            {
                _cachedFontAssetError = ex.Message;
            }
        }

        if (_cachedFontAssetError != null)
        {
            ImGui.TextDisabled($"(error: {_cachedFontAssetError})");
        }
        else if (_cachedFontAsset != null)
        {
            DrawAssetFieldRow("Glyphs", _cachedFontAsset.GlyphTable.Count.ToString());
            DrawAssetFieldRow("Atlas Type", _cachedFontAsset.AtlasType.ToString());
            DrawAssetFieldRow("Atlas Size", $"{_cachedFontAsset.AtlasWidth} x {_cachedFontAsset.AtlasHeight}");
            DrawAssetFieldRow("Point Size", _cachedFontAsset.PointSize.ToString("F0"));
            DrawAssetFieldRow("Line Height", _cachedFontAsset.LineHeight.ToString("F1"));
            DrawAssetFieldRow("Ascender", _cachedFontAsset.Ascender.ToString("F1"));
            DrawAssetFieldRow("Descender", _cachedFontAsset.Descender.ToString("F1"));
            DrawAssetFieldRow("Px Range", _cachedFontAsset.AtlasPxRange.ToString("F1"));

            // Glyph list
            if (_cachedFontAsset.GlyphTable.Count > 0 && ImGui.TreeNodeEx("Glyph Table", ImGuiTreeNodeFlags.None))
            {
                int shown = 0;
                foreach (var glyph in _cachedFontAsset.GlyphTable)
                {
                    if (shown >= 200)
                    {
                        ImGui.TextDisabled($"... ({_cachedFontAsset.GlyphTable.Count - shown} more)");
                        break;
                    }
                    string ch = glyph.GlyphIndex < 0x10000 ? $"U+{glyph.GlyphIndex:X4}" : $"U+{glyph.GlyphIndex:X6}";
                    string charStr = char.IsControl((char)glyph.GlyphIndex) ? "" : $" '{(char)glyph.GlyphIndex}'";
                    ImGui.BulletText($"{ch}{charStr}  W:{glyph.Width:F0} H:{glyph.Height:F0} Adv:{glyph.Advance:F1}");
                    shown++;
                }
                ImGui.TreePop();
            }

            // Kerning pairs
            int kerningCount = _cachedFontAsset.KerningPairs.Count;
            DrawAssetFieldRow("Kerning Pairs", kerningCount.ToString());

            if (kerningCount > 0 && ImGui.TreeNodeEx("Kerning Pair Table", ImGuiTreeNodeFlags.None))
            {
                // Build reverse lookup: FreeType glyph index → first matching codepoint
                if (_glyphIndexToCodepoint == null)
                {
                    _glyphIndexToCodepoint = [];
                    foreach (var entry in _cachedFontAsset.CharacterTable)
                    {
                        int tableIdx = entry.Value;
                        if (tableIdx >= 0 && tableIdx < _cachedFontAsset.GlyphTable.Count)
                        {
                            uint ftIdx = _cachedFontAsset.GlyphTable[tableIdx].GlyphIndex;
                            _glyphIndexToCodepoint.TryAdd(ftIdx, entry.Key);
                        }
                    }
                }

                ulong? pairToRemove = null;
                int kernShown = 0;
                foreach (var pair in _cachedFontAsset.KerningPairs)
                {
                    if (kernShown >= 200)
                    {
                        ImGui.TextDisabled($"... ({kerningCount - kernShown} more)");
                        break;
                    }

                    uint leftIdx = (uint)(pair.Key >> 32);
                    uint rightIdx = (uint)(pair.Key & 0xFFFFFFFF);

                    string leftLabel = FormatGlyphLabel(leftIdx, _glyphIndexToCodepoint);
                    string rightLabel = FormatGlyphLabel(rightIdx, _glyphIndexToCodepoint);

                    ImGui.PushID(kernShown);

                    float value = pair.Value;
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text($"{leftLabel} \u2194 {rightLabel}");
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X * 0.55f);
                    ImGui.SetNextItemWidth(80f);
                    if (ImGui.DragFloat("##kv", ref value, 0.01f, -100f, 100f, "%.2f"))
                    {
                        _cachedFontAsset.SetKerningPair(leftIdx, rightIdx, value);
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("X"))
                    {
                        pairToRemove = pair.Key;
                    }

                    ImGui.PopID();
                    kernShown++;
                }

                if (pairToRemove.HasValue)
                {
                    uint removeLeft = (uint)(pairToRemove.Value >> 32);
                    uint removeRight = (uint)(pairToRemove.Value & 0xFFFFFFFF);
                    _cachedFontAsset.RemoveKerningPair(removeLeft, removeRight);
                }

                // Add new kerning pair
                ImGui.Separator();
                ImGui.TextDisabled("Add Kerning Pair (chars):");
                ImGui.SetNextItemWidth(40f);
                ImGui.InputText("##kernL", ref _kernLeftBuffer, 4);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(40f);
                ImGui.InputText("##kernR", ref _kernRightBuffer, 4);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80f);
                ImGui.DragFloat("##kernNV", ref _kernNewValue, 0.01f, -100f, 100f, "%.2f");
                ImGui.SameLine();
                if (ImGui.SmallButton("+") && _kernLeftBuffer.Length > 0 && _kernRightBuffer.Length > 0)
                {
                    uint leftCp = (uint)char.ConvertToUtf32(_kernLeftBuffer, 0);
                    uint rightCp = (uint)char.ConvertToUtf32(_kernRightBuffer, 0);
                    // Find the FreeType glyph indices for these codepoints
                    uint? leftFt = FindGlyphIndexForCodepoint(_cachedFontAsset, leftCp);
                    uint? rightFt = FindGlyphIndexForCodepoint(_cachedFontAsset, rightCp);
                    if (leftFt.HasValue && rightFt.HasValue)
                    {
                        _cachedFontAsset.SetKerningPair(leftFt.Value, rightFt.Value, _kernNewValue);
                        _kernLeftBuffer = string.Empty;
                        _kernRightBuffer = string.Empty;
                        _kernNewValue = 0f;
                    }
                    else
                    {
                        Runtime.Debug.LogWarning("[Inspector] One or both characters not found in the font's glyph table.");
                    }
                }

                ImGui.TreePop();
            }
        }
        else
        {
            ImGui.TextDisabled("(failed to load font)");
        }

        // ── Import Settings ──
        if (!ImGui.CollapsingHeader("Import Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // Load settings lazily or when the asset path changes
        if (_fontImportSettings == null || _fontImportSettingsPath != asset.FullPath)
        {
            _fontImportSettings = Importing.FontAssetImporter.LoadSettings(asset.FullPath);
            _fontImportSettingsPath = asset.FullPath;
            _fontCustomCharsBuffer = _fontImportSettings.CustomCharacters;
        }

        bool changed = false;

        // Atlas Type
        DrawFieldRow("Atlas Type", () =>
        {
            string[] atlasTypes = ["SDF", "MSDF", "Bitmap"];
            int atlasIdx = (int)_fontImportSettings.AtlasType;
            if (ImGui.Combo("##fontAtlasType", ref atlasIdx, atlasTypes, atlasTypes.Length))
            {
                _fontImportSettings.AtlasType = (Runtime.Text.AtlasType)atlasIdx;
                changed = true;
            }
        });

        // Point Size
        DrawFieldRow("Point Size", () =>
        {
            int pointSize = _fontImportSettings.PointSize;
            if (ImGui.DragInt("##fontPointSize", ref pointSize, 1f, 8, 200))
            {
                _fontImportSettings.PointSize = pointSize;
                changed = true;
            }
        });

        // Atlas Resolution
        DrawFieldRow("Atlas Resolution", () =>
        {
            string[] resOptions = ["Auto", "256", "512", "1024", "2048", "4096"];
            int[] resValues = [0, 256, 512, 1024, 2048, 4096];
            int currentIdx = Array.IndexOf(resValues, _fontImportSettings.AtlasResolution);
            if (currentIdx < 0) currentIdx = 0;
            if (ImGui.Combo("##fontAtlasRes", ref currentIdx, resOptions, resOptions.Length))
            {
                _fontImportSettings.AtlasResolution = resValues[currentIdx];
                changed = true;
            }
        });

        // SDF Pixel Range
        DrawFieldRow("Px Range", () =>
        {
            float pxRange = _fontImportSettings.PxRange;
            if (ImGui.DragFloat("##fontPxRange", ref pxRange, 0.5f, 1f, 32f, "%.1f"))
            {
                _fontImportSettings.PxRange = pxRange;
                changed = true;
            }
        });

        // Padding
        DrawFieldRow("Padding", () =>
        {
            int padding = _fontImportSettings.Padding;
            if (ImGui.DragInt("##fontPadding", ref padding, 1f, 0, 16))
            {
                _fontImportSettings.Padding = padding;
                changed = true;
            }
        });

        // Character Set
        DrawFieldRow("Character Set", () =>
        {
            string[] presets = ["ASCII", "LatinExtended", "Custom"];
            int presetIdx = _fontImportSettings.CharacterSet switch
            {
                "LatinExtended" => 1,
                "Custom" => 2,
                _ => 0,
            };
            if (ImGui.Combo("##fontCharSet", ref presetIdx, presets, presets.Length))
            {
                _fontImportSettings.CharacterSet = presets[presetIdx];
                changed = true;
            }
        });

        // Custom Characters (only shown when Character Set is "Custom")
        if (_fontImportSettings.CharacterSet == "Custom")
        {
            DrawFieldRow("Characters", () =>
            {
                if (ImGui.InputText("##fontCustomChars", ref _fontCustomCharsBuffer, 4096))
                {
                    _fontImportSettings.CustomCharacters = _fontCustomCharsBuffer;
                    changed = true;
                }
            });
        }

        // SDF Oversample (only shown for SDF type)
        if (_fontImportSettings.AtlasType == Runtime.Text.AtlasType.SDF)
        {
            DrawFieldRow("SDF Oversample", () =>
            {
                int oversample = _fontImportSettings.SdfOversample;
                if (ImGui.DragInt("##fontOversample", ref oversample, 1f, 1, 8))
                {
                    _fontImportSettings.SdfOversample = oversample;
                    changed = true;
                }
            });
        }

        // Generate Mipmaps
        DrawFieldRow("Mipmaps", () =>
        {
            bool mipmaps = _fontImportSettings.GenerateMipmaps;
            if (ImGui.Checkbox("##fontMipmaps", ref mipmaps))
            {
                _fontImportSettings.GenerateMipmaps = mipmaps;
                changed = true;
            }
            ImGui.SameLine();
            ImGui.TextDisabled(_fontImportSettings.AtlasType == Runtime.Text.AtlasType.Bitmap
                ? "(recommended for Bitmap)"
                : "(not recommended for SDF/MSDF)");
        });

        // Character count preview
        {
            int codepointCount = _fontImportSettings.GetCodepoints().Count;
            ImGui.Spacing();
            ImGui.TextDisabled($"Character set contains {codepointCount} codepoints");
        }

        // Apply / Revert buttons
        ImGui.Spacing();
        if (ImGui.Button("Apply & Reimport"))
        {
            Importing.FontAssetImporter.SaveSettings(asset.FullPath, _fontImportSettings!);
            // Invalidate cached font so it reloads with new settings
            _cachedFontAsset?.AtlasTexture?.Dispose();
            _cachedFontAsset?.Dispose();
            _cachedFontAsset = null;
            _cachedFontAssetPath = null;
            _glyphIndexToCodepoint = null;
            Runtime.Debug.Log($"[Inspector] Saved font import settings for: {asset.Name}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Revert"))
        {
            _fontImportSettings = Importing.FontAssetImporter.LoadSettings(asset.FullPath);
            _fontCustomCharsBuffer = _fontImportSettings.CustomCharacters;
        }
    }

    /// <summary>
    /// Formats a FreeType glyph index as a human-readable label (e.g. "'A' (65)" or "GID 42").
    /// </summary>
    private static string FormatGlyphLabel(uint ftGlyphIndex, Dictionary<uint, uint>? glyphIndexToCodepoint)
    {
        if (glyphIndexToCodepoint != null && glyphIndexToCodepoint.TryGetValue(ftGlyphIndex, out uint cp))
        {
            if (cp < 0x10000 && !char.IsControl((char)cp))
                return $"'{(char)cp}' ({cp})";
            return $"U+{cp:X4}";
        }
        return $"GID {ftGlyphIndex}";
    }

    /// <summary>
    /// Finds the FreeType glyph index for a Unicode codepoint in a FontAsset.
    /// </summary>
    private static uint? FindGlyphIndexForCodepoint(Runtime.Resources.FontAsset font, uint codepoint)
    {
        if (font.CharacterTable.TryGetValue(codepoint, out int tableIdx)
            && tableIdx >= 0 && tableIdx < font.GlyphTable.Count)
        {
            return font.GlyphTable[tableIdx].GlyphIndex;
        }
        return null;
    }

    // ── Model inspector cached state ─────────────────────────
    private static Prowl.Runtime.Resources.Model? _cachedModel;
    private static string? _cachedModelPath;
    private static string? _cachedModelError;
    private static Prowl.Runtime.AssetImporting.ModelImporterSettings? _modelImportSettings;
    private static string? _modelImportSettingsPath;

    private static void DrawModelAssetInfo(AssetEntry asset)
    {
        if (!ImGui.CollapsingHeader("Model", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (!File.Exists(asset.FullPath))
        {
            ImGui.TextDisabled("(file not found)");
            return;
        }

        // Load the model once and cache it to avoid reloading every frame
        if (_cachedModel == null || _cachedModelPath != asset.FullPath)
        {
            _cachedModel = null;
            _cachedModelError = null;
            _cachedModelPath = asset.FullPath;
            try
            {
                _cachedModel = Prowl.Runtime.Resources.Model.LoadFromFile(asset.FullPath);
            }
            catch (Exception ex)
            {
                _cachedModelError = ex.Message;
            }
        }

        if (_cachedModelError != null)
        {
            ImGui.TextDisabled($"(error: {_cachedModelError})");
        }
        else if (_cachedModel != null)
        {
            DrawAssetFieldRow("Meshes", _cachedModel.Meshes.Count.ToString());
            DrawAssetFieldRow("Materials", _cachedModel.Materials.Count.ToString());
            DrawAssetFieldRow("Animations", _cachedModel.Animations.Count.ToString());
            if (_cachedModel.Cameras.Count > 0)
                DrawAssetFieldRow("Cameras", _cachedModel.Cameras.Count.ToString());
            if (_cachedModel.Lights.Count > 0)
                DrawAssetFieldRow("Lights", _cachedModel.Lights.Count.ToString());
            if (_cachedModel.RootNode != null)
                DrawAssetFieldRow("Root Node", _cachedModel.RootNode.Name ?? "(unnamed)");

            // List mesh names
            if (_cachedModel.Meshes.Count > 0 && ImGui.TreeNodeEx("Mesh List", ImGuiTreeNodeFlags.None))
            {
                for (int i = 0; i < _cachedModel.Meshes.Count; i++)
                {
                    var m = _cachedModel.Meshes[i];
                    string meshName = m.Mesh?.Name ?? $"Mesh {i}";
                    int vertCount = m.Mesh?.VertexCount ?? 0;
                    int idxCount = m.Mesh?.IndexCount ?? 0;
                    ImGui.BulletText($"{meshName}  (V:{vertCount}  I:{idxCount})");
                }
                ImGui.TreePop();
            }
        }
        else
        {
            ImGui.TextDisabled("(failed to load model)");
        }

        // ── Import Settings ──
        if (!ImGui.CollapsingHeader("Import Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // Load settings lazily or when the asset path changes
        if (_modelImportSettings == null || _modelImportSettingsPath != asset.FullPath)
        {
            _modelImportSettings = LoadModelImportSettings(asset.FullPath);
            _modelImportSettingsPath = asset.FullPath;
        }

        var s = _modelImportSettings.Value;
        bool changed = false;

        // Scale
        DrawFieldRow("Unit Scale", () =>
        {
            float scale = s.UnitScale;
            if (ImGui.DragFloat("##unitScale", ref scale, 0.01f, 0.001f, 1000f, "%.3f"))
            { s.UnitScale = scale; changed = true; }
        });

        // Index Format
        DrawFieldRow("Index Format", () =>
        {
            string[] fmtNames = ["UInt16", "UInt32"];
            int fmtIdx = (int)s.IndexFormat;
            if (ImGui.Combo("##idxFmt", ref fmtIdx, fmtNames, fmtNames.Length))
            { s.IndexFormat = (Prowl.Runtime.Resources.IndexFormat)fmtIdx; changed = true; }
        });

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "Normals & Tangents");

        DrawFieldRow("Generate Normals", () =>
        {
            bool v = s.GenerateNormals;
            if (ImGui.Checkbox("##genNormals", ref v))
            { s.GenerateNormals = v; changed = true; }
        });

        DrawFieldRow("Smooth Normals", () =>
        {
            bool v = s.GenerateSmoothNormals;
            if (ImGui.Checkbox("##smoothNormals", ref v))
            { s.GenerateSmoothNormals = v; changed = true; }
        });

        DrawFieldRow("Calc Tangent Space", () =>
        {
            bool v = s.CalculateTangentSpace;
            if (ImGui.Checkbox("##calcTangent", ref v))
            { s.CalculateTangentSpace = v; changed = true; }
        });

        DrawFieldRow("Invert Normals", () =>
        {
            bool v = s.InvertNormals;
            if (ImGui.Checkbox("##invertNormals", ref v))
            { s.InvertNormals = v; changed = true; }
        });

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "Coordinate System");

        DrawFieldRow("Make Left Handed", () =>
        {
            bool v = s.MakeLeftHanded;
            if (ImGui.Checkbox("##makeLeftHanded", ref v))
            { s.MakeLeftHanded = v; changed = true; }
        });

        DrawFieldRow("Flip UVs", () =>
        {
            bool v = s.FlipUVs;
            if (ImGui.Checkbox("##flipUVs", ref v))
            { s.FlipUVs = v; changed = true; }
        });

        DrawFieldRow("Flip Winding Order", () =>
        {
            bool v = s.FlipWindingOrder;
            if (ImGui.Checkbox("##flipWinding", ref v))
            { s.FlipWindingOrder = v; changed = true; }
        });

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "Optimization");

        DrawFieldRow("Optimize Graph", () =>
        {
            bool v = s.OptimizeGraph;
            if (ImGui.Checkbox("##optGraph", ref v))
            { s.OptimizeGraph = v; changed = true; }
        });

        DrawFieldRow("Optimize Meshes", () =>
        {
            bool v = s.OptimizeMeshes;
            if (ImGui.Checkbox("##optMeshes", ref v))
            { s.OptimizeMeshes = v; changed = true; }
        });

        DrawFieldRow("Weld Vertices", () =>
        {
            bool v = s.WeldVertices;
            if (ImGui.Checkbox("##weldVerts", ref v))
            { s.WeldVertices = v; changed = true; }
        });

        DrawFieldRow("Global Scale", () =>
        {
            bool v = s.GlobalScale;
            if (ImGui.Checkbox("##globalScale", ref v))
            { s.GlobalScale = v; changed = true; }
        });

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "Scene Content");

        DrawFieldRow("Import Cameras", () =>
        {
            bool v = s.ImportCameras;
            if (ImGui.Checkbox("##importCameras", ref v))
            { s.ImportCameras = v; changed = true; }
        });

        DrawFieldRow("Import Lights", () =>
        {
            bool v = s.ImportLights;
            if (ImGui.Checkbox("##importLights", ref v))
            { s.ImportLights = v; changed = true; }
        });

        if (changed)
            _modelImportSettings = s;

        // Apply / Revert buttons
        ImGui.Spacing();
        if (ImGui.Button("Apply Import Settings"))
        {
            SaveModelImportSettings(asset.FullPath, _modelImportSettings.Value);
            // Invalidate cached model so it reloads with new settings
            _cachedModel = null;
            _cachedModelPath = null;
            Runtime.Debug.Log($"[Inspector] Saved model import settings for: {asset.Name}");
        }
        ImGui.SameLine();
        if (ImGui.Button("Revert"))
        {
            _modelImportSettings = LoadModelImportSettings(asset.FullPath);
        }
    }

    /// <summary>
    /// Loads model import settings from the .meta file associated with the given path.
    /// </summary>
    private static Prowl.Runtime.AssetImporting.ModelImporterSettings LoadModelImportSettings(string absolutePath)
    {
        string metaPath = MetaFile.GetMetaPath(absolutePath);
        MetaFile? meta = MetaFile.Load(metaPath);
        if (meta != null && meta.ImportSettings.Count > 0)
            return ModelImportSettingsFromMeta(meta.ImportSettings);
        return new Prowl.Runtime.AssetImporting.ModelImporterSettings();
    }

    /// <summary>
    /// Saves model import settings to the .meta file.
    /// </summary>
    private static void SaveModelImportSettings(string absolutePath, Prowl.Runtime.AssetImporting.ModelImporterSettings settings)
    {
        string metaPath = MetaFile.GetMetaPath(absolutePath);
        MetaFile? meta = MetaFile.Load(metaPath);
        if (meta == null)
            meta = MetaFile.CreateNew();

        ModelImportSettingsToMeta(settings, meta.ImportSettings);
        meta.Save(metaPath);
    }

    private static Prowl.Runtime.AssetImporting.ModelImporterSettings ModelImportSettingsFromMeta(Dictionary<string, object?> d)
    {
        var s = new Prowl.Runtime.AssetImporting.ModelImporterSettings();
        if (TryGetMetaBool(d, "generateNormals", out bool gn)) s.GenerateNormals = gn;
        if (TryGetMetaBool(d, "generateSmoothNormals", out bool gsn)) s.GenerateSmoothNormals = gsn;
        if (TryGetMetaBool(d, "calculateTangentSpace", out bool cts)) s.CalculateTangentSpace = cts;
        if (TryGetMetaBool(d, "makeLeftHanded", out bool mlh)) s.MakeLeftHanded = mlh;
        if (TryGetMetaBool(d, "flipUVs", out bool fuv)) s.FlipUVs = fuv;
        if (TryGetMetaBool(d, "optimizeGraph", out bool og)) s.OptimizeGraph = og;
        if (TryGetMetaBool(d, "optimizeMeshes", out bool om)) s.OptimizeMeshes = om;
        if (TryGetMetaBool(d, "flipWindingOrder", out bool fwo)) s.FlipWindingOrder = fwo;
        if (TryGetMetaBool(d, "weldVertices", out bool wv)) s.WeldVertices = wv;
        if (TryGetMetaBool(d, "invertNormals", out bool inv)) s.InvertNormals = inv;
        if (TryGetMetaBool(d, "globalScale", out bool gs)) s.GlobalScale = gs;
        if (TryGetMetaFloat(d, "unitScale", out float us)) s.UnitScale = us;
        if (TryGetMetaInt(d, "indexFormat", out int ifmt)) s.IndexFormat = (Prowl.Runtime.Resources.IndexFormat)ifmt;
        if (TryGetMetaBool(d, "importCameras", out bool ic)) s.ImportCameras = ic;
        if (TryGetMetaBool(d, "importLights", out bool il)) s.ImportLights = il;
        return s;
    }

    private static void ModelImportSettingsToMeta(Prowl.Runtime.AssetImporting.ModelImporterSettings s, Dictionary<string, object?> d)
    {
        d["generateNormals"] = s.GenerateNormals;
        d["generateSmoothNormals"] = s.GenerateSmoothNormals;
        d["calculateTangentSpace"] = s.CalculateTangentSpace;
        d["makeLeftHanded"] = s.MakeLeftHanded;
        d["flipUVs"] = s.FlipUVs;
        d["optimizeGraph"] = s.OptimizeGraph;
        d["optimizeMeshes"] = s.OptimizeMeshes;
        d["flipWindingOrder"] = s.FlipWindingOrder;
        d["weldVertices"] = s.WeldVertices;
        d["invertNormals"] = s.InvertNormals;
        d["globalScale"] = s.GlobalScale;
        d["unitScale"] = s.UnitScale;
        d["indexFormat"] = (int)s.IndexFormat;
        d["importCameras"] = s.ImportCameras;
        d["importLights"] = s.ImportLights;
    }

    private static bool TryGetMetaBool(Dictionary<string, object?> d, string key, out bool result)
    {
        result = false;
        if (!d.TryGetValue(key, out var v) || v == null) return false;
        if (v is bool b) { result = b; return true; }
        if (v is System.Text.Json.JsonElement je)
        {
            if (je.ValueKind == System.Text.Json.JsonValueKind.True) { result = true; return true; }
            if (je.ValueKind == System.Text.Json.JsonValueKind.False) { result = false; return true; }
        }
        return false;
    }

    private static bool TryGetMetaFloat(Dictionary<string, object?> d, string key, out float result)
    {
        result = 0;
        if (!d.TryGetValue(key, out var v) || v == null) return false;
        if (v is float f) { result = f; return true; }
        if (v is double dv) { result = (float)dv; return true; }
        if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Number)
        { result = (float)je.GetDouble(); return true; }
        return false;
    }

    private static bool TryGetMetaInt(Dictionary<string, object?> d, string key, out int result)
    {
        result = 0;
        if (!d.TryGetValue(key, out var v) || v == null) return false;
        if (v is int i) { result = i; return true; }
        if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Number)
        { result = je.GetInt32(); return true; }
        return false;
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
