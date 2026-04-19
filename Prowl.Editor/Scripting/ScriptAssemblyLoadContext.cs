// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace Prowl.Editor.Scripting;

/// <summary>
/// A collectible AssemblyLoadContext for user script assemblies.
/// Resolves dependencies against the default context first (Prowl.Runtime, etc.),
/// falling back to the script assembly directory.
/// Supports cooperative unloading via the <see cref="UnloadGracefully"/> method
/// and tracks live object references for hotload diagnostics.
/// </summary>
public class ScriptAssemblyLoadContext : AssemblyLoadContext
{
    private readonly string _assemblyDir;
    private readonly List<WeakReference> _trackedObjects = [];

    /// <summary>True once <see cref="UnloadGracefully"/> or <see cref="Unload"/> has been called.</summary>
    public bool IsUnloading { get; private set; }

    /// <summary>Number of tracked objects still alive.</summary>
    public int LiveObjectCount
    {
        get
        {
            int count = 0;
            foreach (var wr in _trackedObjects)
            {
                if (wr.IsAlive) count++;
            }
            return count;
        }
    }

    public ScriptAssemblyLoadContext(string assemblyDir)
        : base("ProwlScripts", isCollectible: true)
    {
        _assemblyDir = assemblyDir;
        Unloading += OnUnloading;

        // Bridge assembly resolution so that Type.GetType(assemblyQualifiedName) in the
        // default context can find types defined in this collectible ALC.
        Default.Resolving += OnDefaultContextResolving;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Try default context first (engine assemblies, BCL, NuGet)
        try
        {
            return Default.LoadFromAssemblyName(assemblyName);
        }
        catch { }

        // Try script assembly directory (e.g. NuGet packages copied there)
        string path = Path.Combine(_assemblyDir, assemblyName.Name + ".dll");
        if (File.Exists(path))
            return LoadFromAssemblyPath(path);

        return null;
    }

    /// <summary>
    /// Resolves assembly requests from the default context by forwarding to this ALC.
    /// This allows <c>Type.GetType(assemblyQualifiedName)</c> and serialization frameworks
    /// to find user script types without them being in the default context.
    /// </summary>
    private Assembly? OnDefaultContextResolving(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (IsUnloading) return null;

        return Assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
    }

    /// <summary>
    /// Track a live object created from this ALC for diagnostics.
    /// Used by the migration engine to verify all references are released.
    /// </summary>
    public void TrackObject(object obj)
    {
        _trackedObjects.Add(new WeakReference(obj));
    }

    /// <summary>
    /// Perform a cooperative unload: fires the <see cref="AssemblyLoadContext.Unloading"/> event
    /// to notify subscribers, then triggers the actual unload.
    /// </summary>
    public void UnloadGracefully()
    {
        if (IsUnloading) return;
        IsUnloading = true;

        // Remove the default-context resolving bridge before unload
        Default.Resolving -= OnDefaultContextResolving;

        // Prune dead references before reporting
        _trackedObjects.RemoveAll(wr => !wr.IsAlive);

        int liveCount = LiveObjectCount;
        if (liveCount > 0)
        {
            HotloadLogger.LogDetail($"ALC has {liveCount} tracked live object(s) before unload.");
        }

        Unload();
    }

    /// <summary>
    /// Get diagnostic info about objects still referenced from this context.
    /// </summary>
    public string GetDiagnostics()
    {
        _trackedObjects.RemoveAll(wr => !wr.IsAlive);

        var sb = new StringBuilder();
        sb.AppendLine($"ScriptAssemblyLoadContext '{Name}': {_trackedObjects.Count} tracked reference(s)");

        foreach (var wr in _trackedObjects)
        {
            if (wr.Target is object obj)
                sb.AppendLine($"  - {obj.GetType().FullName}");
        }

        return sb.ToString();
    }

    private void OnUnloading(AssemblyLoadContext context)
    {
        IsUnloading = true;
        Default.Resolving -= OnDefaultContextResolving;
        HotloadLogger.LogTrace($"ALC '{Name}' is unloading...");
    }
}
