using System.Runtime.InteropServices;
using SkiaSharp;

namespace KanTuNext.Desktop.Services;

/// <summary>
/// macOS: decodes whatever ImageIO can read — HEIC/HEIF, AVIF (macOS 13+), JPEG XL (14+), TIFF, PSD, camera RAW —
/// through its C API. CGImageSourceCreateThumbnailAtIndex scales during decode and applies the EXIF orientation,
/// so the result is upright and only as large as asked for. Used when Skia has no codec for the file.
/// </summary>
internal static class MacImageDecoder
{
    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    private static readonly Lazy<Symbols?> Loaded = new(LoadSymbols);

    private sealed record Symbols(IntPtr FromImageAlways, IntPtr MaxPixelSize, IntPtr WithTransform, IntPtr True,
        IntPtr KeyCallbacks, IntPtr ValueCallbacks, IntPtr Srgb);

    private static Symbols? LoadSymbols()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            var io = NativeLibrary.Load(ImageIO);
            var cf = NativeLibrary.Load(CoreFoundation);
            var cg = NativeLibrary.Load(CoreGraphics);
            // Exported constants are pointers to the CFStringRef/CFBooleanRef values; the callback structs are used by address
            IntPtr Value(IntPtr lib, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, name));
            return new Symbols(
                Value(io, "kCGImageSourceCreateThumbnailFromImageAlways"),
                Value(io, "kCGImageSourceThumbnailMaxPixelSize"),
                Value(io, "kCGImageSourceCreateThumbnailWithTransform"),
                Value(cf, "kCFBooleanTrue"),
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"),
                Value(cg, "kCGColorSpaceSRGB"));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public static bool IsAvailable => Loaded.Value is not null;

    /// <summary>Upright BGRA decode whose longest edge is at most <paramref name="maxEdge"/>, or null if ImageIO can't read it.</summary>
    public static SKBitmap? Decode(string path, int maxEdge)
    {
        if (Loaded.Value is not { } s) return null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(path);
        var url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, bytes, bytes.Length, false);
        if (url == IntPtr.Zero) return null;
        IntPtr source = IntPtr.Zero, options = IntPtr.Zero, number = IntPtr.Zero, image = IntPtr.Zero;
        try
        {
            source = CGImageSourceCreateWithURL(url, IntPtr.Zero);
            if (source == IntPtr.Zero || CGImageSourceGetCount(source) == 0) return null;

            var edge = maxEdge;
            number = CFNumberCreate(IntPtr.Zero, 9 /* kCFNumberIntType */, ref edge);
            IntPtr[] keys = [s.FromImageAlways, s.MaxPixelSize, s.WithTransform];
            IntPtr[] values = [s.True, number, s.True];
            options = CFDictionaryCreate(IntPtr.Zero, keys, values, keys.Length, s.KeyCallbacks, s.ValueCallbacks);
            image = CGImageSourceCreateThumbnailAtIndex(source, 0, options);
            if (image == IntPtr.Zero) return null;

            return FromCGImage(image);
        }
        finally
        {
            foreach (var r in new[] { image, options, number, source, url })
                if (r != IntPtr.Zero) CFRelease(r);
        }
    }

    /// <summary>Draws a CGImage into a new BGRA premultiplied bitmap (sRGB). Doesn't release the image.</summary>
    internal static SKBitmap? FromCGImage(IntPtr image)
    {
        if (Loaded.Value is not { } s || image == IntPtr.Zero) return null;
        var w = (int)CGImageGetWidth(image);
        var h = (int)CGImageGetHeight(image);
        if (w <= 0 || h <= 0) return null;
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        var space = CGColorSpaceCreateWithName(s.Srgb);
        // BGRA premultiplied = alpha first, 32-bit little endian
        const uint bitmapInfo = 2 /* kCGImageAlphaPremultipliedFirst */ | (2 << 12) /* kCGBitmapByteOrder32Little */;
        var context = CGBitmapContextCreate(bitmap.GetPixels(), (nuint)w, (nuint)h, 8, (nuint)bitmap.RowBytes, space, bitmapInfo);
        try
        {
            if (context == IntPtr.Zero)
            {
                bitmap.Dispose();
                return null;
            }
            CGContextDrawImage(context, new CGRect(0, 0, w, h), image);
            return bitmap;
        }
        finally
        {
            if (context != IntPtr.Zero) CFRelease(context);
            if (space != IntPtr.Zero) CFRelease(space);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(double X, double Y, double Width, double Height);

    [DllImport(CoreFoundation)] private static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator, byte[] buffer, nint length, [MarshalAs(UnmanagedType.U1)] bool isDirectory);
    [DllImport(CoreFoundation)] private static extern IntPtr CFNumberCreate(IntPtr allocator, nint type, ref int value);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(CoreFoundation)] internal static extern void CFRelease(IntPtr obj);
    [DllImport(ImageIO)] private static extern IntPtr CGImageSourceCreateWithURL(IntPtr url, IntPtr options);
    [DllImport(ImageIO)] private static extern nuint CGImageSourceGetCount(IntPtr source);
    [DllImport(ImageIO)] private static extern IntPtr CGImageSourceCreateThumbnailAtIndex(IntPtr source, nuint index, IntPtr options);
    [DllImport(CoreGraphics)] private static extern nuint CGImageGetWidth(IntPtr image);
    [DllImport(CoreGraphics)] private static extern nuint CGImageGetHeight(IntPtr image);
    [DllImport(CoreGraphics)] private static extern IntPtr CGColorSpaceCreateWithName(IntPtr name);
    [DllImport(CoreGraphics)] private static extern IntPtr CGBitmapContextCreate(IntPtr data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, IntPtr space, uint bitmapInfo);
    [DllImport(CoreGraphics)] private static extern void CGContextDrawImage(IntPtr context, CGRect rect, IntPtr image);
}
