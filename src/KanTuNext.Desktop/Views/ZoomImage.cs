using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace KanTuNext.Desktop.Views;

/// <summary>
/// Shows one image with zoom (wheel, trackpad pinch, keys), drag-to-pan and quarter-turn rotation.
/// Zoom is "screen points per image pixel": 1 is 1:1 the way macOS Preview means it (a 600px photo is 600pt
/// wide, so it isn't shrunk to half on a Retina display).
/// In fit mode the image follows the view size; any manual zoom or pan leaves fit mode.
/// </summary>
public sealed class ZoomImage : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ZoomImage, Bitmap?>(nameof(Source));

    private const double MinZoom = 0.02, MaxZoom = 32;

    private double _zoom = 1;          // DIPs (points) per image pixel
    private Vector _offset;            // image centre relative to view centre, in DIPs
    private int _quarterTurns;
    private bool _fit = true;
    private Point? _dragFrom;

    static ZoomImage()
    {
        AffectsRender<ZoomImage>(SourceProperty);
        FocusableProperty.OverrideDefaultValue<ZoomImage>(true);
    }

    public ZoomImage()
    {
        ClipToBounds = true;
        // Trackpad pinch (macOS)
        PointerTouchPadGestureMagnify += OnMagnify;
    }

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>Raised when zoom, fit mode or rotation changes (toolbar state, zoom readout).</summary>
    public event Action? ViewChanged;

    public bool IsFit => _fit;

    /// <summary>Set when switching photos, so a same-sized next photo still starts fitted and upright.</summary>
    public bool ResetOnNextSource { get; set; }
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
        if (change.Property == SourceProperty &&
            ((change.OldValue as Bitmap)?.PixelSize != (change.NewValue as Bitmap)?.PixelSize || ResetOnNextSource))
        {
            ResetOnNextSource = false;
            Reset();
        }
        else if (change.Property == SourceProperty) InvalidateVisual();
        if (change.Property == BoundsProperty && _fit) { _zoom = FitZoom; InvalidateVisual(); }
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

    public override void Render(DrawingContext context)
    {
        if (Source is not { } bitmap) return;
        var dipPerPixel = _zoom;
        var w = bitmap.PixelSize.Width;
        var h = bitmap.PixelSize.Height;
        var centre = Bounds.Center + _offset;
        var transform = Matrix.CreateTranslation(-w / 2.0, -h / 2.0)
                        * Matrix.CreateScale(dipPerPixel, dipPerPixel)
                        * Matrix.CreateRotation(_quarterTurns * Math.PI / 2)
                        * Matrix.CreateTranslation(centre.X, centre.Y);
        // Smooth when shrinking; crisp pixels from 1:1 up, as image viewers do for inspection
        var mode = _zoom < 1 ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.None;
        using (context.PushTransform(transform))
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
            context.DrawImage(bitmap, new Rect(0, 0, w, h));
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
        if (e.ClickCount == 2)
        {
            if (_fit) SetZoom(1, e.GetPosition(this));
            else FitToView();
            return;
        }
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
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
        _dragFrom = null;
        e.Pointer.Capture(null);
    }
}
