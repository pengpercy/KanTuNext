using System.Runtime.InteropServices;
using KanTuNext.Core.Ocr;

namespace KanTuNext.Desktop.Services.Ocr;

/// <summary>
/// macOS text recognition through Apple Vision (VNRecognizeTextRequest), called through the Objective-C runtime
/// directly: the synchronous perform path needs no blocks, so there is no native bridge library to build or ship.
/// Tuning follows Snapzy's Vision profiles: accurate level, zh-Hans + en-US with language correction and a 0.006
/// minimum text height; when that pass is weak (no lines, or mean confidence under 0.55), CJK and auto-language
/// recovery passes run and the best-scoring result wins.
/// </summary>
internal sealed class VisionOcrEngine : IOcrEngine
{
    private const float AcceptConfidence = 0.55f;

    private sealed record Profile(string[] Languages, bool AutoDetect, float MinimumTextHeight);

    private static readonly Profile Primary = new(["zh-Hans", "en-US"], false, 0.006f);
    private static readonly Profile[] Recovery =
    [
        new(["zh-Hans", "zh-Hant", "ja-JP", "ko-KR", "en-US"], false, 0.006f),
        new([], true, 0.008f),
    ];

    private static readonly Lazy<bool> Loaded = new(() =>
        dlopen("/System/Library/Frameworks/Vision.framework/Vision", 2) != IntPtr.Zero &&
        objc_getClass("VNRecognizeTextRequest") != IntPtr.Zero);

    public bool IsAvailable => OperatingSystem.IsMacOSVersionAtLeast(12) && Loaded.Value;

    /// <param name="orientation">EXIF orientation (1–8; same values as CGImagePropertyOrientation), so the
    /// returned boxes are relative to the upright photo.</param>
    public Task<OcrResult> RecognizeAsync(string imagePath, int orientation, CancellationToken cancel = default) =>
        Task.Run(() =>
        {
            if (!IsAvailable) return OcrResult.Empty;
            var best = Run(imagePath, orientation, Primary);
            if (best.Lines.Count > 0 && best.AverageConfidence >= AcceptConfidence) return best;
            foreach (var profile in Recovery)
            {
                cancel.ThrowIfCancellationRequested();
                var candidate = Run(imagePath, orientation, profile);
                if (candidate.Score > best.Score) best = candidate;
            }
            return best;
        }, cancel);

    public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancel = default) =>
        RecognizeAsync(imagePath, 1, cancel);

    private static OcrResult Run(string path, int orientation, Profile profile)
    {
        var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        var utf8 = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            var url = Send(Class("NSURL"), Sel("fileURLWithPath:"), Send(Class("NSString"), Sel("stringWithUTF8String:"), utf8));
            var options = Send(Class("NSDictionary"), Sel("dictionary"));
            var handler = Send(Send(Class("VNImageRequestHandler"), Sel("alloc")), Sel("initWithURL:orientation:options:"),
                url, (nint)Math.Clamp(orientation, 1, 8), options);
            var request = Send(Send(Class("VNRecognizeTextRequest"), Sel("alloc")), Sel("init"));
            try
            {
                SendNint(request, Sel("setRecognitionLevel:"), 0); // VNRequestTextRecognitionLevelAccurate
                SendBool(request, Sel("setUsesLanguageCorrection:"), 1);
                SendFloat(request, Sel("setMinimumTextHeight:"), profile.MinimumTextHeight);
                if (profile.Languages.Length > 0)
                    Send(request, Sel("setRecognitionLanguages:"), StringArray(profile.Languages));
                if (RespondsTo(request, "setAutomaticallyDetectsLanguage:")) // macOS 13+
                    SendBool(request, Sel("setAutomaticallyDetectsLanguage:"), (byte)(profile.AutoDetect ? 1 : 0));

                var requests = Send(Class("NSArray"), Sel("arrayWithObject:"), request);
                if (SendPerform(handler, Sel("performRequests:error:"), requests, out _) == 0) return OcrResult.Empty;

                var results = Send(request, Sel("results"));
                var lines = new List<OcrLine>();
                for (nuint i = 0, n = Count(results); i < n; i++)
                {
                    var observation = At(results, i);
                    var candidates = SendNuint(observation, Sel("topCandidates:"), 1);
                    if (Count(candidates) == 0) continue;
                    var top = At(candidates, 0);
                    var text = Marshal.PtrToStringUTF8(Send(Send(top, Sel("string")), Sel("UTF8String"))) ?? "";
                    if (text.Length == 0) continue;
                    var confidence = SendFloatRet(top, Sel("confidence"));
                    var box = Frame(observation, Sel("boundingBox"));
                    // Vision boxes are normalized with the origin bottom-left
                    lines.Add(new OcrLine(text, new OcrRect(box.X, 1 - box.Y - box.Height, box.Width, box.Height), confidence));
                }
                return new OcrResult(lines);
            }
            finally
            {
                Send(request, Sel("release"));
                Send(handler, Sel("release"));
            }
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException or SEHException)
        {
            return OcrResult.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
            Send(pool, Sel("drain"));
        }
    }

    private static IntPtr StringArray(string[] values)
    {
        var items = new IntPtr[values.Length];
        var buffers = new IntPtr[values.Length];
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                buffers[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
                items[i] = Send(Class("NSString"), Sel("stringWithUTF8String:"), buffers[i]);
            }
            var pinned = GCHandle.Alloc(items, GCHandleType.Pinned);
            try
            {
                return SendArray(Class("NSArray"), Sel("arrayWithObjects:count:"), pinned.AddrOfPinnedObject(), (nuint)items.Length);
            }
            finally
            {
                pinned.Free();
            }
        }
        finally
        {
            foreach (var b in buffers) Marshal.FreeCoTaskMem(b);
        }
    }

    private static bool RespondsTo(IntPtr obj, string selector) => SendSelBool(obj, Sel("respondsToSelector:"), Sel(selector)) != 0;
    private static nuint Count(IntPtr array) => SendNuintRet(array, Sel("count"));
    private static IntPtr At(IntPtr array, nuint index) => SendNuint(array, Sel("objectAtIndex:"), index);
    private static IntPtr Class(string name) => objc_getClass(name);
    private static IntPtr Sel(string name) => sel_registerName(name);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X, Y, Width, Height;
    }

    /// <summary>CGRect is returned in registers on arm64 but through a hidden pointer (stret) on x86_64.</summary>
    private static CGRect Frame(IntPtr obj, IntPtr selector)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            objc_msgSend_stret(out var rect, obj, selector);
            return rect;
        }
        return SendRect(obj, selector);
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a, nint b, IntPtr c);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendArray(IntPtr obj, IntPtr sel, IntPtr items, nuint count);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendNuint(IntPtr obj, IntPtr sel, nuint a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nuint SendNuintRet(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendNint(IntPtr obj, IntPtr sel, nint a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendBool(IntPtr obj, IntPtr sel, byte a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendFloat(IntPtr obj, IntPtr sel, float a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern float SendFloatRet(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte SendSelBool(IntPtr obj, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte SendPerform(IntPtr obj, IntPtr sel, IntPtr a, out IntPtr error);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRect(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void objc_msgSend_stret(out CGRect rect, IntPtr obj, IntPtr sel);
}
