using Avalonia.Controls;
using Avalonia.Interactivity;
using KanTuNext.Desktop.ViewModels;

namespace KanTuNext.Desktop.Views;

public partial class UpdateWindow : Window
{
    private static UpdateWindow? _current;
    private static bool _preparingInBackground;

    public UpdateWindow()
    {
        InitializeComponent();
    }

    private UpdateWindow(UpdateViewModel model) : this()
    {
        DataContext = model;
        model.CloseRequested += Close;
        // Closing the window (Esc, close button) abandons whatever is in flight
        Closed += (_, _) =>
        {
            model.Cancel();
            if (ReferenceEquals(_current, this)) _current = null;
        };
    }

    private UpdateViewModel? Model => DataContext as UpdateViewModel;

    /// <summary>「检查更新…」: one window, showing the check right away; asking again brings it forward.</summary>
    public static void CheckInteractively(MainViewModel main, Window owner)
    {
        if (BringForward()) return;
        var model = main.CreateUpdate();
        Show(model, owner);
        _ = model.CheckAsync();
    }

    /// <summary>
    /// The check a few seconds after launch: silent unless a newer, non-skipped release exists. With automatic
    /// installs on it downloads and unpacks in the background and only surfaces 「重新启动」.
    /// </summary>
    public static async Task CheckAutomaticallyAsync(MainViewModel main, Window owner)
    {
        if (_current is not null || _preparingInBackground) return;
        var model = main.CreateUpdate();
        var release = await model.FindNewerQuietlyAsync();
        if (release is null || string.Equals(release.TagName, main.SkippedVersion, StringComparison.OrdinalIgnoreCase)) return;

        if (model is { InstallAutomatically: true, CanInstall: true })
        {
            _preparingInBackground = true;
            try
            {
                if (!await model.DownloadAndPrepareAsync(release)) return;
            }
            finally
            {
                _preparingInBackground = false;
            }
        }
        else model.ShowAvailable(release);
        // The user may have opened the window by hand meanwhile
        if (_current is null) Show(model, owner);
    }

    private static bool BringForward()
    {
        if (_current is not { } window) return false;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        return true;
    }

    private static void Show(UpdateViewModel model, Window owner)
    {
        _current = new UpdateWindow(model);
        // Not modal: the app stays usable while an update downloads
        if (owner.IsVisible) _current.Show(owner);
        else
        {
            _current.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _current.Show();
        }
    }

    private void OnSkip(object? sender, RoutedEventArgs e) => Model?.Skip();
    private void OnLater(object? sender, RoutedEventArgs e) => Close();
    private void OnInstall(object? sender, RoutedEventArgs e) => _ = Model?.InstallAsync();
    private void OnReleasePage(object? sender, RoutedEventArgs e)
    {
        Model?.OpenReleasePage();
        Close();
    }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
    private void OnRestart(object? sender, RoutedEventArgs e) => Model?.Restart();
}
