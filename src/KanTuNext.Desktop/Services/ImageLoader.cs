using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;

namespace KanTuNext.Desktop.Services;

/// <summary>A decoded photo: one bitmap, or the frames of an animation (GIF, animated WebP) with their durations.</summary>
public sealed record LoadedImage(Bitmap First, IReadOnlyList<Bitmap>? Frames = null, IReadOnlyList<TimeSpan>? Durations = null)
{
    public bool IsAnimated => Frames is { Count: > 1 };

    /// <summary>Decoded smaller than the file (screen-sized); a full-resolution decode is available on zoom.</summary>
    public bool IsReduced { get; init; }

    public long Bytes => (Frames ?? [First]).Sum(b => (long)b.PixelSize.Width * b.PixelSize.Height * 4);

    public void Dispose()
    {
        foreach (var bitmap in Frames ?? [First]) bitmap.Dispose();
    }
}

/// <summary>
/// Decodes for the viewer, upright per EXIF orientation. Photos are first decoded at screen size (a 24 MP photo
/// becomes ~10–30 MB instead of ~96 MB); the full resolution is decoded only when the user zooms in past it.
/// Results are cached within a memory budget (1/16 of RAM, 128–512 MB), least recently used out first, and the
/// photo on screen is never evicted.
/// </summary>
public sealed class ImageLoader
{
    /// <summary>Longest edge ever decoded; larger images are decoded scaled down (SKCodec does this cheaply for JPEG).</summary>
    public const int FullEdge = 12000;
    /// <summary>Animations beyond these limits show their first frame only.</summary>
    private const int MaxFrames = 600;
    private const long MaxAnimationBytes = 384L * 1024 * 1024;

    private static readonly long Budget = Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 16,
        128L * 1024 * 1024, 512L * 1024 * 1024);

    private sealed record Entry(string Path, int MaxEdge, Task<LoadedImage?> Task);

    private readonly object _gate = new();
    private readonly LinkedList<Entry> _recent = new();
    private string? _pinned;

    /// <summary>The photo on screen: its decodes are kept whatever the budget says.</summary>
    public void Pin(string? path)
    {
        lock (_gate) _pinned = path;
    }

    public Task<LoadedImage?> LoadAsync(string path, int maxEdge)
    {
        lock (_gate)
        {
            for (var node = _recent.First; node is not null; node = node.Next)
            {
                if (node.Value.Path != path || node.Value.MaxEdge != maxEdge) continue;
                _recent.Remove(node);
                _recent.AddFirst(node);
                return node.Value.Task;
            }
            var task = Task.Run(() => Decode(path, maxEdge));
            _recent.AddFirst(new Entry(path, maxEdge, task));
            _ = task.ContinueWith(_ => Trim(), TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>Starts decoding without waiting, so the next/previous photo is ready when asked for.</summary>
    public void Prefetch(string path, int maxEdge) => _ = LoadAsync(path, maxEdge);

    /// <summary>Evicts least-recently-used finished decodes until the cache fits the budget.</summary>
    private void Trim()
    {
        var evicted = new List<LoadedImage>();
        lock (_gate)
        {
            long total = _recent.Sum(e => e.Task.IsCompletedSuccessfully ? e.Task.Result?.Bytes ?? 0 : 0);
            for (var node = _recent.Last; node is not null && total > Budget;)
            {
                var previous = node.Previous;
                var entry = node.Value;
                if (entry.Path != _pinned && entry.Task.IsCompletedSuccessfully && entry.Task.Result is { } image)
                {
                    _recent.Remove(node);
                    total -= image.Bytes;
                    evicted.Add(image);
                }
                node = previous;
            }
        }
        // Released on the UI thread: draw calls already recorded keep their own reference to the pixels
        if (evicted.Count > 0) Dispatcher.UIThread.Post(() => evicted.ForEach(i => i.Dispose()), DispatcherPriority.Background);
    }

    private static LoadedImage? Decode(string path, int maxEdge)
    {
        try
        {
            using (var stream = File.OpenRead(path))
            using (var codec = SKCodec.Create(stream))
            {
                // No Skia codec isn't the end: DecodeUpright falls back to the platform decoder
                if (codec is { FrameCount: > 1 } && DecodeAnimation(codec) is { } animation) return animation;
            }
            using var upright = DecodeUpright(path, maxEdge, out var reduced);
            return upright is null ? null : new LoadedImage(ToBitmap(upright)) { IsReduced = reduced };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes a still image (first frame), scaled so its longest edge is at most <paramref name="maxEdge"/>, and
    /// turned upright per its EXIF orientation. Shared by the viewer and OCR engines that need raw pixels.
    /// </summary>
    internal static SKBitmap? DecodeUpright(string path, int maxEdge, SKColorType colorType = SKColorType.Bgra8888) =>
        DecodeUpright(path, maxEdge, out _, colorType);

    internal static SKBitmap? DecodeUpright(string path, int maxEdge, out bool reduced, SKColorType colorType = SKColorType.Bgra8888)
    {
        reduced = false;
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            // No Skia codec (HEIC, AVIF, JPEG XL, TIFF, RAW…): the platform's decoder, already upright. Its size
            // is unknown up front, so a result that fills the limit is taken as reduced (full decode on zoom).
            if (PlatformDecoder.Decode(path, maxEdge) is not { } platform) return null;
            if (platform.ColorType != colorType)
            {
                var converted = platform.Copy(colorType);
                platform.Dispose();
                platform = converted;
            }
            reduced = Math.Max(platform.Width, platform.Height) >= maxEdge;
            return platform;
        }

        var full = codec.Info;
        reduced = Math.Max(full.Width, full.Height) > maxEdge;
        var decoded = ScaledDecoder.Decode(codec, maxEdge, colorType);
        if (decoded is null) return null;
        // Sampling lands at or above the target; bring it down to exactly fit (high-quality filtering)
        var longest = Math.Max(decoded.Width, decoded.Height);
        if (longest > maxEdge)
        {
            var scale = (double)maxEdge / longest;
            var resized = decoded.Resize(new SKImageInfo(Math.Max(1, (int)(decoded.Width * scale)), Math.Max(1, (int)(decoded.Height * scale)),
                colorType, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (resized is not null)
            {
                decoded.Dispose();
                decoded = resized;
            }
        }
        if (Orient(decoded, codec.EncodedOrigin) is not { } upright) return decoded;
        decoded.Dispose();
        return upright;
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
        var rotated = new SKBitmap(new SKImageInfo(w, h, bitmap.ColorType, SKAlphaType.Premul));
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
