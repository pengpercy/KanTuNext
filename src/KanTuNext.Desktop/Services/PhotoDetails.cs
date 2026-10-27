using System.Globalization;
using System.Text;
using KanTuNext.Core.Library;
using KanTuNext.Core.Metadata;
using KanTuNext.Core.Timeline;
using SkiaSharp;

namespace KanTuNext.Desktop.Services;

public sealed record DetailSection(string Title, IReadOnlyList<InfoRow> Rows);

/// <summary>Everything the viewer's 图片信息 panel shows about one photo.</summary>
public sealed record PhotoDetails(
    string Name,
    string Summary,
    string? Camera,
    string? Lens,
    IReadOnlyList<string> Exposure,
    IReadOnlyList<DetailSection> Sections,
    string? MapUrl)
{
    public bool HasCamera => Camera is not null || Exposure.Count > 0;

    /// <summary>The whole panel as plain text, for 「复制」.</summary>
    public string ToText()
    {
        var text = new StringBuilder().AppendLine(Name);
        foreach (var section in Sections)
        {
            text.AppendLine().AppendLine($"【{section.Title}】");
            foreach (var row in section.Rows) text.AppendLine($"{row.Label}：{row.Value}");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>Reads the file's header, colour profile and EXIF; call off the UI thread.</summary>
    /// <param name="decoded">Pixel size of a full-resolution decode, for formats Skia can't read the header of.</param>
    public static PhotoDetails Read(MediaFile file, (int Width, int Height)? decoded)
    {
        var exif = ExifReader.ReadTiff(file.Path) is { } tiff ? ExifDetails.Parse(tiff) : new ExifDetails();
        var codec = CodecInfo(file.Path);
        var format = Path.GetExtension(file.Path).TrimStart('.').ToUpperInvariant();

        // Pixel size as stored, then as seen: EXIF orientations 5–8 keep the photo sideways
        var size = codec?.Size ?? (exif.PixelWidth is { } ew && exif.PixelHeight is { } eh ? (ew, eh) : null);
        (int W, int H)? shown = size is var (w, h) ? file.Orientation >= 5 ? (h, w) : (w, h) : decoded;

        var fileRows = new List<InfoRow>
        {
            new("名称", file.Name),
            new("格式", format + (file.IsLivePhoto ? "（实况照片）" : "")),
        };
        if (shown is var (sw, sh))
        {
            var pixels = sw * (double)sh;
            fileRows.Add(new("尺寸", $"{sw} × {sh}（{(pixels >= 1_000_000 ? Math.Round(pixels / 10_000).ToString(CultureInfo.InvariantCulture) + " 万" : pixels.ToString("N0", CultureInfo.InvariantCulture) + " ")}像素）"));
            fileRows.Add(new("宽高比", AspectRatio(sw, sh)));
        }
        fileRows.Add(new("文件大小", $"{TimelineText.FileSize(file.Length)}（{file.Length.ToString("N0", CultureInfo.InvariantCulture)} 字节）"));
        try
        {
            fileRows.Add(new("创建时间", Format(File.GetCreationTime(file.Path))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        fileRows.Add(new("修改时间", Format(file.Modified)));
        fileRows.Add(new("位置", Path.GetDirectoryName(file.Path) ?? ""));

        var imageRows = new List<InfoRow>();
        if (codec is { } c)
        {
            imageRows.Add(new("编码", c.Format));
            if (c.Frames > 1) imageRows.Add(new("帧数", $"{c.Frames} 帧（动图）"));
            imageRows.Add(new("透明通道", c.HasAlpha ? "有" : "无"));
            if (c.BitDepth is { } depth) imageRows.Add(new("位深", $"每通道 {depth} 位"));
        }
        if (MetadataChunks.ReadIcc(file.Path) is { } icc)
            imageRows.Add(new("颜色配置", MetadataChunks.IccDescription(icc) is { Length: > 0 } name ? name : "已嵌入"));
        else if (codec?.IsSrgb == true) imageRows.Add(new("颜色配置", "sRGB（默认）"));

        var sections = new List<DetailSection> { new("文件", fileRows) };
        foreach (var group in Enum.GetValues<ExifGroup>())
        {
            var rows = exif.Entries.Where(e => e.Group == group).Select(e => new InfoRow(e.Name, e.Value)).ToList();
            if (group == ExifGroup.Image) rows.InsertRange(0, imageRows.Where(i => rows.All(r => r.Label != i.Label)));
            if (rows.Count > 0) sections.Add(new(GroupTitle(group), rows));
        }

        var exposure = new List<string>();
        if (exif.Iso is { } iso) exposure.Add($"ISO {iso}");
        if (exif.FocalLength is { } focal) exposure.Add($"{Math.Round(focal, 1).ToString(CultureInfo.InvariantCulture)} mm");
        if (exif.FNumber is { } f) exposure.Add(ExifDetails.Aperture(f));
        if (exif.ExposureTime is { } t) exposure.Add(ExifDetails.Shutter(t) + " s");
        if (exif.ExposureBias is { } ev) exposure.Add(ExifDetails.Bias(ev));

        var summary = string.Join(" · ", new[]
        {
            format,
            shown is var (a, b) ? $"{a} × {b}" : null,
            TimelineText.FileSize(file.Length),
        }.Where(s => s is not null));
        var map = exif is { Latitude: { } lat, Longitude: { } lon } ? SystemMaps.Url(lat, lon) : null;
        return new PhotoDetails(file.Name, summary, exif.Camera, exif.Lens, exposure, sections, map);
    }

    private static string GroupTitle(ExifGroup group) => group switch
    {
        ExifGroup.Camera => "相机",
        ExifGroup.Exposure => "拍摄参数",
        ExifGroup.Image => "图像",
        ExifGroup.Time => "时间",
        ExifGroup.Location => "位置",
        _ => "描述与版权",
    };

    private static string Format(DateTime t) => t.ToString("yyyy年M月d日 HH:mm:ss", CultureInfo.InvariantCulture);

    private static readonly (int W, int H)[] CommonRatios = [(1, 1), (5, 4), (4, 3), (3, 2), (16, 10), (16, 9), (2, 1), (21, 9)];

    /// <summary>4032 × 3024 → 4:3; within 1% of a common ratio counts as it (8000 × 5333 is 3:2).</summary>
    private static string AspectRatio(int w, int h)
    {
        var ratio = Math.Max(w, h) / (double)Math.Min(w, h);
        foreach (var (a, b) in CommonRatios)
            if (Math.Abs(ratio / ((double)a / b) - 1) < 0.01) return w >= h ? $"{a}:{b}" : $"{b}:{a}";
        return (w / (double)h).ToString("0.00", CultureInfo.InvariantCulture) + ":1";
    }

    private sealed record Codec(string Format, (int, int) Size, int Frames, bool HasAlpha, int? BitDepth, bool IsSrgb);

    /// <summary>What Skia learns from the header: format, size, frames, alpha, depth. Null for platform-only formats.</summary>
    private static Codec? CodecInfo(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return null;
            var info = codec.Info;
            int? depth = info.ColorType switch
            {
                SKColorType.Rgba16161616 or SKColorType.Rg1616 or SKColorType.Alpha16 => 16,
                SKColorType.RgbaF16 or SKColorType.RgbaF16Clamped => 16,
                SKColorType.RgbaF32 => 32,
                SKColorType.Rgba1010102 or SKColorType.Bgra1010102 or SKColorType.Rgb101010x => 10,
                SKColorType.Unknown => null,
                _ => 8,
            };
            var format = codec.EncodedFormat switch
            {
                SKEncodedImageFormat.Jpeg => "JPEG",
                SKEncodedImageFormat.Png => "PNG",
                SKEncodedImageFormat.Gif => "GIF",
                SKEncodedImageFormat.Webp => "WebP",
                SKEncodedImageFormat.Bmp => "BMP",
                SKEncodedImageFormat.Ico => "ICO",
                SKEncodedImageFormat.Wbmp => "WBMP",
                SKEncodedImageFormat.Dng => "DNG",
                SKEncodedImageFormat.Heif => "HEIF",
                SKEncodedImageFormat.Avif => "AVIF",
                var other => other.ToString().ToUpperInvariant(),
            };
            return new Codec(format, (info.Width, info.Height), Math.Max(1, codec.FrameCount),
                info.AlphaType != SKAlphaType.Opaque, depth, info.ColorSpace is null || info.ColorSpace.IsSrgb);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
