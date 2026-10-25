using KanTuNext.Core.Ocr;

namespace KanTuNext.Desktop.Services.Ocr;

/// <summary>The text recognizer for this platform, or null where none is available (the OCR button hides).</summary>
internal static class OcrEngines
{
    private static readonly Lazy<VisionOcrEngine?> Mac = new(() =>
        OperatingSystem.IsMacOS() && new VisionOcrEngine() is { IsAvailable: true } engine ? engine : null);

    public static bool IsAvailable => OperatingSystem.IsMacOS() && Mac.Value is not null;

    /// <param name="orientation">EXIF orientation of the file, so boxes line up with the upright photo.</param>
    public static Task<OcrResult> RecognizeAsync(string path, int orientation, CancellationToken cancel = default)
    {
        if (OperatingSystem.IsMacOS() && Mac.Value is { } vision) return vision.RecognizeAsync(path, orientation, cancel);
        return Task.FromResult(OcrResult.Empty);
    }
}
