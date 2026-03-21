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

        // Editor always prefers Vulkan for its rendering.
        // If Vulkan initialization fails, Game.Run() automatically falls back to OpenGL.
        GraphicsBackendType backend = GraphicsBackendType.Vulkan;

        var editor = new EditorApplication(projectPath);
        string title = projectPath != null
            ? $"Prowl Editor — {Path.GetFileName(projectPath)}"
            : "Prowl Editor";

        try
        {
            editor.Run(title, (int)(1600 * Game.DpiScale), (int)(900 * Game.DpiScale), GraphicsBackendType.OpenGL);
        }
        catch (Exception ex) when (backend != GraphicsBackendType.OpenGL)
        {
            // Safety net: if Game.Run()'s internal fallback also failed somehow,
            // attempt a completely fresh start with OpenGL.
            Console.Error.WriteLine($"[Prowl] {backend} backend failed: {ex.Message}");
            Console.Error.WriteLine("[Prowl] Retrying with a fresh OpenGL instance...");

            Window.Cleanup();
            editor = new EditorApplication(projectPath);
            editor.Run(title, (int)(1600 * Game.DpiScale), (int)(900 * Game.DpiScale), GraphicsBackendType.OpenGL);
        }
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
