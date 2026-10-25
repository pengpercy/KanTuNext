using Avalonia.Controls;
using Avalonia.Interactivity;
using KanTuNext.Desktop.ViewModels;

namespace KanTuNext.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void OnClearCache(object? sender, RoutedEventArgs e) => _ = (DataContext as SettingsViewModel)?.ClearCacheAsync();
}
