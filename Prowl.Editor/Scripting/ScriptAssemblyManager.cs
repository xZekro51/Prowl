using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;

using Prowl.Echo;
using Prowl.Editor.Importers;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Prowl.Editor.Scripting;

/// <summary>
/// Manages script assembly compilation, loading, and in-process hot-reload
/// via a collectible <see cref="ScriptAssemblyLoadContext"/>.
/// Debounces recompile requests and orchestrates the compile → reload cycle.
/// </summary>
public static class ScriptAssemblyManager
{
    private static ScriptAssemblyLoadContext? _scriptContext;
    private static WeakReference? _previousContextRef;

    /// <summary>
    /// Fired before script assemblies are unloaded.
    /// Use to release references to user types.
    /// </summary>
    public static event Action? OnBeforeAssemblyReload;

    /// <summary>
    /// Fired after new script assemblies are loaded and registries re-initialized.
    /// </summary>
    public static event Action? OnAfterAssemblyReload;

    /// <summary>
    /// The user script assemblies currently loaded in the collectible ALC, or null.
    /// </summary>
    public static Assembly[]? LoadedScriptAssemblies { get; private set; }

    /// <summary>
    /// True when a reload has been deferred because play mode was active.
    /// </summary>
    public static bool ReloadPending { get; set; }

    private static bool _recompileRequested;
    private static DateTime _lastScriptChange;
    private static bool _isCompiling;
    private static ScriptCompiler.CompileResult? _pendingResult;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(1);

    /// <summary>Signal that scripts have changed and need recompilation.</summary>
    public static void RequestRecompile()
    {
        _recompileRequested = true;
        _lastScriptChange = DateTime.UtcNow;
    }

    /// <summary>Call once per frame. Triggers compilation after debounce period.</summary>
    public static void Update()
    {
        // Check if background compilation finished
        if (_pendingResult.HasValue)
        {
            var result = _pendingResult.Value;
            _pendingResult = null;
            _isCompiling = false;

            if (result.Success)
            {
                Runtime.Debug.Log("[ScriptAssemblyManager] Compilation successful. Reloading assemblies...");
                ReloadAssemblies(Project.Current!);
            }
            else
            {
                Runtime.Debug.LogError("[ScriptAssemblyManager] Compilation failed. Fix errors and save to retry.");
            }
            return;
        }

        if (!_recompileRequested || _isCompiling) return;
        if (Project.Current == null) return;

        // Only compile when the editor window is focused (don't spam while user is editing externally)
        if (!Window.IsFocused) return;

        // Wait for debounce
        if (DateTime.UtcNow - _lastScriptChange < DebounceDelay) return;

        _recompileRequested = false;

        // Quick check: any .cs files at all?
        var project = Project.Current;
        if (!Directory.Exists(project.AssetsPath) ||
            !Directory.EnumerateFiles(project.AssetsPath, "*.cs", SearchOption.AllDirectories).Any())
        {
            Runtime.Debug.Log("[ScriptAssemblyManager] No scripts found, skipping compilation.");
            return;
        }

        _isCompiling = true;
        Runtime.Debug.Log("[ScriptAssemblyManager] Starting compilation...");

        // Run on background thread — result polled on main thread via _pendingResult
        Task.Run(() =>
        {
            try
            {
                _pendingResult = ScriptCompiler.CompileAll(project);
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogError($"[ScriptAssemblyManager] Compilation exception: {ex.Message}");
                _pendingResult = new ScriptCompiler.CompileResult { Success = false, Errors = ex.Message };
            }
        });
    }

    // ================================================================
    //  Assembly Loading / Unloading
    // ================================================================

    /// <summary>Load pre-built script assemblies from Library/ScriptAssemblies/ into a collectible ALC.
    /// Copies to a temp path first so the original DLL stays unlocked for recompilation.</summary>
    public static void LoadAssemblies(Project project)
    {
        var loaded = new List<Assembly>();
        LoadAssembly(project.GameAssemblyPath, "game", loaded);
        LoadAssembly(project.EditorAssemblyPath, "editor", loaded);
        LoadedScriptAssemblies = loaded.Count > 0 ? loaded.ToArray() : null;

        // Register with the centralized runtime AssemblyManager so that
        // RuntimeUtils.FindType, serialization, and registry scanning all work.
        if (LoadedScriptAssemblies != null)
            Runtime.AssemblyManager.Register(LoadedScriptAssemblies);

        // Clear serialization caches so stale null entries from before load are purged
        Echo.Serializer.ClearCache();
    }

    private static void LoadAssembly(string dllPath, string label, List<Assembly> loaded)
    {
        if (!File.Exists(dllPath)) return;

        try
        {
            // Copy to a unique temp path so the original stays unlocked for recompilation
            // and we don't collide with leftover files from previous sessions
            string tempDir = Path.Combine(Path.GetDirectoryName(dllPath)!, ".loaded");
            Directory.CreateDirectory(tempDir);

            // Clean old temp files
            try { foreach (var f in Directory.GetFiles(tempDir, "*.dll")) File.Delete(f); } catch { }

            string tempPath = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(dllPath)}_{Guid.NewGuid():N}.dll");
            File.Copy(dllPath, tempPath, true);

            _scriptContext ??= new ScriptAssemblyLoadContext(Path.GetDirectoryName(dllPath)!);
            var asm = _scriptContext.LoadFromAssemblyPath(tempPath);
            loaded.Add(asm);

            Runtime.Debug.Log($"[ScriptAssemblyManager] Loaded {label} assembly: {Path.GetFileName(dllPath)}");
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"[ScriptAssemblyManager] Failed to load {label} assembly: {ex.Message}");
        }
    }

    /// <summary>Unload all user script assemblies and release the collectible ALC.</summary>
    public static void UnloadAssemblies()
    {
        // Unregister from runtime AssemblyManager first (clears bridges and caches)
        Runtime.AssemblyManager.UnregisterAll();

        LoadedScriptAssemblies = null;

        if (_scriptContext != null)
        {
            _scriptContext.Unload();
            _previousContextRef = new WeakReference(_scriptContext);
            _scriptContext = null;
        }

        // Clear Echo's serialization caches to purge stale type handles
        Echo.Serializer.ClearCache();

        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        if (_previousContextRef != null && _previousContextRef.IsAlive)
            Runtime.Debug.LogWarning("[ScriptAssemblyManager] Previous AssemblyLoadContext is still alive — possible reference leak.");
        else
            Runtime.Debug.Log("[ScriptAssemblyManager] Previous AssemblyLoadContext collected successfully.");
    }

    /// <summary>Check if compiled assemblies exist for this project.</summary>
    public static bool HasScriptAssemblies(Project project)
        => File.Exists(project.GameAssemblyPath) || File.Exists(project.EditorAssemblyPath);

    // ================================================================
    //  Assembly Enumeration (for registry scanning)
    // ================================================================

    /// <summary>
    /// Returns the combined set of assemblies that registries should scan:
    /// all default-context assemblies (engine, BCL) plus any loaded script assemblies.
    /// Delegates to the centralized <see cref="Runtime.AssemblyManager"/>.
    /// </summary>
    public static IEnumerable<Assembly> GetAllRelevantAssemblies()
        => Runtime.AssemblyManager.GetAllRelevantAssemblies();

    // ================================================================
    //  In-Process Hot Reload
    // ================================================================

    /// <summary>
    /// Perform an in-process assembly reload: serialize scene state, unload old assemblies,
    /// load new ones, re-initialize registries, and restore scene state.
    /// </summary>
    public static void ReloadAssemblies(Project project)
    {
        if (Application.IsPlaying)
        {
            ReloadPending = true;
            Runtime.Debug.LogWarning("[ScriptAssemblyManager] Scripts compiled. Reload will occur when you exit play mode.");
            return;
        }

        PerformReload(project);
    }

    /// <summary>
    /// Execute a deferred reload that was postponed because play mode was active.
    /// Call this from EditorApplication after exiting play mode.
    /// </summary>
    public static void PerformPendingReload()
    {
        if (!ReloadPending) return;
        ReloadPending = false;

        if (Project.Current == null) return;
        Runtime.Debug.Log("[ScriptAssemblyManager] Performing deferred assembly reload...");
        PerformReload(Project.Current);
    }

    public static IEnumerable<Type> GetAllTypes()
        => Runtime.AssemblyManager.GetAllTypes();

    private static void PerformReload(Project project)
    {
        // Delegate to HotloadPipeline for enhanced reload with migration support
        HotloadPipeline.ForceFullHotload();
    }

    /// <summary>Invoke OnBeforeAssemblyReload event (used by HotloadPipeline).</summary>
    internal static void InvokeBeforeReload() => OnBeforeAssemblyReload?.Invoke();

    /// <summary>Invoke OnAfterAssemblyReload event (used by HotloadPipeline).</summary>
    internal static void InvokeAfterReload() => OnAfterAssemblyReload?.Invoke();
}
