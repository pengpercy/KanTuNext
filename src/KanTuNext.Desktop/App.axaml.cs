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

    /// <summary>macOS app menu: 设置… (⌘,).</summary>
    private void OnSettingsMenu(object? sender, EventArgs e) =>
        ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow)?.ShowSettings();

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            SettingsViewModel.Apply(vm.Theme);
            var window = new MainWindow { DataContext = vm };
            window.RestorePlacement(vm.Placement);
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
