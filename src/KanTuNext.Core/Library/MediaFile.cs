namespace KanTuNext.Core.Library;

public enum MediaKind { Image, Video }

/// <summary>One image or video found on disk. <see cref="Taken"/> comes from EXIF when present.</summary>
public sealed record MediaFile(string Path, MediaKind Kind, long Length, DateTime Modified)
{
    public DateTime? Taken { get; init; }
    public int Orientation { get; init; } = 1;

    /// <summary>Pixel size from the file header; 0 when the format doesn't tell cheaply (HEIC, RAW, TIFF…).</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>For a Live Photo still: the paired motion clip (same folder, same name, .mov).</summary>
    public string? LiveVideo { get; init; }
    public bool IsLivePhoto => LiveVideo is not null;

    /// <summary>The date the timeline files this under: capture time, else last modified.</summary>
    public DateTime SortDate => Taken ?? Modified;
    public string Name => System.IO.Path.GetFileName(Path);

    public MediaFile With(Metadata.ImageHeader header) =>
        this with { Taken = header.Taken, Orientation = header.Orientation, Width = header.Width, Height = header.Height };
}

public static class MediaTypes
{
    // Skia decodes the common formats; the platform decoder (macOS ImageIO, Windows WIC) the rest:
    // HEIC/HEIF, AVIF, JPEG XL, TIFF, PSD and camera RAW. Videos only list with a placeholder. Icons (.ico) are
    // left out: they are app and website graphics, never photos.
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".bmp", ".webp",
        ".heic", ".heif", ".hif", ".avif", ".jxl", ".tif", ".tiff", ".psd",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2", ".pef", ".srw",
    };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".wmv", ".3gp" };

    public static MediaKind? KindOf(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        if (Images.Contains(ext)) return MediaKind.Image;
        if (Videos.Contains(ext)) return MediaKind.Video;
        return null;
    }
}
