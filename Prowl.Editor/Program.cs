// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Build;
using Prowl.Runtime;
using Prowl.Runtime.Graphite;

namespace Prowl.Editor;

internal class Program
{
    static int Main(string[] args)
    {
        // ── Headless build mode (no editor window) ─────────────
        if (HasFlag(args, "--build"))
            return BuildManager.RunFromCommandLine(args);

        // ── Normal editor mode ─────────────────────────────────
        DpiManager.EnsureProcessDpiAware();

        string? projectPath = ParseProjectPath(args);
        bool gpuDebug = HasFlag(args, "--gpu-debug");
        bool debugMode = HasFlag(args, "--debug");

        // --debug implies --gpu-debug so a single flag covers all diagnostics.
        if (debugMode)
            gpuDebug = true;

        // The editor can now run on any backend (OpenGL or Vulkan) because the
        // ImGui integration uses the Graphite abstraction layer instead of
        // Silk.NET.OpenGL.Extensions.ImGui. Game.Run() has Vulkan → OpenGL fallback.
        GraphicsBackendType backend = GraphicsBackendType.Vulkan;

        if (debugMode)
        {
            Debug.IsVerbose = true;

            // Persist all log output to a file so it survives crashes.
            string logPath = projectPath != null
                ? Path.Combine(projectPath, "Editor.log")
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Editor.log");
            PlayerFileLogger.Initialize(logPath);

            Debug.Log("[Editor] Debug mode enabled — verbose logging active, writing to: " + logPath);
        }

        if (gpuDebug)
        {
            Window.GpuDebug = true;
            Debug.Log("[Editor] GPU debug mode enabled — Vulkan validation layers and debug markers will be active.");
        }

        var editor = new EditorApplication(projectPath);
        string title = projectPath != null
            ? $"Prowl Editor — {Path.GetFileName(projectPath)}"
            : "Prowl Editor";

        if (debugMode)
            title += " [DEBUG]";
        else if (gpuDebug)
            title += " [GPU DEBUG]";

        editor.Run(title, (int)(1600 * Game.DpiScale), (int)(900 * Game.DpiScale), backend);

        if (debugMode)
            PlayerFileLogger.Shutdown();

        return 0;
    }

    /// <summary>
    /// Parses --project "path" from the command-line arguments.
    /// Returns the project folder path, or null if not specified.
    /// </summary>
    private static string? ParseProjectPath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--project", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }
        return null;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
