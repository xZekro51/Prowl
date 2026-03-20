// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using System.Reflection.Metadata;

using ImGuiNET;

using Prowl.Echo;
using Prowl.Editor.Docking;
using Prowl.Editor.Icons;
using Prowl.Editor.Prefabs;
using Prowl.Editor.Services;
using Prowl.Editor.Undo;
using Prowl.Editor.Undo.Commands;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

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

    // Inline rename state
    private int _renamingInstanceId;
    private string _renameBuffer = string.Empty;

    // Scene rename state
    private bool _renamingScene;
    private string _sceneRenameBuffer = string.Empty;

    // Clipboard: stores a serialized snapshot of the copied GameObject hierarchy
    private static EchoObject? _clipboard;

    // Search filter for the hierarchy tree
    private string _searchFilter = string.Empty;

    public HierarchyPanel() : base("Hierarchy") { }

    protected override void DrawContent()
    {
        var sceneService = EditorServices.Get<ISceneService>();
        var selService = EditorServices.Get<ISelectionService>();

        // ── Toolbar ────────────────────────────────────────────
        if (EditorIcons.ImageButtonWithLabel("HierCreate", EditorIconType.Plus, "Create"))
            ImGui.OpenPopup("##HierCreate");

        if (ImGui.BeginPopup("##HierCreate"))
        {
            DrawCreateMenu(sceneService, selService);
            ImGui.EndPopup();
        }

        /*ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();

        // Scene name label
        string sceneName = sceneService.CurrentScene?.Name ?? "No Scene";
        ImGui.TextColored(new Vector4(0.45f, 0.45f, 0.45f, 0.80f), sceneName);*/

        ImGui.Separator();

        // ── Search bar ─────────────────────────────────────────
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##HierSearch", "Search hierarchy...", ref _searchFilter, 256);

        // ── Scrollable hierarchy tree ──────────────────────────
        ImGui.BeginChild("##HierTree");

        try
        {
            bool headerOpen = true;
            if (sceneService.CurrentScene != null)
            {
                // Scene rename mode: replace header with input field
                if (_renamingScene)
                {
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    bool committed = ImGui.InputText("##SceneRename", ref _sceneRenameBuffer, 256,
                        ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
                    if (committed || ImGui.IsKeyPressed(ImGuiKey.Escape))
                    {
                        if (committed)
                        {
                            string newName = _sceneRenameBuffer.Trim();
                            if (!string.IsNullOrEmpty(newName))
                                sceneService.CurrentScene.Name = newName;
                        }
                        _renamingScene = false;
                    }
                    headerOpen = true;
                }
                else
                {
                    headerOpen = ImGui.CollapsingHeader($"     {sceneService.CurrentScene.Name}", ImGuiTreeNodeFlags.DefaultOpen);

                    // Context menu on scene header
                    if (ImGui.BeginPopupContextItem("##SceneHeaderCtx"))
                    {
                        if (ImGui.MenuItem("Rename Scene"))
                        {
                            _renamingScene = true;
                            _sceneRenameBuffer = sceneService.CurrentScene.Name ?? "Untitled";
                        }
                        ImGui.EndPopup();
                    }
                }
            }
            if (headerOpen)
            {
                var roots = sceneService.GetRootGameObjects();
                bool any = false;
                foreach (var go in roots)
                {
                    if (!string.IsNullOrEmpty(_searchFilter) && !MatchesSearch(go, _searchFilter))
                        continue;
                    any = true;
                    DrawGameObject(go, selService, sceneService, 0);
                }

                if (!any)
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 0.6f),
                        "Scene is empty. Use \"+ Create\" to add objects.");
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Hierarchy] Error displaying scene objects: {ex.Message}");
            Debug.LogException(ex);
        }

        // ── Context menu on empty area ─────────────────────────
        if (ImGui.BeginPopupContextWindow("##HierEmptyCtx", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
        {
            DrawCreateMenu(sceneService, selService);
            ImGui.EndPopup();
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

            EditorIcons.InlineIcon(EditorIconType.Dropdown, new Vector4(0.55f, 0.70f, 1.0f, 0.80f));
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.55f, 0.70f, 1.0f, 0.80f), "Drop here to move to root");
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

            EditorIcons.InlineIcon(EditorIconType.Dropdown, new Vector4(0.60f, 0.80f, 0.60f, 1f));
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.60f, 0.80f, 0.60f, 1f), "Drop asset here to instantiate");
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
                ImGui.SetTooltip(draggedGo.Name);
        }

        // Clear drag state if mouse released with no drop
        if (_draggedInstanceId != 0 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            _draggedInstanceId = 0;

        // ── Keyboard shortcuts (only when hierarchy window is focused) ──
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            HandleKeyboardShortcuts(selService, sceneService);

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

        // Force nodes open when search filter is active
        if (!string.IsNullOrEmpty(_searchFilter))
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);

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
            if (ImGui.MenuItem("Rename", "F2"))
            {
                _renamingInstanceId = go.InstanceID;
                _renameBuffer = go.Name ?? "Unnamed";
            }
            if (EditorIcons.IconMenuItem(EditorIconType.Plus, "Create Empty Child"))
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
            ImGui.Separator();
            if (ImGui.MenuItem("Copy", "Ctrl+C"))
            {
                CopyGameObject(go);
            }
            if (ImGui.MenuItem("Paste", "Ctrl+V", false, _clipboard != null))
            {
                PasteGameObject(sceneService, sel, go);
            }
            if (ImGui.MenuItem("Duplicate", "Ctrl+D"))
            {
                DuplicateGameObject(go, sceneService, sel);
            }
            ImGui.Separator();
            if (EditorIcons.IconMenuItem(EditorIconType.Delete, "Delete"))
            {
                sceneService.DestroyGameObject(go);
            }
            ImGui.EndPopup();
        }

        // ── Inline rename input ────────────────────────────────
        if (_renamingInstanceId == go.InstanceID)
        {
            ImGui.OpenPopup("##RenamePopup");
            if (ImGui.BeginPopup("##RenamePopup"))
            {
                ImGui.Text("Rename:");
                bool submit = ImGui.InputText("##rename", ref _renameBuffer, 256,
                    ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

                if (submit)
                {
                    string newName = _renameBuffer.Trim();
                    if (!string.IsNullOrEmpty(newName) && newName != go.Name)
                    {
                        string oldName = go.Name ?? "Unnamed";
                        if (EditorServices.TryGet<UndoRedoService>(out var undoSvc))
                            undoSvc!.Execute(new RenameCommand(go, oldName, newName));
                        else
                            go.Name = newName;
                    }
                    _renamingInstanceId = 0;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
            else
            {
                // Popup was closed without submitting
                _renamingInstanceId = 0;
            }
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
                if (!string.IsNullOrEmpty(_searchFilter) && !MatchesSearch(child, _searchFilter))
                    continue;
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

    /// <summary>
    /// Draws the shared "Create" menu used by both the toolbar popup and the
    /// right-click context menu on empty space.
    /// </summary>
    private static void DrawCreateMenu(ISceneService sceneService, ISelectionService selService)
    {
        if (EditorIcons.IconMenuItem(EditorIconType.Plus, "Empty GameObject"))
        {
            CreateAndSelect(sceneService, selService, "New GameObject");
        }
        if (EditorIcons.IconMenuItem(EditorIconType.Plus, "Empty Child (of selection)"))
        {
            CreateChildOfSelection(sceneService, selService);
        }

        ImGui.Separator();

        if (ImGui.BeginMenu("3D Object"))
        {
            if (ImGui.MenuItem("Cube"))
                CreatePrimitive(sceneService, selService, "Cube",
                    Mesh.CreateCube(new Prowl.Vector.Float3(1, 1, 1)));
            if (ImGui.MenuItem("Sphere"))
                CreatePrimitive(sceneService, selService, "Sphere",
                    Mesh.CreateSphere(0.5f, 24, 24));
            if (ImGui.MenuItem("Cylinder"))
                CreatePrimitive(sceneService, selService, "Cylinder",
                    Mesh.CreateCylinder(0.5f, 2f, 24));
            if (ImGui.MenuItem("Plane"))
                CreatePrimitive(sceneService, selService, "Plane",
                    Mesh.CreateCube(new Prowl.Vector.Float3(10, 0.01f, 10)));
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Light"))
        {
            if (ImGui.MenuItem("Directional Light"))
            {
                var go = CreateAndSelect(sceneService, selService, "Directional Light");
                go?.AddComponent<DirectionalLight>();
            }
            if (ImGui.MenuItem("Point Light"))
            {
                var go = CreateAndSelect(sceneService, selService, "Point Light");
                go?.AddComponent<PointLight>();
            }
            ImGui.EndMenu();
        }

        if (ImGui.MenuItem("Camera"))
        {
            var go = CreateAndSelect(sceneService, selService, "Camera");
            go?.AddComponent<Camera>();
        }
    }

    private static GameObject? CreateAndSelect(ISceneService sceneService, ISelectionService selService, string name)
    {
        GameObject? go;
        if (EditorServices.TryGet<UndoRedoService>(out var undo))
        {
            var cmd = new CreateGameObjectCommand(name);
            undo!.Execute(cmd);
            go = cmd.CreatedObject;
        }
        else
        {
            go = sceneService.CreateGameObject(name);
        }
        if (go != null)
            selService.ActiveObject = go;
        return go;
    }

    private static void CreatePrimitive(ISceneService sceneService, ISelectionService selService, string name, Mesh mesh)
    {
        var go = CreateAndSelect(sceneService, selService, name);
        if (go != null)
        {
            var renderer = go.AddComponent<MeshRenderer>();
            if (renderer != null)
                renderer.Mesh = mesh;
        }
    }

    // ────────────────────────────────────────────────────────────
    // Keyboard shortcuts
    // ────────────────────────────────────────────────────────────

    private void HandleKeyboardShortcuts(ISelectionService sel, ISceneService sceneService)
    {
        bool ctrl = ImGui.GetIO().KeyCtrl;
        GameObject? selected = sel.ActiveObject as GameObject;

        // Delete — remove selected GO
        if (ImGui.IsKeyPressed(ImGuiKey.Delete) && selected != null)
        {
            sceneService.DestroyGameObject(selected);
            sel.ActiveObject = null;
        }

        // F2 — rename selected GO
        if (ImGui.IsKeyPressed(ImGuiKey.F2) && selected != null)
        {
            _renamingInstanceId = selected.InstanceID;
            _renameBuffer = selected.Name ?? "Unnamed";
        }

        // Ctrl+C — copy
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.C) && selected != null)
        {
            CopyGameObject(selected);
        }

        // Ctrl+V — paste
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.V) && _clipboard != null)
        {
            PasteGameObject(sceneService, sel, selected);
        }

        // Ctrl+D — duplicate
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.D) && selected != null)
        {
            DuplicateGameObject(selected, sceneService, sel);
        }
    }

    /// <summary>
    /// Serializes the given <see cref="GameObject"/> (and its full hierarchy
    /// including components) into the static clipboard.
    /// </summary>
    private static void CopyGameObject(GameObject go)
    {
        try
        {
            var ctx = new SerializationContext();
            AssetDatabase.ConfigureContext(ctx);
            _clipboard = Serializer.Serialize(typeof(GameObject), go, ctx);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Hierarchy] Copy failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Deserializes the clipboard into a new <see cref="GameObject"/> and adds
    /// it to the scene. If <paramref name="parent"/> is not null the pasted
    /// object becomes a child of that object. Supports undo.
    /// </summary>
    private static void PasteGameObject(ISceneService sceneService, ISelectionService sel, GameObject? parent)
    {
        if (_clipboard == null) return;

        try
        {
            if (EditorServices.TryGet<UndoRedoService>(out var undo))
            {
                var cmd = new PasteGameObjectCommand(_clipboard, parent, "Paste GameObject", " (Copy)");
                undo!.Execute(cmd);
                if (cmd.PastedObject != null)
                    sel.ActiveObject = cmd.PastedObject;
            }
            else
            {
                var ctx = new SerializationContext();
                AssetDatabase.ConfigureContext(ctx);
                GameObject? pasted = Serializer.Deserialize<GameObject>(_clipboard, ctx);
                if (pasted == null) return;

                pasted.Name += " (Copy)";
                pasted.RegenerateIdentifiers();

                var scene = sceneService.CurrentScene;
                if (scene == null) return;

                scene.Add(pasted);
                if (parent != null)
                    pasted.SetParent(parent);

                sel.ActiveObject = pasted;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Hierarchy] Paste failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Duplicates the given <see cref="GameObject"/> via serialize → deserialize
    /// round-trip, preserving all component data. Uses separate serialization
    /// contexts to avoid reference-identity issues. Supports undo.
    /// </summary>
    private static void DuplicateGameObject(GameObject source, ISceneService sceneService, ISelectionService sel)
    {
        try
        {
            // Serialize with one context
            var serCtx = new SerializationContext();
            AssetDatabase.ConfigureContext(serCtx);
            EchoObject data = Serializer.Serialize(typeof(GameObject), source, serCtx);

            if (EditorServices.TryGet<UndoRedoService>(out var undo))
            {
                var cmd = new PasteGameObjectCommand(data, source.Parent, $"Duplicate '{source.Name}'", " (Clone)");
                undo!.Execute(cmd);
                if (cmd.PastedObject != null)
                    sel.ActiveObject = cmd.PastedObject;
            }
            else
            {
                // Deserialize with a FRESH context to avoid reference identity issues
                var desCtx = new SerializationContext();
                AssetDatabase.ConfigureContext(desCtx);
                GameObject? clone = Serializer.Deserialize<GameObject>(data, desCtx);
                if (clone == null) return;

                clone.Name += " (Clone)";
                clone.RegenerateIdentifiers();

                var scene = sceneService.CurrentScene;
                if (scene == null) return;

                scene.Add(clone);
                if (source.Parent != null)
                    clone.SetParent(source.Parent);

                sel.ActiveObject = clone;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Hierarchy] Duplicate failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns true if the given <see cref="GameObject"/> or any of its
    /// descendants has a name containing <paramref name="filter"/> (case-insensitive).
    /// </summary>
    private static bool MatchesSearch(GameObject go, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        if (go.Name != null && go.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var child in go.Children)
        {
            if (MatchesSearch(child, filter))
                return true;
        }
        return false;
    }
}
