// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Editor.Services;

namespace Prowl.Editor.Prefabs;

/// <summary>
/// Manages an isolated "Prefab Edit Mode" where the user can open a
/// prefab asset in its own temporary scene, make changes, and either
/// save them back to disk or discard.
///
/// Flow (mirrors Unity's prefab editing experience):
///   1. User double-clicks a prefab in the Project panel or clicks "Open" in the Inspector.
///   2. The current scene is snapshotted and hidden.
///   3. A temporary scene is created containing only the prefab hierarchy.
///   4. The Hierarchy panel shows a breadcrumb bar indicating prefab edit mode.
///   5. The user edits components, children, etc.
///   6. On "Save &amp; Close" the prefab file is updated and the original scene is restored.
///   7. On "Close" without saving, the original scene is restored and edits are discarded.
/// </summary>
public sealed class PrefabEditMode
{
    private object? _sceneSnapshot;
    private string? _scenePath;
    private bool _sceneDirty;

    /// <summary> True when the editor is in prefab edit mode. </summary>
    public bool IsActive { get; private set; }

    /// <summary> The absolute file path of the prefab being edited. </summary>
    public string? PrefabPath { get; private set; }

    /// <summary> The display name of the prefab (without extension). </summary>
    public string PrefabName =>
        string.IsNullOrEmpty(PrefabPath) ? string.Empty : Path.GetFileNameWithoutExtension(PrefabPath);

    /// <summary> The root GameObject of the prefab being edited (lives in the temp scene). </summary>
    public GameObject? PrefabRoot { get; private set; }

    /// <summary> Per-instance event domain for prefab edit mode notifications. </summary>
    public PrefabEditModeEvents Events { get; } = new();

    /// <summary>
    /// Opens a prefab asset for editing in an isolated scene.
    /// </summary>
    public bool Enter(string absolutePrefabPath)
    {
        if (IsActive)
        {
            Debug.LogWarning("[PrefabEditMode] Already editing a prefab. Close the current one first.");
            return false;
        }

        if (!File.Exists(absolutePrefabPath))
        {
            Debug.LogError($"[PrefabEditMode] Prefab file not found: {absolutePrefabPath}");
            return false;
        }

        // Prevent entering prefab edit mode during play mode
        if (EditorServices.TryGet<Core.EditorPlayMode>(out var playMode)
            && playMode!.State != Core.PlayModeState.Stopped)
        {
            Debug.LogWarning("[PrefabEditMode] Cannot open a prefab while in play mode.");
            return false;
        }

        var sceneService = EditorServices.Get<ISceneService>();

        // Snapshot the current scene so we can restore it later
        _sceneSnapshot = sceneService.SnapshotScene();
        _scenePath = sceneService.SceneFilePath;
        _sceneDirty = sceneService.IsDirty;

        // Instantiate the prefab (without prefab links — we're editing the source)
        var prefabMgr = new PrefabManager();
        GameObject? root = LoadPrefabForEditing(absolutePrefabPath);
        if (root == null)
        {
            Debug.LogError("[PrefabEditMode] Failed to load prefab for editing.");
            _sceneSnapshot = null;
            return false;
        }

        // Create a fresh temporary scene for the prefab
        var tempScene = new Scene { Name = $"[Prefab] {Path.GetFileNameWithoutExtension(absolutePrefabPath)}" };
        Scene.Load(tempScene);
        tempScene.Add(root);

        PrefabRoot = root;
        PrefabPath = absolutePrefabPath;
        IsActive = true;

        // Clear selection to avoid stale references
        if (EditorServices.TryGet<ISelectionService>(out var sel))
            sel!.ActiveObject = root;

        sceneService.ClearDirty();
        sceneService.SceneFilePath = null;

        Events.InvokeOnModeChanged(new PrefabModeChangedArgs(true));
        Debug.Log($"[PrefabEditMode] Opened prefab: {PrefabName}");
        return true;
    }

    /// <summary>
    /// Saves the edited prefab back to disk and exits prefab edit mode,
    /// restoring the previous scene.
    /// </summary>
    public void SaveAndClose()
    {
        if (!IsActive || PrefabRoot == null || string.IsNullOrEmpty(PrefabPath))
            return;

        // Save the prefab root back to the .prefab file
        SavePrefabToDisk(PrefabRoot, PrefabPath);
        Debug.Log($"[PrefabEditMode] Saved prefab: {PrefabName}");

        // Refresh asset database
        if (EditorServices.TryGet<IAssetService>(out var assets))
            assets!.Refresh();

        // Capture the path before Close() clears it
        string savedPrefabPath = PrefabPath;

        Close();

        // Update all instances of the edited prefab in the restored scene
        // so they reflect the changes just saved to disk.
        var sceneService = EditorServices.Get<ISceneService>();
        if (sceneService.CurrentScene != null)
        {
            var prefabMgr = new PrefabManager();
            prefabMgr.RevertAllInstances(sceneService.CurrentScene, savedPrefabPath);
        }
    }

    /// <summary>
    /// Exits prefab edit mode without saving, restoring the previous scene.
    /// </summary>
    public void Close()
    {
        if (!IsActive) return;

        var sceneService = EditorServices.Get<ISceneService>();

        // Restore the original scene from the snapshot
        sceneService.RestoreScene(_sceneSnapshot);
        sceneService.SceneFilePath = _scenePath;
        if (_sceneDirty)
            sceneService.MarkDirty();
        else
            sceneService.ClearDirty();

        _sceneSnapshot = null;
        _scenePath = null;
        _sceneDirty = false;
        PrefabRoot = null;
        PrefabPath = null;
        IsActive = false;

        Events.InvokeOnModeChanged(new PrefabModeChangedArgs(false));
        Debug.Log("[PrefabEditMode] Exited prefab edit mode. Scene restored.");
    }

    /// <summary>
    /// Loads a prefab from disk for editing (no prefab links stamped).
    /// </summary>
    private static GameObject? LoadPrefabForEditing(string absolutePath)
    {
        try
        {
            string json = File.ReadAllText(absolutePath);
            var root = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (root == null) return null;

            EchoObject envelope = JsonSceneSerializer.JsonToEcho(root);

            EchoObject? prefabData = null;
            if (envelope.TagType == EchoType.Compound && envelope.TryGet("prefab", out EchoObject? pd))
                prefabData = pd;
            else
                prefabData = envelope;
            if (prefabData == null) return null;

            var ctx = new SerializationContext();
            AssetDatabase.ConfigureContext(ctx);

            return Serializer.Deserialize<GameObject>(prefabData, ctx);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PrefabEditMode] Failed to load prefab: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Serializes a GameObject hierarchy and writes it to a .prefab file.
    /// </summary>
    private static void SavePrefabToDisk(GameObject root, string absolutePath)
    {
        try
        {
            // Strip any prefab links that may have been attached during editing
            PrefabManager.ClearPrefabLinksRecursive(root);

            var ctx = new SerializationContext();
            AssetDatabase.ConfigureContext(ctx);
            EchoObject echoData = Serializer.Serialize(typeof(GameObject), root, ctx);

            var envelope = EchoObject.NewCompound();
            envelope.Add("version", new EchoObject(1));
            envelope.Add("prefab", echoData);

            var jsonNode = JsonSceneSerializer.EchoToJson(envelope);
            string json = jsonNode?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "{}";

            File.WriteAllText(absolutePath, json);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PrefabEditMode] Failed to save prefab: {ex.Message}");
        }
    }
}
