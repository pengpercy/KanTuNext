using System.Runtime.InteropServices;
using SkiaSharp;

namespace KanTuNext.Desktop.Services;

/// <summary>
/// HEIC/HEIF and AVIF through libheif, which the Windows and Linux packages bundle with libde265 (HEVC) and dav1d
/// (AV1) built in, so no system codec or store extension is needed. libheif applies the irot/imir transforms, so
/// results are upright; an embedded thumbnail is decoded instead of the full image when it's big enough.
/// </summary>
internal static class HeifDecoder
{
    private const string Library = "kantunext-heif";
    private const int ColorspaceRgb = 1, ChromaInterleavedRgba = 11, ChannelInterleaved = 10;
    private const int FiletypeYesSupported = 1, FiletypeMaybe = 3;

    private static readonly Lazy<IntPtr> Handle = new(Load);
    // Separate from Load: calling into the library from inside the resolver would re-enter Handle
    private static readonly Lazy<bool> Initialized = new(() => heif_init(IntPtr.Zero).Code == 0);

    static HeifDecoder()
    {
        NativeLibraries.Register(Library, () => Handle.Value);
    }

    public static bool IsAvailable => Handle.Value != IntPtr.Zero && Initialized.Value;

    private static IntPtr Load()
    {
        var name = OperatingSystem.IsWindows() ? "heif.dll" : OperatingSystem.IsMacOS() ? "libheif.1.dylib" : "libheif.so.1";
        return NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, name), out var handle) ? handle : IntPtr.Zero;
    }

    /// <summary>Upright BGRA decode whose longest edge is at most <paramref name="maxEdge"/>, or null if libheif can't read it.</summary>
    public static SKBitmap? Decode(string path, int maxEdge)
    {
        if (!IsAvailable) return null;
        byte[] data;
        try
        {
            // The ftyp box is in the first 12 bytes: rule out other formats before reading the whole file
            using (var stream = File.OpenRead(path))
            {
                var head = new byte[12];
                if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return null;
                if (heif_check_filetype(head, head.Length) is not (FiletypeYesSupported or FiletypeMaybe)) return null;
            }
            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        IntPtr context = heif_context_alloc(), primary = IntPtr.Zero, thumbnail = IntPtr.Zero, image = IntPtr.Zero;
        try
        {
            if (context == IntPtr.Zero) return null;
            if (heif_context_read_from_memory_without_copy(context, pin.AddrOfPinnedObject(), (nuint)data.Length, IntPtr.Zero).Code != 0) return null;
            if (heif_context_get_primary_image_handle(context, out primary).Code != 0) return null;

            thumbnail = SmallestThumbnail(primary, maxEdge);
            if (heif_decode_image(thumbnail != IntPtr.Zero ? thumbnail : primary, out image, ColorspaceRgb, ChromaInterleavedRgba, IntPtr.Zero).Code != 0)
                return null;
            var pixels = heif_image_get_plane_readonly2(image, ChannelInterleaved, out var stride);
            int width = heif_image_get_width(image, ChannelInterleaved), height = heif_image_get_height(image, ChannelInterleaved);
            if (pixels == IntPtr.Zero || width <= 0 || height <= 0) return null;

            using var source = new SKBitmap();
            if (!source.InstallPixels(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul), pixels, (int)stride))
                return null;
            // One pass converts to BGRA premultiplied and scales down to the limit
            var scale = Math.Min(1.0, (double)maxEdge / Math.Max(width, height));
            var target = new SKImageInfo(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)),
                SKColorType.Bgra8888, SKAlphaType.Premul);
            return source.Resize(target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        finally
        {
            if (image != IntPtr.Zero) heif_image_release(image);
            if (thumbnail != IntPtr.Zero) heif_image_handle_release(thumbnail);
            if (primary != IntPtr.Zero) heif_image_handle_release(primary);
            if (context != IntPtr.Zero) heif_context_free(context);
            pin.Free();
        }
    }

    /// <summary>
    /// The smallest embedded thumbnail whose longest edge still reaches <paramref name="maxEdge"/> (phones store
    /// a small one beside a large tiled image); one with a different orientation than the image is skipped.
    /// </summary>
    private static IntPtr SmallestThumbnail(IntPtr primary, int maxEdge)
    {
        var count = heif_image_handle_get_number_of_thumbnails(primary);
        if (count <= 0) return IntPtr.Zero;
        var ids = new uint[count];
        count = heif_image_handle_get_list_of_thumbnail_IDs(primary, ids, count);
        var landscape = heif_image_handle_get_width(primary) >= heif_image_handle_get_height(primary);

        IntPtr best = IntPtr.Zero;
        var bestEdge = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            if (heif_image_handle_get_thumbnail(primary, ids[i], out var candidate).Code != 0) continue;
            int w = heif_image_handle_get_width(candidate), h = heif_image_handle_get_height(candidate), edge = Math.Max(w, h);
            if (edge >= maxEdge && edge < bestEdge && (w >= h) == landscape)
            {
                if (best != IntPtr.Zero) heif_image_handle_release(best);
                best = candidate;
                bestEdge = edge;
            }
            else heif_image_handle_release(candidate);
        }
        return best;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct HeifError
    {
        public readonly int Code;
        public readonly int Subcode;
        public readonly IntPtr Message;
    }

    [DllImport(Library)] private static extern HeifError heif_init(IntPtr parameters);
    [DllImport(Library)] private static extern int heif_check_filetype(byte[] data, int length);
    [DllImport(Library)] private static extern IntPtr heif_context_alloc();
    [DllImport(Library)] private static extern void heif_context_free(IntPtr context);
    [DllImport(Library)] private static extern HeifError heif_context_read_from_memory_without_copy(IntPtr context, IntPtr memory, nuint size, IntPtr options);
    [DllImport(Library)] private static extern HeifError heif_context_get_primary_image_handle(IntPtr context, out IntPtr handle);
    [DllImport(Library)] private static extern int heif_image_handle_get_width(IntPtr handle);
    [DllImport(Library)] private static extern int heif_image_handle_get_height(IntPtr handle);
    [DllImport(Library)] private static extern int heif_image_handle_get_number_of_thumbnails(IntPtr handle);
    [DllImport(Library)] private static extern int heif_image_handle_get_list_of_thumbnail_IDs(IntPtr handle, uint[] ids, int count);
    [DllImport(Library)] private static extern HeifError heif_image_handle_get_thumbnail(IntPtr handle, uint id, out IntPtr thumbnail);
    [DllImport(Library)] private static extern void heif_image_handle_release(IntPtr handle);
    [DllImport(Library)] private static extern HeifError heif_decode_image(IntPtr handle, out IntPtr image, int colorspace, int chroma, IntPtr options);
    [DllImport(Library)] private static extern IntPtr heif_image_get_plane_readonly2(IntPtr image, int channel, out nuint stride);
    [DllImport(Library)] private static extern int heif_image_get_width(IntPtr image, int channel);
    [DllImport(Library)] private static extern int heif_image_get_height(IntPtr image, int channel);
    [DllImport(Library)] private static extern void heif_image_release(IntPtr image);
}
