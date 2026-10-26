using SkiaSharp;

namespace KanTuNext.Desktop.Services;

/// <summary>
/// Decoders for formats Skia can't read: the operating system's, plus the bundled libheif (HEIC/AVIF) and, on
/// Linux, libtiff. Results are upright (EXIF applied).
/// </summary>
internal static class PlatformDecoder
{
    /// <summary>Longest edge at most <paramref name="maxEdge"/>; null where there is no platform decoder or it can't read the file.</summary>
    public static SKBitmap? Decode(string path, int maxEdge)
    {
        // macOS: ImageIO first (hardware HEVC, HDR); packages don't carry libheif there
        if (OperatingSystem.IsMacOS())
            return MacImageDecoder.Decode(path, maxEdge) ?? HeifDecoder.Decode(path, maxEdge) ?? TiffDecoder.Decode(path, maxEdge);
        // Windows and Linux: libheif first, so HEIC/AVIF open without the store's codec extensions
        if ((HeifDecoder.Decode(path, maxEdge) ?? TiffDecoder.Decode(path, maxEdge)) is { } bundled) return bundled;
#if WINDOWS && !WIN7
        if (OperatingSystem.IsWindows()) return WindowsImageDecoder.Decode(path, maxEdge);
#endif
        return null;
    }

    /// <summary>A representative frame of a video for its thumbnail; null where unsupported (Linux) or unreadable.</summary>
    public static SKBitmap? VideoFrame(string path, int maxEdge)
    {
        if (OperatingSystem.IsMacOS()) return MacLivePhoto.FirstFrame(path, maxEdge);
#if WINDOWS && !WIN7
        if (OperatingSystem.IsWindows()) return WindowsImageDecoder.VideoFrame(path, maxEdge);
#endif
        return null;
    }
}
