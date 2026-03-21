// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Editor.Build;
using Prowl.Runtime;

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

        // Load the rendering backend from project settings (default: OpenGL)
        RenderingBackend backend = RenderingBackend.OpenGL;
        if (!string.IsNullOrEmpty(projectPath))
        {
            try
            {
                var buildSettings = BuildSettings.Load(projectPath);
                backend = buildSettings.RenderingBackend;
            }
            catch
            {
                // If settings can't be loaded, fall back to default.
            }
        }

        var editor = new EditorApplication(projectPath);
        string title = projectPath != null
            ? $"Prowl Editor — {Path.GetFileName(projectPath)}"
            : "Prowl Editor";

        try
        {
            editor.Run(title, (int)(1600 * Game.DpiScale), (int)(900 * Game.DpiScale), backend);
        }
        catch (Exception ex) when (backend != RenderingBackend.OpenGL)
        {
            // Safety net: if Game.Run()'s internal fallback also failed somehow,
            // attempt a completely fresh start with OpenGL.
            Console.Error.WriteLine($"[Prowl] {backend} backend failed: {ex.Message}");
            Console.Error.WriteLine("[Prowl] Retrying with a fresh OpenGL instance...");

            Window.Cleanup();
            editor = new EditorApplication(projectPath);
            editor.Run(title, (int)(1600 * Game.DpiScale), (int)(900 * Game.DpiScale), RenderingBackend.OpenGL);
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
