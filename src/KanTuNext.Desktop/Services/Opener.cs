using System.Diagnostics;

namespace KanTuNext.Desktop.Services;

/// <summary>Opens a file or web address with the system's default application (e.g. a video in the default player).</summary>
internal static class Opener
{
    public static void Open(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            else Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [path]) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
