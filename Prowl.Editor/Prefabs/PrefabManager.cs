// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using System.Text.Json.Nodes;
using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Prefabs;
using Prowl.Runtime.Resources;
using Prowl.Editor.Services;

namespace Prowl.Editor.Prefabs;

/// <summary>
/// Manages prefab creation, saving, and instantiation.
/// Prefabs are stored as JSON files (.prefab) containing the full
/// GameObject hierarchy serialized through Echo, using the same
/// pipeline as scenes (including ISerializationCallbackReceiver
/// and asset-reference resolution).
///
/// Also provides Unity-like prefab operations: Apply, Revert, Unpack,
/// and the ability to locate all prefab instances in a scene.
/// </summary>
public sealed class PrefabManager
{
    public const string PrefabExtension = ".prefab";

    // ────────────────────────────────────────────────────────────
    // Create / Save
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a prefab asset file from a GameObject hierarchy.
    /// Saves the hierarchy to the given absolute path.
    /// Optionally links the source GameObject to the new prefab.
    /// </summary>
    public void CreatePrefab(GameObject source, string absolutePath, bool linkSource = true)
    {
        try
        {
            // Strip any existing prefab link from the serialized data
            // (the prefab asset itself should not contain a link)
            PrefabLink? oldLink = source.PrefabLink;
            ClearPrefabLinksRecursive(source);

            var ctx = new SerializationContext();
            AssetDatabase.ConfigureContext(ctx);
            EchoObject echoData = Serializer.Serialize(typeof(GameObject), source, ctx);

            var envelope = EchoObject.NewCompound();
            envelope.Add("version", new EchoObject(1));
            envelope.Add("prefab", echoData);

            JsonNode? jsonNode = JsonSceneSerializer.EchoToJson(envelope);
            string json = jsonNode?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";

            File.WriteAllText(absolutePath, json);
            Debug.Log($"[Prefab] Created prefab: {absolutePath}");

            // Link the source GO to the new prefab so the hierarchy shows it as an instance
            if (linkSource)
            {
                string relativePath = ResolveRelativePath(absolutePath);
                string? guid = ResolveGuid(relativePath);
                ApplyPrefabLinksRecursive(source, relativePath, guid ?? string.Empty);
            }
            else
            {
                // Restore old link if we chose not to link
                RestorePrefabLinksRecursive(source, oldLink);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Prefab] Failed to create prefab: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────────────
    // Instantiate
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Instantiates a GameObject hierarchy from a prefab file.
    /// Returns the root GameObject (not yet added to a scene).
    /// The returned object has a <see cref="PrefabLink"/> attached.
    /// </summary>
    public GameObject? InstantiatePrefab(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            Debug.LogError($"[Prefab] File not found: {absolutePath}");
            return null;
        }

        try
        {
            string json = File.ReadAllText(absolutePath);
            JsonNode? root = JsonNode.Parse(json);
            if (root == null) return null;

            EchoObject envelope = JsonSceneSerializer.JsonToEcho(root);

            // Read prefab data from envelope (supports both versioned and legacy formats)
            EchoObject? prefabData = null;
            if (envelope.TagType == EchoType.Compound && envelope.TryGet("prefab", out EchoObject? pd))
                prefabData = pd;
            else
                prefabData = envelope;

            if (prefabData == null) return null;

            var ctx = new SerializationContext();
            AssetDatabase.ConfigureContext(ctx);

            GameObject? go = Serializer.Deserialize<GameObject>(prefabData, ctx);

            if (go != null)
            {
                // Regenerate identifiers so each instance is unique
                go.RegenerateIdentifiers();

                // Stamp a prefab link onto the hierarchy
                string relativePath = ResolveRelativePath(absolutePath);
                string? guid = ResolveGuid(relativePath);
                ApplyPrefabLinksRecursive(go, relativePath, guid ?? string.Empty);

                Debug.Log($"[Prefab] Instantiated from: {absolutePath}");
            }

            return go;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Prefab] Failed to load prefab: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Instantiates a prefab and adds it to the current scene.
    /// </summary>
    public GameObject? InstantiatePrefabInScene(string absolutePath, ISceneService sceneService)
    {
        var go = InstantiatePrefab(absolutePath);
        if (go == null) return null;

        if (sceneService.CurrentScene == null)
            sceneService.CreateNewScene();

        sceneService.CurrentScene!.Add(go);
        return go;
    }

    // ────────────────────────────────────────────────────────────
    // Apply (instance → prefab asset)
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves the current state of a prefab instance back to its source
    /// prefab asset file. All other instances in the scene are
    /// <em>not</em> automatically updated; call <see cref="RevertInstance"/>
    /// on each to pick up the new data.
    /// </summary>
    public bool ApplyInstance(GameObject instanceRoot)
    {
        if (instanceRoot.PrefabLink == null || !instanceRoot.PrefabLink.IsRoot)
        {
            Debug.LogWarning("[Prefab] Cannot apply — not a prefab instance root.");
            return false;
        }

        string absolutePath = ResolvePrefabAbsolutePath(instanceRoot.PrefabLink);
        if (string.IsNullOrEmpty(absolutePath))
        {
            Debug.LogError("[Prefab] Cannot resolve prefab asset path for apply.");
            return false;
        }

        try
        {
            // Temporarily strip prefab links so they are not baked into the asset
            PrefabLink savedLink = instanceRoot.PrefabLink.Clone();
            ClearPrefabLinksRecursive(instanceRoot);

            var ctx = new SerializationContext();
            AssetDatabase.ConfigureContext(ctx);
            EchoObject echoData = Serializer.Serialize(typeof(GameObject), instanceRoot, ctx);

            var envelope = EchoObject.NewCompound();
            envelope.Add("version", new EchoObject(1));
            envelope.Add("prefab", echoData);

            JsonNode? jsonNode = JsonSceneSerializer.EchoToJson(envelope);
            string json = jsonNode?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";

            File.WriteAllText(absolutePath, json);

            // Restore the link on the live instance
            ApplyPrefabLinksRecursive(instanceRoot, savedLink.PrefabAssetPath, savedLink.PrefabAssetGuid);

            Debug.Log($"[Prefab] Applied overrides to: {absolutePath}");

            // Refresh asset database so the project panel picks up the change
            if (EditorServices.TryGet<IAssetService>(out var assets))
                assets!.Refresh();

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Prefab] Apply failed: {ex.Message}");
            return false;
        }
    }

    // ────────────────────────────────────────────────────────────
    // Revert (prefab asset → instance)
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reverts a prefab instance to match its source prefab asset,
    /// discarding any local overrides. The instance's transform
    /// (position/rotation/scale in the scene) is preserved.
    /// </summary>
    public bool RevertInstance(GameObject instanceRoot)
    {
        if (instanceRoot.PrefabLink == null || !instanceRoot.PrefabLink.IsRoot)
        {
            Debug.LogWarning("[Prefab] Cannot revert — not a prefab instance root.");
            return false;
        }

        string absolutePath = ResolvePrefabAbsolutePath(instanceRoot.PrefabLink);
        if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
        {
            Debug.LogError("[Prefab] Cannot resolve or find prefab asset for revert.");
            return false;
        }

        try
        {
            // Remember scene-side state
            var scene = instanceRoot.Scene;
            var parent = instanceRoot.Parent;
            var localPos = instanceRoot.Transform.LocalPosition;
            var localRot = instanceRoot.Transform.LocalRotation;
            var localScl = instanceRoot.Transform.LocalScale;
            string linkPath = instanceRoot.PrefabLink.PrefabAssetPath;
            string linkGuid = instanceRoot.PrefabLink.PrefabAssetGuid;

            // Load a fresh copy from the asset
            string json = File.ReadAllText(absolutePath);
            JsonNode? root = JsonNode.Parse(json);
            if (root == null) return false;

            EchoObject envelope = JsonSceneSerializer.JsonToEcho(root);
            EchoObject? prefabData = null;
            if (envelope.TagType == EchoType.Compound && envelope.TryGet("prefab", out EchoObject? pd))
                prefabData = pd;
            else
                prefabData = envelope;
            if (prefabData == null) return false;

            var ctx = new SerializationContext();
            AssetDatabase.ConfigureContext(ctx);
            GameObject? fresh = Serializer.Deserialize<GameObject>(prefabData, ctx);
            if (fresh == null) return false;

            fresh.RegenerateIdentifiers();

            // Remove the old instance from the scene
            scene?.Remove(instanceRoot);
            instanceRoot.Dispose();

            // Stamp prefab link on the fresh copy
            ApplyPrefabLinksRecursive(fresh, linkPath, linkGuid);

            // Restore scene-side transform
            fresh.Transform.LocalPosition = localPos;
            fresh.Transform.LocalRotation = localRot;
            fresh.Transform.LocalScale = localScl;

            // Add to scene and reparent
            scene?.Add(fresh);
            if (parent.IsValid())
                fresh.SetParent(parent, false);

            // Select the new instance
            if (EditorServices.TryGet<ISelectionService>(out var sel))
                sel!.ActiveObject = fresh;

            Debug.Log($"[Prefab] Reverted instance to match: {absolutePath}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Prefab] Revert failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reverts all instances of a prefab in the given scene to match
    /// the latest asset on disk. Used after prefab edit mode saves
    /// changes so every instance picks up the new data.
    /// </summary>
    public void RevertAllInstances(Scene scene, string absolutePrefabPath)
    {
        string relativePath = ResolveRelativePath(absolutePrefabPath);
        string? guid = ResolveGuid(relativePath);

        var link = new PrefabLink
        {
            PrefabAssetPath = relativePath,
            PrefabAssetGuid = guid ?? string.Empty,
            IsRoot = true,
        };

        foreach (var instance in FindAllInstances(scene, link).ToList())
        {
            RevertInstance(instance);
        }
    }

    // ────────────────────────────────────────────────────────────
    // Unpack (remove prefab link)
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes the prefab link from a prefab instance, converting it
    /// into a regular GameObject hierarchy.
    /// </summary>
    public static void UnpackInstance(GameObject instanceRoot)
    {
        if (instanceRoot.PrefabLink == null)
        {
            Debug.LogWarning("[Prefab] Cannot unpack — not a prefab instance.");
            return;
        }

        ClearPrefabLinksRecursive(instanceRoot);
        Debug.Log($"[Prefab] Unpacked '{instanceRoot.Name}'");
    }

    // ────────────────────────────────────────────────────────────
    // Scene queries
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds all root prefab instances in the current scene that
    /// reference the same prefab asset as <paramref name="link"/>.
    /// </summary>
    public static IEnumerable<GameObject> FindAllInstances(Scene scene, PrefabLink link)
    {
        if (scene == null) yield break;
        foreach (var go in scene.AllObjects)
        {
            if (go.PrefabLink is { IsRoot: true } other)
            {
                bool matchByGuid = !string.IsNullOrEmpty(link.PrefabAssetGuid)
                                    && other.PrefabAssetGuid == link.PrefabAssetGuid;
                bool matchByPath = !string.IsNullOrEmpty(link.PrefabAssetPath)
                                    && other.PrefabAssetPath == link.PrefabAssetPath;
                if (matchByGuid || matchByPath)
                    yield return go;
            }
        }
    }

    /// <summary>
    /// Returns the display name of the prefab asset linked to the given GO.
    /// </summary>
    public static string GetPrefabName(PrefabLink link)
    {
        string path = link.PrefabAssetPath;
        if (string.IsNullOrEmpty(path)) return "(missing)";
        return Path.GetFileNameWithoutExtension(path);
    }

    // ────────────────────────────────────────────────────────────
    // Prefab link helpers
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Stamps a <see cref="PrefabLink"/> onto a GO and all its children.
    /// The root gets <c>IsRoot = true</c>; children get <c>IsRoot = false</c>.
    /// </summary>
    private static void ApplyPrefabLinksRecursive(GameObject go, string relativePath, string guid)
    {
        go.PrefabLink = new PrefabLink
        {
            PrefabAssetPath = relativePath,
            PrefabAssetGuid = guid,
            IsRoot = true,
        };
        foreach (var child in go.Children)
            ApplyPrefabLinksChild(child, relativePath, guid);
    }

    private static void ApplyPrefabLinksChild(GameObject go, string relativePath, string guid)
    {
        go.PrefabLink = new PrefabLink
        {
            PrefabAssetPath = relativePath,
            PrefabAssetGuid = guid,
            IsRoot = false,
        };
        foreach (var child in go.Children)
            ApplyPrefabLinksChild(child, relativePath, guid);
    }

    /// <summary>
    /// Removes prefab links from a GO and all its children.
    /// </summary>
    internal static void ClearPrefabLinksRecursive(GameObject go)
    {
        go.PrefabLink = null;
        foreach (var child in go.Children)
            ClearPrefabLinksRecursive(child);
    }

    /// <summary>
    /// Restores a previously-saved prefab link on a GO and its children.
    /// </summary>
    private static void RestorePrefabLinksRecursive(GameObject go, PrefabLink? link)
    {
        if (link == null) return;
        ApplyPrefabLinksRecursive(go, link.PrefabAssetPath, link.PrefabAssetGuid);
    }

    // ────────────────────────────────────────────────────────────
    // Path / GUID resolution
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves an absolute path to a project-relative asset path.
    /// </summary>
    private static string ResolveRelativePath(string absolutePath)
    {
        if (EditorServices.TryGet<IAssetService>(out var assets))
        {
            string root = assets!.AssetRootPath;
            if (!string.IsNullOrEmpty(root) && absolutePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                string rel = absolutePath[(root.Length)..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return rel.Replace('\\', '/');
            }
        }
        return absolutePath;
    }

    /// <summary>
    /// Resolves a relative asset path to its GUID (if the .meta system is available).
    /// </summary>
    private static string? ResolveGuid(string relativePath)
    {
        if (EditorServices.TryGet<IAssetService>(out var assets))
            return assets!.GetGuidByPath(relativePath);
        return null;
    }

    /// <summary>
    /// Resolves a <see cref="PrefabLink"/> to an absolute file path.
    /// Tries GUID first, falls back to relative path.
    /// </summary>
    internal static string ResolvePrefabAbsolutePath(PrefabLink link)
    {
        if (EditorServices.TryGet<IAssetService>(out var assets))
        {
            // Try GUID resolution first
            if (!string.IsNullOrEmpty(link.PrefabAssetGuid))
            {
                string? pathByGuid = assets!.GetAssetPathByGuid(link.PrefabAssetGuid);
                if (!string.IsNullOrEmpty(pathByGuid))
                    return assets.GetAbsolutePath(pathByGuid);
            }

            // Fallback to relative path
            if (!string.IsNullOrEmpty(link.PrefabAssetPath))
                return assets!.GetAbsolutePath(link.PrefabAssetPath);
        }
        return string.Empty;
    }
}
