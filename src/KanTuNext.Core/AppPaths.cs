namespace KanTuNext.Core;

/// <summary>Per-user locations for the index and thumbnail cache. Everything stays on this machine.</summary>
public static class AppPaths
{
    /// <summary>KANTUNEXT_DATA_DIR overrides the location (tests, portable use).</summary>
    public static string DataDirectory { get; } = Resolve();

    public static string ThumbnailCacheDirectory => Path.Combine(DataDirectory, "thumbnails");

    private static string Resolve()
    {
        if (Environment.GetEnvironmentVariable("KANTUNEXT_DATA_DIR") is { Length: > 0 } custom) return custom;
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "KanTuNext");
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KanTuNext");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var root = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
        return Path.Combine(root, "kantunext");
    }
}
