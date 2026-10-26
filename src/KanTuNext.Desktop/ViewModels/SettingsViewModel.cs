using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using KanTuNext.Core;
using KanTuNext.Core.Settings;
using KanTuNext.Core.Timeline;

namespace KanTuNext.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _save;

    public SettingsViewModel(AppSettings settings, Action save)
    {
        _settings = settings;
        _save = save;
        _ = MeasureCacheAsync();
    }

    public string VersionText => $"看图 KanTuNext {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}";

    public bool CheckUpdatesAutomatically
    {
        get => _settings.CheckUpdatesAutomatically;
        set
        {
            _settings.CheckUpdatesAutomatically = value;
            _save();
        }
    }

    public bool InstallUpdatesAutomatically
    {
        get => _settings.InstallUpdatesAutomatically;
        set
        {
            _settings.InstallUpdatesAutomatically = value;
            _save();
        }
    }

    public bool IsSystemTheme { get => _settings.Theme == AppTheme.System; set { if (value) SetTheme(AppTheme.System); } }
    public bool IsLightTheme { get => _settings.Theme == AppTheme.Light; set { if (value) SetTheme(AppTheme.Light); } }
    public bool IsDarkTheme { get => _settings.Theme == AppTheme.Dark; set { if (value) SetTheme(AppTheme.Dark); } }

    private void SetTheme(AppTheme theme)
    {
        if (_settings.Theme == theme) return;
        _settings.Theme = theme;
        Apply(theme);
        _save();
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    /// <summary>Applies a theme choice to the whole app (also used at startup).</summary>
    public static void Apply(AppTheme theme)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
    }

    [ObservableProperty]
    public partial string CacheText { get; private set; } = "正在计算…";

    private async Task MeasureCacheAsync()
    {
        var bytes = await Task.Run(() =>
        {
            var dir = new DirectoryInfo(AppPaths.ThumbnailCacheDirectory);
            return dir.Exists ? dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        });
        CacheText = $"缩略图缓存占用 {TimelineText.FileSize(bytes)}，清除后会在浏览时重新生成";
    }

    public async Task ClearCacheAsync()
    {
        CacheText = "正在清除…";
        await Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(AppPaths.ThumbnailCacheDirectory)) Directory.Delete(AppPaths.ThumbnailCacheDirectory, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A file in use stays; what could be removed is gone
            }
        });
        await MeasureCacheAsync();
    }
}
