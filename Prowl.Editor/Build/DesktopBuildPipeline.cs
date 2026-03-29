// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Diagnostics;
using System.Security;
using System.Text;

using Prowl.Runtime;
using Prowl.Editor.Services;

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
        ("Magick.NET-Q16-AnyCPU",           "14.11.0"),
        ("Prowl.Echo",                      "2.0.0"),
        ("Prowl.Paper",                     "0.7.0"),
        ("Silk.NET",                        "2.23.0"),
        ("Silk.NET.Assimp",                 "2.23.0"),
        ("Silk.NET.OpenAL.Soft.Native",     "1.23.1"),
        ("Silk.NET.Shaderc",                "2.23.0"),
        ("Silk.NET.Shaderc.Native",         "2.23.0"),
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

        // Clean destination so previous build artefacts don't linger
        if (Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, recursive: true);
        Directory.CreateDirectory(outputDirectory);

        string rid = GetRuntimeIdentifier(target);
        string configuration = settings.Configuration ?? "Release";

        // ── Generate a temporary publish project ───────────────
        string tempDir = Path.Combine(projectPath, "Library", "BuildTemp");
        Directory.CreateDirectory(tempDir);

        // Publish into a staging folder so we can reorganize before the final output
        string stagingDir = Path.Combine(tempDir, "staging");

        string csprojPath = Path.Combine(tempDir, "PlayerBuild.csproj");
        string programCsPath = Path.Combine(tempDir, "Program.cs");

        try
        {
            progress?.Log("Generating player project...");
            GeneratePlayerCsProj(csprojPath, projectPath, settings, target);
            bool isDebug = string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase);
            GeneratePlayerProgramCs(programCsPath, settings.ProductName, settings.StartupScenePath, isDebug, settings.RenderingBackend);

            // ── Run dotnet publish ─────────────────────────────
            var defines = BuildDefineString(settings, target);
            var args = new StringBuilder();
            args.Append($"publish \"{csprojPath}\"");
            args.Append($" -c {configuration}");
            args.Append($" -r {rid}");
            args.Append($" -o \"{stagingDir}\"");
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

            // ── Copy publish output to final directory ────────
            // With PublishSingleFile all managed DLLs and NuGet native
            // libraries are bundled inside the executable, so the staging
            // directory typically contains just the exe (and PDB).
            progress?.Log("Copying publish output...");
            foreach (string srcFile in Directory.GetFiles(stagingDir, "*", SearchOption.TopDirectoryOnly))
            {
                File.Copy(srcFile, Path.Combine(outputDirectory, Path.GetFileName(srcFile)), overwrite: true);
            }
            // Copy any remaining subdirectories from staging (rare)
            foreach (string srcSubDir in Directory.GetDirectories(stagingDir))
            {
                CopyDirectory(srcSubDir, Path.Combine(outputDirectory, Path.GetFileName(srcSubDir)), skipCsFiles: false);
            }

            // ── Copy assets to output ──────────────────────────
            progress?.Log("Copying assets...");
            string outputAssetsDir = Path.Combine(outputDirectory, "Assets");
            CopyDirectory(assetsDir, outputAssetsDir);
            Runtime.Debug.Log($"[Build] Assets copied to {outputAssetsDir}");

            // ── Convert .scene files to binary for faster load times ──
            progress?.Log("Converting scenes to binary format...");
            int converted = ConvertScenesToBinary(outputAssetsDir, progress);
            if (converted > 0)
                Runtime.Debug.Log($"[Build] Converted {converted} scene(s) to binary format.");

            // ── Copy engine-bundled native libraries ────────────
            // Native libraries shipped with the engine (e.g. miniaudioex)
            // are not part of any NuGet package and therefore not bundled
            // by PublishSingleFile.  Copy the target platform's native
            // libs beside the executable so the P/Invoke loader finds them.
            string editorBaseDir = AppDomain.CurrentDomain.BaseDirectory;
            string targetNativeDir = Path.Combine(editorBaseDir, "runtimes", rid, "native");
            if (Directory.Exists(targetNativeDir))
            {
                progress?.Log("Copying engine native libraries...");
                foreach (string nativeLib in Directory.GetFiles(targetNativeDir))
                {
                    string destFile = Path.Combine(outputDirectory, Path.GetFileName(nativeLib));
                    File.Copy(nativeLib, destFile, overwrite: true);
                }
                Runtime.Debug.Log($"[Build] Engine native libraries copied to output root");
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
        // Use WinExe to hide the console window unless the user explicitly wants one.
        string outputType = (settings.ShowConsole || target == BuildTarget.Linux) ? "Exe" : "WinExe";
        sb.AppendLine($"    <OutputType>{outputType}</OutputType>");
        sb.AppendLine("    <TargetFramework>net9.0</TargetFramework>");
        sb.AppendLine("    <LangVersion>13</LangVersion>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        sb.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
        sb.AppendLine($"    <AssemblyName>{SecurityElement.Escape(settings.ProductName)}</AssemblyName>");
        // Bundle all managed DLLs and NuGet native libraries into a single
        // executable so the output directory stays tidy and there are no
        // assembly-probing issues.
        sb.AppendLine("    <PublishSingleFile>true</PublishSingleFile>");
        sb.AppendLine("    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>");
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
    internal static void GeneratePlayerProgramCs(
        string outputPath,
        string productName,
        string startupScenePath,
        bool isDebug,
        Runtime.Graphite.GraphicsBackendType renderingBackend = Runtime.Graphite.GraphicsBackendType.OpenGL)
    {
        string escaped = productName.Replace("\"", "\\\"");
        string sceneEscaped = (string.IsNullOrWhiteSpace(startupScenePath)
            ? "DefaultScene.scene"
            : startupScenePath).Replace("\"", "\\\"");
        string backendName = renderingBackend.ToString();

        // In debug builds the outer catch writes a detailed error file beside
        // the executable so crashes during initialization are diagnosable even
        // when no debugger or console is attached.
        string debugCatchBody = isDebug
            ? """
                        string errorPath = System.IO.Path.Combine(
                            System.AppDomain.CurrentDomain.BaseDirectory, "error.log");
                        string msg = $"[{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}] FATAL — unhandled exception during startup:\n{ex}";
                        try { System.IO.File.WriteAllText(errorPath, msg); }
                        catch { /* best effort */ }
                        System.Console.Error.WriteLine(msg);
                        throw; // re-throw so the OS crash dialog still appears
            """
            : """
                        System.Console.Error.WriteLine($"[Player] Fatal error: {ex}");
                        throw;
            """;

        // Use fully qualified type names to avoid conflicts with user-defined
        // namespaces or types that share names with Prowl.Runtime members (e.g. "Game").
        string code = $$"""
            // Auto-generated by Prowl Build Pipeline
            namespace ProwlPlayer;

            internal class Program
            {
                static void Main(string[] args)
                {
                    try
                    {
                        // Start file logging — writes Player.log beside the executable.
                        Prowl.Runtime.PlayerFileLogger.Initialize();

                        try
                        {
                            new PlayerGame().Run("{{escaped}}", 1280, 720, Prowl.Runtime.Graphite.GraphicsBackendType.{{backendName}});
                        }
                        finally
                        {
                            Prowl.Runtime.PlayerFileLogger.Shutdown();
                        }
                    }
                    catch (Exception ex)
                    {
            {{debugCatchBody}}
                    }
                }
            }

            /// <summary>
            /// Minimal player game — loads and runs the configured startup scene.
            /// </summary>
            public sealed class PlayerGame : Prowl.Runtime.Game
            {
                public override void Initialize()
                {
                    string assetsDir = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Assets");

                    // Prefer binary scene format (produced by the build pipeline) over JSON
                    string startupScene = System.IO.Path.Combine(assetsDir, "{{sceneEscaped}}".Replace(".scene", ".bscene"));
                    if (!System.IO.File.Exists(startupScene))
                        startupScene = System.IO.Path.Combine(assetsDir, "{{sceneEscaped}}");

                    // Initialize the runtime asset database so that $assetId references
                    // in scenes and materials are resolved from the shipped Assets folder.
                    var assetDb = new Prowl.Runtime.RuntimeAssetDatabase(assetsDir);
                    Prowl.Runtime.AssetDatabase.Current = assetDb;

                    if (System.IO.File.Exists(startupScene))
                    {
                        Prowl.Runtime.Debug.Log($"[Player] Loading scene: {startupScene}");

                        var scene = Prowl.Runtime.RuntimeAssetDatabase.LoadScene(startupScene);
                        if (scene != null)
                        {
                            Prowl.Runtime.Resources.Scene.Load(scene);
                            Prowl.Runtime.Debug.LogSuccess($"[Player] Scene loaded successfully ({scene.Count} objects).");
                        }
                        else
                        {
                            Prowl.Runtime.Debug.LogWarning("[Player] Scene deserialization returned null. Starting with an empty scene.");
                            Prowl.Runtime.Resources.Scene.Load(new Prowl.Runtime.Resources.Scene());
                        }
                    }
                    else
                    {
                        Prowl.Runtime.Debug.LogWarning($"[Player] Startup scene not found: {startupScene}. Starting with an empty scene.");
                        Prowl.Runtime.Resources.Scene.Load(new Prowl.Runtime.Resources.Scene());
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

    /// <summary>
    /// Converts all <c>.scene</c> (JSON) files in <paramref name="assetsDir"/> to
    /// the compact binary format (<c>.bscene</c>) for faster load times in
    /// standalone builds.  The original <c>.scene</c> files are removed from
    /// the build output afterwards.
    /// </summary>
    /// <returns>The number of scenes successfully converted.</returns>
    internal static int ConvertScenesToBinary(string assetsDir, BuildProgress? progress = null)
    {
        int count = 0;
        if (!Directory.Exists(assetsDir))
            return count;

        var jsonSerializer = new JsonSceneSerializer();
        var binarySerializer = new BinarySceneSerializer();

        foreach (string sceneFile in Directory.GetFiles(assetsDir, "*.scene", SearchOption.AllDirectories))
        {
            try
            {
                var scene = jsonSerializer.Load(sceneFile);
                if (scene == null)
                {
                    progress?.Log($"Skipping (could not load): {sceneFile}", Runtime.LogSeverity.Warning);
                    continue;
                }

                string binaryPath = Path.ChangeExtension(sceneFile, binarySerializer.FileExtension);
                binarySerializer.Save(scene, binaryPath);

                // Remove the original JSON scene from the build output
                File.Delete(sceneFile);
                count++;
            }
            catch (Exception ex)
            {
                progress?.Log($"Failed to convert {sceneFile}: {ex.Message}", Runtime.LogSeverity.Warning);
            }
        }

        return count;
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
