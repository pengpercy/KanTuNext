#if WINDOWS && !WIN7
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using KanTuNext.Core.Ocr;
using OcrResult = KanTuNext.Core.Ocr.OcrResult;

namespace KanTuNext.Desktop.Services.Ocr;

/// <summary>
/// Windows 10+ text recognition with the built-in Windows.Media.Ocr (offline; uses the OCR language packs that
/// come with the installed display languages). Prefers Simplified Chinese, which also reads Latin text.
/// </summary>
internal sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly Lazy<OcrEngine?> _engine = new(Create);

    public bool IsAvailable => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) && _engine.Value is not null;

    private static OcrEngine? Create()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) return null;
        try
        {
            var chinese = new Windows.Globalization.Language("zh-Hans-CN");
            return OcrEngine.IsLanguageSupported(chinese)
                ? OcrEngine.TryCreateFromLanguage(chinese)
                : OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancel = default)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) || _engine.Value is not { } engine) return OcrResult.Empty;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(imagePath);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            // Respect EXIF orientation so boxes match the upright photo; shrink past the engine's size limit
            var transform = new BitmapTransform();
            var max = OcrEngine.MaxImageDimension;
            var (w, h) = ((double)decoder.OrientedPixelWidth, (double)decoder.OrientedPixelHeight);
            if (w > max || h > max)
            {
                var scale = max / Math.Max(w, h);
                transform.ScaledWidth = (uint)(decoder.PixelWidth * scale);
                transform.ScaledHeight = (uint)(decoder.PixelHeight * scale);
            }
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            cancel.ThrowIfCancellationRequested();
            var result = await engine.RecognizeAsync(bitmap);

            var lines = new List<KanTuNext.Core.Ocr.OcrLine>();
            double bw = bitmap.PixelWidth, bh = bitmap.PixelHeight;
            foreach (var line in result.Lines)
            {
                if (line.Words.Count == 0) continue;
                double left = double.MaxValue, top = double.MaxValue, right = 0, bottom = 0;
                foreach (var word in line.Words)
                {
                    var r = word.BoundingRect;
                    left = Math.Min(left, r.X);
                    top = Math.Min(top, r.Y);
                    right = Math.Max(right, r.X + r.Width);
                    bottom = Math.Max(bottom, r.Y + r.Height);
                }
                var text = OcrText.JoinWords(line.Words.Select(word => word.Text));
                // Windows OCR reports no confidence; lines it returns are taken as certain
                lines.Add(new KanTuNext.Core.Ocr.OcrLine(text,
                    new OcrRect(left / bw, top / bh, (right - left) / bw, (bottom - top) / bh), 1f));
            }
            return new OcrResult(lines);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return OcrResult.Empty;
        }
    }
}
#endif
