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

    /// <summary>
    /// Raised after a new script assembly has been successfully loaded (or
    /// after the previous one was unloaded due to compilation failure).
    /// Listeners should invalidate any cached type lists.
    /// </summary>
    public event Action? OnAssemblyChanged;

    /// <summary>
    /// The currently loaded user-script assembly, or <c>null</c> if none is loaded.
    /// </summary>
    public Assembly? LoadedAssembly => _loadedAssembly;

    /// <summary>
    /// Whether a recompilation has been requested and is pending.
    /// </summary>
    public bool RecompilePending => _recompileRequested;

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

        OnAssemblyChanged?.Invoke();
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
    /// If a recompilation was flagged (by the file watcher or manually),
    /// performs the compile-and-load cycle. Call this once per frame from
    /// the editor's update loop.
    /// <para>
    /// A debounce delay is applied: the recompile only proceeds once
    /// <see cref="DebounceDelay"/> has elapsed since the last file-system
    /// change, giving external editors time to release file locks.
    /// </para>
    /// </summary>
    public void ProcessPendingRecompile()
    {
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
