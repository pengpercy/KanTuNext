using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
            ViewerWindow.Open(this, images, images.IndexOf(file));
        };
        // Keep the toolbar's right-hand icons clear of the drawn min/max/close buttons
        if (WindowChrome.HasClientCaptionButtons)
            Toolbar.Margin = new Thickness(Toolbar.Margin.Left, 0, WindowChrome.CaptionButtonsWidth + 12, 0);

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

    private (Size Size, PixelPoint Position) _normal, _beforeBurst;
    private long _normalChangedAt;

    /// <summary>
    /// Applies the saved placement before the window is shown: the size always, the position only if its title
    /// bar still lands on a screen (monitors come and go), maximized once open. Then tracks the normal bounds so
    /// closing while maximized still saves the size to restore to.
    /// </summary>
    public void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is { Width: > 0, Height: > 0 })
        {
            Width = Math.Max(MinWidth, placement.Width);
            Height = Math.Max(MinHeight, placement.Height);
            if (placement is { X: { } x, Y: { } y } && Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint(x + 80, y + 16))))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(x, y);
            }
        }
        var maximize = placement?.Maximized == true;
        Opened += (_, _) =>
        {
            _normal = _beforeBurst = (ClientSize, Position);
            if (maximize) WindowState = WindowState.Maximized;
        };
        Resized += (_, _) => TrackNormal(ClientSize, _normal.Position);
        PositionChanged += (_, e) => TrackNormal(_normal.Size, e.Point);
        PropertyChanged += (_, e) =>
        {
            // macOS reports the zoom animation's sizes while the state still reads Normal; a maximize or full
            // screen right after a burst of changes means that burst was the animation, so undo it
            if (e.Property == WindowStateProperty && WindowState is WindowState.Maximized or WindowState.FullScreen
                && Environment.TickCount64 - _normalChangedAt < 1500)
                _normal = _beforeBurst;
        };
        Closing += (_, _) =>
        {
            if (DataContext is not MainViewModel vm || _normal.Size.Width <= 0) return;
            vm.Placement = new WindowPlacement
            {
                Width = Math.Round(_normal.Size.Width),
                Height = Math.Round(_normal.Size.Height),
                X = _normal.Position.X,
                Y = _normal.Position.Y,
                Maximized = WindowState == WindowState.Maximized,
            };
        };
    }

    /// <summary>Records normal-state bounds; changes less than half a second apart form one burst.</summary>
    private void TrackNormal(Size size, PixelPoint position)
    {
        if (WindowState != WindowState.Normal) return;
        var now = Environment.TickCount64;
        if (now - _normalChangedAt > 500) _beforeBurst = _normal;
        _normal = (size, position);
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
