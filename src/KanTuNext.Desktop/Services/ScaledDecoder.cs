using System.Runtime.InteropServices;
using SkiaSharp;

namespace KanTuNext.Desktop.Services;

/// <summary>
/// Decodes an image at (about) a target size without holding the full-size pixels. JPEG and WebP scale inside
/// the codec; PNG, GIF, BMP and others can't, so for those it reads one scanline at a time and keeps every k-th
/// row and pixel — a 9000×5250 PNG then never needs its 189 MB full decode for a 256 px thumbnail.
/// </summary>
internal static class ScaledDecoder
{
    /// <summary>
    /// Returns a decode whose longest edge is at least <paramref name="minEdge"/> (smaller only if the image is),
    /// close to it when the codec or sampling allows; not oriented.
    /// </summary>
    public static SKBitmap? Decode(SKCodec codec, int minEdge, SKColorType colorType = SKColorType.Bgra8888)
    {
        var full = codec.Info;
        var longest = Math.Max(full.Width, full.Height);
        if (longest <= minEdge) return DecodeAt(codec, full.Size, colorType);

        var scaled = codec.GetScaledDimensions((float)minEdge / longest);
        if (scaled.Width < full.Width && Math.Max(scaled.Width, scaled.Height) >= minEdge)
            return DecodeAt(codec, scaled, colorType); // native scaling (JPEG DCT, WebP)

        var factor = longest / minEdge;
        if (factor >= 2 && codec.ScanlineOrder == SKCodecScanlineOrder.TopDown && Sample(codec, factor, colorType) is { } sampled)
            return sampled;
        return DecodeAt(codec, full.Size, colorType);
    }

    private static SKBitmap? DecodeAt(SKCodec codec, SKSizeI size, SKColorType colorType)
    {
        var info = new SKImageInfo(size.Width, size.Height, colorType, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput) return bitmap;
        bitmap.Dispose();
        return null;
    }

    /// <summary>Nearest-pixel sampling by an integer factor, one source row in memory at a time.</summary>
    private static SKBitmap? Sample(SKCodec codec, int factor, SKColorType colorType)
    {
        var full = codec.Info;
        var info = new SKImageInfo(full.Width, full.Height, colorType, SKAlphaType.Premul);
        if (codec.StartScanlineDecode(info) != SKCodecResult.Success) return null;

        var outW = Math.Max(1, full.Width / factor);
        var outH = Math.Max(1, full.Height / factor);
        var result = new SKBitmap(new SKImageInfo(outW, outH, colorType, SKAlphaType.Premul));
        using var row = new SKBitmap(new SKImageInfo(full.Width, 1, colorType, SKAlphaType.Premul));
        var source = new int[full.Width];
        var target = new int[outW];
        var start = factor / 2; // sample the middle of each block, not its first row/column
        if (start > 0 && !codec.SkipScanlines(start))
        {
            result.Dispose();
            return null;
        }
        for (var y = 0; y < outH; y++)
        {
            if (codec.GetScanlines(row.GetPixels(), 1, row.RowBytes) != 1) break;
            Marshal.Copy(row.GetPixels(), source, 0, full.Width);
            for (var x = 0; x < outW; x++) target[x] = source[Math.Min(full.Width - 1, x * factor + start)];
            Marshal.Copy(target, 0, result.GetPixels() + y * result.RowBytes, outW);
            if (y < outH - 1 && !codec.SkipScanlines(factor - 1)) break;
        }
        return result;
    }
}
