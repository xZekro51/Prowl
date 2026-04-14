using BenchmarkDotNet.Attributes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Prowl.Editor.Scripting;
using Microsoft.VSDiagnostics;

[CPUUsageDiagnoser]
public class AlcLoadUnloadBenchmark
{
    private string _tempDir = null!;
    private string _dllPath = null!;
    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AlcBenchmark_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dllPath = CreateMinimalAssembly(_tempDir, "BenchTestAssembly");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Measures a single load, invoke, unload, verify-collected cycle.
    /// Returns true when the ALC is properly garbage-collected.
    /// </summary>
    [Benchmark(Description = "Full ALC load/unload cycle with GC verification")]
    public bool FullLoadUnloadCycle()
    {
        var weakRef = LoadUseAndUnload(_dllPath, _tempDir);
        for (int i = 0; i < 5; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return !weakRef.IsAlive;
    }

    /// <summary>
    /// Measures 10 consecutive load/unload cycles to detect cumulative leaks.
    /// Returns the number of ALCs that were successfully collected.
    /// </summary>
    [Benchmark(Description = "10 consecutive ALC load/unload cycles")]
    public int RepeatedLoadUnloadCycles()
    {
        const int cycles = 10;
        var weakRefs = new WeakReference[cycles];
        for (int i = 0; i < cycles; i++)
        {
            weakRefs[i] = LoadUseAndUnload(_dllPath, _tempDir);
        }

        for (int i = 0; i < 10; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        int collected = 0;
        for (int i = 0; i < cycles; i++)
        {
            if (!weakRefs[i].IsAlive)
                collected++;
        }

        return collected;
    }

    /// <summary>
    /// Loads an assembly into a fresh ScriptAssemblyLoadContext, invokes a method,
    /// then unloads. Marked NoInlining so reflection locals are released from the stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadUseAndUnload(string dllPath, string assemblyDir)
    {
        var alc = new ScriptAssemblyLoadContext(assemblyDir);
        var asm = alc.LoadFromAssemblyPath(dllPath);
        // Exercise the loaded assembly to ensure it is genuinely loaded
        var type = asm.GetType("TestClass")!;
        var method = type.GetMethod("GetValue")!;
        _ = (int)method.Invoke(null, null)!;
        alc.Unload();
        var weakRef = new WeakReference(alc);
        return weakRef;
    }

    /// <summary>
    /// Creates a minimal .NET assembly on disk with a single static method
    /// using PersistedAssemblyBuilder (.NET 9+).
    /// </summary>
    private static string CreateMinimalAssembly(string dir, string name)
    {
        var source = @"
public class TestClass
{
    public static int GetValue() => 42;
}
";
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };

        // Add System.Runtime reference (required for .NET Core/5+)
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var systemRuntimePath = Path.Combine(runtimeDir, "System.Runtime.dll");
        if (File.Exists(systemRuntimePath))
            references.Add(MetadataReference.CreateFromFile(systemRuntimePath));

        var compilation = CSharpCompilation.Create(
            name,
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var dllPath = Path.Combine(dir, name + ".dll");
        var result = compilation.Emit(dllPath);
        if (!result.Success)
        {
            var errors = string.Join(Environment.NewLine,
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException($"Failed to compile test assembly:\n{errors}");
        }
        return dllPath;
    }
}
