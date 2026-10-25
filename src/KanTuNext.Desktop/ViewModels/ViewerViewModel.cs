using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using KanTuNext.Core.Library;
using KanTuNext.Desktop.Services;

namespace KanTuNext.Desktop.ViewModels;

/// <summary>One open viewer: a list of photos (the grid's current order) and the one being shown.</summary>
public sealed partial class ViewerViewModel : ObservableObject
{
    private static readonly ImageLoader Loader = new();
    private readonly IReadOnlyList<MediaFile> _items;
    private int _loadVersion;

    public ViewerViewModel(IReadOnlyList<MediaFile> items, int index)
    {
        _items = items;
        Index = Math.Clamp(index, 0, Math.Max(0, items.Count - 1));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(Title), nameof(CanGoBack), nameof(CanGoForward), nameof(Position))]
    public partial int Index { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    public partial Bitmap? Image { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    public partial bool IsLoading { get; private set; }

    public MediaFile? Current => _items.Count > 0 ? _items[Index] : null;
    public string Title => Current?.Name ?? "看图";
    public string Position => _items.Count > 1 ? $"{Index + 1} / {_items.Count}" : "";
    public bool CanGoBack => Index > 0;
    public bool CanGoForward => Index < _items.Count - 1;
    public bool ShowError => !IsLoading && Image is null && Current is not null;

    public Task GoBackAsync() => CanGoBack ? ShowAsync(Index - 1) : Task.CompletedTask;
    public Task GoForwardAsync() => CanGoForward ? ShowAsync(Index + 1) : Task.CompletedTask;

    public async Task ShowAsync(int index)
    {
        Index = index;
        var version = ++_loadVersion;
        if (Current is not { } item) return;
        var task = Loader.LoadAsync(item.Path);
        // Keep the previous image up for a decode that finishes within a frame or two; otherwise show loading
        if (!task.IsCompleted) IsLoading = true;
        var bitmap = await task;
        if (version != _loadVersion) return; // the user has moved on
        Image = bitmap;
        IsLoading = false;
        if (CanGoForward) Loader.Prefetch(_items[Index + 1].Path);
        if (CanGoBack) Loader.Prefetch(_items[Index - 1].Path);
    }
}
