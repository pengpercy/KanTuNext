using System.Runtime.InteropServices;
using KanTuNext.Core.Ocr;
using SkiaSharp;

namespace KanTuNext.Desktop.Services.Ocr;

/// <summary>
/// Linux text recognition with tesseract's C API. Uses the copy bundled under ocr/ next to the executable when
/// present (libraries plus tessdata_fast chi_sim + eng), otherwise a system libtesseract and its tessdata.
/// </summary>
internal sealed class TesseractOcrEngine : IOcrEngine
{
    private const string Library = "kantunext-tesseract";
    private const int MaxEdge = 4000; // larger photos gain nothing for text and cost seconds
    private const int PageSegAuto = 3, LevelTextLine = 2;

    private static readonly object Gate = new();
    private static readonly string BundledDir = Path.Combine(AppContext.BaseDirectory, "ocr");
    private static readonly Lazy<(string? Data, string Languages)> Setup = new(FindSetup);

    static TesseractOcrEngine()
    {
        NativeLibraries.Register(Library, TryLoad);
    }

    public bool IsAvailable => OperatingSystem.IsLinux() && Setup.Value.Data is not null && TryLoad() != IntPtr.Zero;

    public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancel = default) => Task.Run(() =>
    {
        if (!IsAvailable) return OcrResult.Empty;
        using var bitmap = ImageLoader.DecodeUpright(imagePath, MaxEdge, SKColorType.Rgba8888);
        if (bitmap is null) return OcrResult.Empty;
        cancel.ThrowIfCancellationRequested();
        // One recognizer at a time: tesseract instances are heavy and the viewer only ever asks for one photo
        lock (Gate) return Recognize(bitmap);
    }, cancel);

    private static OcrResult Recognize(SKBitmap bitmap)
    {
        var (data, languages) = Setup.Value;
        var api = TessBaseAPICreate();
        try
        {
            if (TessBaseAPIInit3(api, data!, languages) != 0) return OcrResult.Empty;
            TessBaseAPISetPageSegMode(api, PageSegAuto);
            TessBaseAPISetImage(api, bitmap.GetPixels(), bitmap.Width, bitmap.Height, 4, bitmap.RowBytes);
            if (TessBaseAPIRecognize(api, IntPtr.Zero) != 0) return OcrResult.Empty;

            var lines = new List<OcrLine>();
            var it = TessBaseAPIGetIterator(api);
            if (it == IntPtr.Zero) return OcrResult.Empty;
            try
            {
                var page = TessResultIteratorGetPageIterator(it);
                do
                {
                    var textPtr = TessResultIteratorGetUTF8Text(it, LevelTextLine);
                    if (textPtr == IntPtr.Zero) continue;
                    var text = Marshal.PtrToStringUTF8(textPtr)?.Trim() ?? "";
                    TessDeleteText(textPtr);
                    if (text.Length == 0) continue;
                    if (TessPageIteratorBoundingBox(page, LevelTextLine, out var l, out var t, out var r, out var b) == 0) continue;
                    var confidence = TessResultIteratorConfidence(it, LevelTextLine) / 100f;
                    lines.Add(new OcrLine(OcrText.CollapseCjkSpaces(text),
                        new OcrRect((double)l / bitmap.Width, (double)t / bitmap.Height,
                            (double)(r - l) / bitmap.Width, (double)(b - t) / bitmap.Height), confidence));
                } while (TessResultIteratorNext(it, LevelTextLine) != 0);
            }
            finally
            {
                TessResultIteratorDelete(it);
            }
            return new OcrResult(lines);
        }
        finally
        {
            TessBaseAPIEnd(api);
            TessBaseAPIDelete(api);
        }
    }

    /// <summary>tessdata folder and "chi_sim+eng" (or whichever of the two is installed).</summary>
    private static (string?, string) FindSetup()
    {
        string?[] candidates =
        [
            Path.Combine(BundledDir, "tessdata"),
            Environment.GetEnvironmentVariable("TESSDATA_PREFIX"),
            "/usr/share/tesseract-ocr/5/tessdata",
            "/usr/share/tesseract-ocr/4.00/tessdata",
            "/usr/share/tessdata",
            "/usr/local/share/tessdata",
        ];
        foreach (var dir in candidates)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var languages = new[] { "chi_sim", "eng" }.Where(l => File.Exists(Path.Combine(dir, l + ".traineddata"))).ToArray();
            if (languages.Length > 0) return (dir, string.Join('+', languages));
        }
        return (null, "");
    }

    private static IntPtr _handle;

    private static IntPtr TryLoad()
    {
        if (_handle != IntPtr.Zero) return _handle;
        string[] names =
        [
            Path.Combine(BundledDir, "libtesseract.so.4"),
            Path.Combine(BundledDir, "libtesseract.so.5"),
            "libtesseract.so.5",
            "libtesseract.so.4",
            "libtesseract.so",
        ];
        foreach (var name in names)
            if (NativeLibrary.TryLoad(name, out var handle)) return _handle = handle;
        return IntPtr.Zero;
    }

    [DllImport(Library)] private static extern IntPtr TessBaseAPICreate();
    [DllImport(Library)] private static extern int TessBaseAPIInit3(IntPtr api, string datapath, string language);
    [DllImport(Library)] private static extern void TessBaseAPISetPageSegMode(IntPtr api, int mode);
    [DllImport(Library)] private static extern void TessBaseAPISetImage(IntPtr api, IntPtr data, int width, int height, int bytesPerPixel, int bytesPerLine);
    [DllImport(Library)] private static extern int TessBaseAPIRecognize(IntPtr api, IntPtr monitor);
    [DllImport(Library)] private static extern IntPtr TessBaseAPIGetIterator(IntPtr api);
    [DllImport(Library)] private static extern IntPtr TessResultIteratorGetPageIterator(IntPtr iterator);
    [DllImport(Library)] private static extern IntPtr TessResultIteratorGetUTF8Text(IntPtr iterator, int level);
    [DllImport(Library)] private static extern float TessResultIteratorConfidence(IntPtr iterator, int level);
    [DllImport(Library)] private static extern int TessPageIteratorBoundingBox(IntPtr iterator, int level, out int left, out int top, out int right, out int bottom);
    [DllImport(Library)] private static extern int TessResultIteratorNext(IntPtr iterator, int level);
    [DllImport(Library)] private static extern void TessResultIteratorDelete(IntPtr iterator);
    [DllImport(Library)] private static extern void TessDeleteText(IntPtr text);
    [DllImport(Library)] private static extern void TessBaseAPIEnd(IntPtr api);
    [DllImport(Library)] private static extern void TessBaseAPIDelete(IntPtr api);
}
