using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using KanTuNext.Core;
using SkiaSharp;

namespace KanTuNext.Desktop.Services;

/// <summary>
/// Square, center-cropped thumbnails, cached three ways: an in-memory LRU of decoded bitmaps, JPEG files under
/// <see cref="AppPaths.ThumbnailCacheDirectory"/> keyed by path + mtime + size bucket, and finally a scaled
/// decode (SKCodec decodes JPEG straight at 1/2…1/8 size, so a 24 MP photo never expands in full).
/// Requests are served newest-first by a few background workers; the grid drops requests that scroll away.
/// </summary>
public sealed class ThumbnailService
{
    public static ThumbnailService Shared { get; } = new();

    private const long MemoryBudget = 128L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bitmap, long Bytes)>> _cache = new();
    private readonly LinkedList<(string Key, Bitmap Bitmap, long Bytes)> _lru = new();
    private long _cachedBytes;
    // LIFO: what the user scrolled to last is what they're looking at
    private readonly List<Request> _pending = [];
    private readonly HashSet<string> _queued = [];
    private readonly HashSet<string> _failed = [];
    private readonly Dictionary<object, IReadOnlySet<string>> _visible = [];
    private readonly SemaphoreSlim _work = new(0);

    private ThumbnailService()
    {
        var workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        for (var i = 0; i < workers; i++)
            new Thread(Worker) { IsBackground = true, Name = $"thumb-{i}", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>Raised on the UI thread when a thumbnail becomes available.</summary>
    public event Action? ThumbnailReady;

    private sealed record Request(string Key, string Path, DateTime Modified, int Edge);

    /// <summary>Pixel edge buckets keep the disk cache small and reusable across the 小/中/大/最大 sizes.</summary>
    public static int Bucket(double pixels) => pixels <= 128 ? 128 : pixels <= 256 ? 256 : 512;

    public static string KeyOf(string path, DateTime modified, int edge) => $"{edge}|{modified.Ticks}|{path}";

    /// <summary>Returns the bitmap if it's in memory; otherwise queues it and returns null.</summary>
    public Bitmap? Get(string path, DateTime modified, int edge)
    {
        var key = KeyOf(path, modified, edge);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Bitmap;
            }
            if (_failed.Contains(key)) return null;
            if (_queued.Add(key))
            {
                _pending.Add(new Request(key, path, modified, edge));
                _work.Release();
            }
            else
            {
                // Already queued: move it to the front
                var index = _pending.FindIndex(r => r.Key == key);
                if (index >= 0 && index != _pending.Count - 1)
                {
                    var request = _pending[index];
                    _pending.RemoveAt(index);
                    _pending.Add(request);
                }
            }
        }
        return null;
    }

    /// <summary>Whether this file could not be decoded (unsupported format, damaged file).</summary>
    public bool HasFailed(string path, DateTime modified, int edge)
    {
        lock (_gate) return _failed.Contains(KeyOf(path, modified, edge));
    }

    /// <summary>Drops queued requests that are no longer on screen.</summary>
    public void Retain(object owner, IReadOnlySet<string>? visibleKeys)
    {
        lock (_gate)
        {
            // The grid and a viewer's 预览条 ask at the same time: keep what either still shows
            if (visibleKeys is null) _visible.Remove(owner);
            else _visible[owner] = visibleKeys;
            _pending.RemoveAll(r =>
            {
                if (_visible.Values.Any(keys => keys.Contains(r.Key))) return false;
                _queued.Remove(r.Key);
                return true;
            });
        }
    }

    private void Worker()
    {
        while (true)
        {
            _work.Wait();
            Request? request;
            lock (_gate)
            {
                if (_pending.Count == 0) continue;
                request = _pending[^1];
                _pending.RemoveAt(_pending.Count - 1);
            }

            Bitmap? bitmap = null;
            try
            {
                bitmap = Load(request);
            }
            catch (Exception)
            {
                // Unsupported or damaged: remembered below so the grid shows a placeholder instead of retrying
            }

            lock (_gate)
            {
                _queued.Remove(request.Key);
                if (bitmap is null) _failed.Add(request.Key);
                else Add(request.Key, bitmap);
            }
            Dispatcher.UIThread.Post(() => ThumbnailReady?.Invoke(), DispatcherPriority.Background);
        }
    }

    private void Add(string key, Bitmap bitmap)
    {
        var bytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
        _cache[key] = _lru.AddFirst((key, bitmap, bytes));
        _cachedBytes += bytes;
        while (_cachedBytes > MemoryBudget && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _cache.Remove(last.Value.Key);
            _cachedBytes -= last.Value.Bytes;
            // Freed on the UI thread, between renders: recorded draw calls hold their own reference, and the
            // native pixels are released now instead of whenever the GC notices
            var evicted = last.Value.Bitmap;
            Dispatcher.UIThread.Post(evicted.Dispose, DispatcherPriority.Background);
        }
    }

    private static Bitmap? Load(Request request)
    {
        var cacheFile = CacheFile(request.Key);
        if (File.Exists(cacheFile))
        {
            using var stream = File.OpenRead(cacheFile);
            return new Bitmap(stream);
        }

        SKBitmap? decoded = null;
        var origin = SKEncodedOrigin.TopLeft;
        if (KanTuNext.Core.Library.MediaTypes.KindOf(request.Path) == KanTuNext.Core.Library.MediaKind.Video)
            decoded = PlatformDecoder.VideoFrame(request.Path, request.Edge * 3);
        else
        using (var source = File.OpenRead(request.Path))
        using (var codec = SKCodec.Create(source))
        {
            if (codec is not null)
            {
                var full = codec.Info;
                var shortSide = Math.Min(full.Width, full.Height);
                if (shortSide <= 0) return null;
                // The square crop needs the short side at the bucket edge, so ask for the long side proportionally
                var longSide = Math.Max(full.Width, full.Height);
                decoded = ScaledDecoder.Decode(codec, (int)Math.Ceiling((double)request.Edge * longSide / shortSide));
                origin = codec.EncodedOrigin;
            }
            // No Skia codec (HEIC, AVIF, JPEG XL, TIFF, RAW…): the platform's decoder, already upright.
            // 3× the edge covers panoramas up to 3:1 for the square crop.
            else decoded = PlatformDecoder.Decode(request.Path, request.Edge * 3);
        }
        if (decoded is null) return null;
        using var _ = decoded;
        var dims = new SKSizeI(decoded.Width, decoded.Height);

        // Center square crop, scaled to the bucket edge (never upscaled), then the EXIF orientation:
        // a centered square crop commutes with rotations and flips, so the order doesn't matter.
        var side = Math.Min(dims.Width, dims.Height);
        var edge = Math.Min(request.Edge, side);
        var src = SKRect.Create((dims.Width - side) / 2f, (dims.Height - side) / 2f, side, side);
        var targetInfo = new SKImageInfo(edge, edge, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(targetInfo);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(OrientationMatrix(origin, edge));
        using (var image = SKImage.FromBitmap(decoded))
            canvas.DrawImage(image, src, SKRect.Create(edge, edge), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Jpeg, 85);
            var temp = cacheFile + ".tmp";
            using (var file = File.Create(temp)) encoded.SaveTo(file);
            File.Move(temp, cacheFile, true);
        }
        catch (IOException)
        {
            // The disk cache is an optimisation; a full or read-only disk only costs a re-decode next time
        }

        using var pixmap = snapshot.PeekPixels();
        return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pixmap.GetPixels(),
            new PixelSize(edge, edge), new Vector(96, 96), pixmap.RowBytes);
    }

    /// <summary>Maps an EXIF origin onto a transform of the edge×edge square that shows it upright.</summary>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, float edge)
    {
        var c = edge / 2f;
        return origin switch
        {
            SKEncodedOrigin.TopRight => SKMatrix.CreateScale(-1, 1, c, c),
            SKEncodedOrigin.BottomRight => SKMatrix.CreateRotationDegrees(180, c, c),
            SKEncodedOrigin.BottomLeft => SKMatrix.CreateScale(1, -1, c, c),
            // PreConcat applies the flip first: flip then 270° is a transpose (origin 5), flip then 90° a transverse (7)
            SKEncodedOrigin.LeftTop => SKMatrix.CreateRotationDegrees(270, c, c).PreConcat(SKMatrix.CreateScale(-1, 1, c, c)),
            SKEncodedOrigin.RightTop => SKMatrix.CreateRotationDegrees(90, c, c),
            SKEncodedOrigin.RightBottom => SKMatrix.CreateRotationDegrees(90, c, c).PreConcat(SKMatrix.CreateScale(-1, 1, c, c)),
            SKEncodedOrigin.LeftBottom => SKMatrix.CreateRotationDegrees(270, c, c),
            _ => SKMatrix.Identity,
        };
    }

    /// <summary>thumbnails/ab/abcdef….jpg — FNV-1a 64 of the key, stable across runs (string.GetHashCode isn't).</summary>
    private static string CacheFile(string key)
    {
        var hash = 14695981039346656037UL;
        foreach (var ch in key)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }
        var hex = hash.ToString("x16");
        return Path.Combine(AppPaths.ThumbnailCacheDirectory, hex[..2], hex + ".jpg");
    }
}
