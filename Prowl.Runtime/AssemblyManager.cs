// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Prowl.Runtime;

/// <summary>
/// Centralized, runtime-safe registry for script assemblies.
/// Works in both editor (collectible ALC hot-reload) and player (static load) contexts.
/// <para>
/// The editor registers/unregisters assemblies during hot-reload cycles.
/// The player registers assemblies once at startup.
/// All subsystems that need to enumerate user types should go through this class.
/// </para>
/// </summary>
public static class AssemblyManager
{
    private static readonly List<Assembly> s_scriptAssemblies = [];
    private static readonly object s_lock = new();

    /// <summary>
    /// Fired after the script assembly set changes (register or unregister).
    /// Subscribers should invalidate any cached type metadata.
    /// </summary>
    public static event Action? OnAssembliesChanged;

    /// <summary>
    /// The currently registered script assemblies (user game + editor scripts).
    /// Returns an empty array if none are loaded.
    /// </summary>
    public static Assembly[] ScriptAssemblies
    {
        get
        {
            lock (s_lock)
                return [.. s_scriptAssemblies];
        }
    }

    /// <summary>
    /// Register one or more script assemblies (e.g. after compilation/load).
    /// Automatically registers with <see cref="ProjectAssembly"/> for runtime type resolution
    /// and wires up <see cref="RuntimeUtils.AdditionalAssemblyProvider"/>.
    /// </summary>
    public static void Register(params Assembly[] assemblies)
    {
        if (assemblies.Length == 0) return;

        lock (s_lock)
        {
            foreach (var asm in assemblies)
            {
                if (asm != null && !s_scriptAssemblies.Contains(asm))
                    s_scriptAssemblies.Add(asm);
            }
        }

        EnsureBridgesActive();
        OnAssembliesChanged?.Invoke();
    }

    /// <summary>
    /// Unregister all script assemblies (e.g. before ALC unload).
    /// Clears the <see cref="ProjectAssembly"/> bridge and type caches.
    /// </summary>
    public static void UnregisterAll()
    {
        lock (s_lock)
            s_scriptAssemblies.Clear();

        // Clear the bridge — no script types available during unload window
        ProjectAssembly.Register(null);
        RuntimeUtils.AdditionalAssemblyProvider = null;
        RuntimeUtils.ClearCache();

        OnAssembliesChanged?.Invoke();
    }

    /// <summary>
    /// Returns all assemblies that registries should scan:
    /// default-context assemblies (engine, BCL) plus any loaded script assemblies.
    /// </summary>
    public static IEnumerable<Assembly> GetAllRelevantAssemblies()
    {
        foreach (var asm in AssemblyLoadContext.Default.Assemblies)
            yield return asm;

        Assembly[] scripts;
        lock (s_lock)
            scripts = [.. s_scriptAssemblies];

        foreach (var asm in scripts)
            yield return asm;
    }

    /// <summary>
    /// Enumerates all types from all relevant assemblies (engine + scripts).
    /// Safely handles <see cref="ReflectionTypeLoadException"/>.
    /// </summary>
    public static IEnumerable<Type> GetAllTypes()
    {
        foreach (var assembly in GetAllRelevantAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }
            catch { continue; }

            foreach (var type in types)
                yield return type;
        }
    }

    /// <summary>
    /// Resolve a type by name, searching script assemblies first, then all loaded assemblies.
    /// Handles both assembly-qualified and short type names.
    /// </summary>
    public static Type? FindType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return null;

        // Strip assembly qualification for Assembly.GetType lookups
        string shortName = typeName;
        int commaIdx = typeName.IndexOf(',');
        if (commaIdx > 0)
            shortName = typeName[..commaIdx].Trim();

        // 1) Search script assemblies first (most common for user types)
        Assembly[] scripts;
        lock (s_lock)
            scripts = [.. s_scriptAssemblies];

        foreach (var asm in scripts)
        {
            var t = asm.GetType(shortName, throwOnError: false);
            if (t != null) return t;
        }

        // 2) Try system Type.GetType (handles BCL and assembly-qualified names)
        var result = Type.GetType(typeName);
        if (result != null) return result;

        // 3) Search all loaded assemblies
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(shortName, throwOnError: false);
            if (t != null) return t;
        }

        // 4) Fallback: match by unqualified class name
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray()!; }
            catch { continue; }

            var t = types.FirstOrDefault(x => x.Name.Equals(shortName, StringComparison.OrdinalIgnoreCase));
            if (t != null) return t;
        }

        // Also check script assemblies for unqualified match
        foreach (var asm in scripts)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray()!; }
            catch { continue; }

            var t = types.FirstOrDefault(x => x.Name.Equals(shortName, StringComparison.OrdinalIgnoreCase));
            if (t != null) return t;
        }

        return null;
    }

    /// <summary>
    /// Ensures the runtime bridges are wired up to point at the current script assemblies.
    /// </summary>
    private static void EnsureBridgesActive()
    {
        // Wire up ProjectAssembly bridge for RuntimeUtils.FindType
        ProjectAssembly.Register(ScriptTypeResolver.Instance);

        // Wire up the additional assembly provider
        RuntimeUtils.AdditionalAssemblyProvider = () =>
        {
            lock (s_lock)
                return [.. s_scriptAssemblies];
        };

        // Invalidate stale caches
        RuntimeUtils.ClearCache();
    }

    /// <summary>
    /// Internal <see cref="IProjectTypeResolver"/> that delegates to <see cref="AssemblyManager"/>.
    /// </summary>
    private sealed class ScriptTypeResolver : IProjectTypeResolver
    {
        public static readonly ScriptTypeResolver Instance = new();

        public Assembly? LoadedAssembly
        {
            get
            {
                lock (s_lock)
                    return s_scriptAssemblies.Count > 0 ? s_scriptAssemblies[0] : null;
            }
        }

        public Type? GetType(string typeName) => AssemblyManager.FindType(typeName);
    }
}
