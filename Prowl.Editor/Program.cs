// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Prowl.Runtime;

namespace Prowl.Editor;

internal class Program
{
    static void Main(string[] args)
    {
        DpiManager.EnsureProcessDpiAware();

        string? projectPath = ParseProjectPath(args);

        var editor = new EditorApplication(projectPath);
        string title = projectPath != null
            ? $"Prowl Editor — {Path.GetFileName(projectPath)}"
            : "Prowl Editor";
        editor.Run(title, (int)(1600 * Game.DpiScale), (int)(900 * Game.DpiScale));
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
}
