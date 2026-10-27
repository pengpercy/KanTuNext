using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using KanTuNext.Core.Library;
using KanTuNext.Desktop.Services;

namespace KanTuNext.Desktop.Views;

/// <summary>
/// The viewer's 预览条: square thumbnails of the photos before and after the current one, down the left side
/// (as a slide sorter) or along the bottom. The current photo stays centred, ringed in white; the strip glides
/// to it as photos change. The wheel looks further ahead or back, a click jumps there. Drawn directly, like
/// <see cref="ThumbnailGrid"/>, so thousands of photos cost only what is on screen.
/// </summary>
public sealed class Filmstrip : Control
{
    public static readonly StyledProperty<int> IndexProperty = AvaloniaProperty.Register<Filmstrip, int>(nameof(Index));
    public static readonly StyledProperty<bool> VerticalProperty = AvaloniaProperty.Register<Filmstrip, bool>(nameof(Vertical), true);

    static Filmstrip()
    {
        AffectsRender<Filmstrip>(VerticalProperty);
        ClipToBoundsProperty.OverrideDefaultValue<Filmstrip>(true);
        CursorProperty.OverrideDefaultValue<Filmstrip>(new Cursor(StandardCursorType.Hand));
    }

    public int Index { get => GetValue(IndexProperty); set => SetValue(IndexProperty, value); }

    /// <summary>Down the left side (true) or along the bottom.</summary>
    public bool Vertical { get => GetValue(VerticalProperty); set => SetValue(VerticalProperty, value); }

    /// <summary>The photos, read live: deleting one in the viewer shortens the strip at the next frame.</summary>
    public IReadOnlyList<MediaFile>? Items
    {
        get => _items;
        set
        {
            _items = value;
            InvalidateVisual();
        }
    }

    /// <summary>A thumbnail was clicked.</summary>
    public event Action<int>? Chosen;

    /// <summary>Thumbnail edge and the gap between them, by orientation.</summary>
    private double Cell => Vertical ? 76 : 58;
    private const double Gap = 10, Radius = 6;

    private IReadOnlyList<MediaFile>? _items;
    // The item at the strip's middle, fractional while gliding or after the wheel moved it
    private double _center;
    private int _hover = -1;
    private int _glide;
    private readonly HashSet<string> _visibleKeys = [];

    /// <summary>The strip's background (ViewerWindow's StripPanel), which the ends fade into.</summary>
    private static readonly Color Fade = Color.FromRgb(0x14, 0x14, 0x16);
    private static readonly IBrush Placeholder = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0));
    private static readonly IBrush HoverDim = new SolidColorBrush(Color.FromArgb(0x1F, 0, 0, 0));
    private static readonly IPen Ring = new Pen(Brushes.White, 2);
    private static readonly IPen Hairline = new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)), 1);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThumbnailService.Shared.ThumbnailReady += InvalidateVisual;
        _center = Index;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThumbnailService.Shared.ThumbnailReady -= InvalidateVisual;
        ThumbnailService.Shared.Retain(this, null);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IndexProperty) GlideTo(Index);
    }

    /// <summary>Eases the middle of the strip to <paramref name="index"/> (about a quarter second).</summary>
    private void GlideTo(int index)
    {
        var glide = ++_glide;
        var from = _center;
        if (TopLevel.GetTopLevel(this) is not { } top || Math.Abs(from - index) > 40)
        {
            // Not on screen yet, or a jump too far to watch go by
            _center = index;
            InvalidateVisual();
            return;
        }
        var start = -1d;
        void Step(TimeSpan now)
        {
            if (glide != _glide) return;
            if (start < 0) start = now.TotalMilliseconds;
            var t = Math.Clamp((now.TotalMilliseconds - start) / 260, 0, 1);
            _center = from + (index - from) * (1 - Math.Pow(1 - t, 3));
            InvalidateVisual();
            if (t < 1) top.RequestAnimationFrame(Step);
        }
        top.RequestAnimationFrame(Step);
    }

    /// <summary>Thumbnails melt into the strip's colour at both ends instead of being cut off.</summary>
    private void DrawFades(DrawingContext context)
    {
        var size = Bounds.Size;
        var along = Vertical ? size.Height : size.Width;
        var fade = Math.Min(along / 3, 40);
        if (fade <= 0) return;
        var solid = Fade;
        var clear = Color.FromArgb(0, solid.R, solid.G, solid.B);
        foreach (var start in new[] { true, false })
        {
            var rect = Vertical
                ? new Rect(0, start ? 0 : size.Height - fade, size.Width, fade)
                : new Rect(start ? 0 : size.Width - fade, 0, fade, size.Height);
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = Vertical ? new RelativePoint(0, 1, RelativeUnit.Relative) : new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(start ? solid : clear, 0), new GradientStop(start ? clear : solid, 1) },
            };
            context.FillRectangle(brush, rect);
        }
    }

    private Rect CellRect(int index)
    {
        var size = Bounds.Size;
        var pitch = Cell + Gap;
        var along = Vertical ? size.Height : size.Width;
        var across = Vertical ? size.Width : size.Height;
        var middle = along / 2 + (index - _center) * pitch;
        return Vertical
            ? new Rect((across - Cell) / 2, middle - Cell / 2, Cell, Cell)
            : new Rect(middle - Cell / 2, (across - Cell) / 2, Cell, Cell);
    }

    public override void Render(DrawingContext context)
    {
        // Hit-testable everywhere, so the wheel works between thumbnails too
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_items is not { Count: > 0 } items) return;
        var pitch = Cell + Gap;
        var along = Vertical ? Bounds.Height : Bounds.Width;
        var reach = (int)Math.Ceiling(along / 2 / pitch) + 1;
        var first = Math.Max(0, (int)Math.Floor(_center) - reach);
        var last = Math.Min(items.Count - 1, (int)Math.Ceiling(_center) + reach);
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var bucket = ThumbnailService.Bucket(Cell * scaling);
        _visibleKeys.Clear();
        for (var i = first; i <= last; i++)
        {
            var item = items[i];
            var rect = CellRect(i);
            _visibleKeys.Add(ThumbnailService.KeyOf(item.Path, item.Modified, bucket));
            var bitmap = ThumbnailService.Shared.Get(item.Path, item.Modified, bucket);
            using (context.PushClip(new RoundedRect(rect, Radius)))
            {
                if (bitmap is not null) context.DrawImage(bitmap, new Rect(bitmap.Size), rect);
                else context.FillRectangle(Placeholder, rect);
                // The others step back, so the current photo is the brightest thing in the strip
                if (i != Index) context.FillRectangle(i == _hover ? HoverDim : Dim, rect);
            }
            if (i == Index)
                context.DrawRectangle(null, Ring, new RoundedRect(rect.Inflate(3.5), Radius + 3));
            else
                context.DrawRectangle(null, Hairline, new RoundedRect(rect.Deflate(0.5), Radius));
        }
        DrawFades(context);
        ThumbnailService.Shared.Retain(this, new HashSet<string>(_visibleKeys));
    }

    private int HitTest(Point point)
    {
        if (_items is not { Count: > 0 } items) return -1;
        var pitch = Cell + Gap;
        var along = Vertical ? point.Y - Bounds.Height / 2 : point.X - Bounds.Width / 2;
        var index = (int)Math.Round(_center + along / pitch);
        return index >= 0 && index < items.Count && CellRect(index).Inflate(Gap / 2).Contains(point) ? index : -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hover = HitTest(e.GetPosition(this));
        if (hover == _hover) return;
        _hover = hover;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var index = HitTest(e.GetPosition(this));
        if (index >= 0) Chosen?.Invoke(index);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_items is not { Count: > 0 } items) return;
        // A notch moves one thumbnail; a trackpad scrolls smoothly. Either axis works in either orientation.
        var delta = Math.Abs(e.Delta.Y) >= Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X;
        _glide++;
        _center = Math.Clamp(_center - delta, 0, items.Count - 1);
        _hover = HitTest(e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }
}
