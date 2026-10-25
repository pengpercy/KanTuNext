using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
