// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;
using System.Runtime.Loader;

using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Prowl.Editor.Project;

/// <summary>
/// Manages the lifecycle of the user-script assembly compiled from the
/// project's C# sources. Uses a collectible <see cref="AssemblyLoadContext"/>
/// so that assemblies can be unloaded and reloaded when scripts change,
/// similar to Unity's domain-reload workflow.
/// <para>
/// After calling <see cref="CompileAndLoad"/>, every <c>MonoBehaviour</c>
/// subclass defined in the project's scripts will be discoverable via
/// <c>AppDomain.CurrentDomain.GetAssemblies()</c> and will appear in the
/// editor's "Add Component" list.
/// </para>
/// </summary>
public sealed class ProjectAssemblyManager : IDisposable, IProjectTypeResolver
{
    private readonly string _projectPath;
    private ScriptLoadContext? _loadContext;
    private FileSystemWatcher? _watcher;
    private bool _recompileRequested;
    private Assembly? _loadedAssembly;

    /// <summary>
    /// Debounce delay after the last file-system change before a recompile
    /// is allowed to proceed. This gives external editors time to finish
    /// writing / releasing file locks.
    /// </summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(1);
    private DateTime _lastChangeTime;

    // ── Polling fallback ─────────────────────────────────────
    // FileSystemWatcher is unreliable on some platforms (e.g. Linux
    // with certain filesystems, NFS mounts). We periodically poll
    // for changes as a safety net.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private DateTime _lastPollTime;
    private Dictionary<string, DateTime>? _knownFileTimestamps;

    /// <summary>
    /// The currently loaded user-script assembly, or <c>null</c> if none is loaded.
    /// </summary>
    public Assembly? LoadedAssembly => _loadedAssembly;

    /// <summary>
    /// Whether a recompilation has been requested and is pending.
    /// </summary>
    public bool RecompilePending => _recompileRequested;

    /// <summary>
    /// Whether a compilation is currently in progress.
    /// </summary>
    public bool IsCompiling { get; private set; }

    /// <summary>
    /// The result of the most recent compilation attempt, or <c>null</c> if
    /// no compilation has been performed yet.
    /// </summary>
    public CompilationResult? LastCompilationResult { get; private set; }

    public ProjectAssemblyManager(string projectPath)
    {
        _projectPath = projectPath;
    }

    /// <summary>
    /// Compiles the project scripts and loads the resulting assembly.
    /// If an assembly was previously loaded, it is unloaded first.
    /// </summary>
    /// <returns>The compilation result with diagnostics.</returns>
    public CompilationResult CompileAndLoad()
    {
        _recompileRequested = false;
        IsCompiling = true;

        // Unload any previously loaded assembly
        Unload();

        // Clear Echo's type-resolution caches so that stale entries
        // (including types from the old assembly or null entries cached
        // before the assembly was loaded) are purged before we load
        // the new assembly.
        InvalidateTypeCaches();

        Debug.Log("[Scripts] Compiling project scripts...");
        CompilationResult result = ProjectScriptCompiler.Compile(_projectPath);

        // Log diagnostics
        foreach (string warning in result.Warnings)
            Debug.LogWarning($"[Scripts] {warning}");
        foreach (string error in result.Errors)
            Debug.LogError($"[Scripts] {error}");


        if (result.Success && result.OutputAssemblyPath != null)
        {
            // Load into a new collectible context from a byte stream
            // so the file is not locked and can be overwritten on
            // subsequent recompilations.
            _loadContext = new ScriptLoadContext();
            try
            {
                byte[] asmBytes = File.ReadAllBytes(result.OutputAssemblyPath);
                string pdbPath = Path.ChangeExtension(result.OutputAssemblyPath, ".pdb");
                byte[]? pdbBytes = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;

                using var asmStream = new MemoryStream(asmBytes);
                using var pdbStream = pdbBytes != null ? new MemoryStream(pdbBytes) : null;
                _loadedAssembly = _loadContext.LoadFromStream(asmStream, pdbStream);

                // Bridge assembly resolution so that Type.GetType(assemblyQualifiedName)
                // can find types in the collectible ALC via the default context.
                AssemblyLoadContext.Default.Resolving += Default_Resolving;

                int typeCount = 0;
                try { typeCount = _loadedAssembly.GetTypes().Length; }
                catch (ReflectionTypeLoadException ex) { typeCount = ex.Types.Count(t => t != null); }

                Debug.LogSuccess($"[Scripts] Compilation succeeded — loaded {typeCount} type(s) from {ProjectScriptCompiler.AssemblyName}.dll");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Scripts] Failed to load compiled assembly: {ex.Message}");
                _loadContext.Unload();
                _loadContext = null;
                _loadedAssembly = null;
            }
        }
        else if (!result.Success)
        {
            Debug.LogError($"[Scripts] Compilation failed with {result.Errors.Count} error(s).");
        }


        ProjectAssembly.Register(this);

        // Now that the new assembly is registered, clear caches again
        // so that any lookups performed during the Unload() phase
        // (which may have cached null for user types) are purged.
        InvalidateTypeCaches();

        LastCompilationResult = result;
        IsCompiling = false;

        // Refresh the polling snapshot so we don't immediately
        // detect our own compilation as a change.
        SnapshotFileTimestamps();

        EditorApplication.EditorEventManager.InvokeEvent(Editor.Core.EditorEvents.OnAssemblyChanged);
        return result;
    }

    private Assembly? Default_Resolving(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        // Forward assembly resolution requests to the collectible ALC so that
        // Type.GetType(assemblyQualifiedName) and Echo's TypeNameRegistry
        // can find user-script types without needing them in the default ALC.
        if (_loadContext == null) return null;
        return _loadContext.Assemblies
            .FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
    }

    /// <summary>
    /// Unloads the current script assembly (if any) by unloading its
    /// <see cref="AssemblyLoadContext"/>.
    /// </summary>
    public void Unload()
    {
        ProjectAssembly.Register(null);
        if (_loadContext != null)
        {
            _loadedAssembly = null;
            AssemblyLoadContext.Default.Resolving -= Default_Resolving;
            _loadContext.Unload();
            _loadContext = null;

            // Purge cached type metadata that referenced the now-unloaded
            // assembly to prevent stale Type handles from lingering.
            InvalidateTypeCaches();

            Debug.Log("[Scripts] Previous script assembly unloaded.");
        }
    }

    public global::System.Type? GetType(string typeNameOrAssemblyQualified)
    {
        if (_loadedAssembly == null)
            return global::System.Type.GetType(typeNameOrAssemblyQualified);

        // Fast path for user types (most common case)
        var shortName = typeNameOrAssemblyQualified.Split(',')[0].Trim();
        var t = _loadedAssembly.GetType(shortName, throwOnError: false);
        if (t != null) return t;

        // Fallback for BCL/system types
        return global::System.Type.GetType(typeNameOrAssemblyQualified);
    }

    /// <summary>
    /// Starts watching the project's Assets folder for <c>.cs</c> file changes.
    /// When a change is detected a recompile is flagged; call
    /// <see cref="ProcessPendingRecompile"/> from the main loop to execute it.
    /// </summary>
    public void StartWatching()
    {
        StopWatching();

        string assetsDir = Path.Combine(_projectPath, "Assets");
        if (!Directory.Exists(assetsDir))
            return;

        _watcher = new FileSystemWatcher(assetsDir, "*.cs")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnSourceChanged;
        _watcher.Created += OnSourceChanged;
        _watcher.Deleted += OnSourceChanged;
        _watcher.Renamed += (_, _) => { _recompileRequested = true; _lastChangeTime = DateTime.UtcNow; };

        // Take an initial snapshot for the polling fallback
        SnapshotFileTimestamps();
        _lastPollTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Stops watching for file changes.
    /// </summary>
    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    /// <summary>
    /// If a recompilation was flagged (by the file watcher, the polling
    /// fallback, or manually), performs the compile-and-load cycle. Call
    /// this once per frame from the editor's update loop.
    /// <para>
    /// A debounce delay is applied: the recompile only proceeds once
    /// <see cref="DebounceDelay"/> has elapsed since the last file-system
    /// change, giving external editors time to release file locks.
    /// </para>
    /// <para>
    /// On platforms where <see cref="FileSystemWatcher"/> is unreliable
    /// (e.g. Linux with certain filesystems), a periodic polling pass
    /// detects changes that the watcher may have missed.
    /// </para>
    /// </summary>
    public void ProcessPendingRecompile()
    {
        // ── Polling fallback ───────────────────────────────────
        // If the watcher is active but hasn't flagged a recompile,
        // periodically check for file changes we may have missed.
        if (!_recompileRequested && _watcher != null)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastPollTime >= PollInterval)
            {
                _lastPollTime = now;
                if (PollForChanges())
                {
                    _recompileRequested = true;
                    _lastChangeTime = now;
                    Debug.Log("[Scripts] Polling detected source changes missed by the file watcher.");
                }
            }
        }

        if (!_recompileRequested)
            return;

        // Wait until the debounce period has elapsed since the last change.
        if (DateTime.UtcNow - _lastChangeTime < DebounceDelay)
            return;

        try
        {
            CompileAndLoad();
        }
        catch (Exception ex)
        {
            _recompileRequested = false;
            IsCompiling = false;
            Debug.LogError($"[Scripts] Recompilation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Manually requests a recompile on the next <see cref="ProcessPendingRecompile"/> call.
    /// </summary>
    public void RequestRecompile()
    {
        _recompileRequested = true;
    }

    public void Dispose()
    {
        StopWatching();
        Unload();
    }

    /// <summary>
    /// Clears all cached type-resolution data in Echo, the runtime utilities,
    /// and the project assembly bridge. This must be called whenever the user
    /// script assembly is loaded, unloaded, or reloaded to prevent:
    /// <list type="bullet">
    ///   <item>Stale <c>null</c> entries cached before the assembly was available.</item>
    ///   <item>Dangling <see cref="Type"/> handles from an unloaded assembly.</item>
    ///   <item>Serialization format caches that reference old field layouts.</item>
    /// </list>
    /// </summary>
    private static void InvalidateTypeCaches()
    {
        Serializer.ClearCache();
        RuntimeUtils.ClearCache();
    }

    private void OnSourceChanged(object sender, FileSystemEventArgs e)
    {
        _recompileRequested = true;
        _lastChangeTime = DateTime.UtcNow;
    }

    // ── Polling helpers ──────────────────────────────────────────

    /// <summary>
    /// Captures a snapshot of all <c>.cs</c> files and their last-write
    /// timestamps under the project's Assets folder. Used by the polling
    /// fallback to detect changes that <see cref="FileSystemWatcher"/> missed.
    /// </summary>
    private void SnapshotFileTimestamps()
    {
        string assetsDir = Path.Combine(_projectPath, "Assets");
        if (!Directory.Exists(assetsDir))
        {
            _knownFileTimestamps = null;
            return;
        }

        string[] files = Directory.GetFiles(assetsDir, "*.cs", SearchOption.AllDirectories);
        var snapshot = new Dictionary<string, DateTime>(files.Length, StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            try { snapshot[file] = File.GetLastWriteTimeUtc(file); }
            catch { /* file may have been deleted between enumeration and read */ }
        }
        _knownFileTimestamps = snapshot;
    }

    /// <summary>
    /// Compares the current <c>.cs</c> files against the last snapshot.
    /// Returns <c>true</c> if any file was added, removed, or modified.
    /// </summary>
    private bool PollForChanges()
    {
        string assetsDir = Path.Combine(_projectPath, "Assets");
        if (!Directory.Exists(assetsDir))
            return _knownFileTimestamps != null && _knownFileTimestamps.Count > 0;

        string[] currentFiles;
        try { currentFiles = Directory.GetFiles(assetsDir, "*.cs", SearchOption.AllDirectories); }
        catch { return false; }

        if (_knownFileTimestamps == null)
        {
            // No previous snapshot — treat any files as a change.
            return currentFiles.Length > 0;
        }

        // Check for new or modified files.
        foreach (string file in currentFiles)
        {
            DateTime writeTime;
            try { writeTime = File.GetLastWriteTimeUtc(file); }
            catch { continue; }

            if (!_knownFileTimestamps.TryGetValue(file, out DateTime known) || writeTime != known)
                return true;
        }

        // Check for deleted files.
        if (currentFiles.Length != _knownFileTimestamps.Count)
            return true;

        return false;
    }

    /// <summary>
    /// A collectible <see cref="AssemblyLoadContext"/> that falls back to the
    /// default context for all assemblies it doesn't own (e.g. Prowl.Runtime,
    /// BCL). This ensures that user scripts share the same type universe as
    /// the editor.
    /// </summary>
    private sealed class ScriptLoadContext : AssemblyLoadContext
    {
        public ScriptLoadContext()
            : base(isCollectible: true)
        {
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Fall back to the default context (Prowl.Runtime, BCL, etc.)
            return null;
        }
    }
}
