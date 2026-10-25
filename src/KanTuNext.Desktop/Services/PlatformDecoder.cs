using SkiaSharp;

namespace KanTuNext.Desktop.Services;

/// <summary>The operating system's image decoder, for formats Skia can't read. Results are upright (EXIF applied).</summary>
internal static class PlatformDecoder
{
    /// <summary>Longest edge at most <paramref name="maxEdge"/>; null where there is no platform decoder or it can't read the file.</summary>
    public static SKBitmap? Decode(string path, int maxEdge)
    {
        if (OperatingSystem.IsMacOS()) return MacImageDecoder.Decode(path, maxEdge);
#if WINDOWS && !WIN7
        if (OperatingSystem.IsWindows()) return WindowsImageDecoder.Decode(path, maxEdge);
#endif
        return null;
    }
}
