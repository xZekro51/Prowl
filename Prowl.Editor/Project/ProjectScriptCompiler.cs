// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;
using System.Runtime.Loader;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

using Prowl.Runtime;

namespace Prowl.Editor.Project;

/// <summary>
/// Result of a script compilation attempt.
/// </summary>
public sealed class CompilationResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? OutputAssemblyPath { get; init; }
}

/// <summary>
/// Compiles all C# source files found under a project's Assets folder into a
/// single assembly (<c>Assembly-Project.dll</c>) using the Roslyn compiler API.
/// The resulting assembly references <c>Prowl.Runtime</c> and the standard
/// .NET BCL, allowing user scripts to subclass <see cref="MonoBehaviour"/>
/// and other engine types.
/// </summary>
public static class ProjectScriptCompiler
{
    /// <summary>
    /// The name of the compiled user-script assembly (without extension).
    /// </summary>
    public const string AssemblyName = "Assembly-Project";

    /// <summary>
    /// Compiles every <c>.cs</c> file found recursively under
    /// <paramref name="projectPath"/>/Assets into a single assembly written to
    /// <paramref name="projectPath"/>/Library/ScriptAssemblies/<see cref="AssemblyName"/>.dll.
    /// </summary>
    /// <param name="projectPath">Root folder of the Prowl project.</param>
    /// <param name="extraReferenceDirectories">
    /// Optional extra directories whose <c>.dll</c> files are added as
    /// metadata references. Useful in tests to point at the test output
    /// directory where engine assemblies live.
    /// </param>
    public static CompilationResult Compile(
        string projectPath,
        IEnumerable<string>? extraReferenceDirectories = null)
    {
        string assetsDir = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(assetsDir))
        {
            return new CompilationResult
            {
                Success = false,
                Errors = [$"Assets folder not found: {assetsDir}"],
            };
        }

        // ── Gather source files ────────────────────────────────
        string[] sourceFiles = Directory.GetFiles(assetsDir, "*.cs", SearchOption.AllDirectories);
        if (sourceFiles.Length == 0)
        {
            return new CompilationResult
            {
                Success = true,
                Warnings = ["No C# source files found under Assets/."],
            };
        }

        // ── Parse syntax trees ─────────────────────────────────
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp13);
        var syntaxTrees = new List<SyntaxTree>(sourceFiles.Length);
        foreach (string file in sourceFiles)
        {
            string code = ReadFileWithRetry(file);
            var sourceText = SourceText.From(code, Encoding.UTF8);
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(sourceText, parseOptions, path: file));
        }

        // ── Collect metadata references ────────────────────────
        var references = CollectMetadataReferences(extraReferenceDirectories);

        // ── Configure compilation ──────────────────────────────
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Debug,
            allowUnsafe: true);

        var compilation = CSharpCompilation.Create(
            AssemblyName,
            syntaxTrees,
            references,
            compilationOptions);

        // ── Emit ───────────────────────────────────────────────
        string outDir = Path.Combine(projectPath, "Library", "ScriptAssemblies");
        Directory.CreateDirectory(outDir);
        string dllPath = Path.Combine(outDir, AssemblyName + ".dll");
        string pdbPath = Path.Combine(outDir, AssemblyName + ".pdb");

        EmitResult emitResult = compilation.Emit(dllPath, pdbPath);

        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (Diagnostic diag in emitResult.Diagnostics)
        {
            string message = diag.ToString();
            if (diag.Severity == DiagnosticSeverity.Error)
                errors.Add(message);
            else if (diag.Severity == DiagnosticSeverity.Warning)
                warnings.Add(message);
        }

        if (!emitResult.Success)
        {
            // Clean up failed output
            try { if (File.Exists(dllPath)) File.Delete(dllPath); } catch { }
            try { if (File.Exists(pdbPath)) File.Delete(pdbPath); } catch { }
        }

        return new CompilationResult
        {
            Success = emitResult.Success,
            Errors = errors,
            Warnings = warnings,
            OutputAssemblyPath = emitResult.Success ? dllPath : null,
        };
    }

    /// <summary>
    /// Builds a complete list of <see cref="MetadataReference"/>s that user
    /// scripts may need. References are gathered from three sources:
    /// <list type="number">
    ///   <item>All assemblies currently loaded in the AppDomain.</item>
    ///   <item>All <c>.dll</c> files in the application's base directory —
    ///         this catches engine and NuGet dependency assemblies
    ///         (e.g. <c>Vector.dll</c>, <c>Echo.dll</c>) that the .NET
    ///         runtime hasn't loaded yet due to lazy loading.</item>
    ///   <item>All <c>.dll</c> files in the .NET shared framework directory
    ///         — this provides complete BCL coverage without a fragile
    ///         hard-coded list.</item>
    /// </list>
    /// </summary>
    internal static List<MetadataReference> CollectMetadataReferences(
        IEnumerable<string>? extraDirectories = null)
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Assemblies already loaded in the current process.
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic)
                continue;

            string? location = null;
            try { location = asm.Location; }
            catch { continue; }

            if (string.IsNullOrEmpty(location) || !File.Exists(location))
                continue;

            if (!seen.Add(location))
                continue;

            refs.Add(MetadataReference.CreateFromFile(location));
        }

        // 2. Application base directory — engine + NuGet assemblies that may
        //    not be loaded yet (lazy loading). This is where Prowl.Runtime.dll,
        //    Vector.dll, Echo.dll, Paper.dll, Silk.NET.*.dll etc. reside.
        AddDllsFromDirectory(AppDomain.CurrentDomain.BaseDirectory, refs, seen);

        // 3. .NET shared framework directory — complete BCL coverage.
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        AddDllsFromDirectory(runtimeDir, refs, seen);

        // 4. Any extra directories provided by the caller (e.g. test harness).
        if (extraDirectories != null)
        {
            foreach (string dir in extraDirectories)
                AddDllsFromDirectory(dir, refs, seen);
        }

        return refs;
    }

    private static void AddDllsFromDirectory(
        string directory,
        List<MetadataReference> refs,
        HashSet<string> seen)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (string dll in Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (!seen.Add(dll))
                continue;

            // Skip native / unmanaged DLLs (coreclr.dll, clrjit.dll, etc.).
            // AssemblyName.GetAssemblyName throws for non-managed PE images.
            if (!IsManagedAssembly(dll))
            {
                seen.Remove(dll); // allow retry from another directory
                continue;
            }

            refs.Add(MetadataReference.CreateFromFile(dll));
        }
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            System.Reflection.AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a file's text content, retrying a few times with a short delay
    /// when the file is locked by another process (e.g. an external editor
    /// that is still flushing/saving).
    /// </summary>
    private static string ReadFileWithRetry(string path, int maxRetries = 5, int delayMs = 200)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < maxRetries)
            {
                Thread.Sleep(delayMs);
            }
        }
    }
}
