namespace KanTuNext.Core.Ocr;

/// <summary>
/// One recognized line. <see cref="Bounds"/> is normalized to the upright image: 0..1 on both axes,
/// origin top-left, so it maps onto any zoom/rotation of the displayed photo.
/// </summary>
public sealed record OcrLine(string Text, OcrRect Bounds, float Confidence);

public readonly record struct OcrRect(double X, double Y, double Width, double Height)
{
    public double MidY => Y + Height / 2;
    public double Right => X + Width;
}

public sealed record OcrResult(IReadOnlyList<OcrLine> Lines)
{
    public static OcrResult Empty { get; } = new([]);

    /// <summary>Lines in reading order, joined with newlines; fragments on one visual row joined with spaces.</summary>
    public string Text => OcrLayout.Join(Lines);

    public float AverageConfidence => Lines.Count == 0 ? 0 : Lines.Average(l => l.Confidence);

    /// <summary>How much trustworthy text a pass found: confidence-weighted character count (picks the best pass).</summary>
    public double Score => Lines.Sum(l => l.Confidence * l.Text.Length);
}

/// <summary>Turns engine output into reading order, as Snapzy does for Vision results.</summary>
public static class OcrLayout
{
    /// <summary>
    /// Groups lines whose vertical centres are within 85% of the taller one's height into one row, orders rows
    /// top to bottom and fragments left to right. Engines report lines in their own order (Vision: roughly by
    /// confidence/columns), which reads badly for screenshots with side-by-side text.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<OcrLine>> Rows(IEnumerable<OcrLine> lines)
    {
        var rows = new List<List<OcrLine>>();
        foreach (var line in lines.OrderBy(l => l.Bounds.MidY))
        {
            var row = rows.FirstOrDefault(r =>
            {
                var top = r.Min(l => l.Bounds.Y);
                var bottom = r.Max(l => l.Bounds.Y + l.Bounds.Height);
                var tolerance = Math.Max(line.Bounds.Height, bottom - top) * 0.85;
                return Math.Abs(line.Bounds.MidY - (top + bottom) / 2) <= tolerance / 2 + 1e-9;
            });
            if (row is null) rows.Add([line]);
            else row.Add(line);
        }
        return rows.Select(r => (IReadOnlyList<OcrLine>)r.OrderBy(l => l.Bounds.X).ToArray()).ToArray();
    }

    public static string Join(IEnumerable<OcrLine> lines) =>
        string.Join("\n", Rows(lines).Select(row => string.Join(" ", row.Select(l => l.Text.Trim()))));
}

/// <summary>A platform's text recognizer.</summary>
public interface IOcrEngine
{
    /// <summary>Whether this machine can run it (OS version, installed language data, libraries).</summary>
    bool IsAvailable { get; }

    /// <summary>Recognizes text in an image file. Runs off the UI thread; never throws for unreadable images.</summary>
    Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancel = default);
}
