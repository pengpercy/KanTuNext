using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using KanTuNext.Core.Library;
using KanTuNext.Core.Timeline;
using KanTuNext.Desktop.Services;

namespace KanTuNext.Desktop.Views;

/// <summary>
/// The month-grouped thumbnail grid. Instead of one control per photo it lays the whole timeline out as
/// arithmetic and draws only the rows inside the ScrollViewer's viewport, so 100k items cost the same per
/// frame as 100. Thumbnails come from <see cref="ThumbnailService"/>; off-screen requests are dropped.
/// Metrics follow the original 2.5.2 timeline: 28pt side padding, large first month title, smaller ones below.
/// </summary>
public sealed class ThumbnailGrid : Control
{
    public static readonly StyledProperty<IReadOnlyList<MediaGroup>?> GroupsProperty =
        AvaloniaProperty.Register<ThumbnailGrid, IReadOnlyList<MediaGroup>?>(nameof(Groups));
    public static readonly StyledProperty<double> ItemEdgeProperty =
        AvaloniaProperty.Register<ThumbnailGrid, double>(nameof(ItemEdge), 70);
    public static readonly StyledProperty<double> ItemGapProperty =
        AvaloniaProperty.Register<ThumbnailGrid, double>(nameof(ItemGap), 20);
    /// <summary>File size under each thumbnail (大图片, 屏幕快照), as the original shows it.</summary>
    public static readonly StyledProperty<bool> ShowSizesProperty =
        AvaloniaProperty.Register<ThumbnailGrid, bool>(nameof(ShowSizes));

    private const double CaptionHeight = 18;

    private const double SidePadding = 28, TopPadding = 0, FirstHeader = 52, Header = 44, SectionGap = 14, BottomPadding = 24;

    private readonly List<Section> _sections = [];
    private readonly Dictionary<(string, double, IBrush), FormattedText> _texts = [];
    private ScrollViewer? _scroller;
    private int _columns = 1;
    private (int Section, int Item)? _selected;

    static ThumbnailGrid()
    {
        AffectsMeasure<ThumbnailGrid>(GroupsProperty, ItemEdgeProperty, ItemGapProperty, ShowSizesProperty);
        AffectsRender<ThumbnailGrid>(GroupsProperty, ItemEdgeProperty, ItemGapProperty, ShowSizesProperty);
        FocusableProperty.OverrideDefaultValue<ThumbnailGrid>(true);
    }

    public ThumbnailGrid()
    {
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => { _texts.Clear(); InvalidateVisual(); };
    }

    public IReadOnlyList<MediaGroup>? Groups { get => GetValue(GroupsProperty); set => SetValue(GroupsProperty, value); }
    public double ItemEdge { get => GetValue(ItemEdgeProperty); set => SetValue(ItemEdgeProperty, value); }
    public double ItemGap { get => GetValue(ItemGapProperty); set => SetValue(ItemGapProperty, value); }
    public bool ShowSizes { get => GetValue(ShowSizesProperty); set => SetValue(ShowSizesProperty, value); }

    /// <summary>Vertical distance between rows: the thumbnail, its caption (if any) and the gap.</summary>
    private double RowPitch => ItemEdge + ItemGap + (ShowSizes ? CaptionHeight : 0);

    /// <summary>Raised on double-click or Enter with the chosen file (opens the viewer).</summary>
    public event Action<MediaFile>? Opened;

    /// <summary>Raises <see cref="Opened"/> as a double-click would (menu 「打开」).</summary>
    public void OpenItem(MediaFile file) => Opened?.Invoke(file);

    /// <summary>The selected file changed (null: nothing selected).</summary>
    public event Action<MediaFile?>? SelectionChanged;

    /// <summary>Raised on right-click over a thumbnail (after selecting it), for the context menu.</summary>
    public event Action<MediaFile>? ItemContextRequested;

    private readonly record struct Section(MediaGroup Group, double Top, double ItemsTop, int Rows, double Bottom);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is not null) _scroller.ScrollChanged += OnScrollChanged;
        ThumbnailService.Shared.ThumbnailReady += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scroller is not null) _scroller.ScrollChanged -= OnScrollChanged;
        ThumbnailService.Shared.ThumbnailReady -= InvalidateVisual;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GroupsProperty)
        {
            _selected = null;
            SelectionChanged?.Invoke(null);
            // A fresh folder starts at the top; a live refresh (old list replaced by a new one) keeps the position
            if (change.OldValue is null) _scroller?.ScrollToHome();
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        var pitch = ItemEdge + ItemGap;
        var rowPitch = RowPitch;
        _columns = Math.Max(1, (int)((width - 2 * SidePadding + ItemGap) / pitch));
        _sections.Clear();
        var y = TopPadding;
        if (Groups is { } groups)
        {
            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                var itemsTop = y + (i == 0 ? FirstHeader : Header);
                var rows = (group.Items.Count + _columns - 1) / _columns;
                var bottom = itemsTop + rows * rowPitch - ItemGap;
                _sections.Add(new Section(group, y, itemsTop, rows, bottom));
                y = bottom + SectionGap;
            }
        }
        return new Size(width, y + BottomPadding);
    }

    private Rect ItemRect(Section section, int index)
    {
        var pitch = ItemEdge + ItemGap;
        return new Rect(SidePadding + index % _columns * pitch, section.ItemsTop + index / _columns * RowPitch, ItemEdge, ItemEdge);
    }

    public override void Render(DrawingContext context)
    {
        var viewport = _scroller is null
            ? new Rect(Bounds.Size)
            : new Rect(new Point(_scroller.Offset.X, _scroller.Offset.Y), _scroller.Viewport);
        // One extra row above and below so a slow scroll finds its thumbnails ready
        var prefetch = viewport.Inflate(new Thickness(0, ItemEdge + ItemGap));
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var bucket = ThumbnailService.Bucket(ItemEdge * scaling);
        var service = ThumbnailService.Shared;
        var visibleKeys = new HashSet<string>();

        var text = Brush("TextBrush", Brushes.White);
        var placeholder = Brush("SidebarHoverBrush", Brushes.Gray);
        var faint = Brush("FaintBrush", Brushes.Gray);
        var accent = Brush("AccentBrush", Brushes.DodgerBlue);
        var selectionPen = new Pen(accent, 2);
        var pitch = RowPitch;

        for (var s = 0; s < _sections.Count; s++)
        {
            var section = _sections[s];
            if (section.Bottom < prefetch.Top) continue;
            if (section.Top > prefetch.Bottom) break;

            if (section.ItemsTop > viewport.Top - FirstHeader)
            {
                var title = Text(section.Group.Title, s == 0 ? 26 : 18, text);
                var count = Text(section.Group.CountText, 14, text);
                var baseline = section.ItemsTop - 12;
                context.DrawText(title, new Point(SidePadding, baseline - title.Baseline));
                context.DrawText(count, new Point(SidePadding + title.Width + 18, baseline - count.Baseline));
            }

            var firstRow = Math.Max(0, (int)((prefetch.Top - section.ItemsTop) / pitch));
            var lastRow = Math.Min(section.Rows - 1, (int)((prefetch.Bottom - section.ItemsTop) / pitch));
            var items = section.Group.Items;
            for (var i = firstRow * _columns; i <= Math.Min(items.Count - 1, (lastRow + 1) * _columns - 1); i++)
            {
                var item = items[i];
                var rect = ItemRect(section, i);
                visibleKeys.Add(ThumbnailService.KeyOf(item.Path, item.Modified, bucket));
                var bitmap = service.Get(item.Path, item.Modified, bucket);
                if (bitmap is not null)
                    context.DrawImage(bitmap, new Rect(bitmap.Size), rect);
                else
                    context.FillRectangle(placeholder, rect);
                // Videos carry a play mark over their frame (or over the placeholder where none can be made)
                if (item.Kind == MediaKind.Video) DrawPlayBadge(context, rect, bitmap is null ? faint : Brushes.White);
                if (item.IsLivePhoto) DrawLiveBadge(context, rect);
                if (_selected == (s, i))
                    context.DrawRectangle(null, selectionPen, rect.Inflate(1));
                if (ShowSizes)
                {
                    var caption = Text(TimelineText.FileSize(item.Length), 11, text);
                    context.DrawText(caption, new Point(rect.Center.X - caption.Width / 2, rect.Bottom + 3));
                }
            }
        }
        service.Retain(this, visibleKeys);
    }

    private static readonly IBrush LiveBadgeBrush = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush LiveBadgeShadow = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0));

    /// <summary>The Live Photo mark in the top-left corner: a dot, a ring and a dashed outer ring.</summary>
    private static void DrawLiveBadge(DrawingContext context, Rect rect)
    {
        var r = Math.Clamp(rect.Width / 12, 5, 9);
        var c = new Point(rect.X + r + 5, rect.Y + r + 5);
        context.DrawEllipse(LiveBadgeShadow, null, c, r + 1.5, r + 1.5);
        context.DrawEllipse(LiveBadgeBrush, null, c, r * 0.28, r * 0.28);
        context.DrawEllipse(null, new Pen(LiveBadgeBrush, 1.2), c, r * 0.58, r * 0.58);
        context.DrawEllipse(null, new Pen(LiveBadgeBrush, 1.2) { DashStyle = new DashStyle([1.2, 1.4], 0) }, c, r, r);
    }

    private static void DrawPlayBadge(DrawingContext context, Rect rect, IBrush brush)
    {
        var c = rect.Center;
        var r = Math.Min(14, rect.Width / 5);
        context.DrawEllipse(null, new Pen(brush, 1.5), c, r, r);
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(c.X - r * 0.35, c.Y - r * 0.5), true);
            g.LineTo(new Point(c.X + r * 0.55, c.Y));
            g.LineTo(new Point(c.X - r * 0.35, c.Y + r * 0.5));
            g.EndFigure(true);
        }
        context.DrawGeometry(brush, null, tri);
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private FormattedText Text(string value, double size, IBrush brush)
    {
        if (!_texts.TryGetValue((value, size, brush), out var formatted))
        {
            formatted = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
            _texts[(value, size, brush)] = formatted;
            if (_texts.Count > 512) _texts.Clear();
        }
        return formatted;
    }

    private (int Section, int Item)? HitTest(Point point)
    {
        var pitch = ItemEdge + ItemGap;
        var rowPitch = RowPitch;
        for (var s = 0; s < _sections.Count; s++)
        {
            var section = _sections[s];
            if (point.Y < section.ItemsTop || point.Y > section.Bottom) continue;
            var col = (int)((point.X - SidePadding) / pitch);
            var row = (int)((point.Y - section.ItemsTop) / rowPitch);
            if (col < 0 || col >= _columns) return null;
            var index = row * _columns + col;
            if (index >= section.Group.Items.Count || !ItemRect(section, index).Contains(point)) return null;
            return (s, index);
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        _selected = HitTest(e.GetPosition(this));
        SelectionChanged?.Invoke(_selected is var (ps, pi) ? _sections[ps].Group.Items[pi] : null);
        InvalidateVisual();
        if (_selected is not var (s, i)) return;
        var file = _sections[s].Group.Items[i];
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) ItemContextRequested?.Invoke(file);
        else if (e.ClickCount == 2) Opened?.Invoke(file);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_sections.Count == 0) return;
        switch (e.Key)
        {
            case Key.Enter or Key.Space when _selected is var (s, i):
                Opened?.Invoke(_sections[s].Group.Items[i]);
                break;
            case Key.Left: Move(-1); break;
            case Key.Right: Move(1); break;
            case Key.Up: MoveRow(-1); break;
            case Key.Down: MoveRow(1); break;
            case Key.Home: Select(0, 0); break;
            case Key.End: Select(_sections.Count - 1, _sections[^1].Group.Items.Count - 1); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Previous/next item, continuing into the neighbouring month.</summary>
    private void Move(int delta)
    {
        if (_selected is not var (s, i)) { Select(0, 0); return; }
        i += delta;
        if (i < 0 && s > 0) { s--; i = _sections[s].Group.Items.Count - 1; }
        else if (i >= _sections[s].Group.Items.Count && s < _sections.Count - 1) { s++; i = 0; }
        Select(s, Math.Clamp(i, 0, _sections[s].Group.Items.Count - 1));
    }

    /// <summary>Same column one row up/down; across a month boundary, the nearest item in that month's last/first row.</summary>
    private void MoveRow(int direction)
    {
        if (_selected is not var (s, i)) { Select(0, 0); return; }
        var column = i % _columns;
        var count = _sections[s].Group.Items.Count;
        var next = i + direction * _columns;
        if (next >= 0 && next < count) { Select(s, next); return; }
        if (direction > 0 && next >= count && i / _columns < (count - 1) / _columns) { Select(s, count - 1); return; }
        var target = s + direction;
        if (target < 0 || target >= _sections.Count) return;
        var targetCount = _sections[target].Group.Items.Count;
        var row = direction > 0 ? 0 : (targetCount - 1) / _columns;
        Select(target, Math.Min(row * _columns + column, targetCount - 1));
    }

    private void Select(int section, int index)
    {
        if (section < 0 || section >= _sections.Count || index < 0) return;
        _selected = (section, index);
        SelectionChanged?.Invoke(_sections[section].Group.Items[index]);
        // Keep the selection on screen
        if (_scroller is not null)
        {
            var rect = ItemRect(_sections[section], index);
            var top = rect.Top - (index < _columns ? (section == 0 ? FirstHeader : Header) : 0);
            var bottom = rect.Bottom + (ShowSizes ? CaptionHeight : 0) + ItemGap;
            var offset = _scroller.Offset.Y;
            if (top < offset) offset = top;
            else if (bottom > offset + _scroller.Viewport.Height) offset = bottom - _scroller.Viewport.Height;
            _scroller.Offset = _scroller.Offset.WithY(Math.Max(0, offset));
        }
        InvalidateVisual();
    }
}
