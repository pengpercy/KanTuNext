#if WINDOWS && !WIN7
using System.Runtime.InteropServices;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace KanTuNext.Desktop.Services;

/// <summary>
/// Windows 10+: decodes what the installed WIC codecs can read — TIFF and some RAW out of the box; HEIC/HEIF,
/// AVIF and JPEG XL with the Microsoft Store extensions — scaled during decode and upright per EXIF.
/// Called from background threads, so the WinRT async calls are waited on synchronously.
/// </summary>
internal static class WindowsImageDecoder
{
    /// <summary>The shell's thumbnail of a video (what Explorer shows), decoded; null if there is none.</summary>
    public static SKBitmap? VideoFrame(string path, int maxEdge)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) return null;
        try
        {
            var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            using var thumb = file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, (uint)Math.Min(maxEdge, 1024))
                .AsTask().GetAwaiter().GetResult();
            if (thumb is null || thumb.Size == 0) return null;
            using var stream = thumb.AsStreamForRead();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            return SKBitmap.Decode(copy);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static SKBitmap? Decode(string path, int maxEdge)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) return null;
        try
        {
            var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            using var stream = file.OpenAsync(FileAccessMode.Read).AsTask().GetAwaiter().GetResult();
            var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            var scale = Math.Min(1.0, (double)maxEdge / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var data = decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask().GetAwaiter().GetResult()
                .DetachPixelData();
            // Orientation may have swapped the scaled width and height
            var rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
            var w = (int)(rotated ? transform.ScaledHeight : transform.ScaledWidth);
            var h = (int)(rotated ? transform.ScaledWidth : transform.ScaledHeight);
            if (data.Length != w * h * 4) return null;
            var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            Marshal.Copy(data, 0, bitmap.GetPixels(), data.Length);
            return bitmap;
        }
        catch (Exception)
        {
            return null; // no codec for this format, or a damaged file
        }
    }
}
#endif
