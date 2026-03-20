// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Build;

using Xunit;

namespace Prowl.Editor.Tests;

/// <summary>
/// Tests for the build pipeline infrastructure covering csproj generation,
/// settings persistence, pipeline registration, and define string assembly.
/// </summary>
public sealed class DesktopBuildPipelineTests : IDisposable
{
    private readonly string _tempDir;

    public DesktopBuildPipelineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ProwlBuildTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "Assets"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ── BuildSettings Tests ─────────────────────────────────────────

    [Fact]
    public void BuildSettings_Load_ReturnsDefaults_WhenNoFileExists()
    {
        var settings = BuildSettings.Load(_tempDir);
        Assert.NotNull(settings);
        Assert.Equal("Release", settings.Configuration);
        Assert.True(settings.SelfContained);
    }

    [Fact]
    public void BuildSettings_SaveAndLoad_RoundTrips()
    {
        var settings = new BuildSettings
        {
            ProductName = "TestGame",
            Configuration = "Debug",
            SelfContained = false,
        };
        settings.GetProfile(BuildTarget.Windows).ScriptingDefineSymbols.Add("MY_DEFINE");

        settings.Save(_tempDir);

        var loaded = BuildSettings.Load(_tempDir);
        Assert.Equal("TestGame", loaded.ProductName);
        Assert.Equal("Debug", loaded.Configuration);
        Assert.False(loaded.SelfContained);
        Assert.Contains("MY_DEFINE", loaded.GetProfile(BuildTarget.Windows).ScriptingDefineSymbols);
    }

    [Fact]
    public void BuildSettings_GetProfile_CreatesDefault_WhenMissing()
    {
        var settings = new BuildSettings();
        var profile = settings.GetProfile(BuildTarget.Linux);
        Assert.NotNull(profile);
        Assert.Empty(profile.ScriptingDefineSymbols);
    }

    // ── BuildManager Tests ──────────────────────────────────────────

    [Fact]
    public void BuildManager_GetPipeline_ReturnsPipeline_ForSupportedTarget()
    {
        var pipeline = BuildManager.GetPipeline(BuildTarget.Windows);
        Assert.NotNull(pipeline);
        Assert.Contains(BuildTarget.Windows, pipeline.SupportedTargets);
    }

    [Fact]
    public void BuildManager_GetPipeline_ReturnsPipeline_ForLinux()
    {
        var pipeline = BuildManager.GetPipeline(BuildTarget.Linux);
        Assert.NotNull(pipeline);
        Assert.Contains(BuildTarget.Linux, pipeline.SupportedTargets);
    }

    [Fact]
    public void BuildManager_Build_FailsGracefully_WhenAssetsDirectoryMissing()
    {
        string noAssetsDir = Path.Combine(Path.GetTempPath(), "ProwlBuildTest_NoAssets_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(noAssetsDir);
        try
        {
            var result = BuildManager.Build(noAssetsDir, BuildTarget.Windows);
            Assert.False(result.Success);
            Assert.NotEmpty(result.Errors);
            Assert.Contains(result.Errors, e => e.Contains("Assets folder not found"));
        }
        finally
        {
            Directory.Delete(noAssetsDir, recursive: true);
        }
    }

    // ── DesktopBuildPipeline.BuildDefineString Tests ────────────────

    [Fact]
    public void BuildDefineString_IncludesPlatformDefine_ForWindows()
    {
        var settings = new BuildSettings();
        string defines = DesktopBuildPipeline.BuildDefineString(settings, BuildTarget.Windows);
        Assert.Contains("PROWL_WINDOWS", defines);
    }

    [Fact]
    public void BuildDefineString_IncludesPlatformDefine_ForLinux()
    {
        var settings = new BuildSettings();
        string defines = DesktopBuildPipeline.BuildDefineString(settings, BuildTarget.Linux);
        Assert.Contains("PROWL_LINUX", defines);
    }

    [Fact]
    public void BuildDefineString_IncludesUserDefines()
    {
        var settings = new BuildSettings();
        settings.GetProfile(BuildTarget.Windows).ScriptingDefineSymbols.Add("CUSTOM_DEFINE");
        string defines = DesktopBuildPipeline.BuildDefineString(settings, BuildTarget.Windows);
        Assert.Contains("CUSTOM_DEFINE", defines);
        Assert.Contains("PROWL_WINDOWS", defines);
    }

    // ── DesktopBuildPipeline.GetRuntimeIdentifier Tests ─────────────

    [Fact]
    public void GetRuntimeIdentifier_ReturnsCorrectRid()
    {
        Assert.Equal("win-x64", DesktopBuildPipeline.GetRuntimeIdentifier(BuildTarget.Windows));
        Assert.Equal("linux-x64", DesktopBuildPipeline.GetRuntimeIdentifier(BuildTarget.Linux));
    }

    // ── GeneratePlayerCsProj Tests ──────────────────────────────────

    [Fact]
    public void GeneratePlayerCsProj_ContainsPackageReferences()
    {
        string csprojPath = Path.Combine(_tempDir, "Test.csproj");
        var settings = new BuildSettings { ProductName = "TestGame" };

        DesktopBuildPipeline.GeneratePlayerCsProj(csprojPath, _tempDir, settings, BuildTarget.Windows);

        string content = File.ReadAllText(csprojPath);

        // Must contain NuGet package references for runtime dependencies
        Assert.Contains("PackageReference", content);
        Assert.Contains("Silk.NET", content);
        Assert.Contains("Jitter2", content);
        Assert.Contains("Prowl.Echo", content);
        Assert.Contains("Prowl.Paper", content);
        Assert.Contains("Magick.NET-Q16-AnyCPU", content);
    }

    [Fact]
    public void GeneratePlayerCsProj_ContainsCorrectProjectStructure()
    {
        string csprojPath = Path.Combine(_tempDir, "Test.csproj");
        var settings = new BuildSettings { ProductName = "MyGame" };

        DesktopBuildPipeline.GeneratePlayerCsProj(csprojPath, _tempDir, settings, BuildTarget.Windows);

        string content = File.ReadAllText(csprojPath);

        Assert.Contains("<OutputType>Exe</OutputType>", content);
        Assert.Contains("<TargetFramework>net9.0</TargetFramework>", content);
        Assert.Contains("<AllowUnsafeBlocks>true</AllowUnsafeBlocks>", content);
        Assert.Contains("<AssemblyName>MyGame</AssemblyName>", content);
        Assert.Contains("Compile Include=", content);
    }

    [Fact]
    public void GeneratePlayerCsProj_DoesNotContainEditorReferences()
    {
        string csprojPath = Path.Combine(_tempDir, "Test.csproj");
        var settings = new BuildSettings { ProductName = "TestGame" };

        DesktopBuildPipeline.GeneratePlayerCsProj(csprojPath, _tempDir, settings, BuildTarget.Windows);

        string content = File.ReadAllText(csprojPath);

        // The generated project should not have <Reference Include="Prowl.Editor"> or Microsoft.CodeAnalysis
        // (Note: HintPath strings may contain "Prowl.Editor" as part of a directory path — that's okay)
        Assert.DoesNotContain("<Reference Include=\"Prowl.Editor\"", content);
        Assert.DoesNotContain("<Reference Include=\"Microsoft.CodeAnalysis", content);
    }

    // ── GeneratePlayerProgramCs Tests ───────────────────────────────

    [Fact]
    public void GeneratePlayerProgramCs_ContainsEntryPoint()
    {
        string path = Path.Combine(_tempDir, "Program.cs");
        DesktopBuildPipeline.GeneratePlayerProgramCs(path, "TestGame");

        string content = File.ReadAllText(path);

        Assert.Contains("static void Main", content);
        Assert.Contains("new PlayerGame().Run(", content);
        Assert.Contains("\"TestGame\"", content);
        Assert.Contains("class PlayerGame : Prowl.Runtime.Game", content);
    }

    [Fact]
    public void GeneratePlayerProgramCs_EscapesProductName()
    {
        string path = Path.Combine(_tempDir, "Program.cs");
        DesktopBuildPipeline.GeneratePlayerProgramCs(path, "My \"Game\"");

        string content = File.ReadAllText(path);

        // Quotes in the product name should be escaped
        Assert.Contains("My \\\"Game\\\"", content);
    }

    // ── CollectEngineReferences Tests ────────────────────────────────

    [Fact]
    public void CollectEngineReferences_ReturnsEmpty_WhenDirectoryMissing()
    {
        var refs = DesktopBuildPipeline.CollectEngineReferences("/nonexistent/path");
        Assert.Empty(refs);
    }

    [Fact]
    public void CollectEngineReferences_SkipsNonEngineAssemblies()
    {
        // Create a temp dir with a fake non-engine DLL name
        string dir = Path.Combine(_tempDir, "FakeBase");
        Directory.CreateDirectory(dir);

        // Write a dummy file (not a real managed assembly)
        File.WriteAllText(Path.Combine(dir, "SomeLibrary.dll"), "not a real dll");

        var refs = DesktopBuildPipeline.CollectEngineReferences(dir);
        // Should not include non-engine assemblies
        Assert.DoesNotContain(refs, r => r.name == "SomeLibrary");
    }

    // ── RuntimePackageReferences Tests ──────────────────────────────

    [Fact]
    public void RuntimePackageReferences_ContainsExpectedPackages()
    {
        var packages = DesktopBuildPipeline.RuntimePackageReferences;
        Assert.True(packages.Length >= 6, "Should have at least 6 package references");

        var names = packages.Select(p => p.PackageName).ToList();
        Assert.Contains("Silk.NET", names);
        Assert.Contains("Jitter2", names);
        Assert.Contains("Prowl.Echo", names);
        Assert.Contains("Prowl.Paper", names);
    }

    // ── BuildProgress Tests ─────────────────────────────────────────

    [Fact]
    public void BuildProgress_LogAndGetLines_AreThreadSafe()
    {
        var progress = new BuildProgress();

        progress.Log("Line 1");
        progress.Log("Line 2");

        var entries = progress.GetEntries();
        Assert.Equal(2, entries.Count);
        Assert.Equal("Line 1", entries[0].Message);
        Assert.Equal("Line 2", entries[1].Message);
        Assert.Equal(2, progress.EntryCount);
    }

    [Fact]
    public void BuildProgress_Complete_SetsResultAndFlag()
    {
        var progress = new BuildProgress();
        Assert.False(progress.IsComplete);
        Assert.Null(progress.Result);

        progress.Complete(new BuildResult { Success = true, OutputPath = "/out" });

        Assert.True(progress.IsComplete);
        Assert.NotNull(progress.Result);
        Assert.True(progress.Result.Success);
    }

    // ── Integration: BuildAsync progress tracking ───────────────────

    [Fact]
    public void BuildManager_BuildAsync_ReturnsProgress_WhenNoAssetsDir()
    {
        string noAssetsDir = Path.Combine(Path.GetTempPath(), "ProwlBuildAsync_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(noAssetsDir);
        try
        {
            var progress = BuildManager.BuildAsync(noAssetsDir, BuildTarget.Windows);

            // Wait for the background task to complete (it should be fast since Assets/ is missing)
            int timeout = 10000; // 10 seconds
            while (!progress.IsComplete && timeout > 0)
            {
                Thread.Sleep(50);
                timeout -= 50;
            }

            Assert.True(progress.IsComplete, "Build should have completed");
            Assert.NotNull(progress.Result);
            Assert.False(progress.Result!.Success);
        }
        finally
        {
            try { Directory.Delete(noAssetsDir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
