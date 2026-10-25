using System.Security.Cryptography;
using System.Text.RegularExpressions;
using KanTuNext.Core.Timeline;

namespace KanTuNext.Core.Library;

/// <summary>The 筛选 views: large images, screenshots, videos and duplicates across the common folders.</summary>
public static partial class LibraryFilters
{
    /// <summary>Images at least this big count as 大图片.</summary>
    public const long LargeImageBytes = 5L * 1024 * 1024;

    public static bool IsLargeImage(MediaFile f) => f.Kind == MediaKind.Image && f.Length >= LargeImageBytes;

    /// <summary>Default screenshot names of macOS (中/英), Windows, GNOME/KDE and phones.</summary>
    public static bool IsScreenshot(MediaFile f) => f.Kind == MediaKind.Image && ScreenshotName().IsMatch(f.Name);

    [GeneratedRegex(@"^(屏幕快照|截屏|截图|屏幕截图|Screen ?Shot|Screenshot|Bildschirmfoto|Capture d.écran|スクリーンショット)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScreenshotName();

    /// <summary>One group, biggest first, titled "N 项" with the total size alongside (as the original does).</summary>
    public static IReadOnlyList<MediaGroup> BySize(IEnumerable<MediaFile> files)
    {
        var items = files.OrderByDescending(f => f.Length).ToArray();
        if (items.Length == 0) return [];
        return [new MediaGroup(TimelineText.ItemCount(items.Length), TimelineText.FileSize(items.Sum(f => f.Length)), items)];
    }

    /// <summary>
    /// Byte-identical files: candidates share a length (no read needed to rule the rest out), then a SHA-256 of
    /// the contents decides. Each set is a group, largest wasted space first; within a set, oldest first.
    /// </summary>
    public static IReadOnlyList<MediaGroup> Duplicates(IEnumerable<MediaFile> files, IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        var candidates = files.Where(f => f.Length > 0).GroupBy(f => f.Length).Where(g => g.Count() > 1).ToArray();
        var total = candidates.Sum(g => g.Count());
        var done = 0;
        var sets = new List<MediaFile[]>();
        foreach (var sameSize in candidates)
        {
            var byHash = new Dictionary<string, List<MediaFile>>();
            foreach (var file in sameSize)
            {
                cancel.ThrowIfCancellationRequested();
                if (Hash(file.Path) is { } hash)
                {
                    if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = [];
                    list.Add(file);
                }
                progress?.Report(++done / (double)total);
            }
            sets.AddRange(byHash.Values.Where(l => l.Count > 1).Select(l => l.OrderBy(f => f.SortDate).ToArray()));
        }
        return sets
            .OrderByDescending(s => s[0].Length * (s.Length - 1))
            .Select(s => new MediaGroup($"{s.Length} 张相同", TimelineText.FileSize(s[0].Length), s))
            .ToArray();
    }

    private static string? Hash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
