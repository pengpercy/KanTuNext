using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace KanTuNext.Desktop.Services;

/// <summary>A decoded photo: one bitmap, or the frames of an animation (GIF, animated WebP) with their durations.</summary>
public sealed record LoadedImage(Bitmap First, IReadOnlyList<Bitmap>? Frames = null, IReadOnlyList<TimeSpan>? Durations = null)
{
    public bool IsAnimated => Frames is { Count: > 1 };
}

/// <summary>
/// Full-size decodes for the viewer, upright per EXIF orientation. Keeps the last few results so stepping
/// back and forth is instant, and lets the viewer prefetch the neighbours of the current photo.
/// </summary>
public sealed class ImageLoader
{
    /// <summary>Longest edge decoded; larger images are decoded scaled down (SKCodec does this cheaply for JPEG).</summary>
    private const int MaxEdge = 12000;
    private const int Capacity = 5;
    /// <summary>Animations beyond these limits show their first frame only.</summary>
    private const int MaxFrames = 600;
    private const long MaxAnimationBytes = 384L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly LinkedList<(string Key, Task<LoadedImage?> Task)> _recent = new();

    public Task<LoadedImage?> LoadAsync(string path)
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

    private static LoadedImage? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return null;

            if (codec.FrameCount > 1 && DecodeAnimation(codec) is { } animation) return animation;

            var full = codec.Info;
            var longest = Math.Max(full.Width, full.Height);
            var dims = longest > MaxEdge ? codec.GetScaledDimensions((float)MaxEdge / longest) : full.Size;
            var info = new SKImageInfo(dims.Width, dims.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var decoded = new SKBitmap(info);
            var result = codec.GetPixels(info, decoded.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

            using var upright = Orient(decoded, codec.EncodedOrigin);
            return new LoadedImage(ToBitmap(upright ?? decoded));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static Bitmap ToBitmap(SKBitmap source) =>
        new(PixelFormat.Bgra8888, AlphaFormat.Premul, source.GetPixels(),
            new PixelSize(source.Width, source.Height), new Vector(96, 96), source.RowBytes);

    /// <summary>
    /// Decodes every frame. Frames that build on an earlier one (RequiredFrame) start from a copy of it, as
    /// SKCodec expects; a frame without a duration gets 100 ms, the de-facto browser default.
    /// </summary>
    private static LoadedImage? DecodeAnimation(SKCodec codec)
    {
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var count = codec.FrameCount;
        if (count > MaxFrames || (long)info.BytesSize * count > MaxAnimationBytes) return null;

        var frames = codec.FrameInfo;
        var decoded = new SKBitmap[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                var bitmap = new SKBitmap(info);
                var required = frames[i].RequiredFrame;
                if (required >= 0 && decoded[required] is { } basis) basis.CopyTo(bitmap);
                else bitmap.Erase(SKColors.Transparent);
                var result = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(i, required));
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    bitmap.Dispose();
                    break;
                }
                decoded[i] = bitmap;
            }
            var ready = decoded.TakeWhile(b => b is not null).ToArray();
            if (ready.Length == 0) return null;
            var bitmaps = ready.Select(ToBitmap).ToArray();
            var durations = frames.Take(ready.Length)
                .Select(f => TimeSpan.FromMilliseconds(f.Duration > 10 ? f.Duration : 100)).ToArray();
            return new LoadedImage(bitmaps[0], bitmaps, durations);
        }
        finally
        {
            foreach (var bitmap in decoded) bitmap?.Dispose();
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
