// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prowl.Editor.Project;

/// <summary>
/// Persists per-project editor session state to a JSON file in ProjectSettings/.
/// Tracks values such as open windows, last scene, and game view resolution
/// so they are restored when re-opening the project.
/// New fields can be added freely — unknown keys in the JSON are silently ignored.
/// </summary>
public sealed class ProjectSessionState
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string FileName = "EditorSession.json";

    // ── Tracked values ─────────────────────────────────────────

    /// <summary> Last opened scene file path (relative to project root). </summary>
    public string? LastScenePath { get; set; }

    /// <summary> Game view resolution preset index (matches GamePanel.ResolutionPresets). </summary>
    public int GameViewResolutionIndex { get; set; }

    /// <summary> Which panels were open (keyed by panel title). </summary>
    public Dictionary<string, bool> OpenPanels { get; set; } = new();

    /// <summary> Last window width in pixels when not maximized (null = use default). </summary>
    public int? WindowWidth { get; set; }

    /// <summary> Last window height in pixels when not maximized (null = use default). </summary>
    public int? WindowHeight { get; set; }

    /// <summary> Whether the window was maximized when the editor was last closed. </summary>
    public bool WindowMaximized { get; set; }

    /// <summary> Arbitrary extra values for future use (avoids breaking changes). </summary>
    public Dictionary<string, string> Extra { get; set; } = new();

    // ── Load / Save ────────────────────────────────────────────

    /// <summary>
    /// Loads session state from the project's settings folder.
    /// Returns a default instance if the file doesn't exist or is corrupt.
    /// </summary>
    public static ProjectSessionState Load(string projectPath)
    {
        string filePath = GetFilePath(projectPath);
        if (!File.Exists(filePath))
            return new ProjectSessionState();

        try
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<ProjectSessionState>(json, s_jsonOpts)
                   ?? new ProjectSessionState();
        }
        catch
        {
            return new ProjectSessionState();
        }
    }

    /// <summary>
    /// Saves the current session state to disk.
    /// </summary>
    public void Save(string projectPath)
    {
        string filePath = GetFilePath(projectPath);
        string dir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dir);

        try
        {
            string json = JsonSerializer.Serialize(this, s_jsonOpts);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"[Session] Failed to save session state: {ex.Message}");
        }
    }

    private static string GetFilePath(string projectPath)
        => Path.Combine(projectPath, "ProjectSettings", FileName);
}
