using Avalonia.Controls;
using Avalonia.Interactivity;
using KanTuNext.Desktop.ViewModels;

namespace KanTuNext.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnCheckUpdates(object? sender, RoutedEventArgs e) => (Owner as MainWindow)?.CheckForUpdates();

    private void OnClearCache(object? sender, RoutedEventArgs e) => _ = (DataContext as SettingsViewModel)?.ClearCacheAsync();
}
