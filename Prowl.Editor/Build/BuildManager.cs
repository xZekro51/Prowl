// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;
using Prowl.EventSystem;
using Prowl.Runtime.EventSystem;

namespace Prowl.Editor.Build;

/// <summary>
/// Central entry point for building a Prowl project.
/// Can be invoked from the editor UI or from the command line.
/// <para>
/// Registered <see cref="IBuildPipeline"/> instances are queried to find
/// one that supports the requested <see cref="BuildTarget"/>. Custom
/// pipelines can be registered via <see cref="RegisterPipeline"/> for
/// console/mobile/web support.
/// </para>
/// </summary>
public static class BuildManager
{
    private static readonly List<IBuildPipeline> s_pipelines = [];

    static BuildManager()
    {
        // Register the built-in desktop pipeline by default.
        RegisterPipeline(new DesktopBuildPipeline());
    }

    /// <summary>
    /// Registers a custom build pipeline. Pipelines registered later
    /// take priority over earlier ones for overlapping targets.
    /// </summary>
    public static void RegisterPipeline(IBuildPipeline pipeline)
    {
        s_pipelines.Add(pipeline);
    }

    /// <summary>
    /// Returns the first pipeline that supports <paramref name="target"/>,
    /// searching in reverse-registration order (latest wins).
    /// </summary>
    public static IBuildPipeline? GetPipeline(BuildTarget target)
    {
        for (int i = s_pipelines.Count - 1; i >= 0; i--)
        {
            if (s_pipelines[i].SupportedTargets.Contains(target))
                return s_pipelines[i];
        }
        return null;
    }

    /// <summary>
    /// Returns all registered pipelines.
    /// </summary>
    public static IReadOnlyList<IBuildPipeline> Pipelines => s_pipelines;

    /// <summary>
    /// Builds the project located at <paramref name="projectPath"/> for
    /// the given <paramref name="target"/>.
    /// </summary>
    /// <param name="projectPath">Absolute path to the project root.</param>
    /// <param name="target">Platform to build for.</param>
    /// <param name="outputDirectory">
    /// Explicit output directory, or <c>null</c> to let the pipeline decide.
    /// </param>
    /// <returns>The <see cref="BuildResult"/>.</returns>
    public static BuildResult Build(
        string projectPath,
        BuildTarget target,
        string? outputDirectory = null)
    {
        var pipeline = GetPipeline(target);
        if (pipeline == null)
        {
            return new BuildResult
            {
                Success = false,
                Errors = [$"No build pipeline registered for target '{target}'."],
            };
        }

        var settings = BuildSettings.Load(projectPath);

        Debug.Log($"[Build] Starting build for {target} using '{pipeline.DisplayName}'...");
        return pipeline.Build(projectPath, target, settings, outputDirectory);
    }

    /// <summary>
    /// Starts an asynchronous build on a background thread. Returns
    /// a <see cref="BuildProgress"/> that the UI can poll each frame
    /// for live log output and completion status.
    /// </summary>
    public static BuildProgress BuildAsync(
        string projectPath,
        BuildTarget target,
        string? outputDirectory = null)
    {
        var progress = new BuildProgress();

        var pipeline = GetPipeline(target) as DesktopBuildPipeline;
        if (pipeline == null)
        {
            progress.Log("No build pipeline registered for target.", Runtime.LogSeverity.Error);
            progress.Complete(new BuildResult
            {
                Success = false,
                Errors = [$"No build pipeline registered for target '{target}'."],
            });
            return progress;
        }

        var settings = BuildSettings.Load(projectPath);

        Debug.Log($"[Build] Starting async build for {target} using '{pipeline.DisplayName}'...");
        progress.Log($"Starting build for {target}...");

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await pipeline.BuildAsync(
                    projectPath, target, settings, outputDirectory, progress);
                progress.Complete(result);
            }
            catch (Exception ex)
            {
                progress.Log($"FATAL: {ex.Message}", Runtime.LogSeverity.Error);
                progress.Complete(new BuildResult
                {
                    Success = false,
                    Errors = [ex.ToString()],
                });
            }
        });

        return progress;
    }

    /// <summary>
    /// CLI entry point: parses arguments and runs a headless build.
    /// Returns the process exit code (0 = success, 1 = failure).
    /// <para>
    /// Usage: <c>Prowl.Editor --build --project "path" --target Windows [--output "path"]</c>
    /// </para>
    /// </summary>
    public static int RunFromCommandLine(string[] args)
    {
        string? projectPath = null;
        string? targetStr = null;
        string? outputDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--project", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                projectPath = Path.GetFullPath(args[++i]);
            }
            else if (args[i].Equals("--target", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                targetStr = args[++i];
            }
            else if (args[i].Equals("--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                outputDir = Path.GetFullPath(args[++i]);
            }
        }

        if (string.IsNullOrEmpty(projectPath))
        {
            Console.Error.WriteLine("Error: --project is required when using --build.");
            PrintUsage();
            return 1;
        }

        if (!Directory.Exists(projectPath))
        {
            Console.Error.WriteLine($"Error: Project directory not found: {projectPath}");
            return 1;
        }

        if (string.IsNullOrEmpty(targetStr) ||
            !Enum.TryParse<BuildTarget>(targetStr, ignoreCase: true, out var target))
        {
            Console.Error.WriteLine($"Error: Invalid or missing --target. Valid targets: {string.Join(", ", Enum.GetNames<BuildTarget>())}");
            PrintUsage();
            return 1;
        }

        // Hook Debug output to console for headless mode
        using var logSub = DebugEvents.SubscribeOnLog(
            args => OnConsoleLog(args.Message, args.StackTrace, args.Severity));

        try
        {
            Console.WriteLine($"[Build] Project: {projectPath}");
            Console.WriteLine($"[Build] Target:  {target}");
            if (outputDir != null)
                Console.WriteLine($"[Build] Output:  {outputDir}");

            var result = Build(projectPath, target, outputDir);

            foreach (string warning in result.Warnings)
                Console.WriteLine($"[Build] WARNING: {warning}");
            foreach (string error in result.Errors)
                Console.Error.WriteLine($"[Build] ERROR: {error}");

            if (result.Success)
            {
                Console.WriteLine($"[Build] Build succeeded. Output: {result.OutputPath}");
                return 0;
            }
            else
            {
                Console.Error.WriteLine("[Build] Build FAILED.");
                return 1;
            }
        }
        finally
        {
        }
    }

    private static void OnConsoleLog(string message, DebugStackTrace? stackTrace, LogSeverity severity)
    {
        var writer = severity is LogSeverity.Error or LogSeverity.Exception
            ? Console.Error
            : Console.Out;
        writer.WriteLine(message);
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  Prowl.Editor --build --project <path> --target <target> [--output <path>]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Targets:");
        foreach (string name in Enum.GetNames<BuildTarget>())
            Console.Error.WriteLine($"  {name}");
    }
}
