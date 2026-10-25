using KanTuNext.Core.Ocr;

namespace KanTuNext.Desktop.Services.Ocr;

/// <summary>
/// The text recognizer for this platform, or none (the OCR button hides): Vision on macOS, Windows.Media.Ocr on
/// Windows 10+ (not in the Windows 7 build), tesseract on Linux.
/// </summary>
internal static class OcrEngines
{
    private static readonly Lazy<VisionOcrEngine?> Mac = new(() =>
        OperatingSystem.IsMacOS() && new VisionOcrEngine() is { IsAvailable: true } engine ? engine : null);
#if WINDOWS && !WIN7
    private static readonly Lazy<WindowsOcrEngine?> Windows = new(() =>
        OperatingSystem.IsWindows() && new WindowsOcrEngine() is { IsAvailable: true } engine ? engine : null);
#endif
    private static readonly Lazy<TesseractOcrEngine?> Linux = new(() =>
        OperatingSystem.IsLinux() && new TesseractOcrEngine() is { IsAvailable: true } engine ? engine : null);

    private static IOcrEngine? Engine =>
        OperatingSystem.IsMacOS() ? Mac.Value :
#if WINDOWS && !WIN7
        OperatingSystem.IsWindows() ? Windows.Value :
#endif
        OperatingSystem.IsLinux() ? Linux.Value : null;

    public static bool IsAvailable => Engine is not null;

    /// <param name="orientation">EXIF orientation of the file, so boxes line up with the upright photo
    /// (the Windows and Linux engines read it from the file themselves).</param>
    public static Task<OcrResult> RecognizeAsync(string path, int orientation, CancellationToken cancel = default) => Engine switch
    {
        VisionOcrEngine vision => vision.RecognizeAsync(path, orientation, cancel),
        { } engine => engine.RecognizeAsync(path, cancel),
        null => Task.FromResult(OcrResult.Empty),
    };
}
