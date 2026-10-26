using System.Runtime.InteropServices;
using SkiaSharp;

namespace KanTuNext.Desktop.Services;

/// <summary>
/// TIFF through libtiff's RGBA interface, which handles every photometric/bit depth/compression combination
/// (JPEG via the bundled libjpeg-turbo). The Linux packages carry it; macOS and Windows decode TIFF themselves.
/// First page only. Flips from the Orientation tag are applied; transposed orientations (5–8) are not.
/// </summary>
internal static class TiffDecoder
{
    private const string Library = "kantunext-tiff";
    private const uint TagImageWidth = 256, TagImageLength = 257;
    private const int OrientationTopLeft = 1;
    private const long MaxPixels = 400_000_000; // a full RGBA raster is decoded before scaling

    private static readonly Lazy<IntPtr> Handle = new(Load);
    // libtiff prints warnings and errors to stderr by default; a null handler silences them
    private static readonly Lazy<bool> Initialized = new(() =>
    {
        TIFFSetWarningHandler(IntPtr.Zero);
        TIFFSetErrorHandler(IntPtr.Zero);
        return true;
    });

    static TiffDecoder()
    {
        NativeLibraries.Register(Library, () => Handle.Value);
    }

    public static bool IsAvailable => Handle.Value != IntPtr.Zero && Initialized.Value;

    private static IntPtr Load()
    {
        var name = OperatingSystem.IsWindows() ? "tiff.dll" : OperatingSystem.IsMacOS() ? "libtiff.6.dylib" : "libtiff.so.6";
        return NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, name), out var handle) ? handle : IntPtr.Zero;
    }

    /// <summary>BGRA decode whose longest edge is at most <paramref name="maxEdge"/>, or null if it isn't a TIFF libtiff can read.</summary>
    public static SKBitmap? Decode(string path, int maxEdge)
    {
        if (!IsAvailable || !HasTiffSignature(path)) return null;
        var tiff = TIFFOpen(path, "r");
        if (tiff == IntPtr.Zero) return null;
        try
        {
            if (TIFFGetField(tiff, TagImageWidth, out var width) != 1 || TIFFGetField(tiff, TagImageLength, out var height) != 1) return null;
            if (width == 0 || height == 0 || (long)width * height > MaxPixels) return null;

            // The RGBA interface yields premultiplied R,G,B,A bytes, which is Skia's RGBA premul layout
            using var full = new SKBitmap(new SKImageInfo((int)width, (int)height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (TIFFReadRGBAImageOriented(tiff, width, height, full.GetPixels(), OrientationTopLeft, 0) != 1) return null;

            var scale = Math.Min(1.0, (double)maxEdge / Math.Max(width, height));
            var target = new SKImageInfo(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)),
                SKColorType.Bgra8888, SKAlphaType.Premul);
            return full.Resize(target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        finally
        {
            TIFFClose(tiff);
        }
    }

    /// <summary>"II*\0" / "MM\0*", or BigTIFF's "II+\0" / "MM\0+".</summary>
    private static bool HasTiffSignature(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            if (stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4) return false;
            return (head[0] == 'I' && head[1] == 'I' && head[2] is 42 or 43 && head[3] == 0)
                || (head[0] == 'M' && head[1] == 'M' && head[2] == 0 && head[3] is 42 or 43);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport(Library)] private static extern IntPtr TIFFSetWarningHandler(IntPtr handler);
    [DllImport(Library)] private static extern IntPtr TIFFSetErrorHandler(IntPtr handler);
    [DllImport(Library, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr TIFFOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, string mode);
    [DllImport(Library)] private static extern void TIFFClose(IntPtr tiff);
    // Variadic in C; with one pointer argument the Linux x64/arm64 calling conventions match a normal call
    [DllImport(Library)] private static extern int TIFFGetField(IntPtr tiff, uint tag, out uint value);
    [DllImport(Library)] private static extern int TIFFReadRGBAImageOriented(IntPtr tiff, uint width, uint height, IntPtr raster, int orientation, int stopOnError);
}
