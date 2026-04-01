// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Prowl.Launcher;

/// <summary>
/// Cross-platform native folder picker. Opens the OS-native folder selection dialog
/// on a dedicated thread so the render loop is not blocked.
/// <para>
/// On Windows this uses the modern <c>IFileOpenDialog</c> COM API (the full
/// Explorer-style dialog with breadcrumb bar, search, Quick Access, etc.).
/// On Linux it shells out to <c>zenity</c> or <c>kdialog</c>.
/// On macOS it uses <c>osascript</c>.
/// </para>
/// </summary>
public static class NativeFolderPicker
{
    /// <summary>
    /// Opens a native folder-picker dialog asynchronously.
    /// Returns the selected folder path, or <c>null</c> if the user cancelled.
    /// </summary>
    public static Task<string?> PickFolderAsync(string? initialDirectory = null)
    {
        // COM dialogs on Windows require an STA thread.  Task.Run uses MTA
        // thread-pool threads, so we spin up a dedicated STA thread instead.
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(PickFolderBlocking(initialDirectory));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }

    private static string? PickFolderBlocking(string? initialDirectory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return PickFolderWindows(initialDirectory);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return PickFolderLinux(initialDirectory);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return PickFolderMacOS(initialDirectory);

        return null;
    }

    // ── Windows — modern IFileOpenDialog COM API ────────────────────
    //
    // This gives the full Explorer-style folder picker with breadcrumb
    // navigation, search, Quick Access, address bar, etc. — instead of
    // the legacy SHBrowseForFolder tree dialog.

    private const uint FOS_PICKFOLDERS     = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_FILEMUSTEXIST   = 0x00001000;
    private const uint FOS_PATHMUSTEXIST   = 0x00000800;
    private const uint SIGDN_FILESYSPATH   = 0x80058000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IShellItem ppv);

    private static string? PickFolderWindows(string? initialDirectory)
    {
        try
        {
            IFileOpenDialog dialog = (IFileOpenDialog)new FileOpenDialogClass();

            // Get current options and add folder-picker flags.
            dialog.GetOptions(out uint options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM
                                      | FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST);

            dialog.SetTitle("Select Folder");

            // Set the initial directory if provided.
            if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
            {
                Guid shellItemGuid = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero, ref shellItemGuid, out IShellItem folder);
                dialog.SetFolder(folder);
            }

            // Show returns S_OK (0) on success, or a cancelled HRESULT.
            int hr = dialog.Show(IntPtr.Zero);
            if (hr != 0)
                return null; // User cancelled.

            dialog.GetResult(out IShellItem resultItem);
            resultItem.GetDisplayName(SIGDN_FILESYSPATH, out string path);
            return path;
        }
        catch
        {
            return null;
        }
    }

    // ── COM declarations for IFileOpenDialog ────────────────────────

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogClass { }

    /// <summary>
    /// Minimal projection of IFileOpenDialog.  Every method slot must be
    /// present in vtable order even if unused, because COM dispatches by
    /// slot index.  Methods we never call are declared as void stubs.
    /// </summary>
    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // ── IModalWindow ────────────────────────────────────────
        [PreserveSig] int Show(IntPtr hwndOwner);

        // ── IFileDialog ─────────────────────────────────────────
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);

        // ── IFileOpenDialog ─────────────────────────────────────
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    // ── Linux ───────────────────────────────────────────────────────

    private static string? PickFolderLinux(string? initialDirectory)
    {
        string? result = null;

        if (CommandExists("zenity"))
        {
            string args = "--file-selection --directory --title=\"Select Folder\"";
            if (!string.IsNullOrEmpty(initialDirectory))
                args += $" --filename=\"{initialDirectory}/\"";
            result = RunProcess("zenity", args);
        }
        else if (CommandExists("kdialog"))
        {
            string args = "--getexistingdirectory";
            if (!string.IsNullOrEmpty(initialDirectory))
                args += $" \"{initialDirectory}\"";
            else
                args += " .";
            result = RunProcess("kdialog", args);
        }

        return result;
    }

    // ── macOS ───────────────────────────────────────────────────────

    private static string? PickFolderMacOS(string? initialDirectory)
    {
        string defaultPath = string.IsNullOrEmpty(initialDirectory) ? "" :
            $" default location POSIX file \"{initialDirectory}\"";
        string script = $"choose folder{defaultPath}";
        string? result = RunProcess("osascript", $"-e '{script}'");

        if (result != null && result.StartsWith("alias ", StringComparison.Ordinal))
        {
            string posixScript = $"-e 'POSIX path of (choose folder{defaultPath})'";
            result = RunProcess("osascript", posixScript);
        }

        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string? RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(30_000);

            if (process.ExitCode != 0 || string.IsNullOrEmpty(output))
                return null;

            return output;
        }
        catch
        {
            return null;
        }
    }

    private static bool CommandExists(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = command,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
