using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KanTuNext.Core.Library;
using KanTuNext.Core.Settings;
using KanTuNext.Desktop.Services;
using KanTuNext.Desktop.ViewModels;

namespace KanTuNext.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindows()) WindowsResizeFrame.Attach(this);
        Grid.ItemContextRequested += ShowContextMenu;
        Grid.SelectionChanged += file => { if (DataContext is MainViewModel vm) vm.SelectedFile = file; };
        Grid.Opened += file =>
        {
            if (DataContext is not MainViewModel vm || file.Kind != KanTuNext.Core.Library.MediaKind.Image) return;
            var images = vm.Groups?.SelectMany(g => g.Items).Where(f => f.Kind == KanTuNext.Core.Library.MediaKind.Image).ToList() ?? [];
            // After the double-click has been handled: opened in the middle of it, the viewer would come up and
            // then fall behind the main window, which macOS raises again when it finishes processing the click
            Dispatcher.UIThread.Post(() => ViewerWindow.Open(this, images, images.IndexOf(file), vm.LiveText), DispatcherPriority.Background);
        };
        if (WindowChrome.CaptionButtonsOnLeft) SidebarToggle.IsVisible = true;
        else SidebarMenuButton.IsVisible = true;
        // Dragging the divider: the toggle follows it (a slide places it frame by frame instead)
        Sidebar.SizeChanged += (_, e) =>
        {
            if (Sidebar.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Stretch && Sidebar.IsVisible)
                PlaceSidebarChrome(e.NewSize.Width);
        };
        // Keep the toolbar's right-hand icons clear of the drawn min/max/close buttons
        if (WindowChrome.HasClientCaptionButtons)
            Toolbar.Margin = new Thickness(Toolbar.Margin.Left, 0, WindowChrome.CaptionButtonsWidth + 12, 0);

        // A blurred backdrop behind the sidebar where the platform has one (macOS vibrancy, Windows 11 Mica,
        // Windows 10 acrylic); the window stays opaque, sidebar included, wherever it doesn't
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
            TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ActualTransparencyLevelProperty) UpdateTranslucency();
        };
        UpdateTranslucency();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            ApplySidebar(vm);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SidebarVisible)) SlideSidebar(vm);
            };
        };

        if (OperatingSystem.IsMacOS())
        {
            // AppKit lays the traffic lights out for its 28pt bar and resets them on resize, activation,
            // state and scaling changes; re-centre them in our 52pt toolbar each time.
            Opened += (_, _) => CenterTrafficLights();
            Resized += (_, _) => CenterTrafficLights();
            Activated += (_, _) => CenterTrafficLights();
            ScalingChanged += (_, _) => CenterTrafficLights();
            PropertyChanged += (_, e) =>
            {
                if (e.Property == WindowStateProperty) CenterTrafficLights();
            };
            // A theme switch relays out the title bar after ActualThemeVariantChanged; post the update.
            ActualThemeVariantChanged += (_, _) => Dispatcher.UIThread.Post(CenterTrafficLights, DispatcherPriority.Background);
        }
    }

    private Size _normal, _beforeBurst;
    private long _normalChangedAt;

    /// <summary>
    /// Applies the saved size before the window is shown (it always opens centred), maximized once open. Then
    /// tracks the normal size so closing while maximized still saves the size to restore to.
    /// </summary>
    public void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is { Width: > 0, Height: > 0 })
        {
            Width = Math.Max(MinWidth, placement.Width);
            Height = Math.Max(MinHeight, placement.Height);
        }
        var maximize = placement?.Maximized == true;
        Opened += (_, _) =>
        {
            _normal = _beforeBurst = ClientSize;
            if (maximize) WindowState = WindowState.Maximized;
        };
        Resized += (_, _) => TrackNormal(ClientSize);
        PropertyChanged += (_, e) =>
        {
            // macOS reports the zoom animation's sizes while the state still reads Normal; a maximize or full
            // screen right after a burst of resizes means that burst was the animation, so undo it
            if (e.Property == WindowStateProperty && WindowState is WindowState.Maximized or WindowState.FullScreen
                && Environment.TickCount64 - _normalChangedAt < 1500)
                _normal = _beforeBurst;
        };
        Closing += (_, _) =>
        {
            if (DataContext is not MainViewModel vm || _normal.Width <= 0) return;
            vm.Placement = new WindowPlacement
            {
                Width = Math.Round(_normal.Width),
                Height = Math.Round(_normal.Height),
                Maximized = WindowState == WindowState.Maximized,
            };
        };
    }

    /// <summary>Records the normal-state size; resizes less than half a second apart form one burst.</summary>
    private void TrackNormal(Size size)
    {
        if (WindowState != WindowState.Normal) return;
        var now = Environment.TickCount64;
        if (now - _normalChangedAt > 500) _beforeBurst = _normal;
        _normal = size;
        _normalChangedAt = now;
    }

    /// <summary>Right-click menu on a thumbnail: put back (最近删除) or reveal / mark / delete.</summary>
    private void ShowContextMenu(KanTuNext.Core.Library.MediaFile file)
    {
        if (DataContext is not MainViewModel vm) return;
        var items = new List<object>();
        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
        if (vm.IsRecentlyDeleted)
        {
            items.Add(Item("放回原处", () => vm.Restore(file)));
        }
        else
        {
            items.Add(Item(OperatingSystem.IsMacOS() ? "在访达中显示" : OperatingSystem.IsWindows() ? "在资源管理器中显示" : "打开所在文件夹",
                () => Reveal.InFileManager(file.Path)));
            items.Add(new Separator());
            items.Add(Item("标记为「好」", () => vm.ToggleTag(file, KanTuNext.Core.Library.PhotoTag.Good)));
            items.Add(Item("标记为「中」", () => vm.ToggleTag(file, KanTuNext.Core.Library.PhotoTag.Neutral)));
            items.Add(Item("标记为「差」", () => vm.ToggleTag(file, KanTuNext.Core.Library.PhotoTag.Bad)));
            items.Add(new Separator());
            items.Add(Item("移到废纸篓", () => vm.Delete(file)));
        }
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(Grid);
    }

    private ColumnDefinition SidebarColumn => Layout.ColumnDefinitions[0];

    private void UpdateTranslucency() =>
        Classes.Set("translucent", ActualTransparencyLevel != WindowTransparencyLevel.None
                                   && ActualTransparencyLevel != WindowTransparencyLevel.Transparent);

    private const double SidebarSlideMs = 250;
    private int _sidebarSlide;

    /// <summary>Shows or collapses the sidebar at its saved width.</summary>
    private void ApplySidebar(MainViewModel vm)
    {
        _sidebarSlide++;
        var visible = vm.SidebarVisible;
        Sidebar.IsVisible = SidebarSplitter.IsVisible = visible;
        Sidebar.Width = double.NaN;
        Sidebar.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        SidebarColumn.MinWidth = visible ? 160 : 0;
        SidebarColumn.Width = new GridLength(visible ? SidebarWidth(vm) : 0);
        PlaceSidebarChrome(visible ? SidebarWidth(vm) : 0);
    }

    /// <summary>
    /// Slides the sidebar in or out: the column eases to its new width while the sidebar keeps its full width,
    /// pinned to the column's right edge, so it moves in from the left instead of squeezing its rows.
    /// </summary>
    private void SlideSidebar(MainViewModel vm)
    {
        var visible = vm.SidebarVisible;
        var width = SidebarWidth(vm);
        var from = SidebarColumn.ActualWidth;
        var to = visible ? width : 0;
        var slide = ++_sidebarSlide;
        Sidebar.IsVisible = true;
        SidebarSplitter.IsVisible = false;
        Sidebar.Width = width;
        Sidebar.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        SidebarColumn.MinWidth = 0;
        var start = -1d;
        void Step(TimeSpan now)
        {
            if (slide != _sidebarSlide) return;
            if (start < 0) start = now.TotalMilliseconds;
            var t = Math.Clamp((now.TotalMilliseconds - start) / SidebarSlideMs, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            var column = from + (to - from) * eased;
            SidebarColumn.Width = new GridLength(column);
            PlaceSidebarChrome(column);
            if (t < 1) RequestAnimationFrame(Step);
            else ApplySidebar(vm);
        }
        RequestAnimationFrame(Step);
    }

    private static double SidebarWidth(MainViewModel vm) => Math.Clamp(vm.SidebarWidth, 160, 400);

    private const double ToggleSize = 26, ToggleGap = 10;

    /// <summary>
    /// Lays out the top-left corner for a sidebar column of the given width. The toggle sits just inside the
    /// divider and is pushed left by it until it rests after the window buttons (macOS traffic lights); the
    /// toolbar starts after the divider, or after the toggle or hamburger once the divider has passed them.
    /// </summary>
    private void PlaceSidebarChrome(double column)
    {
        double rest = 48; // after the hamburger
        if (WindowChrome.CaptionButtonsOnLeft)
        {
            var toggleRest = OperatingSystem.IsMacOS() ? 84 : 24;
            SidebarToggle.Margin = new Thickness(Math.Max(column - ToggleGap - ToggleSize, toggleRest), 13, 0, 0);
            rest = toggleRest + ToggleSize + ToggleGap;
        }
        Toolbar.Margin = new Thickness(Math.Max(24, rest - column), 0, Toolbar.Margin.Right, 0);
    }

    private void ToggleSidebar()
    {
        if (DataContext is MainViewModel vm) vm.SidebarVisible = !vm.SidebarVisible;
    }

    private void OnToggleSidebar(object? sender, RoutedEventArgs e) => ToggleSidebar();

    private void OnSidebarResized(object? sender, Avalonia.Input.VectorEventArgs e)
    {
        if (DataContext is MainViewModel vm && Sidebar.IsVisible) vm.SidebarWidth = Math.Round(SidebarColumn.ActualWidth);
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // macOS has ⌃⌘S in the 显示 menu; elsewhere Ctrl+B, as in editors with a side bar
        if (!e.Handled && !OperatingSystem.IsMacOS() && e.Key == Avalonia.Input.Key.B
            && e.KeyModifiers == Avalonia.Input.KeyModifiers.Control)
        {
            ToggleSidebar();
            e.Handled = true;
        }
    }

    private SettingsWindow? _settings;

    /// <summary>One settings window; a second request brings it forward.</summary>
    public void ShowSettings()
    {
        if (DataContext is not MainViewModel vm) return;
        if (_settings is { IsVisible: true })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow { DataContext = vm.CreateSettings() };
        _settings.Show(this);
    }

    private void OnSettings(object? sender, RoutedEventArgs e) => ShowSettings();

    /// <summary>Another launch of the app: show this window instead.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        if (OperatingSystem.IsMacOS()) MacApplication.Activate();
    }

    public void CheckForUpdates()
    {
        if (DataContext is MainViewModel vm) UpdateWindow.CheckInteractively(vm, this);
    }

    // macOS menu bar
    private MainViewModel? Model => DataContext as MainViewModel;
    private void OnAddFolderMenu(object? sender, EventArgs e) => OnAddFolder(sender, new RoutedEventArgs());
    private void OnCloseMenu(object? sender, EventArgs e) => Close();
    private void OnSizeMenu(object? sender, EventArgs e)
    {
        if (Model is not { } vm || sender is not NativeMenuItem item) return;
        vm.Size = item.Header switch { "中" => ThumbnailSize.Medium, "大" => ThumbnailSize.Large, "最大" => ThumbnailSize.Largest, _ => ThumbnailSize.Small };
    }
    private void OnToggleSidebarMenu(object? sender, EventArgs e) => ToggleSidebar();
    private void OnFindMenu(object? sender, EventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
    private void OnInfoMenu(object? sender, EventArgs e)
    {
        if (InfoButton.IsEnabled) InfoButton.Flyout?.ShowAt(InfoButton);
    }
    private void OnOpenMenu(object? sender, EventArgs e)
    {
        if (Model?.SelectedFile is { } file) Grid.OpenItem(file);
    }
    private void OnRevealMenu(object? sender, EventArgs e)
    {
        if (Model?.SelectedFile is { } file) Reveal.InFileManager(file.Path);
    }
    private void OnTagGoodMenu(object? sender, EventArgs e) => TagSelected(PhotoTag.Good);
    private void OnTagNeutralMenu(object? sender, EventArgs e) => TagSelected(PhotoTag.Neutral);
    private void OnTagBadMenu(object? sender, EventArgs e) => TagSelected(PhotoTag.Bad);
    private void TagSelected(PhotoTag tag)
    {
        if (Model is { SelectedFile: { } file } vm) vm.ToggleTag(file, tag);
    }
    private void OnTrashMenu(object? sender, EventArgs e)
    {
        if (Model is { SelectedFile: { } file } vm) vm.Delete(file);
    }
    private void OnMinimizeMenu(object? sender, EventArgs e) => WindowState = WindowState.Minimized;
    private void OnZoomMenu(object? sender, EventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnHomePageMenu(object? sender, EventArgs e) => Opener.Open("https://github.com/pengpercy/KanTuNext");
    private void OnCheckUpdatesMenu(object? sender, EventArgs e) => CheckForUpdates();

    private void OnToggleFolder(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SidebarItem item } && DataContext is MainViewModel vm) vm.ToggleExpanded(item);
        e.Handled = true; // don't select the row as well
    }

    private void OnScanDuplicates(object? sender, RoutedEventArgs e) => _ = (DataContext as MainViewModel)?.ScanDuplicatesAsync();

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "添加常用文件夹" });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path && DataContext is MainViewModel vm)
            vm.AddFolder(path);
    }

    private void CenterTrafficLights()
    {
        if (WindowState == WindowState.FullScreen) return;
        if (TryGetPlatformHandle()?.Handle is { } handle)
            MacTitleBarInsets.TryCenterTrafficLights(handle, ExtendClientAreaTitleBarHeightHint);
    }
}
