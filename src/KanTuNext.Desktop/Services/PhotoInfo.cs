using System.Globalization;
using KanTuNext.Core.Library;
using KanTuNext.Core.Timeline;
using SkiaSharp;

namespace KanTuNext.Desktop.Services;

public sealed record InfoRow(string Label, string Value);

/// <summary>The 简介 panel: what a photo is, how big, when it was taken and where it lives.</summary>
internal static class PhotoInfo
{
    public static IReadOnlyList<InfoRow> Rows(MediaFile file)
    {
        var rows = new List<InfoRow>
        {
            new("名称", file.Name),
            new("类型", Path.GetExtension(file.Path).TrimStart('.').ToUpperInvariant() + (file.IsLivePhoto ? "（实况照片）" : "")),
        };
        // EXIF orientations 5–8 store the photo sideways: show it as seen
        if (Dimensions(file.Path) is var (w, h)) rows.Add(new("尺寸", file.Orientation >= 5 ? $"{h} × {w}" : $"{w} × {h}"));
        rows.Add(new("大小", TimelineText.FileSize(file.Length)));
        if (file.Taken is { } taken) rows.Add(new("拍摄时间", Format(taken)));
        rows.Add(new("修改时间", Format(file.Modified)));
        rows.Add(new("位置", Path.GetDirectoryName(file.Path) ?? ""));
        return rows;
    }

    private static string Format(DateTime t) => t.ToString("yyyy年M月d日 HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Pixel size from the file header (Skia formats); platform-only formats skip it.</summary>
    private static (int, int)? Dimensions(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            return codec is null ? null : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
