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

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel() };
        base.OnFrameworkInitializationCompleted();
    }
}
