using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using KanTuNext.Core.Library;
using KanTuNext.Core.Ocr;
using KanTuNext.Core.Settings;
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
    private readonly AppSettings _settings;
    private readonly Action _save;

    public ViewerViewModel(IReadOnlyList<MediaFile> items, int index, AppSettings settings, Action save)
    {
        _items = [.. items];
        _settings = settings;
        _save = save;
        Index = Math.Clamp(index, 0, Math.Max(0, _items.Count - 1));
        _animation.Tick += (_, _) => NextFrame();
        _slideshow.Tick += (_, _) =>
        {
            // The next photo restarts the clock once it is on screen (ShowAsync), so slow decodes don't shorten it
            _slideshow.Stop();
            _slideStarted = 0;
            _ = ShowAsync(CanGoForward ? Index + 1 : 0);
        };
        _live.Tick += (_, _) => NextLiveFrame();
        AppServices.Tags.Changed += () => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(Tag)));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(Title), nameof(CanGoBack), nameof(CanGoForward), nameof(Position), nameof(PositionSuffix), nameof(Tag), nameof(IsLivePhoto))]
    public partial int Index { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    public partial Bitmap? Image { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    public partial bool IsLoading { get; private set; }

    public MediaFile? Current => _items.Count > 0 ? _items[Index] : null;

    /// <summary>The photos being paged through (shrinks as photos are deleted), for the 预览条.</summary>
    public IReadOnlyList<MediaFile> Items => _items;
    public string Title => Current?.Name ?? "看图";
    public string Position => _items.Count > 1 ? $"{Index + 1} / {_items.Count}" : "";

    /// <summary>The count after the name in the title strip, spaced as the strip had it.</summary>
    public string PositionSuffix => Position.Length > 0 ? "\u2002 " + Position : "";
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

    /// <summary>The photo at full resolution (for 复制图片), decoding it if only the screen-sized one is loaded.</summary>
    public async Task<Bitmap?> FullBitmapAsync()
    {
        if (Current is not { } item) return null;
        if (_loaded is { IsReduced: false } loaded) return loaded.First;
        return (await Loader.LoadAsync(item.Path, ImageLoader.FullEdge))?.First;
    }

    // ---- 预览条: thumbnails of the photos before and after this one, down the left side or along the bottom

    public bool FilmstripVisible
    {
        get => _settings.FilmstripVisible;
        set
        {
            if (_settings.FilmstripVisible == value) return;
            _settings.FilmstripVisible = value;
            _save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilmstripMenuText));
        }
    }

    public bool FilmstripAtBottom
    {
        get => _settings.FilmstripAtBottom;
        set
        {
            if (_settings.FilmstripAtBottom == value) return;
            _settings.FilmstripAtBottom = value;
            _save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilmstripSideMenuText));
        }
    }

    public string FilmstripMenuText => FilmstripVisible ? "隐藏预览条" : "显示预览条";
    public string FilmstripSideMenuText => FilmstripAtBottom ? "预览条放到左侧" : "预览条放到底部";

    // ---- 轮播: pages forward on a timer, back to the first photo after the last

    public static IReadOnlyList<double> SlideshowIntervals { get; } = [1, 2, 3, 5, 8, 15];

    private readonly DispatcherTimer _slideshow = new();
    private long _slideStarted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayMenuText))]
    public partial bool IsPlaying { get; private set; }

    public string PlayMenuText => IsPlaying ? "暂停轮播" : "自动轮播";

    public bool CanPlay => _items.Count > 1;

    public double SlideshowSeconds
    {
        get => Math.Clamp(_settings.SlideshowSeconds, 1, 60);
        set
        {
            _settings.SlideshowSeconds = value;
            _save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SlideshowText));
            RestartSlide();
        }
    }

    public string SlideshowText => $"{SlideshowSeconds:0.#} 秒";

    /// <summary>How far this photo's turn has run, 0–1 (0 while the next photo is still loading).</summary>
    public double SlideProgress => _slideStarted == 0 ? 0
        : Math.Clamp(Stopwatch.GetElapsedTime(_slideStarted).TotalSeconds / SlideshowSeconds, 0, 1);

    /// <summary>The next shorter (<paramref name="step"/> 1, ]) or longer (-1, [) interval.</summary>
    public void ChangeSlideshowSpeed(int step)
    {
        var intervals = SlideshowIntervals;
        var current = Enumerable.Range(0, intervals.Count).MinBy(i => Math.Abs(intervals[i] - SlideshowSeconds));
        var next = Math.Clamp(current - step, 0, intervals.Count - 1);
        if (intervals[next] != SlideshowSeconds) SlideshowSeconds = intervals[next];
    }

    public void ToggleSlideshow()
    {
        IsPlaying = CanPlay && !IsPlaying;
        RestartSlide();
    }

    private void RestartSlide()
    {
        _slideshow.Stop();
        _slideStarted = 0;
        if (!IsPlaying) return;
        _slideshow.Interval = TimeSpan.FromSeconds(SlideshowSeconds);
        _slideStarted = Stopwatch.GetTimestamp();
        _slideshow.Start();
    }

    // ---- 图片信息 panel: stays open from photo to photo, refreshed for each

    [ObservableProperty]
    public partial bool ShowInfo { get; private set; }

    [ObservableProperty]
    public partial PhotoDetails? Details { get; private set; }

    private int _detailsVersion;

    public void ToggleInfo()
    {
        ShowInfo = !ShowInfo;
        if (ShowInfo) _ = LoadDetailsAsync();
    }

    private async Task LoadDetailsAsync()
    {
        var version = ++_detailsVersion;
        if (Current is not { } item) return;
        // A full-resolution decode knows the size of formats whose header Skia can't read (HEIC, RAW…)
        (int, int)? decoded = _loaded is { IsReduced: false, First: { } bitmap } ? (bitmap.PixelSize.Width, bitmap.PixelSize.Height) : null;
        var details = await Task.Run(() => PhotoDetails.Read(item, decoded));
        if (version == _detailsVersion) Details = details;
    }

    // ---- OCR. With live text on, every photo shown is recognized in the background so its text can be selected
    // right on the photo; 识别文字 (ShowOcr) adds the outlines and the side panel, recognizing if needed.
    public bool OcrAvailable => OcrEngines.IsAvailable;

    /// <summary>Recognize each photo shown, not only when 识别文字 is on (a setting).</summary>
    public bool LiveText { get; init; }

    /// <summary>A short pause before recognizing in the background, so paging quickly doesn't queue work.</summary>
    private static readonly TimeSpan LiveTextDelay = TimeSpan.FromMilliseconds(350);

    [ObservableProperty]
    public partial bool ShowOcr { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OcrStatus))]
    public partial bool IsRecognizing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OcrStatus), nameof(HasOcrText), nameof(OcrText), nameof(OcrLines))]
    public partial OcrResult? Ocr { get; private set; }

    public string OcrText => Ocr?.Text ?? "";
    public bool HasOcrText => OcrText.Length > 0;
    /// <summary>Selectable on the photo whenever known; outlined only while 识别文字 is on.</summary>
    public IReadOnlyList<OcrLine>? OcrLines => Ocr?.Lines;
    public string OcrStatus => IsRecognizing ? "正在识别…" : Ocr is { Lines.Count: 0 } ? "没有识别到文字" : "";

    private CancellationTokenSource? _ocr;
    // Results by file, so going back to a photo doesn't recognize it again
    private readonly Dictionary<string, OcrResult> _recognized = [];

    public void ToggleOcr()
    {
        ShowOcr = !ShowOcr;
        if (ShowOcr && Ocr is null && !IsRecognizing) _ = RecognizeAsync(TimeSpan.Zero);
    }

    /// <summary>Clears the previous photo's text and starts on this one's (cached, or recognized if wanted).</summary>
    private void StartRecognition()
    {
        _ocr?.Cancel();
        IsRecognizing = false;
        Ocr = Current is { } item && _recognized.TryGetValue(item.Path, out var known) ? known : null;
        if (Ocr is null && OcrAvailable && (ShowOcr || LiveText)) _ = RecognizeAsync(ShowOcr ? TimeSpan.Zero : LiveTextDelay);
    }

    private async Task RecognizeAsync(TimeSpan delay)
    {
        _ocr?.Cancel();
        var cts = _ocr = new CancellationTokenSource();
        if (Current is not { } item) return;
        IsRecognizing = true;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cts.Token);
            var result = await OcrEngines.RecognizeAsync(item.Path, item.Orientation, cts.Token);
            if (cts.IsCancellationRequested) return;
            _recognized[item.Path] = result;
            Ocr = result;
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

    private readonly DispatcherTimer _live = new(DispatcherPriority.Render);
    private List<Bitmap>? _liveFrames;
    private int _liveFrame;

    [ObservableProperty]
    public partial bool IsPlayingLive { get; private set; }

    /// <summary>
    /// Plays the Live Photo's motion once, in the window (macOS: frames from AVFoundation), then returns to the
    /// still. Elsewhere, or if the clip can't be read, hands it to the system player.
    /// </summary>
    public async Task PlayLiveAsync()
    {
        if (Current?.LiveVideo is not { } clip || IsPlayingLive) return;
        if (!OperatingSystem.IsMacOS())
        {
            Opener.Open(clip);
            return;
        }
        var version = _loadVersion;
        IsPlayingLive = true;
        var frames = await Task.Run(() =>
        {
            if (MacLivePhoto.Frames(clip) is not { } decoded) return null;
            var bitmaps = decoded.Select(ImageLoader.ToBitmap).ToList();
            foreach (var bitmap in decoded) bitmap.Dispose();
            return bitmaps;
        });
        if (frames is null || version != _loadVersion)
        {
            frames?.ForEach(f => f.Dispose());
            IsPlayingLive = false;
            if (frames is null) Opener.Open(clip);
            return;
        }
        StopAnimation();
        _liveFrames = frames;
        _liveFrame = 0;
        Image = frames[0];
        _live.Interval = TimeSpan.FromSeconds(1 / MacLivePhoto.FramesPerSecond);
        _live.Start();
    }

    private void NextLiveFrame()
    {
        if (_liveFrames is null) return;
        if (++_liveFrame < _liveFrames.Count)
        {
            Image = _liveFrames[_liveFrame];
            return;
        }
        StopLive();
        // Back to the still (and its animation, if it has one)
        Image = _loaded?.First;
        if (_loaded is { IsAnimated: true }) StartAnimation();
    }

    private void StopLive()
    {
        _live.Stop();
        var frames = _liveFrames;
        _liveFrames = null;
        IsPlayingLive = false;
        // After the still is back on screen (or the cross-fade to the next photo is over)
        if (frames is not null) ImageLoader.DisposeLater(frames);
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
        OnPropertyChanged(nameof(CanPlay));
        if (!CanPlay && IsPlaying) ToggleSlideshow();
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
        // Index may not change (delete keeps the position): refresh what depends on the item explicitly, Index
        // included, so the view still starts the next photo fitted (and cross-fades to it)
        if (Index == index) OnPropertyChanged(nameof(Index));
        else Index = index;
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Tag));
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(PositionSuffix));
        OnPropertyChanged(nameof(IsLivePhoto));
        var version = ++_loadVersion;
        // 轮播 waits for this photo, whether it advanced or the user paged
        _slideshow.Stop();
        _slideStarted = 0;
        // The previous photo's text must not be selectable on this one while it loads
        _ocr?.Cancel();
        Ocr = null;
        IsRecognizing = false;
        if (Current is not { } item) return;
        Loader.Pin(item.Path);
        var task = Loader.LoadAsync(item.Path, ScreenEdge);
        // Keep the previous image up for a decode that finishes within a frame or two; otherwise show loading
        if (!task.IsCompleted) IsLoading = true;
        var loaded = await task;
        if (version != _loadVersion) return; // the user has moved on
        StopAnimation();
        StopLive();
        _loaded = loaded;
        Image = loaded?.First;
        IsLoading = false;
        if (loaded is { IsAnimated: true }) StartAnimation();
        StartRecognition();
        RestartSlide();
        if (ShowInfo) _ = LoadDetailsAsync();
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

    /// <summary>Stops the frame timers when the window closes.</summary>
    public void Close()
    {
        _ocr?.Cancel();
        _slideshow.Stop();
        StopAnimation();
        StopLive();
    }
}
