// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

using Prowl.Runtime;

namespace Prowl.Editor.Utilities;

/// <summary>Identifies a category of external code editor.</summary>
public enum ExternalEditorKind
{
    SystemDefault,
    VisualStudio,
    VSCode,
    Rider,
    NotepadPlusPlus,
    SublimeText,
}

/// <summary>An IDE installation discovered on the current machine.</summary>
public sealed record DetectedEditor(ExternalEditorKind Kind, string DisplayName, string ExecutablePath);

/// <summary>
/// Auto-detects installed code editors and opens source files at a specific
/// line and column — the same workflow as Unity's "External Tools" preference.
/// </summary>
public static class ExternalEditorUtility
{
    private static List<DetectedEditor>? s_cached;

    // ── Public API ────────────────────────────────────────────

    /// <summary>
    /// Returns code editors detected on this machine.
    /// Results are cached; pass <c>refresh: true</c> to rescan.
    /// </summary>
    public static IReadOnlyList<DetectedEditor> GetDetectedEditors(bool refresh = false)
    {
        if (s_cached is not null && !refresh)
            return s_cached;

        var list = new List<DetectedEditor>();

        try { DetectVisualStudio(list); } catch { /* scan failed — skip */ }
        try { DetectVSCode(list);       } catch { /* scan failed — skip */ }
        try { DetectRider(list);        } catch { /* scan failed — skip */ }
        try { DetectNotepadPlusPlus(list); } catch { /* scan failed — skip */ }
        try { DetectSublimeText(list);  } catch { /* scan failed — skip */ }

        s_cached = list;
        return list;
    }

    /// <summary>
    /// Opens <paramref name="filePath"/> at the given line/column in the
    /// editor whose executable is <paramref name="editorPath"/>.
    /// If <paramref name="editorPath"/> is empty the OS default handler is used.
    /// When <paramref name="solutionOrFolder"/> is provided the IDE is opened
    /// in the context of that solution/workspace so navigation, IntelliSense,
    /// and project references work correctly.
    /// </summary>
    public static void OpenFileAtLine(string editorPath, string filePath, int line, int column, string? solutionOrFolder = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(editorPath))
            {
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                return;
            }

            var kind = ClassifyEditor(editorPath);

            switch (kind)
            {
                case ExternalEditorKind.VisualStudio:
                    OpenVisualStudio(editorPath, filePath, line, column, solutionOrFolder);
                    break;

                case ExternalEditorKind.VSCode:
                    if (!string.IsNullOrEmpty(solutionOrFolder))
                    {
                        string folder = File.Exists(solutionOrFolder)
                            ? Path.GetDirectoryName(solutionOrFolder)!
                            : solutionOrFolder;
                        Launch(editorPath, $"--reuse-window \"{folder}\" --goto \"{filePath}:{line}:{column}\"");
                    }
                    else
                    {
                        Launch(editorPath, $"--reuse-window --goto \"{filePath}:{line}:{column}\"");
                    }
                    break;

                case ExternalEditorKind.Rider:
                    if (!string.IsNullOrEmpty(solutionOrFolder))
                        Launch(editorPath, $"\"{solutionOrFolder}\" --line {line} --column {column} \"{filePath}\"");
                    else
                        Launch(editorPath, $"--line {line} --column {column} \"{filePath}\"");
                    break;

                case ExternalEditorKind.NotepadPlusPlus:
                    Launch(editorPath, $"-n{line} -c{column} \"{filePath}\"");
                    break;

                case ExternalEditorKind.SublimeText:
                    Launch(editorPath, $"\"{filePath}:{line}:{column}\"");
                    break;

                default:
                    Launch(editorPath, $"\"{filePath}\"");
                    break;
            }
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"[ExternalEditor] Failed to open editor: {ex.Message}");
        }
    }

    /// <summary>Determines the editor kind from an executable path.</summary>
    public static ExternalEditorKind ClassifyEditor(string executablePath)
    {
        string name = Path.GetFileNameWithoutExtension(executablePath).ToLowerInvariant();
        return name switch
        {
            "devenv"                   => ExternalEditorKind.VisualStudio,
            "code" or "code-insiders"  => ExternalEditorKind.VSCode,
            "rider" or "rider64"       => ExternalEditorKind.Rider,
            "notepad++"                => ExternalEditorKind.NotepadPlusPlus,
            "subl" or "sublime_text"   => ExternalEditorKind.SublimeText,
            _                          => ExternalEditorKind.SystemDefault,
        };
    }

    // ── Visual Studio (COM + fallback) ────────────────────────

    private static void OpenVisualStudio(string devenvPath, string filePath, int line, int column, string? solutionPath = null)
    {
        // COM automation gives reliable line-navigation in a running VS instance.
        if (OperatingSystem.IsWindows() && TryOpenViaRunningObjectTable(filePath, line, column))
            return;

        // Fallback: launch devenv with the solution so the file opens in the
        // correct project context (IntelliSense, references, etc.).
        if (!string.IsNullOrEmpty(solutionPath) && File.Exists(solutionPath))
            Launch(devenvPath, $"\"{solutionPath}\" /Edit \"{filePath}\"");
        else
            Launch(devenvPath, $"/Edit \"{filePath}\"");

        // devenv /Edit opens the file but cannot navigate to a specific line.
        // Retry COM automation in the background once VS has had time to start.
        if (OperatingSystem.IsWindows() && line > 0)
        {
            _ = Task.Run(async () =>
            {
                for (int attempt = 0; attempt < 15; attempt++)
                {
                    await Task.Delay(2000);
                    try
                    {
                        if (TryOpenViaRunningObjectTable(filePath, line, column))
                            return;
                    }
                    catch { /* VS not ready yet — keep retrying */ }
                }
            });
        }
    }

    // P/Invoke for the COM Running Object Table (Windows only).
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(uint reserved, out IRunningObjectTable rot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx ctx);

    /// <summary>
    /// Enumerates the COM Running Object Table looking for a live
    /// <c>VisualStudio.DTE</c> instance, then uses its automation model
    /// to open the file and navigate to the line — exactly how Unity does it.
    /// </summary>
    private static bool TryOpenViaRunningObjectTable(string filePath, int line, int column)
    {
        try
        {
            if (GetRunningObjectTable(0, out var rot) != 0)
                return false;

            rot.EnumRunning(out var enumMoniker);
            var moniker = new IMoniker[1];

            while (enumMoniker.Next(1, moniker, IntPtr.Zero) == 0)
            {
                if (CreateBindCtx(0, out var ctx) != 0)
                    continue;

                moniker[0].GetDisplayName(ctx, null!, out string displayName);

                if (!displayName.StartsWith("!VisualStudio.DTE", StringComparison.Ordinal))
                    continue;

                rot.GetObject(moniker[0], out object comObj);

                try
                {
                    dynamic dte = comObj;

                    // Open file in the text editor view.
                    dte.ItemOperations.OpenFile(filePath);

                    // Navigate to line:column.
                    dynamic selection = dte.ActiveDocument.Selection;
                    selection.MoveToLineAndOffset(line, Math.Max(1, column), false);

                    // Bring VS to the foreground.
                    dte.MainWindow.Activate();

                    return true;
                }
                finally
                {
                    Marshal.ReleaseComObject(comObj);
                }
            }
        }
        catch
        {
            // COM automation unavailable — caller will use the devenv fallback.
        }

        return false;
    }

    // ── Generic process launcher ──────────────────────────────

    private static void Launch(string fileName, string arguments)
    {
        Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow  = true,
        });
    }

    // ── Detection: Visual Studio ──────────────────────────────

    private static readonly Dictionary<string, string> s_vsVersionYear = new()
    {
        ["15"] = "2017",
        ["16"] = "2019",
        ["17"] = "2022",
        ["18"] = "2026",
    };

    private static void DetectVisualStudio(List<DetectedEditor> editors)
    {
        if (!OperatingSystem.IsWindows()) return;

        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;

            string vsRoot = Path.Combine(root, "Microsoft Visual Studio");
            if (!Directory.Exists(vsRoot)) continue;

            foreach (string versionDir in Directory.GetDirectories(vsRoot))
            {
                string ver = Path.GetFileName(versionDir);
                if (ver.Equals("Installer", StringComparison.OrdinalIgnoreCase))
                    continue;

                string year = s_vsVersionYear.GetValueOrDefault(ver, ver);

                foreach (string editionDir in Directory.GetDirectories(versionDir))
                {
                    string edition = Path.GetFileName(editionDir);
                    string devenv  = Path.Combine(editionDir, "Common7", "IDE", "devenv.exe");

                    if (File.Exists(devenv) && seen.Add(devenv))
                    {
                        editors.Add(new DetectedEditor(
                            ExternalEditorKind.VisualStudio,
                            $"Visual Studio {year} {edition}",
                            devenv));
                    }
                }
            }
        }
    }

    // ── Detection: VS Code ────────────────────────────────────

    private static void DetectVSCode(List<DetectedEditor> editors)
    {
        var candidates = new List<(string path, string name)>();

        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            candidates.Add((Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"), "Visual Studio Code"));
            candidates.Add((Path.Combine(pf,    "Microsoft VS Code", "Code.exe"),             "Visual Studio Code"));
            candidates.Add((Path.Combine(local, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe"), "VS Code Insiders"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add(("/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code", "Visual Studio Code"));
        }
        else
        {
            candidates.Add(("/usr/bin/code",  "Visual Studio Code"));
            candidates.Add(("/snap/bin/code", "Visual Studio Code (Snap)"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, name) in candidates)
        {
            if (File.Exists(path) && seen.Add(Path.GetFullPath(path)))
                editors.Add(new DetectedEditor(ExternalEditorKind.VSCode, name, path));
        }
    }

    // ── Detection: Rider ──────────────────────────────────────

    private static void DetectRider(List<DetectedEditor> editors)
    {
        if (OperatingSystem.IsWindows())
        {
            string local       = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string toolboxApps = Path.Combine(local, "JetBrains", "Toolbox", "apps");

            if (Directory.Exists(toolboxApps))
            {
                foreach (string dir in Directory.GetDirectories(toolboxApps, "Rider", SearchOption.TopDirectoryOnly))
                    ScanRiderToolbox(editors, dir);
            }

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf))
            {
                foreach (string dir in Directory.GetDirectories(pf).Where(
                    d => Path.GetFileName(d).Contains("Rider", StringComparison.OrdinalIgnoreCase)))
                {
                    string rider = Path.Combine(dir, "bin", "rider64.exe");
                    if (File.Exists(rider))
                        editors.Add(new DetectedEditor(ExternalEditorKind.Rider,
                            $"JetBrains Rider ({Path.GetFileName(dir)})", rider));
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            const string app = "/Applications/Rider.app/Contents/MacOS/rider";
            if (File.Exists(app))
                editors.Add(new DetectedEditor(ExternalEditorKind.Rider, "JetBrains Rider", app));
        }
        else
        {
            foreach (string p in new[] { "/usr/bin/rider", "/snap/bin/rider" })
            {
                if (File.Exists(p))
                    editors.Add(new DetectedEditor(ExternalEditorKind.Rider, "JetBrains Rider", p));
            }
        }
    }

    private static void ScanRiderToolbox(List<DetectedEditor> editors, string riderDir)
    {
        foreach (string channelDir in Directory.GetDirectories(riderDir))
        {
            foreach (string versionDir in Directory.GetDirectories(channelDir))
            {
                string exe = OperatingSystem.IsWindows()
                    ? Path.Combine(versionDir, "bin", "rider64.exe")
                    : Path.Combine(versionDir, "bin", "rider.sh");

                if (File.Exists(exe))
                {
                    editors.Add(new DetectedEditor(ExternalEditorKind.Rider,
                        $"JetBrains Rider {Path.GetFileName(versionDir)}", exe));
                }
            }
        }
    }

    // ── Detection: Notepad++ ──────────────────────────────────

    private static void DetectNotepadPlusPlus(List<DetectedEditor> editors)
    {
        if (!OperatingSystem.IsWindows()) return;

        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];

        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            string npp = Path.Combine(root, "Notepad++", "notepad++.exe");
            if (File.Exists(npp))
            {
                editors.Add(new DetectedEditor(ExternalEditorKind.NotepadPlusPlus, "Notepad++", npp));
                return;
            }
        }
    }

    // ── Detection: Sublime Text ───────────────────────────────

    private static void DetectSublimeText(List<DetectedEditor> editors)
    {
        var candidates = new List<(string path, string name)>();

        if (OperatingSystem.IsWindows())
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            candidates.Add((Path.Combine(pf, "Sublime Text",   "sublime_text.exe"), "Sublime Text"));
            candidates.Add((Path.Combine(pf, "Sublime Text 3", "sublime_text.exe"), "Sublime Text 3"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add(("/Applications/Sublime Text.app/Contents/SharedSupport/bin/subl", "Sublime Text"));
        }
        else
        {
            candidates.Add(("/usr/bin/subl", "Sublime Text"));
        }

        foreach (var (path, name) in candidates)
        {
            if (File.Exists(path))
            {
                editors.Add(new DetectedEditor(ExternalEditorKind.SublimeText, name, path));
                return;
            }
        }
    }

    // ── Browse for executable (native file dialog) ────────────

    /// <summary>
    /// Opens a platform-native file dialog for the user to pick an executable.
    /// Returns the selected path, or <c>null</c> if cancelled.
    /// </summary>
    public static string? BrowseForExecutable()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return BrowseWindows();
            if (OperatingSystem.IsMacOS())
                return BrowseMacOS();
            return BrowseLinux();
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"[ExternalEditor] Browse failed: {ex.Message}");
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public nint lpstrFilter;
        public nint lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public nint lpstrFile;
        public int nMaxFile;
        public nint lpstrFileTitle;
        public int nMaxFileTitle;
        public nint lpstrInitialDir;
        public nint lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public nint lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public nint lpTemplateName;
        public nint pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

    private static unsafe string? BrowseWindows()
    {
        const int OFN_FILEMUSTEXIST = 0x00001000;
        const int OFN_NOCHANGEDIR = 0x00000008;

        char* fileBuffer = stackalloc char[1024];
        fileBuffer[0] = '\0';

        fixed (char* filter = "Executables (*.exe)\0*.exe\0All Files (*.*)\0*.*\0")
        fixed (char* title = "Select IDE Executable")
        {
            var ofn = new OPENFILENAME();
            ofn.lStructSize = Marshal.SizeOf<OPENFILENAME>();
            ofn.lpstrFilter = (nint)filter;
            ofn.lpstrFile = (nint)fileBuffer;
            ofn.nMaxFile = 1024;
            ofn.lpstrTitle = (nint)title;
            ofn.Flags = OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR;

            if (GetOpenFileNameW(ref ofn))
                return new string(fileBuffer);
            return null;
        }
    }

    private static string? BrowseMacOS()
    {
        var psi = new ProcessStartInfo("osascript")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("POSIX path of (choose file with prompt \"Select IDE executable\")");

        using var proc = Process.Start(psi);
        if (proc == null) return null;
        string result = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        return proc.ExitCode == 0 && !string.IsNullOrEmpty(result) ? result : null;
    }

    private static string? BrowseLinux()
    {
        var psi = new ProcessStartInfo("zenity")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--file-selection");
        psi.ArgumentList.Add("--title=Select IDE executable");

        using var proc = Process.Start(psi);
        if (proc == null) return null;
        string result = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        return proc.ExitCode == 0 && !string.IsNullOrEmpty(result) ? result : null;
    }
}
