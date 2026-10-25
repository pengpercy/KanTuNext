using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace KanTuNext.Desktop.Services;

/// <summary>
/// Full-size decodes for the viewer, upright per EXIF orientation. Keeps the last few results so stepping
/// back and forth is instant, and lets the viewer prefetch the neighbours of the current photo.
/// </summary>
public sealed class ImageLoader
{
    /// <summary>Longest edge decoded; larger images are decoded scaled down (SKCodec does this cheaply for JPEG).</summary>
    private const int MaxEdge = 12000;
    private const int Capacity = 5;

    private readonly object _gate = new();
    private readonly LinkedList<(string Key, Task<Bitmap?> Task)> _recent = new();

    public Task<Bitmap?> LoadAsync(string path)
    {
        lock (_gate)
        {
            for (var node = _recent.First; node is not null; node = node.Next)
            {
                if (node.Value.Key != path) continue;
                _recent.Remove(node);
                _recent.AddFirst(node);
                return node.Value.Task;
            }
            var task = Task.Run(() => Decode(path));
            _recent.AddFirst((path, task));
            while (_recent.Count > Capacity) _recent.RemoveLast();
            return task;
        }
    }

    /// <summary>Starts decoding without waiting, so the next/previous photo is ready when asked for.</summary>
    public void Prefetch(string path) => _ = LoadAsync(path);

    private static Bitmap? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return null;

            var full = codec.Info;
            var longest = Math.Max(full.Width, full.Height);
            var dims = longest > MaxEdge ? codec.GetScaledDimensions((float)MaxEdge / longest) : full.Size;
            var info = new SKImageInfo(dims.Width, dims.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var decoded = new SKBitmap(info);
            var result = codec.GetPixels(info, decoded.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

            using var upright = Orient(decoded, codec.EncodedOrigin);
            var source = upright ?? decoded;
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, source.GetPixels(),
                new PixelSize(source.Width, source.Height), new Vector(96, 96), source.RowBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Returns a rotated/flipped copy for non-default EXIF origins, or null when already upright.</summary>
    private static SKBitmap? Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return null;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (w, h) = swap ? (bitmap.Height, bitmap.Width) : (bitmap.Width, bitmap.Height);
        var rotated = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(rotated);
        // Each case maps the stored pixels onto the upright w×h canvas
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        canvas.SetMatrix(m);
        canvas.DrawBitmap(bitmap, 0, 0);
        return rotated;
    }
}
