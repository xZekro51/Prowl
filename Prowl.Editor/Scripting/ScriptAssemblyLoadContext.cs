// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace Prowl.Editor.Scripting;

/// <summary>
/// A collectible AssemblyLoadContext for user script assemblies.
/// Resolves dependencies against the default context first (Prowl.Runtime, etc.),
/// falling back to the script assembly directory.
/// </summary>
public class ScriptAssemblyLoadContext : AssemblyLoadContext
{
    private readonly string _assemblyDir;

    public ScriptAssemblyLoadContext(string assemblyDir)
        : base("ProwlScripts", isCollectible: true)
    {
        _assemblyDir = assemblyDir;
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


}
