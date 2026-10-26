using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using KanTuNext.Core.Ocr;
using KanTuNext.Desktop.Services;
using KanTuNext.Desktop.ViewModels;

namespace KanTuNext.Desktop.Views;

public partial class ViewerWindow : Window
{
    private readonly DispatcherTimer _hideToolbar;
    private bool _actualSizeWhenFull;
    private int _shownIndex;

    public ViewerWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindows()) WindowsResizeFrame.Attach(this);
        _hideToolbar = new DispatcherTimer(TimeSpan.FromSeconds(2.5), DispatcherPriority.Normal, (_, _) =>
        {
            _hideToolbar!.Stop();
            if (!ToolbarDock.IsPointerOver) ToolbarDock.Opacity = 0;
        });
        PointerMoved += (_, _) => ShowToolbar();
        // Windows and Linux draw min/max/close in that corner: level with the panel's header instead
        if (WindowChrome.HasClientCaptionButtons)
        {
            OcrClose.Margin = new Thickness(0, 42, 6, 0);
            OcrHeader.Margin = new Thickness(0, 0, 22, 12);
        }
        // Buttons, selectable values and the scroll bar take their own presses; the rest of the panel drags it
        InfoPanel.PointerPressed += OnInfoDragStart;
        InfoPanel.PointerMoved += OnInfoDrag;
        InfoPanel.PointerReleased += (_, e) =>
        {
            _infoDragFrom = null;
            e.Pointer.Capture(null);
        };
        // A smaller window must not leave the moved panel out of reach
        InfoPanel.SizeChanged += (_, _) => MoveInfoPanel(default);
        Viewer.SizeChanged += (_, _) => MoveInfoPanel(default);
        Viewer.ContextRequested += (_, e) =>
        {
            ShowContextMenu();
            e.Handled = true;
        };
        // Leaving fit mode (zoom, 1:1, pinch) is when the screen-sized decode stops being enough
        Viewer.ViewChanged += () =>
        {
            if (!Viewer.IsFit && Model is { IsReduced: true } vm) _ = vm.LoadFullResolutionAsync();
        };
        Opened += (_, _) => { ShowToolbar(); Viewer.Focus(); };
        Closed += (_, _) => Model?.Close();
        DataContextChanged += (_, _) =>
        {
            if (Model is not { } vm) return;
            _shownIndex = vm.Index;
            vm.FullResolutionShown += () =>
            {
                if (!_actualSizeWhenFull) return;
                _actualSizeWhenFull = false;
                Viewer.ActualSize();
            };
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewerViewModel.Index))
                {
                    Viewer.ResetOnNextSource = true;
                    Viewer.TransitionDirection = Math.Sign(vm.Index - _shownIndex);
                    _shownIndex = vm.Index;
                }
                if (e.PropertyName is nameof(ViewerViewModel.Tag) or nameof(ViewerViewModel.Index)) UpdateTagButtons();
            };
            UpdateTagButtons();
        };
    }

    private Point? _infoDragFrom;

    private void OnInfoDragStart(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(InfoPanel).Properties.IsLeftButtonPressed) return;
        _infoDragFrom = e.GetPosition(this);
        e.Pointer.Capture(InfoPanel);
        e.Handled = true;
    }

    private void OnInfoDrag(object? sender, PointerEventArgs e)
    {
        if (_infoDragFrom is not { } from) return;
        var now = e.GetPosition(this);
        _infoDragFrom = now;
        MoveInfoPanel(now - from);
    }

    /// <summary>Moves the info panel by <paramref name="delta"/>, keeping it inside the photo area.</summary>
    private void MoveInfoPanel(Vector delta)
    {
        if (InfoPanel.RenderTransform is not TranslateTransform shift || InfoPanel.Parent is not Control area) return;
        // Bounds is the panel's place in the layout (top right); the transform is how far it was dragged from it
        var home = InfoPanel.Bounds;
        shift.X = Math.Clamp(shift.X + delta.X, -home.X, Math.Max(-home.X, area.Bounds.Width - home.Right));
        shift.Y = Math.Clamp(shift.Y + delta.Y, -home.Y, Math.Max(-home.Y, area.Bounds.Height - home.Bottom));
    }

    private void UpdateTagButtons()
    {
        var tag = Model?.Tag ?? KanTuNext.Core.Library.PhotoTag.None;
        TagGood.Classes.Set("on", tag == KanTuNext.Core.Library.PhotoTag.Good);
        TagNeutral.Classes.Set("on", tag == KanTuNext.Core.Library.PhotoTag.Neutral);
        TagBad.Classes.Set("on", tag == KanTuNext.Core.Library.PhotoTag.Bad);
    }

    private ViewerViewModel? Model => DataContext as ViewerViewModel;

    /// <summary>Opens a viewer on <paramref name="index"/> of <paramref name="items"/>, sized to the screen.</summary>
    public static void Open(Window owner, IReadOnlyList<KanTuNext.Core.Library.MediaFile> items, int index, bool liveText)
    {
        var model = new ViewerViewModel(items, index) { LiveText = liveText };
        var window = new ViewerWindow { DataContext = model };
        if (owner.Screens.ScreenFromWindow(owner) is { } screen)
        {
            // First decodes match the screen in physical pixels: enough for fit-to-window on this display
            model.ScreenEdge = Math.Clamp(Math.Max(screen.Bounds.Width, screen.Bounds.Height), 1600, 6000);
            var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
            window.Width = Math.Round(area.Width * 0.8);
            window.Height = Math.Round(area.Height * 0.85);
        }
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        window.Activate();
        _ = model.ShowAsync(model.Index);
    }

    private void ShowToolbar()
    {
        ToolbarDock.Opacity = 1;
        _hideToolbar.Stop();
        _hideToolbar.Start();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Already taken by a focused control (text selected in the info or OCR panel copies itself)
        if (e.Handled || Model is not { } vm) return;
        switch (e.Key)
        {
            case Key.Left or Key.Up or Key.PageUp: _ = vm.GoBackAsync(); break;
            case Key.Right or Key.Down or Key.PageDown or Key.Space: _ = vm.GoForwardAsync(); break;
            case Key.Home: _ = vm.ShowAsync(0); break;
            case Key.OemPlus or Key.Add: Viewer.ZoomBy(1.25); break;
            case Key.OemMinus or Key.Subtract: Viewer.ZoomBy(0.8); break;
            case Key.D0 or Key.NumPad0: Viewer.FitToView(); break;
            case Key.D1 or Key.NumPad1: ActualSize(); break;
            case Key.R: Viewer.Rotate(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : -1); break;
            case Key.Escape when Viewer.HasSelection: Viewer.ClearSelection(); break;
            case Key.Escape: Close(); break;
            case Key.A when Viewer.HasText && (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control)):
                Viewer.SelectAll();
                break;
            case Key.Delete or Key.Back: _ = DeleteAsync(); break;
            case Key.T when vm.OcrAvailable: vm.ToggleOcr(); break;
            case Key.I when e.KeyModifiers == KeyModifiers.None: vm.ToggleInfo(); break;
            case Key.H when e.KeyModifiers == KeyModifiers.None: ToggleHandTool(); break;
            case Key.L when vm.IsLivePhoto: _ = vm.PlayLiveAsync(); break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control):
                // Copies text selected on the photo, else all recognized text while it's shown, else the photo
                if (Viewer.HasSelection) _ = CopySelectionAsync();
                else if (vm.HasOcrText && vm.ShowOcr) _ = CopyTextAsync();
                else _ = CopyImageAsync();
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnPrevious(object? sender, RoutedEventArgs e) => _ = Model?.GoBackAsync();
    private void OnNext(object? sender, RoutedEventArgs e) => _ = Model?.GoForwardAsync();
    private void OnZoomIn(object? sender, RoutedEventArgs e) => Viewer.ZoomBy(1.25);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => Viewer.ZoomBy(0.8);
    private void OnActualSize(object? sender, RoutedEventArgs e) => ActualSize();

    /// <summary>1:1 means file pixels: on a screen-sized decode, redo it once the full resolution is in.</summary>
    private void ActualSize()
    {
        _actualSizeWhenFull = Model?.IsReduced == true;
        Viewer.ActualSize();
    }
    private void OnFit(object? sender, RoutedEventArgs e) => Viewer.FitToView();
    private void OnRotate(object? sender, RoutedEventArgs e) => Viewer.Rotate(-1);
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private void OnDelete(object? sender, RoutedEventArgs e) => _ = DeleteAsync();
    private void OnOcr(object? sender, RoutedEventArgs e) => Model?.ToggleOcr();
    private void OnPlayLive(object? sender, RoutedEventArgs e) => _ = Model?.PlayLiveAsync();
    private void OnCopyText(object? sender, RoutedEventArgs e) => _ = CopyTextAsync();

    private void OnInfo(object? sender, RoutedEventArgs e) => Model?.ToggleInfo();
    private void OnHandTool(object? sender, RoutedEventArgs e) => ToggleHandTool();

    /// <summary>Hand tool on: drags pan the photo everywhere, text included (as Photoshop's H).</summary>
    private void ToggleHandTool()
    {
        Viewer.HandTool = !Viewer.HandTool;
        HandButton.Classes.Set("on", Viewer.HandTool);
    }

    private void OnCopyDetails(object? sender, RoutedEventArgs e)
    {
        if (Model?.Details is { } details) _ = Clipboard?.SetTextAsync(details.ToText());
    }

    private void OnOpenMap(object? sender, RoutedEventArgs e)
    {
        if (Model?.Details?.MapUrl is { } url) Opener.Open(url);
    }

    private async Task CopyImageAsync()
    {
        if (Model is { } vm && Clipboard is { } clipboard && await vm.FullBitmapAsync() is { } bitmap)
            await clipboard.SetBitmapAsync(bitmap);
    }

    /// <summary>
    /// Copies the text selected on the photo. Where the selection starts or ends inside a line, that part is
    /// recognized again on its own (character positions within a line are only estimated); if that finds
    /// nothing, the estimate is copied.
    /// </summary>
    private async Task CopySelectionAsync()
    {
        if (Model?.Current is not { } file || Clipboard is not { } clipboard) return;
        var pieces = Viewer.SelectionPieces;
        if (pieces.Count == 0) return;
        var partial = pieces.Where(p => p.Region is not null).ToList();
        var exact = partial.Count == 0 ? []
            : await Services.Ocr.OcrRegions.RecognizeAsync(file.Path, partial.Select(p => p.Region!.Value).ToList());
        var text = ZoomImage.JoinPieces(pieces.Select(p =>
        {
            var i = partial.IndexOf(p);
            return (p.Row, i >= 0 && exact[i] is { Length: > 0 } recognized ? OcrText.Reconcile(recognized, p.LineText) : p.Text);
        }));
        if (text.Length > 0) await clipboard.SetTextAsync(text);
    }

    private async Task CopyPathAsync()
    {
        if (Model?.Current is { } file && Clipboard is { } clipboard) await clipboard.SetTextAsync(file.Path);
    }

    /// <summary>Right-click on the photo: info, copy, open elsewhere, view, mark, delete.</summary>
    private void ShowContextMenu()
    {
        if (Model is not { Current: { } file } vm) return;
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        MenuItem Item(string header, string? icon, Action action, KeyGesture? gesture = null, bool isChecked = false)
        {
            var item = new MenuItem { Header = header, InputGesture = gesture };
            if (isChecked) item.Icon = new PathIcon { Data = (Geometry)this.FindResource("Icon.check")!, Width = 12, Height = 12 };
            else if (icon is not null) item.Icon = new PathIcon { Data = (Geometry)this.FindResource("Icon." + icon)!, Width = 13, Height = 13 };
            item.Click += (_, _) => action();
            return item;
        }
        var tag = vm.Tag;
        var items = new List<object>();
        if (Viewer.HasText)
        {
            // Text recognized on the photo comes first: what the right-click was most likely for
            if (Viewer.HasSelection) items.Add(Item("复制所选文字", "content-copy", () => _ = CopySelectionAsync(), new KeyGesture(Key.C, command)));
            items.Add(Item("全选文字", "text-recognition", Viewer.SelectAll, new KeyGesture(Key.A, command)));
            items.Add(Item("复制全部文字", null, () => _ = CopyTextAsync()));
            items.Add(new Separator());
        }
        items.AddRange(new List<object>
        {
            Item(vm.ShowInfo ? "隐藏图片信息" : "图片信息", "information-outline", vm.ToggleInfo, new KeyGesture(Key.I)),
            new Separator(),
            Item("复制图片", Viewer.HasText ? null : "content-copy", () => _ = CopyImageAsync(), Viewer.HasSelection ? null : new KeyGesture(Key.C, command)),
            Item("复制文件路径", null, () => _ = CopyPathAsync()),
            Item(OperatingSystem.IsMacOS() ? "在访达中显示" : OperatingSystem.IsWindows() ? "在资源管理器中显示" : "打开所在文件夹",
                "folder-search-outline", () => Reveal.InFileManager(file.Path), new KeyGesture(Key.R, command | KeyModifiers.Shift)),
            Item("用默认应用打开", "open-in-app", () => Opener.Open(file.Path)),
            new Separator(),
            Item(Viewer.HandTool ? "关闭抓手工具" : "抓手工具", "hand-back-right-outline", ToggleHandTool, new KeyGesture(Key.H)),
            Item("实际大小", null, ActualSize, new KeyGesture(Key.D1)),
            Item("适应窗口", "fit-to-screen-outline", Viewer.FitToView, new KeyGesture(Key.D0)),
            Item("向左旋转", "rotate-left", () => Viewer.Rotate(-1), new KeyGesture(Key.R)),
            Item("向右旋转", "rotate-right", () => Viewer.Rotate(1), new KeyGesture(Key.R, KeyModifiers.Shift)),
        });
        if (vm.OcrAvailable || vm.IsLivePhoto) items.Add(new Separator());
        if (vm.OcrAvailable) items.Add(Item(vm.ShowOcr ? "关闭识别文字" : "识别文字", "text-recognition", vm.ToggleOcr, new KeyGesture(Key.T)));
        if (vm.IsLivePhoto) items.Add(Item("播放实况", "play-circle-outline", () => _ = vm.PlayLiveAsync(), new KeyGesture(Key.L)));
        items.Add(new Separator());
        items.Add(Item("标记为「好」", "thumb-up-outline", () => vm.ToggleTag(KanTuNext.Core.Library.PhotoTag.Good), isChecked: tag == KanTuNext.Core.Library.PhotoTag.Good));
        items.Add(Item("标记为「中」", "emoticon-neutral-outline", () => vm.ToggleTag(KanTuNext.Core.Library.PhotoTag.Neutral), isChecked: tag == KanTuNext.Core.Library.PhotoTag.Neutral));
        items.Add(Item("标记为「差」", "thumb-down-outline", () => vm.ToggleTag(KanTuNext.Core.Library.PhotoTag.Bad), isChecked: tag == KanTuNext.Core.Library.PhotoTag.Bad));
        items.Add(new Separator());
        items.Add(Item("移到废纸篓", "trash-can-outline", () => _ = DeleteAsync(), new KeyGesture(Key.Delete)));
        new ContextMenu { ItemsSource = items }.Open(Viewer);
    }

    private async Task CopyTextAsync()
    {
        if (Model is { HasOcrText: true } vm && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.OcrText);
    }

    private void OnTag(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && Enum.TryParse<KanTuNext.Core.Library.PhotoTag>(name, out var tag))
            Model?.ToggleTag(tag);
    }

    // macOS menu bar
    private void OnRevealMenu(object? sender, EventArgs e)
    {
        if (Model?.Current is { } file) Reveal.InFileManager(file.Path);
    }
    private void OnCloseMenu(object? sender, EventArgs e) => Close();
    private void OnZoomInMenu(object? sender, EventArgs e) => Viewer.ZoomBy(1.25);
    private void OnZoomOutMenu(object? sender, EventArgs e) => Viewer.ZoomBy(0.8);
    private void OnActualSizeMenu(object? sender, EventArgs e) => ActualSize();
    private void OnFitMenu(object? sender, EventArgs e) => Viewer.FitToView();
    private void OnRotateLeftMenu(object? sender, EventArgs e) => Viewer.Rotate(-1);
    private void OnRotateRightMenu(object? sender, EventArgs e) => Viewer.Rotate(1);
    private void OnPreviousMenu(object? sender, EventArgs e) => _ = Model?.GoBackAsync();
    private void OnNextMenu(object? sender, EventArgs e) => _ = Model?.GoForwardAsync();
    private void OnOcrMenu(object? sender, EventArgs e) => Model?.ToggleOcr();
    private void OnInfoMenu(object? sender, EventArgs e) => Model?.ToggleInfo();
    private void OnCopyImageMenu(object? sender, EventArgs e) => _ = CopyImageAsync();
    private void OnPlayLiveMenu(object? sender, EventArgs e) => _ = Model?.PlayLiveAsync();
    private void OnTagGoodMenu(object? sender, EventArgs e) => Model?.ToggleTag(KanTuNext.Core.Library.PhotoTag.Good);
    private void OnTagNeutralMenu(object? sender, EventArgs e) => Model?.ToggleTag(KanTuNext.Core.Library.PhotoTag.Neutral);
    private void OnTagBadMenu(object? sender, EventArgs e) => Model?.ToggleTag(KanTuNext.Core.Library.PhotoTag.Bad);
    private void OnTrashMenu(object? sender, EventArgs e) => _ = DeleteAsync();
    private void OnMinimizeMenu(object? sender, EventArgs e) => WindowState = WindowState.Minimized;
    private void OnZoomWindowMenu(object? sender, EventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private async Task DeleteAsync()
    {
        if (Model is not { } vm) return;
        await vm.DeleteCurrentAsync();
        if (vm.IsEmpty) Close();
    }
}
