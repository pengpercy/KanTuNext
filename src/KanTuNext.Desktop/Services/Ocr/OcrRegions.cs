using KanTuNext.Core.Ocr;
using SkiaSharp;

namespace KanTuNext.Desktop.Services.Ocr;

/// <summary>
/// Recognizes parts of a photo again on their own. Engines only report whole lines, so a selection that starts or
/// ends inside a line is placed by estimated character widths; recognizing just that part gives its exact text.
/// </summary>
internal static class OcrRegions
{
    /// <summary>Text below this height (pixels) is enlarged before recognition: engines read small text poorly.</summary>
    private const int MinTextHeight = 48;

    /// <summary>
    /// The text in each region (normalized to the upright photo), or null where nothing was recognized. Decodes
    /// the photo once; each crop goes to a temporary PNG, since the engines read files.
    /// </summary>
    public static async Task<string?[]> RecognizeAsync(string path, IReadOnlyList<OcrRect> regions, CancellationToken cancel = default)
    {
        var results = new string?[regions.Count];
        using var photo = await Task.Run(() => ImageLoader.DecodeUpright(path, ImageLoader.FullEdge), cancel);
        if (photo is null) return results;
        for (var i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            var crop = SKRectI.Round(new SKRect((float)(r.X * photo.Width), (float)(r.Y * photo.Height),
                (float)(r.Right * photo.Width), (float)((r.Y + r.Height) * photo.Height)));
            crop.Intersect(new SKRectI(0, 0, photo.Width, photo.Height));
            if (crop.Width < 2 || crop.Height < 2) continue;
            var file = Path.Combine(Path.GetTempPath(), $"kantunext-ocr-{Guid.NewGuid():N}.png");
            try
            {
                await Task.Run(() => WriteCrop(photo, crop, file), cancel);
                var result = await OcrEngines.RecognizeAsync(file, 1, cancel);
                if (result.Lines.Count > 0) results[i] = result.Text.Replace('\n', ' ').Trim();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return results;
    }

    /// <summary>The crop on a margin of its own edge colour, enlarged when the text is small.</summary>
    private static void WriteCrop(SKBitmap photo, SKRectI crop, string file)
    {
        var scale = Math.Clamp(MinTextHeight / (crop.Height * 0.6f), 1f, 4f);
        var margin = (int)(crop.Height * scale * 0.5f);
        var width = (int)(crop.Width * scale) + margin * 2;
        var height = (int)(crop.Height * scale) + margin * 2;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        // Breathing room around the text, in the background colour next to it (top-left pixel of the crop)
        canvas.Clear(photo.GetPixel(crop.Left, crop.Top));
        using var image = SKImage.FromBitmap(photo);
        canvas.DrawImage(image, crop, SKRect.Create(margin, margin, crop.Width * scale, crop.Height * scale),
            new SKSamplingOptions(SKCubicResampler.Mitchell), null);
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(file);
        data.SaveTo(stream);
    }
}
