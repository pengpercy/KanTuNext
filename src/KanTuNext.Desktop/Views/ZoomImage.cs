using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using KanTuNext.Core.Ocr;

namespace KanTuNext.Desktop.Views;

/// <summary>
/// Shows one image with zoom (wheel, trackpad pinch, keys), drag-to-pan and quarter-turn rotation.
/// Zoom is "screen points per image pixel": 1 is 1:1 the way macOS Preview means it (a 600px photo is 600pt
/// wide, so it isn't shrunk to half on a Retina display).
/// In fit mode the image follows the view size; any manual zoom or pan leaves fit mode.
/// Moving to another photo slides the old one out and the new one in, in the direction of travel, fading through
/// the background so the two are never stacked half-transparent.
/// Over recognized text (outlined or not), dragging selects text, Live Text style, instead of panning.
/// </summary>
public sealed class ZoomImage : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ZoomImage, Bitmap?>(nameof(Source));

    /// <summary>Recognized text lines to outline and make selectable, bounds normalized to the upright image.</summary>
    public static readonly StyledProperty<IReadOnlyList<OcrLine>?> TextLinesProperty =
        AvaloniaProperty.Register<ZoomImage, IReadOnlyList<OcrLine>?>(nameof(TextLines));

    private static readonly IBrush HighlightFill = new SolidColorBrush(Color.FromArgb(0x33, 0x2E, 0x9B, 0xFF));
    private static readonly IPen HighlightPen = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0x2E, 0x9B, 0xFF)), 1);
    private static readonly IBrush SelectionFill = new SolidColorBrush(Color.FromArgb(0x8C, 0x2E, 0x9B, 0xFF));

    private const double MinZoom = 0.02, MaxZoom = 32;
    private const double TransitionMs = 300;
    // Longest step one frame may advance the transition: a frame that took long (the first draw of a large
    // photo uploads it to the GPU) pauses the motion instead of skipping half of it
    private const double MaxFrameStepMs = 34;

    // The photo being left, drawn as it was on screen while the new one comes in
    private Bitmap? _outgoing;
    private Matrix _outgoingTransform;
    private int _outgoingDirection;
    private double _transitionMs;
    private TimeSpan? _lastFrame;
    private bool _warmedUp;

    private double _zoom = 1;          // DIPs (points) per image pixel
    private Vector _offset;            // image centre relative to view centre, in DIPs
    private int _quarterTurns;
    private bool _fit = true;
    private Point? _dragFrom;

    static ZoomImage()
    {
        AffectsRender<ZoomImage>(SourceProperty, TextLinesProperty, ShowTextOutlinesProperty);
        FocusableProperty.OverrideDefaultValue<ZoomImage>(true);
    }

    public ZoomImage()
    {
        ClipToBounds = true;
        // Trackpad pinch (macOS)
        PointerTouchPadGestureMagnify += OnMagnify;
    }

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>Outline the recognized lines (识别文字 on); without it the text is selectable but not drawn.</summary>
    public static readonly StyledProperty<bool> ShowTextOutlinesProperty =
        AvaloniaProperty.Register<ZoomImage, bool>(nameof(ShowTextOutlines));

    public bool ShowTextOutlines
    {
        get => GetValue(ShowTextOutlinesProperty);
        set => SetValue(ShowTextOutlinesProperty, value);
    }

    /// <summary>Hand tool (H): dragging always pans, even over text, which isn't selectable meanwhile.</summary>
    public static readonly StyledProperty<bool> HandToolProperty =
        AvaloniaProperty.Register<ZoomImage, bool>(nameof(HandTool));

    public bool HandTool
    {
        get => GetValue(HandToolProperty);
        set => SetValue(HandToolProperty, value);
    }

    public IReadOnlyList<OcrLine>? TextLines
    {
        get => GetValue(TextLinesProperty);
        set => SetValue(TextLinesProperty, value);
    }

    /// <summary>Raised when zoom, fit mode or rotation changes (toolbar state, zoom readout).</summary>
    public event Action? ViewChanged;

    public bool IsFit => _fit;

    /// <summary>Set when switching photos, so the next photo starts fitted and upright (otherwise a source change
    /// is taken as the same photo at another resolution, or the next frame of an animation).</summary>
    public bool ResetOnNextSource { get; set; }

    /// <summary>Which way the next photo change goes: 1 forward, -1 back, 0 in place (a delete): sets the slide.</summary>
    public int TransitionDirection { get; set; }
    public double Zoom => _zoom;

    /// <summary>Image size after rotation, in image pixels.</summary>
    private Size RotatedPixels => Source is null ? default
        : _quarterTurns % 2 == 0 ? new Size(Source.PixelSize.Width, Source.PixelSize.Height)
        : new Size(Source.PixelSize.Height, Source.PixelSize.Width);

    /// <summary>Zoom that fits the whole image in view, never enlarging past 1:1.</summary>
    private double FitZoom
    {
        get
        {
            var px = RotatedPixels;
            if (px.Width <= 0 || Bounds.Width <= 0) return 1;
            var fit = Math.Min(Bounds.Width / px.Width, Bounds.Height / px.Height);
            return Math.Min(fit, 1);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            var before = change.OldValue as Bitmap;
            var after = change.NewValue as Bitmap;
            if (ResetOnNextSource || before is null || after is null)
            {
                if (ResetOnNextSource && before is not null && after is not null) BeginTransition(before);
                ResetOnNextSource = false;
                Reset();
            }
            else if (before.PixelSize != after.PixelSize)
            {
                // The same photo at another resolution (screen-sized → full): keep what's on screen as it is
                _zoom = _fit ? FitZoom : _zoom * before.PixelSize.Width / after.PixelSize.Width;
                Changed();
            }
            else InvalidateVisual(); // next animation frame
        }
        if (change.Property == TextLinesProperty) SetReadingOrder(change.NewValue as IReadOnlyList<OcrLine>);
        if (change.Property == HandToolProperty)
        {
            _overText = false;
            Cursor = HandTool ? OpenHandCursor.Value : Cursor.Default;
        }
        if (change.Property == BoundsProperty && _fit) { _zoom = FitZoom; InvalidateVisual(); }
    }

    /// <summary>Starts fading <paramref name="leaving"/> out from where it is on screen now.</summary>
    private void BeginTransition(Bitmap leaving)
    {
        _outgoing = leaving;
        _outgoingTransform = TransformFor(leaving);
        _outgoingDirection = TransitionDirection;
        _transitionMs = 0;
        _lastFrame = null;
        _warmedUp = false;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnTransitionFrame);
        InvalidateVisual();
    }

    private void OnTransitionFrame(TimeSpan now)
    {
        if (_outgoing is null) return;
        // The clock starts once the new photo has been drawn (warmed up) at least once
        if (_warmedUp && _lastFrame is { } last) _transitionMs += Math.Clamp((now - last).TotalMilliseconds, 0, MaxFrameStepMs);
        if (_warmedUp) _lastFrame = now;
        if (_transitionMs >= TransitionMs) _outgoing = null;
        else TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnTransitionFrame);
        InvalidateVisual();
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
    private static double EaseInOut(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    /// <summary>Back to fit mode and upright, as when a new photo is shown.</summary>
    public void Reset()
    {
        _quarterTurns = 0;
        FitToView();
    }

    public void FitToView()
    {
        _fit = true;
        _zoom = FitZoom;
        _offset = default;
        Changed();
    }

    public void ActualSize() => SetZoom(1, Bounds.Center);

    public void ZoomBy(double factor) => SetZoom(_zoom * factor, Bounds.Center);

    public void Rotate(int quarterTurns)
    {
        _quarterTurns = ((_quarterTurns + quarterTurns) % 4 + 4) % 4;
        if (_fit) _zoom = FitZoom;
        _offset = default;
        Changed();
    }

    /// <summary>Zooms keeping the image point under <paramref name="anchor"/> (view coordinates) in place.</summary>
    private void SetZoom(double zoom, Point anchor)
    {
        if (Source is null) return;
        zoom = Math.Clamp(zoom, Math.Min(MinZoom, FitZoom), MaxZoom);
        var fromCentre = anchor - Bounds.Center;
        _offset = fromCentre - (fromCentre - _offset) * (zoom / _zoom);
        _zoom = zoom;
        _fit = false;
        ClampOffset();
        Changed();
    }

    /// <summary>Keeps at least part of the image on screen; centres an axis that fits entirely.</summary>
    private void ClampOffset()
    {
        var px = RotatedPixels;
        var w = px.Width * _zoom;
        var h = px.Height * _zoom;
        var maxX = Math.Max(0, (w - Bounds.Width) / 2);
        var maxY = Math.Max(0, (h - Bounds.Height) / 2);
        _offset = new Vector(Math.Clamp(_offset.X, -maxX, maxX), Math.Clamp(_offset.Y, -maxY, maxY));
    }

    private void Changed()
    {
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Image pixels to view coordinates for the current zoom, pan and rotation.</summary>
    private Matrix TransformFor(Bitmap bitmap)
    {
        var centre = Bounds.Center + _offset;
        return Matrix.CreateTranslation(-bitmap.PixelSize.Width / 2.0, -bitmap.PixelSize.Height / 2.0)
               * Matrix.CreateScale(_zoom, _zoom)
               * Matrix.CreateRotation(_quarterTurns * Math.PI / 2)
               * Matrix.CreateTranslation(centre.X, centre.Y);
    }

    public override void Render(DrawingContext context)
    {
        if (Source is not { } bitmap) return;
        var dipPerPixel = _zoom;
        var w = bitmap.PixelSize.Width;
        var h = bitmap.PixelSize.Height;
        var transform = TransformFor(bitmap);
        // Smooth when shrinking; crisp pixels from 1:1 up, as image viewers do for inspection
        var mode = _zoom < 1 ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.None;

        if (_outgoing is { } outgoing)
        {
            // Bilinear while moving: mipmapped filtering of a large photo would stall the first frames
            var moving = new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.LowQuality };
            var t = _transitionMs / TransitionMs;
            var slide = Math.Min(Bounds.Width * 0.08, 72) * _outgoingDirection;
            // The old photo leaves over the first half, the new one arrives over the last 60%: a brief fade
            // through the background instead of two translucent photos on top of each other
            var leave = EaseInOut(t / 0.5);
            var arrive = EaseOut((t - 0.4) / 0.6);
            if (leave < 1)
            {
                // The outgoing bitmap stays valid: whoever releases a photo's bitmaps does so a second later
                var scale = 1 - 0.03 * leave;
                var centre = Bounds.Center;
                using (context.PushTransform(_outgoingTransform * Matrix.CreateTranslation(-centre.X, -centre.Y)
                           * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(centre.X - slide * leave, centre.Y)))
                using (context.PushOpacity(1 - leave))
                using (context.PushRenderOptions(moving))
                    context.DrawImage(outgoing, new Rect(0, 0, outgoing.PixelSize.Width, outgoing.PixelSize.Height));
            }
            if (!_warmedUp)
            {
                // Draw the new photo once, practically invisible, while the old one is still fully shown: its
                // upload happens now, not in the middle of the motion
                using (context.PushOpacity(0.004))
                using (context.PushRenderOptions(moving))
                    context.DrawImage(bitmap, new Rect(0, 0, w, h), new Rect(0, 0, 1, 1));
                _warmedUp = true;
                return;
            }
            if (arrive > 0)
            {
                var scale = 0.97 + 0.03 * arrive;
                var centre = Bounds.Center;
                using (context.PushTransform(transform * Matrix.CreateTranslation(-centre.X, -centre.Y)
                           * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(centre.X + slide * (1 - arrive), centre.Y)))
                using (context.PushOpacity(arrive))
                using (context.PushRenderOptions(moving))
                    context.DrawImage(bitmap, new Rect(0, 0, w, h));
            }
            return;
        }

        using (context.PushTransform(transform))
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
            context.DrawImage(bitmap, new Rect(0, 0, w, h));

        if (_lines.Length > 0)
        {
            // Same transform as the image, so boxes follow zoom, pan and rotation; the pen is in screen units
            using (context.PushTransform(transform))
            {
                var pen = new Pen(HighlightPen.Brush, 1 / dipPerPixel);
                foreach (var line in ShowTextOutlines ? _lines : [])
                {
                    var b = line.Line.Bounds;
                    context.DrawRectangle(HighlightFill, pen, new Rect(b.X * w, b.Y * h, b.Width * w, b.Height * h), 2 / dipPerPixel, 2 / dipPerPixel);
                }
                for (var i = 0; i < _lines.Length; i++)
                {
                    if (SelectedRange(i) is not var (from, to) || from >= to) continue;
                    var line = _lines[i];
                    var b = line.Line.Bounds;
                    var left = b.X + b.Width * line.Stops[from];
                    var right = b.X + b.Width * line.Stops[to];
                    context.DrawRectangle(SelectionFill, null, new Rect(left * w, b.Y * h, (right - left) * w, b.Height * h), 2 / dipPerPixel, 2 / dipPerPixel);
                }
            }
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        SetZoom(_zoom * Math.Pow(1.0015, e.Delta.Y * 100), e.GetPosition(this));
        e.Handled = true;
    }

    private void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        SetZoom(_zoom * (1 + e.Delta.X), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Right button opens the context menu; only the left one pans and double-clicks
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!HandTool && HitText(e.GetPosition(this), false) is { } caret)
        {
            if (e.ClickCount == 2)
            {
                // Double-click selects the line
                _anchor = new Caret(caret.Line, 0);
                _focus = new Caret(caret.Line, _lines[caret.Line].Stops.Length - 1);
            }
            else
            {
                _anchor = _focus = caret;
                _selecting = true;
                e.Pointer.Capture(this);
            }
            SelectionChanged?.Invoke();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (HasSelection) ClearSelection();
        if (e.ClickCount == 2)
        {
            if (_fit) SetZoom(1, e.GetPosition(this));
            else FitToView();
            return;
        }
        _dragFrom = e.GetPosition(this);
        if (HandTool) Cursor = ClosedHandCursor.Value; // the hand grips the photo
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_selecting)
        {
            if (HitText(e.GetPosition(this), true) is { } caret && caret != _focus)
            {
                _focus = caret;
                SelectionChanged?.Invoke();
                InvalidateVisual();
            }
            return;
        }
        if (_dragFrom is null && !HandTool)
        {
            // An I-beam over recognized text says it can be selected
            var overText = _lines.Length > 0 && HitText(e.GetPosition(this), false) is not null;
            if (overText != _overText)
            {
                _overText = overText;
                Cursor = overText ? new Cursor(StandardCursorType.Ibeam) : Cursor.Default;
            }
        }
        if (_dragFrom is not { } from) return;
        var now = e.GetPosition(this);
        _offset += now - from;
        _dragFrom = now;
        ClampOffset();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _selecting = false;
        _dragFrom = null;
        if (HandTool) Cursor = OpenHandCursor.Value;
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// MDI's hand-back-right with the fingers curled to knuckles and the thumb tucked in: the grabbing hand, drawn
    /// to match the open one (MDI has no closed hand).
    /// </summary>
    private const string ClosedHand = "M13 24C9.74 24 6.81 22 5.6 19L4.5 16.2C4.2 15.4 4.75 14.6 5.6 14.65L6.1 14.7C6.6 14.75 7 15.05 7.2 15.5L7.45 16H8V9.75C8 9.06 8.56 8.5 9.25 8.5S10.5 9.06 10.5 9.75V10.5H11.5V8.75C11.5 8.06 12.06 7.5 12.75 7.5S14 8.06 14 8.75V10.5H15V9.25C15 8.56 15.56 8 16.25 8C16.94 8 17.5 8.56 17.5 9.25V10.5H18.5V10.25C18.5 9.56 19.06 9 19.75 9S21 9.56 21 10.25V16C21 20.42 17.42 24 13 24Z";

    // The hand tool's cursors: open while hovering, closed while dragging
    private static readonly Lazy<Cursor> OpenHandCursor = new(() =>
        HandCursorFrom(Application.Current?.FindResource("Icon.hand-back-right") as Geometry, StandardCursorType.Hand));
    private static readonly Lazy<Cursor> ClosedHandCursor = new(() =>
        HandCursorFrom(StreamGeometry.Parse(ClosedHand), StandardCursorType.DragMove));

    /// <summary>A hand glyph as a cursor about the size of the system arrow: white with a soft grey edge, hot spot in
    /// the middle.</summary>
    private static Cursor HandCursorFrom(Geometry? glyph, StandardCursorType fallback)
    {
        if (glyph is null) return new Cursor(fallback);
        const int size = 20;
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        using (var context = bitmap.CreateDrawingContext())
        using (context.PushTransform(Matrix.CreateTranslation(1, 1) * Matrix.CreateScale(size / 26.0, size / 26.0)))
            context.DrawGeometry(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), 1.4, lineJoin: PenLineJoin.Round), glyph);
        return new Cursor(bitmap, new PixelPoint(size / 2, size / 2));
    }

    // ---- Text selection over recognized lines

    /// <summary>A recognized line in reading order: its row (lines on one row join with a space) and the
    /// estimated left edge of each character as a fraction of the line's width (one more for the end).</summary>
    private sealed record ReadingLine(OcrLine Line, int Row, double[] Stops);

    public sealed record SelectionPiece(int Row, string Text, OcrRect? Region, string LineText);

    /// <summary>A position between characters: line index in reading order, character index.</summary>
    private readonly record struct Caret(int Line, int Index);

    private ReadingLine[] _lines = [];
    private Caret? _anchor, _focus;
    private bool _selecting, _overText;

    /// <summary>Raised when the text selection on the image changes.</summary>
    public event Action? SelectionChanged;

    public bool HasSelection => SelectedText.Length > 0;

    public bool HasText => _lines.Length > 0;

    /// <summary>The selected text, rows separated by newlines (character positions inside a line estimated).</summary>
    public string SelectedText => JoinPieces(SelectionPieces.Select(p => (p.Row, p.Text)));

    /// <summary>Selected text per line in reading order. <see cref="SelectionPiece.Region"/> is set where only
    /// part of a line is selected: that part of the photo, for recognizing it again precisely.</summary>
    public IReadOnlyList<SelectionPiece> SelectionPieces
    {
        get
        {
            var pieces = new List<SelectionPiece>();
            for (var i = 0; i < _lines.Length; i++)
            {
                if (SelectedRange(i) is not var (from, to) || from >= to) continue;
                var line = _lines[i];
                var text = line.Line.Text.Substring(from, to - from);
                OcrRect? region = null;
                if (from > 0 || to < line.Line.Text.Length)
                {
                    var b = line.Line.Bounds;
                    // A fifth of an average character either side, so an estimate that's slightly off still takes
                    // in whole glyphs (slivers of neighbours are dropped by OcrText.Reconcile); a fifth of the
                    // line height above and below
                    var padX = b.Width / Math.Max(1, line.Line.Text.Length) * 0.2;
                    var padY = b.Height * 0.2;
                    var left = Math.Max(0, b.X + b.Width * line.Stops[from] - padX);
                    var right = Math.Min(1, b.X + b.Width * line.Stops[to] + padX);
                    var top = Math.Max(0, b.Y - padY);
                    var bottom = Math.Min(1, b.Y + b.Height + padY);
                    region = new OcrRect(left, top, right - left, bottom - top);
                }
                pieces.Add(new SelectionPiece(line.Row, text, region, line.Line.Text));
            }
            return pieces;
        }
    }

    /// <summary>Joins line texts: a space between lines on one row, a newline between rows.</summary>
    public static string JoinPieces(IEnumerable<(int Row, string Text)> pieces)
    {
        var text = new System.Text.StringBuilder();
        var row = -1;
        foreach (var (r, piece) in pieces)
        {
            if (row >= 0) text.Append(r == row ? " " : "\n");
            row = r;
            text.Append(piece.Trim());
        }
        return text.ToString().Trim();
    }

    public void SelectAll()
    {
        if (_lines.Length == 0) return;
        _anchor = new Caret(0, 0);
        _focus = new Caret(_lines.Length - 1, _lines[^1].Stops.Length - 1);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        _anchor = _focus = null;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    private void SetReadingOrder(IReadOnlyList<OcrLine>? lines)
    {
        _anchor = _focus = null;
        _lines = lines is null ? []
            : OcrLayout.Rows(lines).SelectMany((row, r) => row.Select(line => new ReadingLine(line, r, CharacterStops(line.Text)))).ToArray();
        if (_lines.Length == 0 && _overText)
        {
            _overText = false;
            Cursor = Cursor.Default;
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Engines only give line boxes, so character positions are estimated from typical advance widths: CJK and
    /// full-width characters are square, Latin letters and digits a little over half, spaces and thin marks less.
    /// </summary>
    private static double[] CharacterStops(string text)
    {
        static double Advance(char c) =>
            OcrText.IsCjk(c) ? 1 : c == ' ' ? 0.3 : c is 'i' or 'l' or 'j' or 't' or 'f' or 'I' or '.' or ',' or ':' or ';' or '\'' or '!' or '|' ? 0.3
            : c is 'm' or 'w' or 'M' or 'W' ? 0.85 : char.IsUpper(c) ? 0.65 : 0.55;
        var stops = new double[text.Length + 1];
        for (var i = 0; i < text.Length; i++) stops[i + 1] = stops[i] + Advance(text[i]);
        var total = stops[^1];
        if (total > 0) for (var i = 1; i < stops.Length; i++) stops[i] /= total;
        return stops;
    }

    /// <summary>Selected characters of line <paramref name="i"/>, as [from, to), or null.</summary>
    private (int, int)? SelectedRange(int i)
    {
        if (_anchor is not { } a || _focus is not { } f) return null;
        var (start, end) = a.Line < f.Line || a.Line == f.Line && a.Index <= f.Index ? (a, f) : (f, a);
        if (i < start.Line || i > end.Line) return null;
        var length = _lines[i].Stops.Length - 1;
        return (i == start.Line ? start.Index : 0, i == end.Line ? end.Index : length);
    }

    /// <summary>
    /// The caret under a view point. Off the text, <paramref name="nearest"/> picks the closest line (so a drag
    /// can run past the ends); otherwise null.
    /// </summary>
    private Caret? HitText(Point point, bool nearest)
    {
        if (_lines.Length == 0 || Source is not { } bitmap) return null;
        if (!TransformFor(bitmap).TryInvert(out var inverse)) return null;
        var p = point.Transform(inverse);
        var x = p.X / bitmap.PixelSize.Width;
        var y = p.Y / bitmap.PixelSize.Height;
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _lines.Length; i++)
        {
            var b = _lines[i].Line.Bounds;
            // Slack of a few percent of the line height, so thin lines are easy to hit
            var pad = b.Height * 0.15;
            var dx = Math.Max(0, Math.Max(b.X - pad - x, x - b.Right - pad));
            var dy = Math.Max(0, Math.Max(b.Y - pad - y, y - (b.Y + b.Height) - pad));
            var distance = dy * 4 + dx; // a line on the same row beats a nearer one above or below
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        if (best < 0 || !nearest && bestDistance > 0) return null;
        var line = _lines[best];
        var fraction = line.Line.Bounds.Width > 0 ? (x - line.Line.Bounds.X) / line.Line.Bounds.Width : 0;
        var index = 0;
        for (var i = 1; i < line.Stops.Length; i++)
            if (fraction >= (line.Stops[i - 1] + line.Stops[i]) / 2) index = i;
        return new Caret(best, index);
    }
}
