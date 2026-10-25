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
