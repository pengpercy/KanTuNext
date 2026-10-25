namespace KanTuNext.Core.Library;

public enum MediaKind { Image, Video }

/// <summary>One image or video found on disk. <see cref="Taken"/> comes from EXIF when present.</summary>
public sealed record MediaFile(string Path, MediaKind Kind, long Length, DateTime Modified)
{
    public DateTime? Taken { get; init; }
    public int Orientation { get; init; } = 1;

    /// <summary>The date the timeline files this under: capture time, else last modified.</summary>
    public DateTime SortDate => Taken ?? Modified;
    public string Name => System.IO.Path.GetFileName(Path);
}

public static class MediaTypes
{
    // Decodable now (Skia) or later through the OS codecs (HEIC/TIFF); videos only list with a thumbnail
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff" };
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
