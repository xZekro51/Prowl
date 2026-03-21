// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prowl.Editor.Build;

/// <summary>
/// Per-platform scripting define symbols and any future platform-specific
/// build knobs. Serialised as part of <see cref="BuildSettings"/>.
/// </summary>
public sealed class PlatformBuildProfile
{
    /// <summary>
    /// Semicolon-separated list of scripting define symbols that will be
    /// passed to the Roslyn compiler (and <c>dotnet publish</c>) when
    /// building for this platform.
    /// </summary>
    public List<string> ScriptingDefineSymbols { get; set; } = [];
}

/// <summary>
/// Project-wide build settings persisted to
/// <c>ProjectSettings/BuildSettings.json</c>.
/// Contains a dictionary of <see cref="PlatformBuildProfile"/> keyed by
/// <see cref="BuildTarget"/> so that each platform can have its own define
/// symbols and future settings.
/// </summary>
public sealed class BuildSettings
{
    private const string FileName = "BuildSettings.json";

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The product / executable name used when publishing the player.
    /// Defaults to the project folder name at load time.
    /// </summary>
    public string ProductName { get; set; } = "ProwlGame";

    /// <summary>
    /// Whether to build a self-contained application (includes the .NET
    /// runtime) or a framework-dependent one.
    /// </summary>
    public bool SelfContained { get; set; } = true;

    /// <summary>
    /// Build configuration: <c>Debug</c> or <c>Release</c>.
    /// </summary>
    public string Configuration { get; set; } = "Release";

    /// <summary>
    /// Relative path (inside Assets/) of the scene to load on startup.
    /// For example <c>"Scenes/MainMenu.scene"</c>.
    /// </summary>
    public string StartupScenePath { get; set; } = "";

    /// <summary>
    /// When <c>true</c> the built executable keeps a console window open
    /// alongside the game window so that log output is visible.
    /// </summary>
    public bool ShowConsole { get; set; } = false;

    /// <summary>
    /// The rendering backend to use for both the editor and the built player.
    /// Defaults to <see cref="Runtime.RenderingBackend.OpenGL"/>.
    /// </summary>
    public Runtime.RenderingBackend RenderingBackend { get; set; } = Runtime.RenderingBackend.OpenGL;

    /// <summary>
    /// Per-platform profiles.  Missing entries will be created with
    /// defaults on first access.
    /// </summary>
    public Dictionary<BuildTarget, PlatformBuildProfile> PlatformProfiles { get; set; } = new();

    /// <summary>
    /// Returns (or lazily creates) the profile for the given target.
    /// </summary>
    public PlatformBuildProfile GetProfile(BuildTarget target)
    {
        if (!PlatformProfiles.TryGetValue(target, out var profile))
        {
            profile = new PlatformBuildProfile();
            PlatformProfiles[target] = profile;
        }
        return profile;
    }

    // ── Persistence ────────────────────────────────────────────

    public static BuildSettings Load(string projectPath)
    {
        string filePath = GetFilePath(projectPath);
        if (!File.Exists(filePath))
        {
            var settings = new BuildSettings
            {
                ProductName = Path.GetFileName(
                    projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            };
            return settings;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<BuildSettings>(json, s_jsonOpts)
                   ?? new BuildSettings();
        }
        catch
        {
            return new BuildSettings();
        }
    }

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
            Runtime.Debug.LogWarning($"[Build] Failed to save build settings: {ex.Message}");
        }
    }

    private static string GetFilePath(string projectPath)
        => Path.Combine(projectPath, "ProjectSettings", FileName);
}
