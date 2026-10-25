using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using KanTuNext.Core.Library;
using KanTuNext.Core.Metadata;
using KanTuNext.Core.Settings;
using KanTuNext.Core.Timeline;
using KanTuNext.Desktop.Services;

namespace KanTuNext.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        SidebarItem Item(LibraryCategory c, string title, string icon, bool expandable = false, string? path = null) =>
            new(c, title, icon, OnSelected, expandable, path);

        // Sections follow the original 2.5.2 sidebar, minus what depended on online services
        // (人物, 地点, 精选集) and device import (later).
        Sections =
        [
            new SidebarSection("常用文件夹",
            [
                Item(LibraryCategory.Folder, "图片", "folder-outline", true,
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                Item(LibraryCategory.Folder, "下载", "folder-outline", true, Path.Combine(home, "Downloads")),
                .. _settings.Folders.Select(f => Item(LibraryCategory.Folder, FolderTitle(f), "folder-outline", true, f)),
            ], canAdd: true),
            new SidebarSection("筛选",
            [
                Item(LibraryCategory.LargeImages, "大图片", "image-size-select-large"),
                Item(LibraryCategory.Screenshots, "屏幕快照", "monitor-screenshot"),
                Item(LibraryCategory.Videos, "视频", "play-circle-outline", true),
                Item(LibraryCategory.Duplicates, "重复照片", "content-duplicate"),
                Item(LibraryCategory.RecentlyDeleted, "最近删除", "trash-can-outline"),
            ]),
            new SidebarSection("标签",
            [
                Item(LibraryCategory.TagGood, "好", "thumb-up"),
                Item(LibraryCategory.TagNeutral, "中", "emoticon-neutral"),
                Item(LibraryCategory.TagBad, "差", "thumb-down"),
            ]),
        ];
        var folders = Sections[0].Items;
        (folders.FirstOrDefault(i => i.Path == _settings.LastFolder) ?? folders[0]).IsSelected = true;
        _ = CountFoldersAsync();
        UpdateTagCounts();
        AppServices.Tags.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            UpdateTagCounts();
            // The open tag list itself changes when a photo is marked or unmarked
            if (Selected is { } item && TagOf(item.Category) is not PhotoTag.None) _ = LoadAsync(item, refresh: true);
        });
    }

    private static PhotoTag TagOf(LibraryCategory category) => category switch
    {
        LibraryCategory.TagGood => PhotoTag.Good,
        LibraryCategory.TagNeutral => PhotoTag.Neutral,
        LibraryCategory.TagBad => PhotoTag.Bad,
        _ => PhotoTag.None,
    };

    private void UpdateTagCounts()
    {
        foreach (var item in Sections.SelectMany(s => s.Items))
            if (TagOf(item.Category) is var tag and not PhotoTag.None)
                item.Count = AppServices.Tags.Count(tag) is var n and > 0 ? n.ToString() : "0";
    }

    /// <summary>The marked photos that still exist, with their EXIF dates (from the cache when possible).</summary>
    private IReadOnlyList<MediaFile> TaggedFiles(PhotoTag tag)
    {
        var result = new List<MediaFile>();
        foreach (var path in AppServices.Tags.PathsWith(tag))
        {
            var info = new FileInfo(path);
            if (!info.Exists || MediaTypes.KindOf(path) is not { } kind) continue;
            var file = new MediaFile(path, kind, info.Length, info.LastWriteTime);
            if (kind == MediaKind.Image)
            {
                if (!_metadata.Value.TryGet(path, file.Modified, file.Length, out var exif))
                {
                    exif = ExifReader.Read(path);
                    _metadata.Value.Set(path, file.Modified, file.Length, exif);
                }
                file = file with { Taken = exif.Taken, Orientation = exif.Orientation };
            }
            result.Add(file);
        }
        return result;
    }

    private readonly AppSettings _settings = AppSettings.Load();
    // EXIF results survive restarts: a rescan of unchanged files reads no file contents
    private readonly Lazy<MetadataCache> _metadata = new(() => MetadataCache.Load(MetadataCache.DefaultPath));

    public IReadOnlyList<SidebarSection> Sections { get; }

    private static string FolderTitle(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;

    /// <summary>Adds a folder under 常用文件夹 (from the ＋ button), remembers it and opens it.</summary>
    public void AddFolder(string path)
    {
        var folders = Sections[0].Items;
        var existing = folders.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.Ordinal));
        if (existing is null)
        {
            existing = new SidebarItem(LibraryCategory.Folder, FolderTitle(path), "folder-outline", OnSelected, true, path);
            folders.Add(existing);
            _settings.Folders.Add(path);
            SaveSettings();
        }
        existing.IsSelected = true;
    }

    private void SaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    public partial SidebarItem? Selected { get; private set; }

    /// <summary>Empty-state wording, as in the original.</summary>
    public string EmptyText => Selected?.Category switch
    {
        null => "",
        LibraryCategory.Folder => $"{Selected.Title} 里没有图片",
        LibraryCategory.RecentlyDeleted => "没有已删除的图片或视频",
        LibraryCategory.TagGood or LibraryCategory.TagNeutral or LibraryCategory.TagBad => "没有标记过图片",
        _ => $"没有{Selected.Title}",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems), nameof(ShowEmpty))]
    public partial IReadOnlyList<MonthGroup>? Groups { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoading { get; private set; }

    public bool HasItems => Groups is { Count: > 0 };
    public bool ShowEmpty => !IsLoading && !HasItems;

    public double ItemEdge => ThumbnailSizes.Edge(Size);
    public double ItemGap => ThumbnailSizes.Gap(Size);

    private CancellationTokenSource? _load;

    partial void OnSelectedChanged(SidebarItem? value)
    {
        if (value is { Category: LibraryCategory.Folder, Path: { } path } && _settings.LastFolder != path)
        {
            _settings.LastFolder = path;
            SaveSettings();
        }
        _ = LoadAsync(value);
    }

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _refreshTimer;

    /// <param name="refresh">Rescan after a file change: keep the current grid (and its scroll position) until
    /// the new result is ready instead of clearing it.</param>
    private async Task LoadAsync(SidebarItem? item, bool refresh = false)
    {
        _load?.Cancel();
        var cts = _load = new CancellationTokenSource();
        if (!refresh)
        {
            Groups = null;
            Watch(item?.Category == LibraryCategory.Folder ? item.Path : null);
        }
        if (item is not null && TagOf(item.Category) is var tag and not PhotoTag.None)
        {
            var tagged = await Task.Run(() => TimelineBuilder.ByMonth(TaggedFiles(tag)), cts.Token);
            if (!cts.IsCancellationRequested) Groups = tagged;
            return;
        }
        if (item is not { Category: LibraryCategory.Folder, Path: { } folder }) return;
        IsLoading = !refresh;
        try
        {
            var files = await Task.Run(() =>
            {
                var result = FolderScanner.Scan(folder, cts.Token, _metadata.Value);
                SaveMetadata();
                return result;
            }, cts.Token);
            var groups = await Task.Run(() => TimelineBuilder.ByMonth(files), cts.Token);
            if (cts.IsCancellationRequested) return;
            Groups = groups;
            item.Count = files.Count > 0 ? files.Count.ToString() : "";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_load == cts) IsLoading = false;
        }
    }

    /// <summary>Rescans the open folder (debounced) when files in it are added, removed, renamed or rewritten.</summary>
    private void Watch(string? folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (folder is null || !Directory.Exists(folder)) return;
        _refreshTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) =>
        {
            _refreshTimer!.Stop();
            _ = LoadAsync(Selected, refresh: true);
        });
        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, e) =>
            {
                if (e.ChangeType != WatcherChangeTypes.Changed || MediaTypes.KindOf(e.FullPath) is not null) Poke();
            };
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Changed += changed;
            _watcher.Renamed += (_, _) => Poke();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _watcher = null; // too many watchers or an unwatchable volume: the folder still works, just without live updates
        }

        void Poke() => Dispatcher.UIThread.Post(() => { _refreshTimer!.Stop(); _refreshTimer.Start(); });
    }

    /// <summary>Fills the folder counts in the sidebar without waiting for the user to open each one.</summary>
    private async Task CountFoldersAsync()
    {
        foreach (var item in Sections.SelectMany(s => s.Items).Where(i => i is { Category: LibraryCategory.Folder, Path: not null }))
        {
            var count = await Task.Run(() => FolderScanner.Scan(item.Path!, cache: _metadata.Value).Count);
            Dispatcher.UIThread.Post(() => item.Count = count > 0 ? count.ToString() : "");
        }
        await Task.Run(SaveMetadata);
    }

    private void SaveMetadata()
    {
        try { _metadata.Value.Save(MetadataCache.DefaultPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSmall), nameof(IsMedium), nameof(IsLarge), nameof(IsLargest), nameof(ItemEdge), nameof(ItemGap))]
    public partial ThumbnailSize Size { get; set; } = ThumbnailSize.Small;

    public bool IsSmall { get => Size == ThumbnailSize.Small; set { if (value) Size = ThumbnailSize.Small; } }
    public bool IsMedium { get => Size == ThumbnailSize.Medium; set { if (value) Size = ThumbnailSize.Medium; } }
    public bool IsLarge { get => Size == ThumbnailSize.Large; set { if (value) Size = ThumbnailSize.Large; } }
    public bool IsLargest { get => Size == ThumbnailSize.Largest; set { if (value) Size = ThumbnailSize.Largest; } }

    private void OnSelected(SidebarItem item)
    {
        if (Selected is { } previous && previous != item) previous.IsSelected = false;
        Selected = item;
    }
}
