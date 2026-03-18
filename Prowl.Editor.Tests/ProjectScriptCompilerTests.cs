// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;
using System.Runtime.Loader;

using Prowl.Editor.Project;

using Xunit;

namespace Prowl.Editor.Tests;

/// <summary>
/// Tests for <see cref="ProjectScriptCompiler"/> and
/// <see cref="ProjectAssemblyManager"/> ensuring the full compile → load
/// pipeline works for user scripts that reference Prowl.Runtime types.
/// Each test creates a temporary project directory that is cleaned up
/// automatically via <see cref="IDisposable"/>.
/// </summary>
public sealed class ProjectScriptCompilerTests : IDisposable
{
    private readonly string _projectPath;

    /// <summary>
    /// Directory that contains the test runner's output assemblies
    /// (Prowl.Runtime.dll, Vector.dll, Echo.dll, etc.).
    /// Passed as an extra reference directory so the Roslyn compilation
    /// can find engine types even though they may not be in
    /// <c>AppDomain.CurrentDomain.BaseDirectory</c> at test time.
    /// </summary>
    private readonly string[] _extraRefDirs;

    public ProjectScriptCompilerTests()
    {
        _projectPath = Path.Combine(Path.GetTempPath(), "ProwlTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_projectPath, "Assets", "Scripts"));

        // The test output bin contains all the engine assemblies we need.
        string testBin = AppDomain.CurrentDomain.BaseDirectory;
        _extraRefDirs = [testBin];
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectPath, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void WriteScript(string relativePath, string code)
    {
        string fullPath = Path.Combine(_projectPath, "Assets", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, code);
    }

    // ── Tests ───────────────────────────────────────────────────────

    [Fact]
    public void Compile_NoSourceFiles_ReturnsSuccessWithWarning()
    {
        CompilationResult result = ProjectScriptCompiler.Compile(_projectPath, _extraRefDirs);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Single(result.Warnings);
        Assert.Null(result.OutputAssemblyPath);
    }

    [Fact]
    public void Compile_MissingAssetsFolder_ReturnsFailure()
    {
        string emptyProject = Path.Combine(Path.GetTempPath(), "ProwlEmpty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyProject);

        try
        {
            CompilationResult result = ProjectScriptCompiler.Compile(emptyProject, _extraRefDirs);

            Assert.False(result.Success);
            Assert.NotEmpty(result.Errors);
        }
        finally
        {
            Directory.Delete(emptyProject, recursive: true);
        }
    }

    [Fact]
    public void Compile_SimplePlainClass_Succeeds()
    {
        WriteScript("Scripts/Hello.cs", """
            namespace TestProject;

            public class Hello
            {
                public string Greet() => "Hello, Prowl!";
            }
            """);

        CompilationResult result = ProjectScriptCompiler.Compile(_projectPath, _extraRefDirs);

        Assert.True(result.Success, FormatErrors(result));
        Assert.Empty(result.Errors);
        Assert.NotNull(result.OutputAssemblyPath);
        Assert.True(File.Exists(result.OutputAssemblyPath));
    }

    [Fact]
    public void Compile_MonoBehaviourSubclass_Succeeds()
    {
        WriteScript("Scripts/Rotator.cs", """
            using Prowl.Runtime;

            namespace TestProject;

            public class Rotator : MonoBehaviour
            {
                public float Speed = 1f;

                public override void Update()
                {
                    var euler = GameObject.Transform.LocalEulerAngles;
                    euler.Y += Speed * Time.DeltaTime;
                    GameObject.Transform.LocalEulerAngles = euler;
                }
            }
            """);

        CompilationResult result = ProjectScriptCompiler.Compile(_projectPath, _extraRefDirs);

        Assert.True(result.Success, FormatErrors(result));
        Assert.Empty(result.Errors);
        Assert.NotNull(result.OutputAssemblyPath);
    }

    [Fact]
    public void Compile_SyntaxError_ReturnsFailure()
    {
        WriteScript("Scripts/Bad.cs", """
            namespace TestProject;

            public class Bad
            {
                // deliberate syntax error
                public void Foo(  {  }
            }
            """);

        CompilationResult result = ProjectScriptCompiler.Compile(_projectPath, _extraRefDirs);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Null(result.OutputAssemblyPath);
    }

    [Fact]
    public void Compile_MultipleScripts_AllTypesPresent()
    {
        WriteScript("Scripts/ScriptA.cs", """
            namespace TestProject;

            public class ScriptA
            {
                public int Value => 42;
            }
            """);

        WriteScript("Scripts/ScriptB.cs", """
            namespace TestProject;

            public class ScriptB : ScriptA
            {
                public int DoubleValue => Value * 2;
            }
            """);

        CompilationResult result = ProjectScriptCompiler.Compile(_projectPath, _extraRefDirs);
        Assert.True(result.Success, FormatErrors(result));

        // Load the assembly and verify both types exist
        var ctx = new AssemblyLoadContext("test_multi", isCollectible: true);
        try
        {
            Assembly asm = ctx.LoadFromAssemblyPath(result.OutputAssemblyPath!);
            Type? typeA = asm.GetType("TestProject.ScriptA");
            Type? typeB = asm.GetType("TestProject.ScriptB");
            Assert.NotNull(typeA);
            Assert.NotNull(typeB);
            Assert.True(typeB!.IsSubclassOf(typeA!));
        }
        finally
        {
            ctx.Unload();
        }
    }

    [Fact]
    public void Compile_OutputDllIsWrittenToLibrary()
    {
        WriteScript("Scripts/Dummy.cs", """
            namespace TestProject;
            public class Dummy { }
            """);

        CompilationResult result = ProjectScriptCompiler.Compile(_projectPath, _extraRefDirs);
        Assert.True(result.Success, FormatErrors(result));

        string expectedDir = Path.Combine(_projectPath, "Library", "ScriptAssemblies");
        Assert.True(Directory.Exists(expectedDir));
        Assert.True(File.Exists(Path.Combine(expectedDir, "Assembly-Project.dll")));
        Assert.True(File.Exists(Path.Combine(expectedDir, "Assembly-Project.pdb")));
    }

    [Fact]
    public void Compile_DefaultTemplateScripts_Succeed()
    {
        // The exact scripts the Launcher generates for a new project.
        WriteScript("Scripts/GameController.cs", """
            using Prowl.Runtime;

            namespace TestGame;

            public class GameController : MonoBehaviour
            {
                public float RotationSpeed = 45f;

                public override void Update()
                {
                    var euler = GameObject.Transform.LocalEulerAngles;
                    euler.Y += RotationSpeed * Time.DeltaTime;
                    GameObject.Transform.LocalEulerAngles = euler;
                }
            }
            """);

        WriteScript("Scripts/PlayerController.cs", """
            using Prowl.Runtime;

            namespace TestGame;

            public class PlayerController : MonoBehaviour
            {
                public float MoveSpeed = 5f;

                public override void Update()
                {
                    // placeholder
                }
            }
            """);

        CompilationResult result = ProjectScriptCompiler.Compile(_projectPath, _extraRefDirs);

        Assert.True(result.Success, FormatErrors(result));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Compile_ScriptUsesVectorTypes_Succeeds()
    {
        // Exercises Prowl.Vector types (Float3, Quaternion) which are
        // transitive dependencies and were previously missed.
        WriteScript("Scripts/Mover.cs", """
            using Prowl.Runtime;
            using Prowl.Vector;

            namespace TestProject;

            public class Mover : MonoBehaviour
            {
                public Float3 Direction = Float3.UnitY;
                public float Speed = 2f;

                public override void Update()
                {
                    Transform.LocalPosition += Direction * Speed * Time.DeltaTime;
                }
            }
            """);

        CompilationResult result = ProjectScriptCompiler.Compile(_projectPath, _extraRefDirs);

        Assert.True(result.Success, FormatErrors(result));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void CollectMetadataReferences_IncludesRuntimeAssembly()
    {
        var refs = ProjectScriptCompiler.CollectMetadataReferences(_extraRefDirs);

        // There should be a nontrivial number of references.
        Assert.True(refs.Count > 10, $"Expected many references, got {refs.Count}");

        // Prowl.Runtime must be among them.
        bool hasProwlRuntime = refs.Any(r =>
            r.Display?.Contains("Prowl.Runtime", StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(hasProwlRuntime, "Prowl.Runtime.dll must be in the metadata references.");
    }

    [Fact]
    public void CollectMetadataReferences_IncludesVectorAssembly()
    {
        var refs = ProjectScriptCompiler.CollectMetadataReferences(_extraRefDirs);

        // Vector.dll (Prowl.Vector) must be among them — this was
        // the root cause of the missing-symbol compilation failure.
        bool hasVector = refs.Any(r =>
            r.Display?.Contains("Vector", StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(hasVector, "Vector.dll (Prowl.Vector) must be in the metadata references.");
    }

    [Fact]
    public void CollectMetadataReferences_IncludesSystemRuntime()
    {
        var refs = ProjectScriptCompiler.CollectMetadataReferences(_extraRefDirs);

        bool hasSystemRuntime = refs.Any(r =>
            r.Display?.Contains("System.Runtime", StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(hasSystemRuntime, "System.Runtime.dll must be in the metadata references.");
    }

    private static string FormatErrors(CompilationResult result)
    {
        if (result.Errors.Count == 0)
            return string.Empty;
        return "Compilation errors:\n" + string.Join("\n", result.Errors);
    }
}
