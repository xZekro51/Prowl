// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;

using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Prowl.Launcher;

/// <summary>
/// Manages the list of known projects: persistence, scanning, creation, and removal.
/// The list is stored in <c>projects.json</c> next to the launcher executable.
/// </summary>
public sealed class ProjectManager
{
    private const string ProjectSettingsDir = "ProjectSettings";
    private const string ProjectSettingsFile = "ProjectSettings.asset";
    private const string AssetsDir = "Assets";

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
    };

    private readonly string _listPath;
    private List<ProjectInfo> _projects = new();

    /// <summary> All known projects (read-only view). </summary>
    public IReadOnlyList<ProjectInfo> Projects => _projects;

    public ProjectManager()
    {
        _listPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "projects.json");
        Load();
    }

    // ── Persistence ─────────────────────────────────────────────────

    public void Load()
    {
        if (!File.Exists(_listPath))
        {
            _projects = new List<ProjectInfo>();
            return;
        }

        try
        {
            string json = File.ReadAllText(_listPath);
            _projects = JsonSerializer.Deserialize<List<ProjectInfo>>(json, s_jsonOpts)
                        ?? new List<ProjectInfo>();
        }
        catch
        {
            _projects = new List<ProjectInfo>();
        }

        RefreshTimestamps();
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(_projects, s_jsonOpts);
            // Write to a temp file first, then move — prevents data loss on crash.
            string tmpPath = _listPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _listPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ProjectManager] Failed to save project list: {ex.Message}");
        }
    }

    // ── Query / Mutate ──────────────────────────────────────────────

    /// <summary>
    /// Creates a new project folder at <paramref name="parentDir"/>/<paramref name="projectName"/>
    /// with the standard directory layout, adds it to the list, and returns the project info.
    /// </summary>
    public ProjectInfo CreateProject(string parentDir, string projectName)
    {
        string projectPath = Path.GetFullPath(Path.Combine(parentDir, projectName));
        Directory.CreateDirectory(projectPath);

        // Assets/
        string assetsPath = Path.Combine(projectPath, AssetsDir);
        Directory.CreateDirectory(assetsPath);

        // ProjectSettings/ProjectSettings.asset
        string settingsDir = Path.Combine(projectPath, ProjectSettingsDir);
        Directory.CreateDirectory(settingsDir);
        string settingsFile = Path.Combine(settingsDir, ProjectSettingsFile);
        File.WriteAllText(settingsFile, GenerateProjectSettings(projectName));

        // ── Default project assets ─────────────────────────────
        PopulateDefaultAssets(assetsPath, projectName);

        var info = new ProjectInfo
        {
            Name = projectName,
            Path = projectPath,
            LastModified = DateTime.Now,
        };

        // Avoid duplicates
        _projects.RemoveAll(p => NormPath(p.Path) == NormPath(projectPath));
        _projects.Insert(0, info);
        Save();
        return info;
    }

    /// <summary>
    /// Adds an existing project folder to the list if it looks like a valid project.
    /// Returns the project info, or null if the folder is not a valid project.
    /// If the project is already in the list, moves it to the top and returns it.
    /// </summary>
    public ProjectInfo? AddExistingProject(string projectPath)
    {
        projectPath = Path.GetFullPath(projectPath);
        if (!IsValidProject(projectPath))
            return null;

        // Already in list? Move to top so it shows as most recent.
        var existing = _projects.FirstOrDefault(p => NormPath(p.Path) == NormPath(projectPath));
        if (existing != null)
        {
            _projects.Remove(existing);
            existing.LastModified = GetProjectTimestamp(projectPath);
            _projects.Insert(0, existing);
            Save();
            return existing;
        }

        var info = new ProjectInfo
        {
            Name = Path.GetFileName(projectPath),
            Path = projectPath,
            LastModified = GetProjectTimestamp(projectPath),
        };

        _projects.Insert(0, info);
        Save();
        return info;
    }

    /// <summary> Removes a project from the list (does NOT delete the folder). </summary>
    public void RemoveFromList(ProjectInfo project)
    {
        _projects.Remove(project);
        Save();
    }

    /// <summary>
    /// Removes a project from the list AND deletes the folder from disk.
    /// </summary>
    public void DeleteProject(ProjectInfo project)
    {
        _projects.Remove(project);
        Save();

        if (Directory.Exists(project.Path))
        {
            try
            {
                Directory.Delete(project.Path, true);
                Debug.Log($"[ProjectManager] Deleted project folder: {project.Path}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ProjectManager] Failed to delete project folder '{project.Path}': {ex.Message}");
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// A valid project has either a <c>ProjectSettings/ProjectSettings.asset</c> file
    /// or at minimum an <c>Assets</c> folder.
    /// </summary>
    public static bool IsValidProject(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return false;

        string settingsFile = Path.Combine(folderPath, ProjectSettingsDir, ProjectSettingsFile);
        if (File.Exists(settingsFile))
            return true;

        string assetsDir = Path.Combine(folderPath, AssetsDir);
        return Directory.Exists(assetsDir);
    }

    private void RefreshTimestamps()
    {
        foreach (var p in _projects)
        {
            if (Directory.Exists(p.Path))
                p.LastModified = GetProjectTimestamp(p.Path);
        }
    }

    private static DateTime GetProjectTimestamp(string projectPath)
    {
        string settingsFile = Path.Combine(projectPath, ProjectSettingsDir, ProjectSettingsFile);
        if (File.Exists(settingsFile))
            return File.GetLastWriteTime(settingsFile);
        return Directory.GetLastWriteTime(projectPath);
    }

    private static string GenerateProjectSettings(string projectName) =>
        $$"""
        {
            "projectName": "{{projectName}}",
            "version": "1.0",
            "engine": "Prowl"
        }
        """;

    private static string NormPath(string p) =>
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();

    /// <summary>
    /// Populates a new project's Assets folder with useful default assets:
    /// a scene with camera + light, a default material, and a script template.
    /// </summary>
    private static void PopulateDefaultAssets(string assetsPath, string projectName)
    {
        // ── Scenes/ ──
        string scenesDir = Path.Combine(assetsPath, "Scenes");
        Directory.CreateDirectory(scenesDir);

        string defaultScene = Path.Combine(scenesDir, "Default Scene.scene");
        File.WriteAllText(defaultScene, GenerateDefaultScene());

        // Also place a copy at root for backward compat
        string rootScene = Path.Combine(assetsPath, "Default Scene.scene");
        if (!File.Exists(rootScene))
            File.WriteAllText(rootScene, GenerateDefaultScene());

        // ── Materials/ ──
        string materialsDir = Path.Combine(assetsPath, "Materials");
        Directory.CreateDirectory(materialsDir);

        File.WriteAllText(Path.Combine(materialsDir, "Default.mat"),
            """
            {
                "shader": "Standard",
                "color": [0.8, 0.8, 0.8, 1.0],
                "metallic": 0.0,
                "smoothness": 0.5
            }
            """);

        File.WriteAllText(Path.Combine(materialsDir, "Red.mat"),
            """
            {
                "shader": "Standard",
                "color": [0.9, 0.2, 0.2, 1.0],
                "metallic": 0.0,
                "smoothness": 0.5
            }
            """);

        // ── Scripts/ ──
        string scriptsDir = Path.Combine(assetsPath, "Scripts");
        Directory.CreateDirectory(scriptsDir);

        string safeProjectName = new string(projectName
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .ToArray());
        if (string.IsNullOrEmpty(safeProjectName) || char.IsDigit(safeProjectName[0]))
            safeProjectName = "Game";

        File.WriteAllText(Path.Combine(scriptsDir, "GameController.cs"),
$@"using Prowl.Runtime;

namespace {safeProjectName};

/// <summary>
/// Main game controller — attach this to a GameObject to get started.
/// </summary>
public class GameController : MonoBehaviour
{{
    public float RotationSpeed = 45f;

    public override void Update()
    {{
        // Example: rotate the object
        var euler = GameObject.Transform.LocalEulerAngles;
        euler.Y += RotationSpeed * Time.DeltaTime;
        GameObject.Transform.LocalEulerAngles = euler;
    }}
}}
");

        File.WriteAllText(Path.Combine(scriptsDir, "PlayerController.cs"),
$@"using Prowl.Runtime;

namespace {safeProjectName};

/// <summary>
/// Simple player controller template.
/// </summary>
public class PlayerController : MonoBehaviour
{{
    public float MoveSpeed = 5f;

    public override void Update()
    {{
        // TODO: Add movement logic
    }}
}}
");

        // ── Meshes/ (placeholder) ──
        string meshesDir = Path.Combine(assetsPath, "Meshes");
        Directory.CreateDirectory(meshesDir);

        // Create a simple cube OBJ
        File.WriteAllText(Path.Combine(meshesDir, "Cube.obj"), GenerateCubeObj());

        // ── Textures/ (placeholder) ──
        string texturesDir = Path.Combine(assetsPath, "Textures");
        Directory.CreateDirectory(texturesDir);
    }

    private static string GenerateDefaultScene()
    {
        Scene scene = new Scene();
        scene.Name = "Default Scene";

        // Create directional light
        GameObject lightGO = new("Directional Light");
        lightGO.AddComponent<DirectionalLight>();
        lightGO.Transform.LocalEulerAngles = new Float3(-80, 5, 0);
        scene.Add(lightGO);

        // Create camera
        var cameraGO = new GameObject("Main Camera");
        cameraGO.Tag = "Main Camera";
        cameraGO.Transform.Position = new(0, 2, -8);
        Camera camera = cameraGO.AddComponent<Camera>();
        camera.Depth = -1;
        camera.HDR = true;
        camera.Effects =
        [
            new FXAAEffect(),
            new KawaseBloomEffect(),
            new TonemapperEffect(),
        ];
        scene.Add(cameraGO);

        // Create ground plane
        GameObject groundGO = new("Ground");
        MeshRenderer mr = groundGO.AddComponent<MeshRenderer>();
        mr.Mesh = Mesh.CreateCube(Float3.One);
        mr.Material = new Material(Shader.LoadDefault(DefaultShader.Standard));
        groundGO.Transform.Position = new(0, -3, 0);
        groundGO.Transform.LocalScale = new(20, 1, 20);
        scene.Add(groundGO);

        var serializedScene = Serializer.Serialize(scene, TypeMode.Auto);
        var sceneString = serializedScene.WriteToString();


        return sceneString;
    }
        

    private static string GenerateCubeObj() =>
        """
        # Prowl Engine - Default Cube
        o Cube
        v -0.5 -0.5  0.5
        v  0.5 -0.5  0.5
        v  0.5  0.5  0.5
        v -0.5  0.5  0.5
        v -0.5 -0.5 -0.5
        v  0.5 -0.5 -0.5
        v  0.5  0.5 -0.5
        v -0.5  0.5 -0.5
        vn  0  0  1
        vn  0  0 -1
        vn  1  0  0
        vn -1  0  0
        vn  0  1  0
        vn  0 -1  0
        f 1//1 2//1 3//1 4//1
        f 6//2 5//2 8//2 7//2
        f 2//3 6//3 7//3 3//3
        f 5//4 1//4 4//4 8//4
        f 4//5 3//5 7//5 8//5
        f 5//6 6//6 2//6 1//6
        """;
}
