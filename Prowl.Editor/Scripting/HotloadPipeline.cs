// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Prowl.Editor.Scripting;

/// <summary>
/// Orchestrates the complete hotload pipeline: detects changes, classifies them,
/// routes to IL fast-path or full structural hotload, and manages instance migration.
/// This is the central controller that ties together all hotload subsystems.
/// </summary>
public static class HotloadPipeline
{
    /// <summary>Current state of the hotload system.</summary>
    public enum HotloadState
    {
        Idle,
        DetectingChanges,
        Classifying,
        Compiling,
        UnloadingAssemblies,
        LoadingAssemblies,
        MigratingInstances,
        Finalizing,
    }

    /// <summary>Current hotload state.</summary>
    public static HotloadState State { get; private set; } = HotloadState.Idle;

    /// <summary>True if a hotload is currently in progress.</summary>
    public static bool IsHotloading => State != HotloadState.Idle;

    /// <summary>Statistics from the last hotload operation.</summary>
    public static HotloadStats LastStats { get; private set; } = new();

    /// <summary>The file watcher instance.</summary>
    private static ScriptFileWatcher? s_fileWatcher;

    /// <summary>Pending compilation result from background thread.</summary>
    private static ScriptCompiler.CompileResult? s_pendingCompileResult;
    private static bool s_isCompiling;

    /// <summary>Pending change set waiting for compilation to complete.</summary>
    private static ScriptFileWatcher.ChangeSet? s_pendingChangeSet;
    private static ChangeClassifier.ClassificationResult s_pendingClassification;

    /// <summary>Fired when a hotload completes (success or failure).</summary>
    public static event Action<bool>? OnHotloadComplete;

    /// <summary>
    /// Initialize the hotload pipeline for a project.
    /// Call after project is opened and initial assemblies are loaded.
    /// </summary>
    public static void Initialize(Project project)
    {
        Shutdown();

        // Apply settings
        ApplySettings();

        // Snapshot baseline file hashes
        ChangeClassifier.SnapshotBaseline(project.AssetsPath);

        // Start file watcher
        s_fileWatcher = new ScriptFileWatcher();
        ApplyDebounceToWatcher();
        s_fileWatcher.Start(project.AssetsPath);

        HotloadLogger.Log("Hotload pipeline initialized.");
    }

    /// <summary>Apply settings from <see cref="HotloadSettings"/> to subsystems.</summary>
    private static void ApplySettings()
    {
        try
        {
            var settings = ProjectSettingsRegistry.Get<HotloadSettings>();
            HotloadLogger.Verbosity = settings.LogVerbosity;
        }
        catch
        {
            // Settings may not be registered yet during early init
        }
    }

    private static void ApplyDebounceToWatcher()
    {
        if (s_fileWatcher == null) return;
        try
        {
            var settings = ProjectSettingsRegistry.Get<HotloadSettings>();
            s_fileWatcher.DebounceMs = settings.DebounceMs;
        }
        catch { }
    }

    /// <summary>
    /// Shutdown the hotload pipeline. Call when closing a project.
    /// </summary>
    public static void Shutdown()
    {
        s_fileWatcher?.Dispose();
        s_fileWatcher = null;
        s_pendingCompileResult = null;
        s_pendingChangeSet = null;
        s_isCompiling = false;
        State = HotloadState.Idle;
        ChangeClassifier.ClearBaseline();

        HotloadLogger.LogDetail("Hotload pipeline shut down.");
    }

    /// <summary>
    /// Main update loop — call once per frame from EditorApplication.
    /// Handles change detection, compilation polling, and hotload execution.
    /// </summary>
    public static void Update()
    {
        if (s_fileWatcher == null) return;
        if (Project.Current == null) return;

        // Don't process while already hotloading
        if (IsHotloading && State != HotloadState.Compiling) return;

        // Poll for completed background compilation
        if (s_pendingCompileResult.HasValue)
        {
            var result = s_pendingCompileResult.Value;
            s_pendingCompileResult = null;
            s_isCompiling = false;

            if (result.Success)
            {
                HotloadLogger.LogTimingAndRestart("Compilation");
                ExecuteHotload(Project.Current, s_pendingClassification);
            }
            else
            {
                HotloadLogger.LogError("Compilation failed. Fix errors and save to retry.");
                State = HotloadState.Idle;
                OnHotloadComplete?.Invoke(false);
            }
            return;
        }

        // Don't start new detection while compiling
        if (s_isCompiling) return;

        // Only process when window is focused
        if (!Window.IsFocused) return;

        // Check if auto-hotload is enabled
        try
        {
            var settings = ProjectSettingsRegistry.Get<HotloadSettings>();
            if (!settings.AutoHotload) return;
        }
        catch { }

        // Check for file changes
        var changes = s_fileWatcher.DrainChanges();
        if (changes == null) return;

        State = HotloadState.Classifying;
        HotloadLogger.StartTimer();

        var classification = ChangeClassifier.Classify(changes);

        if (classification.Type == ChangeClassifier.HotloadType.None)
        {
            HotloadLogger.LogTrace("No real changes detected after classification.");
            State = HotloadState.Idle;
            return;
        }

        HotloadLogger.Log($"Changes detected: {classification.Type} — {classification.Reason}");

        // Defer reload during play mode
        if (Application.IsPlaying)
        {
            ScriptAssemblyManager.ReloadPending = true;
            HotloadLogger.LogWarning("Scripts changed during play mode. Reload will occur when you exit play mode.");
            State = HotloadState.Idle;
            return;
        }

        // Start compilation
        State = HotloadState.Compiling;
        s_pendingChangeSet = changes;
        s_pendingClassification = classification;
        s_isCompiling = true;

        HotloadLogger.Log("Starting compilation...");
        HotloadLogger.StartTimer();

        var project = Project.Current;
        Task.Run(() =>
        {
            try
            {
                s_pendingCompileResult = ScriptCompiler.CompileAll(project);
            }
            catch (Exception ex)
            {
                HotloadLogger.LogError($"Compilation exception: {ex.Message}");
                s_pendingCompileResult = new ScriptCompiler.CompileResult { Success = false, Errors = ex.Message };
            }
        });
    }

    /// <summary>
    /// Force a full hotload regardless of change classification.
    /// Useful as a toolbar/console command.
    /// </summary>
    public static void ForceFullHotload()
    {
        if (Project.Current == null) return;
        if (IsHotloading) return;
        if (Application.IsPlaying)
        {
            HotloadLogger.LogWarning("Cannot force hotload during play mode.");
            return;
        }

        HotloadLogger.Log("Forcing full hotload...");
        HotloadLogger.StartTimer();

        State = HotloadState.Compiling;
        s_pendingClassification = new ChangeClassifier.ClassificationResult
        {
            Type = ChangeClassifier.HotloadType.Full,
            ChangedFiles = [],
            Reason = "Forced by user"
        };
        s_isCompiling = true;

        var project = Project.Current;
        Task.Run(() =>
        {
            try
            {
                s_pendingCompileResult = ScriptCompiler.CompileAll(project);
            }
            catch (Exception ex)
            {
                HotloadLogger.LogError($"Compilation exception: {ex.Message}");
                s_pendingCompileResult = new ScriptCompiler.CompileResult { Success = false, Errors = ex.Message };
            }
        });
    }

    /// <summary>
    /// Execute the hotload after successful compilation.
    /// Routes to IL fast-path or full structural hotload based on classification.
    /// </summary>
    private static void ExecuteHotload(Project project, ChangeClassifier.ClassificationResult classification)
    {
        var stats = new HotloadStats { Type = classification.Type };
        HotloadLogger.StartTimer();

        try
        {
            // Both IL-safe and Full currently go through the full reload path.
            // The IL fast-path (Phase 2) would patch method bodies in-place here.
            // For now, IL-safe changes still benefit from faster compilation + smarter migration.
            if (classification.Type == ChangeClassifier.HotloadType.ILSafe)
            {
                HotloadLogger.Log("IL-safe changes detected. Performing fast reload...");
                PerformFullReload(project, stats, isILSafe: true);
            }
            else
            {
                HotloadLogger.Log("Structural changes detected. Performing full hotload with migration...");
                PerformFullReload(project, stats, isILSafe: false);
            }

            // Update baseline hashes
            if (classification.ChangedFiles.Length > 0)
                ChangeClassifier.UpdateBaseline(classification.ChangedFiles);
            else
                ChangeClassifier.SnapshotBaseline(project.AssetsPath);

            stats.TotalTimeMs = HotloadLogger.ElapsedMs;
            LastStats = stats;

            HotloadLogger.Log($"Hotload complete in {stats.TotalTimeMs:F0}ms — {stats.Report}");
            OnHotloadComplete?.Invoke(true);
        }
        catch (Exception ex)
        {
            HotloadLogger.LogError($"Hotload failed: {ex.Message}");
            OnHotloadComplete?.Invoke(false);
        }
        finally
        {
            State = HotloadState.Idle;
        }
    }

    /// <summary>
    /// Perform a full assembly reload with instance migration.
    /// This is the enhanced version of ScriptAssemblyManager.PerformReload()
    /// that adds change-aware migration.
    /// </summary>
    private static void PerformFullReload(Project project, HotloadStats stats, bool isILSafe)
    {
        // 1. Notify listeners
        State = HotloadState.UnloadingAssemblies;
        ScriptAssemblyManager.InvokeBeforeReload();

        // 2. Capture scene state for migration
        var scene = Scene.Current;
        EchoObject? savedScene = null;

        if (scene != null)
        {
            // Invoke [OnCodeCleanup] on user components
            HotloadLogger.StartTimer();
            InstanceMigrationEngine.InvokeCodeCleanup(scene);
            HotloadLogger.LogTimingAndRestart("[OnCodeCleanup]");

            try
            {
                var savedId = scene.AssetID;
                scene.AssetID = Guid.Empty;
                savedScene = Serializer.Serialize(scene);
                scene.AssetID = savedId;
                stats.SceneSerialized = true;
            }
            catch (Exception ex)
            {
                HotloadLogger.LogWarning($"Failed to serialize scene for migration: {ex.Message}");
            }
        }

        // Remember scene path
        string? scenePath = EditorSceneManager.CurrentScenePath;

        // 3. Clear selection
        Selection.Clear();

        // 4. Unload scene
        Scene.Unload();

        // 5. Unload old assemblies
        HotloadLogger.StartTimer();
        ScriptAssemblyManager.UnloadAssemblies();
        HotloadLogger.LogTimingAndRestart("Assembly unload");

        // 6. Load new assemblies
        State = HotloadState.LoadingAssemblies;
        HotloadLogger.StartTimer();
        ScriptAssemblyManager.LoadAssemblies(project);
        HotloadLogger.LogTimingAndRestart("Assembly load");

        // Clean up auto-statics in new assemblies
        if (ScriptAssemblyManager.LoadedScriptAssemblies != null)
        {
            InstanceMigrationEngine.CleanupAutoStatics(ScriptAssemblyManager.LoadedScriptAssemblies);
        }

        // 7. Re-initialize registries
        State = HotloadState.MigratingInstances;
        HotloadLogger.StartTimer();
        EditorApplication.Instance?.ReinitializeRegistriesForReload();
        HotloadLogger.LogTimingAndRestart("Registry re-initialization");

        // 8. Restore scene
        if (savedScene != null)
        {
            try
            {
                HotloadLogger.StartTimer();
                var ctx = Importers.ImportHelper.CreateTrackingContext(out _);
                var restoredScene = Serializer.Deserialize<Scene>(savedScene, ctx);
                if (restoredScene != null)
                {
                    Scene.Load(restoredScene);
                    EditorSceneManager.CurrentScenePath = scenePath;

                    // Invoke [OnCodeInitializing] on restored components
                    InstanceMigrationEngine.InvokeCodeInitializing(restoredScene);

                    // Invoke [OnHotloaded] on restored components
                    InstanceMigrationEngine.InvokeOnHotloaded(restoredScene);

                    stats.SceneRestored = true;
                    HotloadLogger.LogTimingAndRestart("Scene restore + migration");
                }
            }
            catch (Exception ex)
            {
                HotloadLogger.LogError($"Failed to restore scene after reload: {ex.Message}");
                EditorSceneManager.EnsureSceneLoaded();
            }
        }

        // 9. Clear undo (state references are invalid)
        Undo.Clear();

        // 10. Finalize
        State = HotloadState.Finalizing;
        ScriptAssemblyManager.InvokeAfterReload();

        HotloadLogger.Log("Assembly reload complete.");
    }
}

/// <summary>
/// Statistics from a hotload operation.
/// </summary>
public sealed class HotloadStats
{
    public ChangeClassifier.HotloadType Type { get; set; }
    public double TotalTimeMs { get; set; }
    public bool SceneSerialized { get; set; }
    public bool SceneRestored { get; set; }
    public MigrationReport? Migration { get; set; }

    public string Report =>
        $"{Type} hotload in {TotalTimeMs:F0}ms" +
        (Migration != null ? $" | {Migration}" : "") +
        (SceneSerialized && SceneRestored ? " | Scene preserved" : "");
}
