using System.Diagnostics;

namespace KanTuNext.Desktop.Services;

/// <summary>Shows a file in the system file manager, selected where the platform supports it.</summary>
internal static class Reveal
{
    public static void InFileManager(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Run("open", "-R", path);
            else if (OperatingSystem.IsWindows()) Run("explorer.exe", "/select," + path);
            else Run("xdg-open", Path.GetDirectoryName(path) ?? path);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No file manager to hand it to: nothing else to do
        }
    }

    private static void Run(string file, params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });
    }
}
