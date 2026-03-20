// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Editor.Docking;
using Prowl.Editor.Icons;
using Prowl.Editor.Prefabs;
using Prowl.Editor.Services;
using Prowl.Editor.Undo;

using Material = Prowl.Runtime.Resources.Material;
using Shader = Prowl.Runtime.Resources.Shader;

namespace Prowl.Editor.Panels;

/// <summary>
/// Project browser panel — split layout with a folder tree on the left
/// and folder contents on the right (Unity-style). Supports expand/collapse,
/// context menus for creating assets/folders, and drag-to-hierarchy.
/// </summary>
public sealed class ProjectPanel : EditorPanel
{
    private readonly HashSet<string> _expandedFolders = new() { "." };
    private string? _selectedFolder = ".";
    private string? _selectedEntry;
    private string _searchFilter = string.Empty;
    private string _renameBuffer = string.Empty;
    private string? _renamingPath;
    private bool _renameNeedsFocus;

    // Deferred selection: wait for mouse release so drags don't trigger inspector switch
    private string? _pendingSelectPath;
    private bool _dragOccurred;

    // Clipboard for copy / paste / duplicate
    private static readonly List<string> s_clipboardPaths = new();
    private static bool s_clipboardIsCut;

    // Relative folder tree width fraction
    private const float TreeWidthFraction = 0.25f;
    private const float MinTreeWidth = 120f;
    private const float MaxTreeWidth = 400f;

    // Ping state: highlight a specific entry briefly
    private string? _pingPath;
    private float _pingTimer;
    private const float PingDuration = 2.0f;

    public ProjectPanel() : base("Project") { }

    /// <summary>
    /// Navigates to the folder containing the specified asset and highlights it.
    /// If the asset is in a different folder, the project view switches to that folder first.
    /// </summary>
    public void PingAsset(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return;

        // Navigate to the containing folder
        string? dir = Path.GetDirectoryName(relativePath);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        dir = dir.Replace('\\', '/');

        _selectedFolder = dir;

        // Expand all parent folders in the tree
        string current = dir;
        while (!string.IsNullOrEmpty(current) && current != ".")
        {
            _expandedFolders.Add(current);
            current = Path.GetDirectoryName(current) ?? ".";
            current = current.Replace('\\', '/');
        }
        _expandedFolders.Add(".");

        // Select and ping the entry
        _selectedEntry = relativePath;
        _pingPath = relativePath;
        _pingTimer = PingDuration;

        // Ensure the panel is visible
        IsOpen = true;
    }

    protected override void DrawContent()
    {
        var assets = EditorServices.Get<IAssetService>();

        // Tick ping timer
        if (_pingTimer > 0)
        {
            _pingTimer -= Runtime.Time.DeltaTime;
            if (_pingTimer <= 0)
                _pingPath = null;
        }

        if (!assets.HasProject)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
                "No project open. Assets folder will be created automatically.");
            return;
        }

        // ── Toolbar ────────────────────────────────────────────
        DrawToolbar(assets);

        ImGui.Separator();

        // ── Split: folder tree (left) + contents (right) ──────
        float availW = ImGui.GetContentRegionAvail().X;
        float treeW = Math.Clamp(availW * TreeWidthFraction, MinTreeWidth * Game.DpiScale, MaxTreeWidth * Game.DpiScale);

        // Left: Folder tree
        ImGui.BeginChild("##FolderTree", new Vector2(treeW, 0), ImGuiChildFlags.Border);
        DrawFolderTree(assets, ".", "Assets");
        ImGui.EndChild();

        ImGui.SameLine();

        // Right: Folder contents
        ImGui.BeginChild("##FolderContents", new Vector2(0, 0));
        DrawFolderContents(assets);
        ImGui.EndChild();
    }

    // ── Toolbar ────────────────────────────────────────────────

    private void DrawToolbar(IAssetService assets)
    {
        // Create button with dropdown
        if (EditorIcons.ImageButtonWithLabel("ProjCreate", EditorIconType.Plus, "Create"))
            ImGui.OpenPopup("##CreateAssetPopup");

        if (ImGui.BeginPopup("##CreateAssetPopup"))
        {
            string contextDir = _selectedFolder ?? ".";

            if (EditorIcons.IconMenuItem(EditorIconType.Folder, "New Folder"))
            {
                var entry = assets.CreateFolder(contextDir, $"NewFolder_{DateTime.Now:HHmmss}");
                _selectedFolder = entry.RelativePath;
                _expandedFolders.Add(contextDir);
                BeginRename(entry.RelativePath, entry.Name);
            }

            ImGui.Separator();

            if (EditorIcons.IconMenuItem(EditorIconType.Script, "C# Script"))
            {
                string name = $"NewBehaviour.cs";
                var scriptEntry = assets.CreateFile(contextDir, name, GenerateScriptTemplate(name.Replace(".cs", "")));
                BeginRename(scriptEntry.RelativePath, Path.GetFileNameWithoutExtension(scriptEntry.Name));
            }

            if (EditorIcons.IconMenuItem(EditorIconType.Material, "Material"))
            {
                string matName = $"NewMaterial_{DateTime.Now:HHmmss}";
                string matFileName = $"{matName}.mat";
                string relPath = Path.Combine(contextDir == "." ? "" : contextDir, matFileName);
                string absPath = assets.GetAbsolutePath(relPath);

                // Create a default material and save it using the serializer
                var defaultShader = Shader.LoadDefault(DefaultShader.Standard);
                var mat = new Material(defaultShader);
                mat.Name = matName;
                mat.SetColor("_MainColor", Prowl.Vector.Color.White);
                MaterialSerializer.Save(mat, absPath);
                assets.MetaManager.EnsureMeta(absPath);
                assets.Refresh();
                BeginRename(relPath, matName);
            }

            if (EditorIcons.IconMenuItem(EditorIconType.Scene, "Scene"))
            {
                string name = $"NewScene_{DateTime.Now:HHmmss}.scene";
                if (EditorServices.TryGet<ISceneSerializer>(out var serializer))
                {
                    var scene = EditorServices.Get<ISceneService>().CurrentScene;
                    if (scene != null)
                    {
                        string relPath = Path.Combine(contextDir == "." ? "" : contextDir, name);
                        string absPath = assets.GetAbsolutePath(relPath);
                        serializer!.Save(scene, absPath);
                        assets.Refresh();
                        BeginRename(relPath, Path.GetFileNameWithoutExtension(name));
                    }
                }
            }

            ImGui.EndPopup();
        }

        ImGui.SameLine();

        if (EditorIcons.ImageButtonWithLabel("ProjRefresh", EditorIconType.Refresh, "Refresh"))
            assets.Refresh();

        ImGui.SameLine();

        // Search bar
        EditorIcons.InlineIcon(EditorIconType.Search);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(120 * Game.DpiScale, ImGui.GetContentRegionAvail().X - 10));
        ImGui.InputTextWithHint("##Search", "Search assets...", ref _searchFilter, 256);
    }

    // ── Folder Tree (Left Panel) ──────────────────────────────

    private void DrawFolderTree(IAssetService assets, string relativeDir, string displayName)
    {
        bool isSelected = _selectedFolder == relativeDir;
        bool isExpanded = _expandedFolders.Contains(relativeDir);

        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (isSelected) flags |= ImGuiTreeNodeFlags.Selected;
        if (isExpanded) flags |= ImGuiTreeNodeFlags.DefaultOpen;

        // Check if the folder has subfolders
        var entries = assets.GetEntries(relativeDir);
        bool hasSubFolders = false;
        foreach (var e in entries)
        {
            if (e.IsDirectory) { hasSubFolders = true; break; }
        }
        if (!hasSubFolders) flags |= ImGuiTreeNodeFlags.Leaf;

        string folderIconName = isExpanded && hasSubFolders ? "FolderOpen" : "Folder";
        bool open = ImGui.TreeNodeEx($"##{relativeDir}", flags, $"     {displayName}");

        // Overlay the folder icon via IconManager
        IconManager.DrawIconOverLastItem(folderIconName);

        // Click to select folder
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _selectedFolder = relativeDir;
        }

        // Track expansion state
        if (open != isExpanded)
        {
            if (open) _expandedFolders.Add(relativeDir);
            else _expandedFolders.Remove(relativeDir);
        }

        // Context menu on folder tree nodes
        if (ImGui.BeginPopupContextItem())
        {
            if (ImGui.MenuItem("New Folder"))
            {
                var entry = assets.CreateFolder(relativeDir, $"NewFolder_{DateTime.Now:HHmmss}");
                _selectedFolder = entry.RelativePath;
                _expandedFolders.Add(relativeDir);
                BeginRename(entry.RelativePath, entry.Name);
            }
            if (relativeDir != "." && ImGui.MenuItem("Delete Folder"))
            {
                var dirEntry = new AssetEntry
                {
                    Name = displayName,
                    FullPath = assets.GetAbsolutePath(relativeDir),
                    RelativePath = relativeDir,
                    IsDirectory = true,
                };
                try { assets.Delete(dirEntry); }
                catch (Exception ex) { Runtime.Debug.LogWarning($"Cannot delete: {ex.Message}"); }
                if (_selectedFolder == relativeDir) _selectedFolder = ".";
            }
            ImGui.EndPopup();
        }

        if (open)
        {
            // Recursively draw subfolder nodes
            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                    DrawFolderTree(assets, entry.RelativePath, entry.Name);
            }
            ImGui.TreePop();
        }
    }

    // ── Folder Contents (Right Panel) ─────────────────────────

    private void DrawFolderContents(IAssetService assets)
    {
        string dir = _selectedFolder ?? ".";

        // Breadcrumb path
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.55f, 1f));
        string breadcrumb = dir == "." ? "Assets/" : $"Assets/{dir}/";
        ImGui.Text(breadcrumb);
        ImGui.PopStyleColor();
        ImGui.Separator();

        var entries = assets.GetEntries(dir);
        bool hasFilter = !string.IsNullOrWhiteSpace(_searchFilter);

        // If searching, do a recursive search across all assets
        if (hasFilter)
        {
            DrawSearchResults(assets);
            return;
        }

        // Context menu on empty area
        if (ImGui.BeginPopupContextWindow("##ContentAreaCtx", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
        {
            DrawContextMenu(dir, assets);
            ImGui.EndPopup();
        }

        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
                DrawContentFolderItem(entry, assets);
            else
                DrawContentFileItem(entry, assets);
        }
    }

    private void DrawContentFolderItem(AssetEntry entry, IAssetService assets)
    {
        // Inline rename mode for folders
        if (_renamingPath == entry.RelativePath)
        {
            DrawRenameInput(entry, assets, isDirectory: true);
            return;
        }

        bool isSelected = _selectedEntry == entry.RelativePath;

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.90f, 0.80f, 0.40f, 1f));
        var flags = ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (isSelected) flags |= ImGuiTreeNodeFlags.Selected;

        // Draw with spacing for icon, then overlay it
        ImGui.TreeNodeEx(entry.RelativePath, flags, $"     {entry.Name}");
        ImGui.PopStyleColor();

        // Overlay the folder icon via IconManager
        IconManager.DrawIconOverLastItem("Folder", useTreeIndent: false);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _selectedEntry = entry.RelativePath;
        }

        // Double-click to navigate into folder
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            _selectedFolder = entry.RelativePath;
            _expandedFolders.Add(entry.RelativePath);
        }

        // Folder context menu in content area
        if (ImGui.BeginPopupContextItem())
        {
            if (ImGui.MenuItem("Rename"))
            {
                BeginRename(entry.RelativePath, entry.Name);
            }
            if (ImGui.MenuItem("Delete"))
            {
                try { assets.Delete(entry); }
                catch (Exception ex) { Runtime.Debug.LogWarning($"Cannot delete: {ex.Message}"); }
            }
            ImGui.EndPopup();
        }
    }

    private void DrawContentFileItem(AssetEntry entry, IAssetService assets)
    {
        // Inline rename mode — replace the tree node with an input field
        if (_renamingPath == entry.RelativePath)
        {
            DrawRenameInput(entry, assets, isDirectory: false);
            return;
        }

        bool isSelected = _selectedEntry == entry.RelativePath;

        var flags = ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (isSelected) flags |= ImGuiTreeNodeFlags.Selected;

        // Draw with spacing for icon, then overlay it
        ImGui.TreeNodeEx(entry.RelativePath, flags, $"     {entry.Name}");

        // Overlay the icon based on file extension via IconManager
        {
            string fileIconName = IconManager.GetIconNameForExtension(entry.Extension);
            IconManager.DrawIconOverLastItem(fileIconName, useTreeIndent: false);
        }

        // Ping highlight: pulsing border around the pinged asset
        if (_pingPath == entry.RelativePath && _pingTimer > 0)
        {
            float alpha = 0.4f + 0.4f * MathF.Sin(_pingTimer * 6f);
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                ImGui.GetColorU32(new Vector4(0.28f, 0.56f, 1.0f, alpha)), 3f, ImDrawFlags.None, 2f);

            // Scroll to make the pinged item visible
            if (_pingTimer > PingDuration - 0.1f)
                ImGui.SetScrollHereY(0.5f);
        }

        // On mouse press: highlight the item but defer inspector selection until release
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _selectedEntry = entry.RelativePath;
            _pendingSelectPath = entry.RelativePath;
            _dragOccurred = false;
        }

        // ImGui drag-drop source for cross-panel drag (project → scene / hierarchy)
        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
        {
            _dragOccurred = true;
            EditorDragDrop.BeginDrag("AssetEntry", entry);

            // Store the GUID as the payload if available, otherwise the path
            string payloadText = entry.RelativePath;
            if (EditorServices.TryGet<IAssetService>(out var assetSvc))
            {
                string? guid = assetSvc!.GetGuidByPath(entry.RelativePath);
                if (guid != null) payloadText = guid;
            }

            unsafe
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(payloadText + '\0');
                fixed (byte* ptr = bytes)
                {
                    ImGui.SetDragDropPayload("ASSET_ENTRY", (nint)ptr, (uint)bytes.Length);
                }
            }

            ImGui.Text($"\ud83d\udcc4 {entry.Name}");
            ImGui.EndDragDropSource();
        }

        // On mouse release: if this was the pressed item and no drag occurred, select in inspector
        if (_pendingSelectPath == entry.RelativePath && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (!_dragOccurred && ImGui.IsItemHovered())
            {
                if (EditorServices.TryGet<ISelectionService>(out var sel))
                    sel!.SelectedAsset = entry;
            }
            _pendingSelectPath = null;
        }

        // Double-click to open scene files or open prefabs for editing
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            if (entry.Extension == ".scene")
            {
                EditorMenuBar.LoadSceneFromFile(entry.FullPath);
            }
            else if (entry.Extension == PrefabManager.PrefabExtension)
            {
                if (EditorServices.TryGet<PrefabEditMode>(out var prefabMode))
                    prefabMode!.Enter(entry.FullPath);
            }
        }

        // Tooltip on hover
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(entry.RelativePath);
        }

        // File context menu
        if (ImGui.BeginPopupContextItem())
        {
            if (entry.Extension == ".scene")
            {
                if (EditorIcons.IconMenuItem(EditorIconType.Scene, "Open Scene"))
                {
                    EditorMenuBar.LoadSceneFromFile(entry.FullPath);
                }
                ImGui.Separator();
            }

            if (entry.Extension == PrefabManager.PrefabExtension)
            {
                if (EditorIcons.IconMenuItem(EditorIconType.Prefab, "Open Prefab"))
                {
                    if (EditorServices.TryGet<PrefabEditMode>(out var prefabMode))
                        prefabMode!.Enter(entry.FullPath);
                }

                if (EditorIcons.IconMenuItem(EditorIconType.Prefab, "Instantiate in Scene"))
                {
                    var sceneService = EditorServices.Get<ISceneService>();
                    if (EditorServices.TryGet<UndoRedoService>(out var undoPrefab))
                        undoPrefab!.Execute(new Undo.Commands.InstantiateAssetCommand(entry.FullPath, entry.Name));
                    else
                    {
                        var prefabMgr = new PrefabManager();
                        prefabMgr.InstantiatePrefabInScene(entry.FullPath, sceneService);
                    }
                }
                ImGui.Separator();
            }

            bool isScript = entry.Extension is ".cs" or ".csx";
            if (isScript && ImGui.MenuItem("\u270e Open in Editor"))
            {
                try { Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true }); }
                catch (Exception ex) { Runtime.Debug.LogWarning($"Cannot open file: {ex.Message}"); }
            }

            if (ImGui.MenuItem("\ud83d\udcc2 Show in Explorer"))
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                        Process.Start("explorer.exe", $"/select,\"{entry.FullPath}\"");
                    else if (OperatingSystem.IsMacOS())
                        Process.Start("open", $"-R \"{entry.FullPath}\"");
                    else
                        Process.Start("xdg-open", Path.GetDirectoryName(entry.FullPath) ?? ".");
                }
                catch (Exception ex) { Runtime.Debug.LogWarning($"Show in explorer failed: {ex.Message}"); }
            }

            ImGui.Separator();

            if (ImGui.MenuItem("Rename"))
            {
                BeginRename(entry.RelativePath, Path.GetFileNameWithoutExtension(entry.Name));
            }

            if (ImGui.MenuItem("Copy"))
            {
                s_clipboardPaths.Clear();
                s_clipboardPaths.Add(entry.FullPath);
                s_clipboardIsCut = false;
            }

            if (ImGui.MenuItem("Paste", s_clipboardPaths.Count > 0))
            {
                PasteClipboard(assets, _selectedFolder ?? ".");
            }

            if (ImGui.MenuItem("Duplicate"))
            {
                DuplicateFile(assets, entry);
            }

            if (ImGui.MenuItem("Copy Path"))
            {
                ImGui.SetClipboardText(entry.FullPath);
            }

            if (ImGui.MenuItem("Copy Relative Path"))
            {
                ImGui.SetClipboardText(entry.RelativePath);
            }

            ImGui.Separator();

            if (ImGui.MenuItem("Delete"))
            {
                assets.Delete(entry);
            }

            ImGui.Separator();

            if (ImGui.MenuItem("Create Prefab (from selected)"))
            {
                var sel = EditorServices.Get<ISelectionService>();
                if (sel.ActiveObject is GameObject go)
                {
                    string safeName = (go.Name ?? "Prefab").Replace(" ", "_");
                    string fname = $"{safeName}{PrefabManager.PrefabExtension}";
                    string absPath = assets.GetAbsolutePath(
                        Path.Combine(_selectedFolder == "." ? "" : _selectedFolder ?? "", fname));
                    var prefabMgr = new PrefabManager();
                    prefabMgr.CreatePrefab(go, absPath);
                }
                else
                {
                    Runtime.Debug.LogWarning("[Project] Select a GameObject first to create a prefab.");
                }
            }
            ImGui.EndPopup();
        }

        }

    private void DrawSearchResults(IAssetService assets)
    {
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f),
            $"Search results for \"{_searchFilter}\":");
        ImGui.Separator();

        DrawSearchInDir(assets, ".");
    }

    private void DrawSearchInDir(IAssetService assets, string dir)
    {
        var entries = assets.GetEntries(dir);
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                DrawSearchInDir(assets, entry.RelativePath);
            }
            else if (entry.Name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
            {
                string searchIconName = IconManager.GetIconNameForExtension(entry.Extension);
                bool isSelected = _selectedEntry == entry.RelativePath;
                var flags = ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth;
                if (isSelected) flags |= ImGuiTreeNodeFlags.Selected;

                ImGui.TreeNodeEx(entry.RelativePath, flags, $"     {entry.Name}");

                // Overlay icon via IconManager
                IconManager.DrawIconOverLastItem(searchIconName, useTreeIndent: false);

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(entry.RelativePath);
                }

                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                {
                    _selectedEntry = entry.RelativePath;

                    if (EditorServices.TryGet<ISelectionService>(out var sel))
                        sel!.SelectedAsset = entry;

                    EditorDragDrop.BeginDrag("AssetEntry", entry);
                }

                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    if (entry.Extension == ".scene")
                        EditorMenuBar.LoadSceneFromFile(entry.FullPath);
                    else if (entry.Extension == PrefabManager.PrefabExtension)
                    {
                        if (EditorServices.TryGet<PrefabEditMode>(out var prefabMode))
                            prefabMode!.Enter(entry.FullPath);
                    }
                }
            }
        }
    }

    private void DrawContextMenu(string contextDir, IAssetService assets)
    {
        if (ImGui.MenuItem("New Folder"))
        {
            var entry = assets.CreateFolder(contextDir, $"NewFolder_{DateTime.Now:HHmmss}");
            _selectedFolder = entry.RelativePath;
            _expandedFolders.Add(contextDir);
            BeginRename(entry.RelativePath, entry.Name);
        }

        if (ImGui.MenuItem("New C# Script"))
        {
            string name = $"NewBehaviour.cs";
            var scriptEntry = assets.CreateFile(contextDir, name, GenerateScriptTemplate(name.Replace(".cs", "")));
            BeginRename(scriptEntry.RelativePath, Path.GetFileNameWithoutExtension(scriptEntry.Name));
        }

        if (ImGui.MenuItem("New Material"))
        {
            var matEntry = assets.CreateFile(contextDir, $"NewMaterial_{DateTime.Now:HHmmss}.mat",
                "{ \"shader\": \"Standard\", \"color\": [1,1,1,1] }");
            BeginRename(matEntry.RelativePath, Path.GetFileNameWithoutExtension(matEntry.Name));
        }

        if (ImGui.MenuItem("New Scene"))
        {
            string name = $"NewScene_{DateTime.Now:HHmmss}.scene";
            if (EditorServices.TryGet<ISceneSerializer>(out var serializer))
            {
                var scene = EditorServices.Get<ISceneService>().CurrentScene;
                if (scene != null)
                {
                    string relPath = Path.Combine(contextDir == "." ? "" : contextDir, name);
                    string absPath = assets.GetAbsolutePath(relPath);
                    serializer!.Save(scene, absPath);
                    assets.Refresh();
                    BeginRename(relPath, Path.GetFileNameWithoutExtension(name));
                }
            }
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Paste", s_clipboardPaths.Count > 0))
        {
            PasteClipboard(assets, contextDir);
        }

        if (ImGui.MenuItem("\ud83d\udcc2 Show in Explorer"))
        {
            string absDir = assets.GetAbsolutePath(contextDir);
            try
            {
                if (OperatingSystem.IsWindows())
                    Process.Start("explorer.exe", $"\"{absDir}\"");
                else if (OperatingSystem.IsMacOS())
                    Process.Start("open", $"\"{absDir}\"");
                else
                    Process.Start("xdg-open", absDir);
            }
            catch (Exception ex) { Runtime.Debug.LogWarning($"Show in explorer failed: {ex.Message}"); }
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Create Prefab (from selected)"))
        {
            var sel = EditorServices.Get<ISelectionService>();
            if (sel.ActiveObject is GameObject go)
            {
                string safeName = (go.Name ?? "Prefab").Replace(" ", "_");
                string fname = $"{safeName}{PrefabManager.PrefabExtension}";
                string absPath = assets.GetAbsolutePath(
                    Path.Combine(contextDir == "." ? "" : contextDir, fname));

                var prefabMgr = new PrefabManager();
                prefabMgr.CreatePrefab(go, absPath);
            }
            else
            {
                Runtime.Debug.LogWarning("[Project] Select a GameObject first to create a prefab.");
            }
        }
    }

    // ── Inline rename ──────────────────────────────────────────

    private void BeginRename(string relativePath, string initialName)
    {
        _renamingPath = relativePath;
        _renameBuffer = initialName;
        _renameNeedsFocus = true;
    }

    private void DrawRenameInput(AssetEntry entry, IAssetService assets, bool isDirectory)
    {
        if (_renameNeedsFocus)
        {
            ImGui.SetKeyboardFocusHere();
            _renameNeedsFocus = false;
        }

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        bool committed = ImGui.InputText("##Rename", ref _renameBuffer, 256,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

        // Cancel on Escape
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _renamingPath = null;
            return;
        }

        // Commit when focus is lost (but not on the very first frame)
        if (!committed && !ImGui.IsItemActive() && !ImGui.IsItemFocused())
        {
            Runtime.Debug.Log($"Committed! {!ImGui.IsItemActive()} - {!ImGui.IsItemFocused()}");
            //committed = true;
        }

        if (committed)
        {
            string newName = _renameBuffer.Trim();
            if (!string.IsNullOrEmpty(newName))
            {
                string dir = Path.GetDirectoryName(entry.FullPath) ?? "";
                string ext = isDirectory ? "" : entry.Extension;
                string newFullPath = Path.Combine(dir, newName + ext);
                bool alreadyExists = isDirectory ? Directory.Exists(newFullPath) : File.Exists(newFullPath);
                if (!alreadyExists && newFullPath != entry.FullPath)
                {
                    try
                    {
                        if (isDirectory)
                        {
                            Directory.Move(entry.FullPath, newFullPath);
                        }
                        else
                        {
                            File.Move(entry.FullPath, newFullPath);
                            // Move .meta file if it exists
                            string metaOld = entry.FullPath + ".meta";
                            string metaNew = newFullPath + ".meta";
                            if (File.Exists(metaOld))
                                File.Move(metaOld, metaNew);
                        }
                        assets.Refresh();
                    }
                    catch (Exception ex) { Runtime.Debug.LogWarning($"Rename failed: {ex.Message}"); }
                }
            }
            _renamingPath = null;
        }
    }

    private static string GenerateScriptTemplate(string className) =>
$@"using Prowl.Runtime;

namespace Game;

public class {className} : MonoBehaviour
{{
    public override void Update()
    {{
        // TODO: Implement
    }}
}}
";

    // ── File helpers ───────────────────────────────────────────

    private static void PasteClipboard(IAssetService assets, string targetDir)
    {
        string absDir = assets.GetAbsolutePath(targetDir);
        foreach (string srcPath in s_clipboardPaths)
        {
            if (!File.Exists(srcPath) && !Directory.Exists(srcPath)) continue;

            string name = Path.GetFileName(srcPath);
            string destPath = Path.Combine(absDir, name);

            // Handle duplicates
            destPath = GetUniqueFilePath(destPath);

            try
            {
                if (File.Exists(srcPath))
                {
                    File.Copy(srcPath, destPath);
                    string metaSrc = srcPath + ".meta";
                    if (File.Exists(metaSrc))
                        File.Copy(metaSrc, destPath + ".meta");
                }
                else if (Directory.Exists(srcPath))
                {
                    CopyDirectoryRecursive(srcPath, destPath);
                }
            }
            catch (Exception ex) { Runtime.Debug.LogWarning($"Paste failed: {ex.Message}"); }
        }

        if (s_clipboardIsCut)
        {
            foreach (string srcPath in s_clipboardPaths)
            {
                try
                {
                    if (File.Exists(srcPath)) File.Delete(srcPath);
                    else if (Directory.Exists(srcPath)) Directory.Delete(srcPath, true);

                    string meta = srcPath + ".meta";
                    if (File.Exists(meta)) File.Delete(meta);
                }
                catch { /* best effort */ }
            }
            s_clipboardPaths.Clear();
            s_clipboardIsCut = false;
        }

        assets.Refresh();
    }

    private static void DuplicateFile(IAssetService assets, AssetEntry entry)
    {
        if (!File.Exists(entry.FullPath)) return;

        string destPath = GetUniqueFilePath(entry.FullPath);
        try
        {
            File.Copy(entry.FullPath, destPath);
            assets.Refresh();
        }
        catch (Exception ex) { Runtime.Debug.LogWarning($"Duplicate failed: {ex.Message}"); }
    }

    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        string dir = Path.GetDirectoryName(path) ?? ".";
        string nameNoExt = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{nameNoExt} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        foreach (string subDir in Directory.GetDirectories(sourceDir))
            CopyDirectoryRecursive(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
    }
}
