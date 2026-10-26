using System.Runtime.InteropServices;

namespace KanTuNext.Desktop.Services.Updates;

/// <summary>Picks the release package this installation can update itself from.</summary>
internal static class UpdateAssetSelector
{
    /// <summary>
    /// The zip for Windows/macOS, the AppImage on Linux — or null where the app can't replace itself (a deb/rpm
    /// install, which the package manager owns; the update window then links to the release page instead).
    /// </summary>
    public static ReleaseAsset? Select(ReleaseInfo release)
    {
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        string name;
        if (OperatingSystem.IsWindows())
            // Windows 7/8 run the separate Win7 package; Windows 10+ the regular one
            name = $"KanTuNext_{release.VersionText}.{(arm ? "win-arm64" : OperatingSystem.IsWindowsVersionAtLeast(10) ? "win-x64" : "win7-x64")}.zip";
        else if (OperatingSystem.IsMacOS())
            name = $"KanTuNext_{release.VersionText}.{(arm ? "osx-arm64" : "osx-x64")}.zip";
        else if (CanReplaceSelf)
            name = $"KanTuNext-{release.VersionText}.linux.{(arm ? "aarch64" : "x86_64")}.AppImage";
        else
            return null;
        return release.Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>False for Linux deb/rpm installs: only an AppImage (APPIMAGE set by its runtime) can swap itself.</summary>
    public static bool CanReplaceSelf =>
        !OperatingSystem.IsLinux() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE"));
}
