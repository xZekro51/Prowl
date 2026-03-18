// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;

using Prowl.Editor.Project;
using Prowl.Runtime;

using Xunit;

namespace Prowl.Editor.Tests;

/// <summary>
/// Tests for <see cref="ProjectAssemblyManager"/> covering the full
/// compile → load → unload → reload lifecycle.
/// </summary>
public sealed class ProjectAssemblyManagerTests : IDisposable
{
    private readonly string _projectPath;

    public ProjectAssemblyManagerTests()
    {
        _projectPath = Path.Combine(Path.GetTempPath(), "ProwlMgrTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_projectPath, "Assets", "Scripts"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectPath, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private void WriteScript(string relativePath, string code)
    {
        string fullPath = Path.Combine(_projectPath, "Assets", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, code);
    }

    [Fact]
    public void CompileAndLoad_ValidScript_LoadsAssembly()
    {
        WriteScript("Scripts/Foo.cs", """
            namespace TestProject;
            public class Foo { public int Bar() => 7; }
            """);

        using var mgr = new ProjectAssemblyManager(_projectPath);
        CompilationResult result = mgr.CompileAndLoad();

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.NotNull(mgr.LoadedAssembly);

        Type? fooType = mgr.LoadedAssembly!.GetType("TestProject.Foo");
        Assert.NotNull(fooType);
    }

    [Fact]
    public void CompileAndLoad_Failure_LeavesAssemblyNull()
    {
        WriteScript("Scripts/Bad.cs", """
            namespace TestProject;
            public class Bad { INVALID }
            """);

        using var mgr = new ProjectAssemblyManager(_projectPath);
        CompilationResult result = mgr.CompileAndLoad();

        Assert.False(result.Success);
        Assert.Null(mgr.LoadedAssembly);
    }

    [Fact]
    public void CompileAndLoad_RaisesAssemblyChangedEvent()
    {
        WriteScript("Scripts/Trigger.cs", """
            namespace TestProject;
            public class Trigger { }
            """);

        using var mgr = new ProjectAssemblyManager(_projectPath);
        int callCount = 0;
        mgr.OnAssemblyChanged += () => callCount++;

        mgr.CompileAndLoad();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void CompileAndLoad_Twice_UnloadsPreviousAssembly()
    {
        WriteScript("Scripts/V1.cs", """
            namespace TestProject;
            public class Versioned { public int Version => 1; }
            """);

        using var mgr = new ProjectAssemblyManager(_projectPath);
        mgr.CompileAndLoad();
        Assembly? first = mgr.LoadedAssembly;
        Assert.NotNull(first);

        // Change the source and recompile.
        WriteScript("Scripts/V1.cs", """
            namespace TestProject;
            public class Versioned { public int Version => 2; }
            """);

        mgr.CompileAndLoad();
        Assembly? second = mgr.LoadedAssembly;
        Assert.NotNull(second);

        // Should be a different assembly instance.
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Unload_ClearsLoadedAssembly()
    {
        WriteScript("Scripts/Temp.cs", """
            namespace TestProject;
            public class Temp { }
            """);

        using var mgr = new ProjectAssemblyManager(_projectPath);
        mgr.CompileAndLoad();
        Assert.NotNull(mgr.LoadedAssembly);

        mgr.Unload();
        Assert.Null(mgr.LoadedAssembly);
    }

    [Fact]
    public void RequestRecompile_FlagsRecompilePending()
    {
        using var mgr = new ProjectAssemblyManager(_projectPath);

        Assert.False(mgr.RecompilePending);
        mgr.RequestRecompile();
        Assert.True(mgr.RecompilePending);
    }

    [Fact]
    public void ProcessPendingRecompile_OnlyCompilesWhenRequested()
    {
        WriteScript("Scripts/Stub.cs", """
            namespace TestProject;
            public class Stub { }
            """);

        using var mgr = new ProjectAssemblyManager(_projectPath);
        int callCount = 0;
        mgr.OnAssemblyChanged += () => callCount++;

        // No pending request — should not compile.
        mgr.ProcessPendingRecompile();
        Assert.Equal(0, callCount);

        // After requesting — should compile.
        mgr.RequestRecompile();
        mgr.ProcessPendingRecompile();
        Assert.Equal(1, callCount);
        Assert.False(mgr.RecompilePending);
    }

    [Fact]
    public void CompileAndLoad_NoScripts_SucceedsWithNoAssembly()
    {
        // Empty project — no scripts at all.
        using var mgr = new ProjectAssemblyManager(_projectPath);
        CompilationResult result = mgr.CompileAndLoad();

        Assert.True(result.Success);
        Assert.Null(mgr.LoadedAssembly);
    }

    [Fact]
    public void CompileAndLoad_MonoBehaviourSubclass_IsDiscoverableInAppDomain()
    {
        WriteScript("Scripts/MyComponent.cs", """
            using Prowl.Runtime;

            namespace TestProject;

            public class MyComponent : MonoBehaviour
            {
                public override void Update() { }
            }
            """);

        using var mgr = new ProjectAssemblyManager(_projectPath);
        CompilationResult result = mgr.CompileAndLoad();
        Assert.True(result.Success, string.Join("\n", result.Errors));

        // The loaded assembly should be discoverable in the AppDomain.
        bool found = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
            .Any(t => t.FullName == "TestProject.MyComponent");

        Assert.True(found, "MyComponent should be discoverable via AppDomain.GetAssemblies().");
    }
}
