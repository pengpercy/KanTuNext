using System.Runtime.InteropServices;
using SkiaSharp;

namespace KanTuNext.Desktop.Services;

/// <summary>
/// macOS: the frames of a Live Photo's motion clip, through AVAssetImageGenerator (synchronous copyCGImageAtTime,
/// so no blocks are needed), upright per the track's transform and at most 800 px — about 50 MB for a 3 s clip.
/// </summary>
internal static class MacLivePhoto
{
    public const double FramesPerSecond = 12;
    private const int MaxEdge = 800;
    private const int MaxFrames = 72;

    [StructLayout(LayoutKind.Sequential)]
    private struct CMTime
    {
        public long Value;
        public int Timescale;
        public uint Flags; // 1 = valid
        public long Epoch;

        public static CMTime FromSeconds(double seconds) => new() { Value = (long)(seconds * 600), Timescale = 600, Flags = 1 };
        public readonly double Seconds => Timescale > 0 ? (double)Value / Timescale : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width, Height;
    }

    private static readonly Lazy<bool> Loaded = new(() =>
        OperatingSystem.IsMacOS() &&
        NativeLibrary.TryLoad("/System/Library/Frameworks/AVFoundation.framework/AVFoundation", out _) &&
        objc_getClass("AVAssetImageGenerator") != IntPtr.Zero);

    /// <summary>The clip's frames, or null if it can't be read.</summary>
    public static IReadOnlyList<SKBitmap>? Frames(string clipPath) => Extract(clipPath, MaxEdge, MaxFrames) is { Count: > 1 } f ? f : null;

    /// <summary>One frame near the start of a video (thumbnails), at most <paramref name="maxEdge"/>.</summary>
    public static SKBitmap? FirstFrame(string videoPath, int maxEdge) => Extract(videoPath, maxEdge, 1)?.FirstOrDefault();

    private static List<SKBitmap>? Extract(string clipPath, int maxEdge, int maxFrames)
    {
        if (!Loaded.Value) return null;
        var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        var utf8 = Marshal.StringToCoTaskMemUTF8(clipPath);
        var frames = new List<SKBitmap>();
        try
        {
            var url = Send(Class("NSURL"), Sel("fileURLWithPath:"), Send(Class("NSString"), Sel("stringWithUTF8String:"), utf8));
            var asset = Send(Class("AVURLAsset"), Sel("URLAssetWithURL:options:"), url, IntPtr.Zero);
            if (asset == IntPtr.Zero) return null;
            var duration = Duration(asset).Seconds;
            if (duration <= 0) return null;

            var generator = Send(Class("AVAssetImageGenerator"), Sel("assetImageGeneratorWithAsset:"), asset);
            SendBool(generator, Sel("setAppliesPreferredTrackTransform:"), 1);
            SendSize(generator, Sel("setMaximumSize:"), new CGSize { Width = maxEdge, Height = maxEdge });
            var exact = CMTime.FromSeconds(0);
            SendTime(generator, Sel("setRequestedTimeToleranceBefore:"), exact);
            SendTime(generator, Sel("setRequestedTimeToleranceAfter:"), exact);

            var count = Math.Min(maxFrames, Math.Max(1, (int)(duration * FramesPerSecond)));
            for (var i = 0; i < count; i++)
            {
                // A single frame (thumbnail) is taken a little in, past fade-ins and black first frames
                var at = maxFrames == 1 ? Math.Min(1.0, duration / 3) : i / FramesPerSecond;
                var image = CopyImage(generator, Sel("copyCGImageAtTime:actualTime:error:"), CMTime.FromSeconds(at), IntPtr.Zero, IntPtr.Zero);
                if (image == IntPtr.Zero) continue;
                try
                {
                    if (MacImageDecoder.FromCGImage(image) is { } bitmap) frames.Add(bitmap);
                }
                finally
                {
                    MacImageDecoder.CFRelease(image); // "copy": the caller owns it
                }
            }
            return frames.Count > 0 ? frames : null;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
            Send(pool, Sel("drain"));
        }
    }

    /// <summary>CMTime (24 bytes) comes back through a hidden pointer: objc_msgSend_stret on x86_64.</summary>
    private static CMTime Duration(IntPtr asset)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            SendTimeStret(out var time, asset, Sel("duration"));
            return time;
        }
        return SendTimeRet(asset, Sel("duration"));
    }

    private static IntPtr Class(string name) => objc_getClass(name);
    private static IntPtr Sel(string name) => sel_registerName(name);

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendBool(IntPtr obj, IntPtr sel, byte a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendSize(IntPtr obj, IntPtr sel, CGSize a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendTime(IntPtr obj, IntPtr sel, CMTime a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CMTime SendTimeRet(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void SendTimeStret(out CMTime result, IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr CopyImage(IntPtr obj, IntPtr sel, CMTime time, IntPtr actual, IntPtr error);
}
