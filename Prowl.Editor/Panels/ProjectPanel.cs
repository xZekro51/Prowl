// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Numerics;
using ImGuiNET;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Editor.Docking;
using Prowl.Editor.Icons;
using Prowl.Editor.Services;
using Prowl.Editor.Prefabs;

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

    // Relative folder tree width fraction
    private const float TreeWidthFraction = 0.25f;
    private const float MinTreeWidth = 120f;
    private const float MaxTreeWidth = 400f;

    public ProjectPanel() : base("Project") { }

    protected override void DrawContent()
    {
        var assets = EditorServices.Get<IAssetService>();

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
            }

            ImGui.Separator();

            if (EditorIcons.IconMenuItem(EditorIconType.Script, "C# Script"))
            {
                string name = $"NewScript_{DateTime.Now:HHmmss}.cs";
                assets.CreateFile(contextDir, name, GenerateScriptTemplate(name.Replace(".cs", "")));
            }

            if (EditorIcons.IconMenuItem(EditorIconType.Material, "Material"))
            {
                assets.CreateFile(contextDir, $"NewMaterial_{DateTime.Now:HHmmss}.mat",
                    "{ \"shader\": \"Standard\", \"color\": [1,1,1,1] }");
            }

            if (EditorIcons.IconMenuItem(EditorIconType.Scene, "Scene"))
            {
                string name = $"NewScene_{DateTime.Now:HHmmss}.scene";
                if (EditorServices.TryGet<ISceneSerializer>(out var serializer))
                {
                    var scene = EditorServices.Get<ISceneService>().CurrentScene;
                    if (scene != null)
                    {
                        string absPath = assets.GetAbsolutePath(
                            Path.Combine(contextDir == "." ? "" : contextDir, name));
                        serializer!.Save(scene, absPath);
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
                catch (Exception ex) { Debug.LogWarning($"Cannot delete: {ex.Message}"); }
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
                DrawContentFolderItem(entry);
            else
                DrawContentFileItem(entry, assets);
        }
    }

    private void DrawContentFolderItem(AssetEntry entry)
    {
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
    }

    private void DrawContentFileItem(AssetEntry entry, IAssetService assets)
    {
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

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _selectedEntry = entry.RelativePath;

            // Notify the selection service so the inspector can display asset info
            if (EditorServices.TryGet<ISelectionService>(out var sel))
                sel!.SelectedAsset = entry;

            EditorDragDrop.BeginDrag("AssetEntry", entry);
        }

        // Double-click to open scene files
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            if (entry.Extension == ".scene")
            {
                EditorMenuBar.LoadSceneFromFile(entry.FullPath);
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
            if (ImGui.MenuItem("Delete"))
            {
                assets.Delete(entry);
            }
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
                    Debug.LogWarning("[Project] Select a GameObject first to create a prefab.");
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
        }

        if (ImGui.MenuItem("New C# Script"))
        {
            string name = $"NewScript_{DateTime.Now:HHmmss}.cs";
            assets.CreateFile(contextDir, name, GenerateScriptTemplate(name.Replace(".cs", "")));
        }

        if (ImGui.MenuItem("New Material"))
        {
            assets.CreateFile(contextDir, $"NewMaterial_{DateTime.Now:HHmmss}.mat",
                "{ \"shader\": \"Standard\", \"color\": [1,1,1,1] }");
        }

        if (ImGui.MenuItem("New Scene"))
        {
            string name = $"NewScene_{DateTime.Now:HHmmss}.scene";
            if (EditorServices.TryGet<ISceneSerializer>(out var serializer))
            {
                var scene = EditorServices.Get<ISceneService>().CurrentScene;
                if (scene != null)
                {
                    string absPath = assets.GetAbsolutePath(
                        Path.Combine(contextDir == "." ? "" : contextDir, name));
                    serializer!.Save(scene, absPath);
                }
            }
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
                Debug.LogWarning("[Project] Select a GameObject first to create a prefab.");
            }
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
}
