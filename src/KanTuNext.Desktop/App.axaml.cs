using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KanTuNext.Desktop.Services;
using KanTuNext.Desktop.ViewModels;
using KanTuNext.Desktop.Views;

namespace KanTuNext.Desktop;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        WindowChrome.Install();
    }

    private MainWindow? Main => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;

    // macOS app menu
    private void OnSettingsMenu(object? sender, EventArgs e) => Main?.ShowSettings();
    private void OnCheckUpdatesMenu(object? sender, EventArgs e) => Main?.CheckForUpdates();
    private void OnHideMenu(object? sender, EventArgs e) => MacApplication.Hide();
    private void OnHideOthersMenu(object? sender, EventArgs e) => MacApplication.HideOthers();
    private void OnShowAllMenu(object? sender, EventArgs e) => MacApplication.ShowAll();
    // Through the windows' Closing, so the main window still saves its size
    private void OnQuitMenu(object? sender, EventArgs e) => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.TryShutdown();

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            SettingsViewModel.Apply(vm.Theme);
            var window = new MainWindow { DataContext = vm };
            window.RestorePlacement(vm.Placement);
            desktop.MainWindow = window;
            // The update check waits until the window is up and the first folder is loading
            window.Opened += async (_, _) =>
            {
                if (!vm.CheckUpdatesAutomatically) return;
                await Task.Delay(TimeSpan.FromSeconds(5));
                await UpdateWindow.CheckAutomaticallyAsync(vm, window);
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
