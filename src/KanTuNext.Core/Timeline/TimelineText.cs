using System.Globalization;

namespace KanTuNext.Core.Timeline;

/// <summary>Text shown in timeline headers and item captions, matching the original's wording.</summary>
public static class TimelineText
{
    /// <summary>"2026年10月"</summary>
    public static string MonthTitle(int year, int month) => $"{year}年{month}月";

    /// <summary>"2 项" — the original puts a space between the number and 项.</summary>
    public static string ItemCount(int count) => $"{count} 项";

    /// <summary>"52.8 MB", "64 KB", "1.91 GB" — one decimal under 100, none above, two for GB.</summary>
    public static string FileSize(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        var inv = CultureInfo.InvariantCulture;
        if (bytes >= gb) return (bytes / gb).ToString("0.##", inv) + " GB";
        if (bytes >= mb) return (bytes / mb).ToString(bytes / mb < 100 ? "0.0" : "0", inv) + " MB";
        if (bytes >= kb) return (bytes / kb).ToString("0", inv) + " KB";
        return bytes.ToString(inv) + " B";
    }
}
