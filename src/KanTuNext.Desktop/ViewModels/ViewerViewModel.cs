using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using KanTuNext.Core.Library;
using KanTuNext.Core.Ocr;
using KanTuNext.Desktop.Services;
using KanTuNext.Desktop.Services.Ocr;

namespace KanTuNext.Desktop.ViewModels;

/// <summary>One open viewer: a list of photos (the grid's current order) and the one being shown.</summary>
public sealed partial class ViewerViewModel : ObservableObject
{
    private static readonly ImageLoader Loader = new();
    private readonly List<MediaFile> _items;
    private int _loadVersion;
    private LoadedImage? _loaded;
    private int _frame;
    private readonly DispatcherTimer _animation = new(DispatcherPriority.Render);

    public ViewerViewModel(IReadOnlyList<MediaFile> items, int index)
    {
        _items = [.. items];
        Index = Math.Clamp(index, 0, Math.Max(0, _items.Count - 1));
        _animation.Tick += (_, _) => NextFrame();
        AppServices.Tags.Changed += () => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(Tag)));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(Title), nameof(CanGoBack), nameof(CanGoForward), nameof(Position), nameof(Tag), nameof(IsLivePhoto))]
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
    public bool IsEmpty => _items.Count == 0;

    /// <summary>Longest edge for the first decode of each photo: the screen's, in physical pixels.</summary>
    public int ScreenEdge { get; set; } = 3000;

    /// <summary>The photo on screen is a screen-sized decode; zooming in asks for the full resolution.</summary>
    public bool IsReduced => _loaded?.IsReduced == true;

    /// <summary>Raised after the full-resolution decode replaced the screen-sized one.</summary>
    public event Action? FullResolutionShown;

    public async Task LoadFullResolutionAsync()
    {
        if (_loaded is not { IsReduced: true } || Current is not { } item) return;
        var version = _loadVersion;
        var full = await Loader.LoadAsync(item.Path, ImageLoader.FullEdge);
        if (version != _loadVersion || full is null || !IsReduced) return;
        _loaded = full;
        Image = full.First;
        FullResolutionShown?.Invoke();
    }

    // ---- OCR
    public bool OcrAvailable => OcrEngines.IsAvailable;

    [ObservableProperty]
    public partial bool ShowOcr { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OcrStatus))]
    public partial bool IsRecognizing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OcrStatus), nameof(HasOcrText))]
    public partial OcrResult? Ocr { get; private set; }

    public string OcrText => Ocr?.Text ?? "";
    public bool HasOcrText => OcrText.Length > 0;
    public IReadOnlyList<OcrRect>? OcrBoxes => ShowOcr ? Ocr?.Lines.Select(l => l.Bounds).ToArray() : null;
    public string OcrStatus => IsRecognizing ? "正在识别…" : Ocr is { Lines.Count: 0 } ? "没有识别到文字" : "";

    private CancellationTokenSource? _ocr;

    public void ToggleOcr()
    {
        ShowOcr = !ShowOcr;
        if (ShowOcr) _ = RecognizeAsync();
        else _ocr?.Cancel();
        OnPropertyChanged(nameof(OcrBoxes));
    }

    private async Task RecognizeAsync()
    {
        _ocr?.Cancel();
        var cts = _ocr = new CancellationTokenSource();
        Ocr = null;
        OnPropertyChanged(nameof(OcrText));
        OnPropertyChanged(nameof(OcrBoxes));
        if (Current is not { } item) return;
        IsRecognizing = true;
        try
        {
            var result = await OcrEngines.RecognizeAsync(item.Path, item.Orientation, cts.Token);
            if (cts.IsCancellationRequested) return;
            Ocr = result;
            OnPropertyChanged(nameof(OcrText));
            OnPropertyChanged(nameof(OcrBoxes));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_ocr == cts) IsRecognizing = false;
        }
    }

    public bool IsLivePhoto => Current?.IsLivePhoto == true;

    /// <summary>Plays the Live Photo's motion clip (in the system player until in-window playback lands).</summary>
    public void PlayLive()
    {
        if (Current?.LiveVideo is { } clip) Opener.Open(clip);
    }

    public PhotoTag Tag => Current is { } c ? AppServices.Tags.Get(c.Path) : PhotoTag.None;

    public void ToggleTag(PhotoTag tag)
    {
        if (Current is { } c) AppServices.Tags.Toggle(c.Path, tag);
    }

    /// <summary>Moves the current photo to the trash and shows the next one (or the previous at the end).</summary>
    public async Task<bool> DeleteCurrentAsync()
    {
        if (Current is not { } item) return false;
        if (!await Task.Run(() => AppServices.Delete(item.Path))) return false;
        _items.RemoveAt(Index);
        OnPropertyChanged(nameof(IsEmpty));
        if (_items.Count == 0)
        {
            StopAnimation();
            Image = null;
            return true;
        }
        await ShowAsync(Math.Min(Index, _items.Count - 1));
        return true;
    }

    public Task GoBackAsync() => CanGoBack ? ShowAsync(Index - 1) : Task.CompletedTask;
    public Task GoForwardAsync() => CanGoForward ? ShowAsync(Index + 1) : Task.CompletedTask;

    public async Task ShowAsync(int index)
    {
        Index = index;
        // Index may not change (delete keeps the position): refresh what depends on the item explicitly
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Tag));
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(IsLivePhoto));
        var version = ++_loadVersion;
        if (Current is not { } item) return;
        Loader.Pin(item.Path);
        var task = Loader.LoadAsync(item.Path, ScreenEdge);
        // Keep the previous image up for a decode that finishes within a frame or two; otherwise show loading
        if (!task.IsCompleted) IsLoading = true;
        var loaded = await task;
        if (version != _loadVersion) return; // the user has moved on
        StopAnimation();
        _loaded = loaded;
        Image = loaded?.First;
        IsLoading = false;
        if (loaded is { IsAnimated: true }) StartAnimation();
        if (ShowOcr) _ = RecognizeAsync();
        if (CanGoForward) Loader.Prefetch(_items[Index + 1].Path, ScreenEdge);
        if (CanGoBack) Loader.Prefetch(_items[Index - 1].Path, ScreenEdge);
    }

    private void StartAnimation()
    {
        _frame = 0;
        _animation.Interval = _loaded!.Durations![0];
        _animation.Start();
    }

    private void StopAnimation() => _animation.Stop();

    private void NextFrame()
    {
        if (_loaded is not { Frames: { } frames, Durations: { } durations }) return;
        _frame = (_frame + 1) % frames.Count;
        Image = frames[_frame];
        _animation.Interval = durations[_frame];
    }

    /// <summary>Stops the frame timer when the window closes.</summary>
    public void Close() => StopAnimation();
}
