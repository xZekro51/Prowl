// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.Editor.Docking;
using Prowl.Editor.Icons;
using Prowl.Editor.Services;
using Prowl.Editor.Prefabs;
using Prowl.Editor.Undo;
using Prowl.Editor.Undo.Commands;

namespace Prowl.Editor.Panels;

/// <summary>
/// Displays the scene hierarchy as a tree view using ImGui TreeNodeEx.
/// Supports expand/collapse, single-click selection, context menus,
/// drag-drop reparenting between hierarchy items, and visual icons.
/// </summary>
public sealed class HierarchyPanel : EditorPanel
{
    // Drag-drop payload type for hierarchy reparenting
    private const string HierarchyDragType = "HierarchyGO";

    // The InstanceID of the GO currently being dragged (0 = none)
    private int _draggedInstanceId;

    public HierarchyPanel() : base("Hierarchy") { }

    protected override void DrawContent()
    {
        var sceneService = EditorServices.Get<ISceneService>();
        var selService = EditorServices.Get<ISelectionService>();

        // ── Toolbar ────────────────────────────────────────────
        if (ImGui.Button("+ Create"))
            ImGui.OpenPopup("##HierCreate");

        if (ImGui.BeginPopup("##HierCreate"))
        {
            if (ImGui.MenuItem("Empty GameObject"))
            {
                if (EditorServices.TryGet<UndoRedoService>(out var undo))
                    undo!.Execute(new CreateGameObjectCommand("New GameObject"));
                else
                    sceneService.CreateGameObject("New GameObject");
            }
            if (ImGui.MenuItem("Empty Child (of selection)"))
            {
                CreateChildOfSelection(sceneService, selService);
            }
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.50f, 0.50f, 0.50f, 1f), "|");
        ImGui.SameLine();

        // Scene name label
        string sceneName = sceneService.CurrentScene?.Name ?? "No Scene";
        ImGui.TextColored(new Vector4(0.45f, 0.45f, 0.45f, 0.80f), sceneName);

        ImGui.Separator();

        // ── Scrollable hierarchy tree ──────────────────────────
        ImGui.BeginChild("##HierTree");

        try
        {
            var roots = sceneService.GetRootGameObjects();
            bool any = false;
            foreach (var go in roots)
            {
                any = true;
                DrawGameObject(go, selService, sceneService, 0);
            }

            if (!any)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 0.6f),
                    "Scene is empty. Use \"+ Create\" to add objects.");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Hierarchy] Error displaying scene objects: {ex.Message}");
            Debug.LogException(ex);
        }

        // Drop zone at the bottom — for un-parenting (drop to root) or project drag
        ImGui.Spacing(); ImGui.Spacing();

        // Un-parent drop target: if user drops a GO here it becomes a root
        if (_draggedInstanceId != 0)
        {
            // Highlighted drop zone with border
            var drawList = ImGui.GetWindowDrawList();
            var cursorPos = ImGui.GetCursorScreenPos();
            float w = ImGui.GetContentRegionAvail().X;
            float h = ImGui.GetTextLineHeightWithSpacing() + 6;
            var rectMin = cursorPos;
            var rectMax = new Vector2(cursorPos.X + w, cursorPos.Y + h);
            drawList.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(new Vector4(0.28f, 0.56f, 1.0f, 0.10f)));
            drawList.AddRect(rectMin, rectMax, ImGui.GetColorU32(new Vector4(0.28f, 0.56f, 1.0f, 0.50f)), 3f, ImDrawFlags.None, 1.5f);

            ImGui.TextColored(new Vector4(0.55f, 0.70f, 1.0f, 0.80f), "> Drop here to move to root");
            if (ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                var dragged = FindGameObjectById(_draggedInstanceId, sceneService);
                if (dragged != null && dragged.Parent != null)
                {
                    if (EditorServices.TryGet<UndoRedoService>(out var undo))
                        undo!.Execute(new ReparentGameObjectCommand(dragged, null));
                    else
                        dragged.SetParent(null!);
                }
                _draggedInstanceId = 0;
            }
        }

        // Asset drop zone from the project browser
        bool hasDrag = EditorDragDrop.IsDragging && EditorDragDrop.PayloadType == "AssetEntry";
        if (hasDrag)
        {
            var drawList = ImGui.GetWindowDrawList();
            var cursorPos = ImGui.GetCursorScreenPos();
            float w = ImGui.GetContentRegionAvail().X;
            float h = ImGui.GetTextLineHeightWithSpacing() + 6;
            var rectMin = cursorPos;
            var rectMax = new Vector2(cursorPos.X + w, cursorPos.Y + h);
            drawList.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(new Vector4(0.30f, 0.70f, 0.30f, 0.10f)));
            drawList.AddRect(rectMin, rectMax, ImGui.GetColorU32(new Vector4(0.30f, 0.70f, 0.30f, 0.50f)), 3f, ImDrawFlags.None, 1.5f);

            ImGui.TextColored(new Vector4(0.60f, 0.80f, 0.60f, 1f), "> Drop asset here to instantiate");
            if (ImGui.IsItemHovered())
            {
                if (EditorDragDrop.Payload is AssetEntry dragEntry)
                    ImGui.SetTooltip($"Drop: {dragEntry.Name}");

                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                    AcceptDrop(sceneService);
            }
        }

        // Floating drag tooltip while dragging a hierarchy item
        if (_draggedInstanceId != 0)
        {
            var draggedGo = FindGameObjectById(_draggedInstanceId, sceneService);
            if (draggedGo != null)
                ImGui.SetTooltip($"= {draggedGo.Name}");
        }

        // Clear drag state if mouse released with no drop
        if (_draggedInstanceId != 0 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            _draggedInstanceId = 0;

        ImGui.EndChild();
    }

    // ────────────────────────────────────────────────────────────
    // Recursive tree drawing
    // ────────────────────────────────────────────────────────────

    private void DrawGameObject(GameObject go, ISelectionService sel, ISceneService sceneService, int depth)
    {
        bool isSelected = sel.ActiveObject is GameObject selected && selected.InstanceID == go.InstanceID;
        bool hasChildren = go.Children != null && go.Children.Count > 0;

        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.FramePadding;
        if (isSelected) flags |= ImGuiTreeNodeFlags.Selected;
        if (!hasChildren) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        ImGui.PushID(go.InstanceID);

        // Determine the best icon for this game object
        string iconName = IconManager.GetIconNameForGameObject(go);
        string displayName = go.Name ?? "Unnamed";

        // Draw tree node — icon is rendered via IconManager after the tree node
        bool open = ImGui.TreeNodeEx("##node", flags, $"     {displayName}");

        // Overlay the icon over the label area
        {
            Vector4 tint = go.Enabled
                ? new Vector4(1f, 1f, 1f, 1f)
                : new Vector4(0.5f, 0.5f, 0.5f, 0.5f);
            IconManager.DrawIconOverLastItem(iconName, tint);
        }

        // Selection on click
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            sel.ActiveObject = go;

        // ── Drag source (for reparenting) ──────────────────────
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3f))
        {
            _draggedInstanceId = go.InstanceID;
            // Also start an EditorDragDrop payload so other panels can see it
            EditorDragDrop.BeginDrag(HierarchyDragType, go);
        }

        // ── Drop target (for reparenting) ──────────────────────
        if (_draggedInstanceId != 0 && _draggedInstanceId != go.InstanceID)
        {
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
            {
                // Visual feedback — highlight the drop target with border
                var drawList = ImGui.GetWindowDrawList();
                drawList.AddRectFilled(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                    ImGui.GetColorU32(new Vector4(0.28f, 0.56f, 1.0f, 0.15f)));
                drawList.AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                    ImGui.GetColorU32(new Vector4(0.28f, 0.56f, 1.0f, 0.60f)), 2f, ImDrawFlags.None, 1.5f);

                ImGui.SetTooltip($"Reparent under '{go.Name}'");

                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    var dragged = FindGameObjectById(_draggedInstanceId, sceneService);
                    if (dragged != null && !go.IsChildOf(dragged) && dragged != go)
                    {
                        if (EditorServices.TryGet<UndoRedoService>(out var undo))
                            undo!.Execute(new ReparentGameObjectCommand(dragged, go));
                        else
                            dragged.SetParent(go);
                    }
                    _draggedInstanceId = 0;
                    EditorDragDrop.Clear();
                }
            }
        }

        // ── Context menu ───────────────────────────────────────
        if (ImGui.BeginPopupContextItem())
        {
            if (ImGui.MenuItem("+ Create Empty Child"))
            {
                if (EditorServices.TryGet<UndoRedoService>(out var undo))
                {
                    var cmd = new CreateGameObjectCommand("Child");
                    undo!.Execute(cmd);
                    cmd.CreatedObject?.SetParent(go);
                }
                else
                {
                    var child = sceneService.CreateGameObject("Child");
                    child.SetParent(go);
                }
            }
            if (ImGui.MenuItem("Duplicate"))
            {
                // Simple clone: create GO with same name
                var clone = sceneService.CreateGameObject(go.Name + " (Clone)");
                if (go.Parent != null) clone.SetParent(go.Parent);
            }
            ImGui.Separator();
            if (ImGui.MenuItem("X Delete"))
            {
                sceneService.DestroyGameObject(go);
            }
            ImGui.EndPopup();
        }

        // ── Draw tree indentation lines for clarity ────────────
        if (depth > 0)
        {
            var drawList = ImGui.GetWindowDrawList();
            Vector2 itemMin = ImGui.GetItemRectMin();
            float lineX = itemMin.X - 8 * Game.DpiScale;
            float lineTop = itemMin.Y;
            float lineBot = itemMin.Y + ImGui.GetItemRectSize().Y * 0.5f;
            uint lineCol = ImGui.GetColorU32(new Vector4(0.35f, 0.35f, 0.35f, 0.40f));

            // Vertical line
            drawList.AddLine(new Vector2(lineX, lineTop), new Vector2(lineX, lineBot), lineCol, 1f);
            // Horizontal connector
            drawList.AddLine(new Vector2(lineX, lineBot), new Vector2(lineX + 6 * Game.DpiScale, lineBot), lineCol, 1f);
        }

        // Recurse into children
        if (open && hasChildren)
        {
            foreach (var child in go.Children)
            {
                DrawGameObject(child, sel, sceneService, depth + 1);
            }
            ImGui.TreePop();
        }

        ImGui.PopID();
    }

    // ────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────

    private static void CreateChildOfSelection(ISceneService sceneService, ISelectionService selService)
    {
        GameObject? parent = selService.ActiveObject as GameObject;
        if (EditorServices.TryGet<UndoRedoService>(out var undo))
        {
            var cmd = new CreateGameObjectCommand("Child");
            undo!.Execute(cmd);
            if (parent != null)
                cmd.CreatedObject?.SetParent(parent);
        }
        else
        {
            var child = sceneService.CreateGameObject("Child");
            if (parent != null)
                child.SetParent(parent);
        }
    }

    private static GameObject? FindGameObjectById(int instanceId, ISceneService sceneService)
    {
        foreach (var root in sceneService.GetRootGameObjects())
        {
            var found = FindRecursive(root, instanceId);
            if (found != null) return found;
        }
        return null;
    }

    private static GameObject? FindRecursive(GameObject go, int instanceId)
    {
        if (go.InstanceID == instanceId) return go;
        foreach (var child in go.Children)
        {
            var found = FindRecursive(child, instanceId);
            if (found != null) return found;
        }
        return null;
    }

    private static void AcceptDrop(ISceneService sceneService)
    {
        var entry = EditorDragDrop.AcceptDrop<AssetEntry>("AssetEntry");
        if (entry != null)
        {
            if (entry.Extension == PrefabManager.PrefabExtension)
            {
                var prefabMgr = new PrefabManager();
                var go = prefabMgr.InstantiatePrefabInScene(entry.FullPath, sceneService);
                if (go != null)
                    Debug.Log($"[DragDrop] Instantiated prefab '{go.Name}' from: {entry.RelativePath}");
            }
            else
            {
                string goName = Path.GetFileNameWithoutExtension(entry.Name);
                if (EditorServices.TryGet<UndoRedoService>(out var undo))
                    undo!.Execute(new CreateGameObjectCommand(goName));
                else
                    sceneService.CreateGameObject(goName);
                Debug.Log($"[DragDrop] Instantiated '{goName}' from asset: {entry.RelativePath}");
            }
        }
    }
}
