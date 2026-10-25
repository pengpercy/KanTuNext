using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KanTuNext.Desktop.Services;

/// <summary>Moves files to the system trash / recycle bin, so a delete can be undone from there.</summary>
internal static class Trash
{
    /// <summary>
    /// Returns false (and leaves the file alone) when it could not be moved to the trash. <paramref name="trashedPath"/>
    /// is where it ended up, when the platform says (macOS, Linux home trash) — what 最近删除 needs to put it back.
    /// </summary>
    public static bool MoveToTrash(string path, out string? trashedPath)
    {
        trashedPath = null;
        try
        {
            if (OperatingSystem.IsMacOS()) return MacTrash(path, out trashedPath);
            if (OperatingSystem.IsWindows()) return WindowsRecycle(path);
            if (OperatingSystem.IsLinux()) return LinuxTrash(path, out trashedPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ExternalException or System.ComponentModel.Win32Exception)
        {
        }
        return false;
    }

    // ---- macOS: -[NSFileManager trashItemAtURL:resultingItemURL:error:], which Finder's "Put Back" understands
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte SendTrash(IntPtr receiver, IntPtr selector, IntPtr url, out IntPtr resultingUrl, IntPtr error);

    private static bool MacTrash(string path, out string? trashedPath)
    {
        trashedPath = null;
        var utf8 = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            var pool = Send(Send(objc_getClass("NSAutoreleasePool"), sel_registerName("alloc")), sel_registerName("init"));
            try
            {
                var str = Send(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8);
                var url = Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), str);
                var manager = Send(objc_getClass("NSFileManager"), sel_registerName("defaultManager"));
                if (SendTrash(manager, sel_registerName("trashItemAtURL:resultingItemURL:error:"), url, out var resulting, IntPtr.Zero) == 0)
                    return false;
                if (resulting != IntPtr.Zero)
                    trashedPath = Marshal.PtrToStringUTF8(Send(Send(resulting, sel_registerName("path")), sel_registerName("UTF8String")));
                return true;
            }
            finally
            {
                Send(pool, sel_registerName("drain"));
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    // ---- Windows: SHFileOperation with FOF_ALLOWUNDO (available back to Windows 7)
    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [DllImport("shell32.dll")]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);

    private static bool WindowsRecycle(string path)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
        // pFrom is a list of paths, each NUL-terminated, ending with an extra NUL
        var from = Marshal.StringToHGlobalUni(Path.GetFullPath(path) + "\0");
        try
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = from,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            return SHFileOperationW(ref op) == 0 && op.fAnyOperationsAborted == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    // ---- Linux: the freedesktop.org trash spec for the home trash; `gio trash` for other volumes
    private static bool LinuxTrash(string path, out string? trashedPath)
    {
        trashedPath = null;
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var files = Path.Combine(dataHome, "Trash", "files");
        var info = Path.Combine(dataHome, "Trash", "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        var name = Path.GetFileName(path);
        var target = name;
        for (var i = 2; File.Exists(Path.Combine(files, target)) || File.Exists(Path.Combine(info, target + ".trashinfo")); i++)
            target = $"{Path.GetFileNameWithoutExtension(name)}.{i}{Path.GetExtension(name)}";

        var infoFile = Path.Combine(info, target + ".trashinfo");
        File.WriteAllText(infoFile,
            $"[Trash Info]\nPath={EscapePath(Path.GetFullPath(path))}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
        try
        {
            File.Move(path, Path.Combine(files, target));
            trashedPath = Path.Combine(files, target);
            return true;
        }
        catch (IOException)
        {
            // Another filesystem: the home trash can't take it without a copy; let GIO pick that volume's trash
            File.Delete(infoFile);
            using var gio = Process.Start(new ProcessStartInfo("gio", ["trash", path]) { UseShellExecute = false });
            if (gio is null) return false;
            gio.WaitForExit(10_000);
            return gio.HasExited && gio.ExitCode == 0;
        }
    }

    /// <summary>Percent-encodes a path as the spec asks (RFC 2396 escaping, '/' kept).</summary>
    private static string EscapePath(string path)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}
