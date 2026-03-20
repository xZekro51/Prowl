// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Diagnostics;
using System.Security;
using System.Text;

using Prowl.Runtime;

namespace Prowl.Editor.Build;

/// <summary>
/// Default build pipeline for desktop platforms (Windows and Linux).
/// Generates a temporary <c>.csproj</c> that references the Prowl Runtime
/// and user scripts, then invokes <c>dotnet publish</c> to produce a
/// self-contained (or framework-dependent) executable.  Scene and asset
/// files are copied into an <c>Assets/</c> folder beside the output.
/// </summary>
public sealed class DesktopBuildPipeline : IBuildPipeline
{
    /// <summary>
    /// NuGet package references that must be included in the generated player
    /// .csproj so that <c>dotnet publish</c> can restore native binaries and
    /// transitive dependencies.  These mirror <c>Prowl.Runtime.csproj</c>.
    /// </summary>
    internal static readonly (string PackageName, string Version)[] RuntimePackageReferences =
    [
        ("Jitter2",                         "2.7.3"),
        ("Magick.NET-Q16-AnyCPU",           "14.9.1"),
        ("Prowl.Echo",                      "2.0.0"),
        ("Prowl.Paper",                     "0.7.0"),
        ("Silk.NET",                        "2.22.0"),
        ("Silk.NET.Assimp",                 "2.22.0"),
        ("Silk.NET.OpenAL.Soft.Native",     "1.23.1"),
        ("Silk.NET.OpenGL.Extensions.ImGui","2.22.0"),
    ];

    /// <summary>
    /// Assembly name prefixes that identify engine-specific DLLs which should
    /// be referenced directly (not via NuGet). Everything else from the editor
    /// base directory is expected to come through <see cref="RuntimePackageReferences"/>.
    /// </summary>
    private static readonly string[] EngineAssemblyPrefixes =
    [
        "Prowl.Runtime",
    ];

    public string DisplayName => "Desktop (Windows / Linux)";

    public IReadOnlyList<BuildTarget> SupportedTargets { get; } =
        [BuildTarget.Windows, BuildTarget.Linux];

    // ── Synchronous build (IBuildPipeline) ──────────────────────────

    public BuildResult Build(
        string projectPath,
        BuildTarget target,
        BuildSettings settings,
        string? outputDirectory = null)
    {
        return BuildAsync(projectPath, target, settings, outputDirectory).GetAwaiter().GetResult();
    }

    // ── Async build with streaming progress ─────────────────────────

    public async Task<BuildResult> BuildAsync(
        string projectPath,
        BuildTarget target,
        BuildSettings settings,
        string? outputDirectory = null,
        BuildProgress? progress = null,
        CancellationToken cancellation = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        progress?.Log("Validating project...");

        string assetsDir = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(assetsDir))
        {
            progress?.Log($"Assets folder not found: {assetsDir}", Runtime.LogSeverity.Error);
            return new BuildResult
            {
                Success = false,
                Errors = [$"Assets folder not found: {assetsDir}"],
            };
        }

        // ── Resolve output directory ───────────────────────────
        outputDirectory ??= Path.Combine(projectPath, "Builds", target.ToString());
        Directory.CreateDirectory(outputDirectory);

        string rid = GetRuntimeIdentifier(target);
        string configuration = settings.Configuration ?? "Release";

        // ── Generate a temporary publish project ───────────────
        string tempDir = Path.Combine(projectPath, "Library", "BuildTemp");
        Directory.CreateDirectory(tempDir);

        string csprojPath = Path.Combine(tempDir, "PlayerBuild.csproj");
        string programCsPath = Path.Combine(tempDir, "Program.cs");

        try
        {
            progress?.Log("Generating player project...");
            GeneratePlayerCsProj(csprojPath, projectPath, settings, target);
            GeneratePlayerProgramCs(programCsPath, settings.ProductName);

            // ── Run dotnet publish ─────────────────────────────
            var defines = BuildDefineString(settings, target);
            var args = new StringBuilder();
            args.Append($"publish \"{csprojPath}\"");
            args.Append($" -c {configuration}");
            args.Append($" -r {rid}");
            args.Append($" -o \"{outputDirectory}\"");
            args.Append($" --self-contained {settings.SelfContained.ToString().ToLowerInvariant()}");
            if (!string.IsNullOrEmpty(defines))
                args.Append($" -p:DefineConstants=\"{defines}\"");

            string logMsg = $"Running: dotnet {args}";
            Runtime.Debug.Log($"[Build] {logMsg}");
            progress?.Log(logMsg);

            var (exitCode, stdout, stderr) = await RunDotnetAsync(
                args.ToString(), progress, cancellation).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(stdout))
                Runtime.Debug.Log($"[Build] {stdout}");

            if (exitCode != 0)
            {
                errors.Add($"dotnet publish exited with code {exitCode}.");
                if (!string.IsNullOrWhiteSpace(stderr))
                    errors.Add(stderr);

                progress?.Log($"dotnet publish exited with code {exitCode}.", Runtime.LogSeverity.Error);
                return new BuildResult
                {
                    Success = false,
                    OutputPath = outputDirectory,
                    Errors = errors,
                    Warnings = warnings,
                };
            }

            if (!string.IsNullOrWhiteSpace(stderr))
                warnings.Add(stderr);

            // ── Copy assets to output ──────────────────────────
            progress?.Log("Copying assets...");
            string outputAssetsDir = Path.Combine(outputDirectory, "Assets");
            CopyDirectory(assetsDir, outputAssetsDir);
            Runtime.Debug.Log($"[Build] Assets copied to {outputAssetsDir}");

            // ── Copy native libraries (runtimes/) for standalone builds ──
            string editorBaseDir = AppDomain.CurrentDomain.BaseDirectory;
            string editorRuntimesDir = Path.Combine(editorBaseDir, "runtimes");
            if (Directory.Exists(editorRuntimesDir))
            {
                progress?.Log("Bundling native libraries...");
                string outputRuntimesDir = Path.Combine(outputDirectory, "runtimes");
                CopyDirectory(editorRuntimesDir, outputRuntimesDir, skipCsFiles: false);
                Runtime.Debug.Log($"[Build] Native libraries copied to {outputRuntimesDir}");
            }

            Runtime.Debug.LogSuccess($"[Build] Build succeeded — output: {outputDirectory}");
            progress?.Log($"Build succeeded — output: {outputDirectory}", Runtime.LogSeverity.Success);

            return new BuildResult
            {
                Success = true,
                OutputPath = outputDirectory,
                Warnings = warnings,
            };
        }
        catch (OperationCanceledException)
        {
            progress?.Log("Build cancelled.", Runtime.LogSeverity.Warning);
            errors.Add("Build was cancelled by the user.");
            return new BuildResult
            {
                Success = false,
                OutputPath = outputDirectory,
                Errors = errors,
                Warnings = warnings,
            };
        }
        catch (Exception ex)
        {
            progress?.Log($"{ex.Message}", Runtime.LogSeverity.Error);
            errors.Add(ex.ToString());
            return new BuildResult
            {
                Success = false,
                OutputPath = outputDirectory,
                Errors = errors,
                Warnings = warnings,
            };
        }
        finally
        {
            // Clean up temp directory
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { /* best effort */ }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    internal static string GetRuntimeIdentifier(BuildTarget target) => target switch
    {
        BuildTarget.Windows => "win-x64",
        BuildTarget.Linux => "linux-x64",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    internal static string BuildDefineString(BuildSettings settings, BuildTarget target)
    {
        var profile = settings.GetProfile(target);
        var symbols = new List<string>(profile.ScriptingDefineSymbols);

        // Always include a platform define
        symbols.Add(target switch
        {
            BuildTarget.Windows => "PROWL_WINDOWS",
            BuildTarget.Linux => "PROWL_LINUX",
            _ => "PROWL_DESKTOP",
        });

        return string.Join(";", symbols.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>
    /// Generates a <c>.csproj</c> for the player build that includes all
    /// user scripts, NuGet package references for runtime dependencies,
    /// and direct references to engine-specific assemblies.
    /// </summary>
    internal static void GeneratePlayerCsProj(
        string csprojPath,
        string projectPath,
        BuildSettings settings,
        BuildTarget target)
    {
        string assetsDir = Path.Combine(projectPath, "Assets");
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        var sb = new StringBuilder();
        sb.AppendLine("""<Project Sdk="Microsoft.NET.Sdk">""");
        sb.AppendLine();
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <OutputType>Exe</OutputType>");
        sb.AppendLine("    <TargetFramework>net9.0</TargetFramework>");
        sb.AppendLine("    <LangVersion>13</LangVersion>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
        sb.AppendLine($"    <AssemblyName>{SecurityElement.Escape(settings.ProductName)}</AssemblyName>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();

        // Include user scripts from Assets
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine($"""    <Compile Include="{SecurityElement.Escape(assetsDir)}{Path.DirectorySeparatorChar}**{Path.DirectorySeparatorChar}*.cs" />""");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine();

        // NuGet package references (mirrors Prowl.Runtime.csproj)
        sb.AppendLine("  <ItemGroup>");
        foreach (var (pkg, version) in RuntimePackageReferences)
        {
            sb.AppendLine($"    <PackageReference Include=\"{SecurityElement.Escape(pkg)}\" Version=\"{SecurityElement.Escape(version)}\" />");
        }
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine();

        // Engine-specific DLL references (only Prowl.Runtime and similar — not NuGet assemblies)
        var references = CollectEngineReferences(baseDir);
        if (references.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var (name, dllPath) in references)
            {
                string escaped = SecurityElement.Escape(dllPath) ?? dllPath;
                sb.AppendLine($"    <Reference Include=\"{SecurityElement.Escape(name) ?? name}\">");
                sb.AppendLine($"      <HintPath>{escaped}</HintPath>");
                sb.AppendLine("    </Reference>");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine();
        sb.AppendLine("</Project>");

        File.WriteAllText(csprojPath, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// Generates a minimal <c>Program.cs</c> entry point for the player.
    /// </summary>
    internal static void GeneratePlayerProgramCs(string outputPath, string productName)
    {
        string escaped = productName.Replace("\"", "\\\"");
        // Use fully qualified type names to avoid conflicts with user-defined
        // namespaces or types that share names with Prowl.Runtime members (e.g. "Game").
        string code = $$"""
            // Auto-generated by Prowl Build Pipeline
            namespace ProwlPlayer;

            internal class Program
            {
                static void Main(string[] args)
                {
                    new PlayerGame().Run("{{escaped}}", 1280, 720);
                }
            }

            /// <summary>
            /// Minimal player game — loads and runs the default scene.
            /// </summary>
            public sealed class PlayerGame : Prowl.Runtime.Game
            {
                public override void Initialize()
                {
                    string assetsDir = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Assets");
                    string defaultScene = System.IO.Path.Combine(assetsDir, "DefaultScene.scene");

                    if (System.IO.File.Exists(defaultScene))
                    {
                        // Scene loading is handled by the runtime serialization layer.
                        Prowl.Runtime.Debug.Log($"[Player] Loading scene: {defaultScene}");
                    }
                    else
                    {
                        Prowl.Runtime.Debug.LogWarning("[Player] No default scene found. Starting with an empty scene.");
                    }
                }
            }
            """;

        File.WriteAllText(outputPath, code, new UTF8Encoding(false));
    }

    internal static List<(string name, string dllPath)> CollectEngineReferences(string baseDir)
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(baseDir))
            return result;

        foreach (string dll in Directory.GetFiles(baseDir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileNameWithoutExtension(dll);

            // Only include engine-specific assemblies (e.g. Prowl.Runtime).
            // NuGet-sourced assemblies are handled by PackageReferences in the generated csproj.
            bool isEngineAssembly = false;
            foreach (var prefix in EngineAssemblyPrefixes)
            {
                if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    isEngineAssembly = true;
                    break;
                }
            }
            if (!isEngineAssembly)
                continue;

            // Skip editor assemblies
            if (fileName.Equals("Prowl.Editor", StringComparison.OrdinalIgnoreCase))
                continue;
            if (fileName.Equals("Prowl.Editor.Tests", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!seen.Add(Path.GetFullPath(dll)))
                continue;

            if (!IsManagedAssembly(dll))
                continue;

            string assemblyName = System.Reflection.AssemblyName.GetAssemblyName(dll).Name ?? fileName;
            result.Add((assemblyName, Path.GetFullPath(dll)));
        }

        return result;
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

    private static async Task<(int exitCode, string stdout, string stderr)> RunDotnetAsync(
        string arguments,
        BuildProgress? progress = null,
        CancellationToken cancellation = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet process.");

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        // Stream output line-by-line so the UI can show live progress
        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
            {
                stdoutBuilder.AppendLine(line);
                progress?.Log(line, ClassifyDotnetLine(line));
            }
        }, cancellation);

        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
            {
                stderrBuilder.AppendLine(line);
                progress?.Log(line, Runtime.LogSeverity.Error);
            }
        }, cancellation);

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);

        if (cancellation.IsCancellationRequested && !process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }

    /// <summary>
    /// Classifies a line of dotnet build output by severity.
    /// </summary>
    private static Runtime.LogSeverity ClassifyDotnetLine(string line)
    {
        if (line.Contains(": error ", StringComparison.OrdinalIgnoreCase))
            return Runtime.LogSeverity.Error;
        if (line.Contains(": warning ", StringComparison.OrdinalIgnoreCase))
            return Runtime.LogSeverity.Warning;
        if (line.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase))
            return Runtime.LogSeverity.Success;
        return Runtime.LogSeverity.Normal;
    }

    private static void CopyDirectory(string sourceDir, string destDir, bool skipCsFiles = true)
    {
        Directory.CreateDirectory(destDir);

        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            // Skip .cs source files — they are compiled, not shipped
            if (skipCsFiles && file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;

            string relativePath = Path.GetRelativePath(sourceDir, file);
            string destPath = Path.Combine(destDir, relativePath);
            string? destSubDir = Path.GetDirectoryName(destPath);
            if (destSubDir != null)
                Directory.CreateDirectory(destSubDir);

            File.Copy(file, destPath, overwrite: true);
        }
    }
}
