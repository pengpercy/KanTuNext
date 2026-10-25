namespace KanTuNext.Core.Library;

/// <summary>The 小/中/大/最大 segmented control. 小 is measured on the original (70pt); the rest are provisional.</summary>
public enum ThumbnailSize
{
    Small,
    Medium,
    Large,
    Largest,
}

public static class ThumbnailSizes
{
    public static double Edge(ThumbnailSize size) => size switch
    {
        ThumbnailSize.Small => 70,
        ThumbnailSize.Medium => 100,
        ThumbnailSize.Large => 140,
        ThumbnailSize.Largest => 200,
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    /// <summary>Gap between thumbnails; 20pt on the original at 小.</summary>
    public static double Gap(ThumbnailSize size) => size == ThumbnailSize.Small ? 20 : 24;

    public static string Label(ThumbnailSize size) => size switch
    {
        ThumbnailSize.Small => "小",
        ThumbnailSize.Medium => "中",
        ThumbnailSize.Large => "大",
        ThumbnailSize.Largest => "最大",
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };
}
