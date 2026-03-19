// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Prowl.Runtime
{
    /// <summary>
    /// Runtime-safe bridge to the editor's project script assembly.
    /// Implemented only in the editor; runtime never sees the editor assembly.
    /// </summary>
    public interface IProjectTypeResolver
    {
        Assembly? LoadedAssembly { get; }
        global::System.Type? GetType(string typeName);   // ← the method we already discussed
    }

    /// <summary>
    /// Static accessor — zero allocation after first use, thread-safe.
    /// </summary>
    public static class ProjectAssembly
    {
        private static IProjectTypeResolver? _resolver;

        /// <summary>Editor calls this once at startup (and after every recompile).</summary>
        public static void Register(IProjectTypeResolver? resolver)
        {
            _resolver = resolver;
            // Optional: clear any internal caches here if you add them
        }

        public static IProjectTypeResolver? Instance => _resolver;

        /// <summary>Public API — used by your shadowed Type.GetType and everywhere else.</summary>
        public static global::System.Type? GetType(string typeName)
        {
            return _resolver?.GetType(typeName) ?? global::System.Type.GetType(typeName);
        }
    }
}
