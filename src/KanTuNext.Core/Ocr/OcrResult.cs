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

/// <summary>Text clean-up shared by engines that split CJK text into one "word" per character.</summary>
public static class OcrText
{
    /// <summary>
    /// Joins words with spaces, except between two CJK characters (and around CJK punctuation), so
    /// "离 线 看 图 KanTuNext 0.1" becomes "离线看图 KanTuNext 0.1". Windows OCR and tesseract both emit
    /// CJK text one character per word.
    /// </summary>
    public static string JoinWords(IEnumerable<string> words)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var raw in words)
        {
            var word = raw.Trim();
            if (word.Length == 0) continue;
            var previous = sb.Length > 0 ? sb[^1] : '\0';
            if (sb.Length > 0 && !(IsCjk(previous) && IsCjk(word[0])) && !IsCjkPunctuation(previous) && !IsCjkPunctuation(word[0]))
                sb.Append(' ');
            sb.Append(word);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Cleans up the text of part of a line recognized on its own: a crop's edges can catch slivers of the
    /// neighbouring characters, read as stray letters ("J文字后可以F"). Drops up to two characters from either end
    /// until what's left appears in the whole line's text (spaces between CJK characters ignored); keeps the
    /// recognized text if nothing does.
    /// </summary>
    public static string Reconcile(string recognized, string line)
    {
        recognized = recognized.Trim();
        var whole = CollapseCjkSpaces(line);
        string? best = null;
        var bestCut = int.MaxValue;
        for (var lead = 0; lead <= 2; lead++)
        for (var trail = 0; trail <= 2; trail++)
        {
            if (lead + trail >= recognized.Length || lead + trail >= bestCut) continue;
            var candidate = recognized[lead..^trail].Trim();
            if (candidate.Length == 0 || !whole.Contains(CollapseCjkSpaces(candidate), StringComparison.Ordinal)) continue;
            best = candidate;
            bestCut = lead + trail;
        }
        return best ?? recognized;
    }

    /// <summary>Removes the spaces an engine put between CJK characters inside an already-joined line.</summary>
    public static string CollapseCjkSpaces(string line) => JoinWords(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>CJK and full-width punctuation hugs its neighbours: no space on either side.</summary>
    public static bool IsCjkPunctuation(char c) => c is >= '\u3000' and <= '\u303F' or >= '\uFF00' and <= '\uFFEF';

    /// <summary>Han, kana, hangul, and CJK / full-width punctuation.</summary>
    public static bool IsCjk(char c) =>
        c is >= '一' and <= '鿿'   // CJK unified ideographs
          or >= '㐀' and <= '䶿'   // extension A
          or >= '　' and <= '〿'   // CJK punctuation
          or >= '぀' and <= 'ヿ'   // hiragana, katakana
          or >= '가' and <= '힯'   // hangul syllables
          or >= '＀' and <= '￯';  // full-width forms
}
