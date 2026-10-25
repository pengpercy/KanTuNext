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
                // On macOS SpecialFolder.MyPictures asks AppKit and ignores HOME, which would make 图片 and 下载
                // disagree about whose home it is; elsewhere it follows HOME (Linux: the localized XDG folder)
                Item(LibraryCategory.Folder, "图片", "folder-outline", true,
                    OperatingSystem.IsMacOS() ? Path.Combine(home, "Pictures") : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                Item(LibraryCategory.Folder, "下载", "folder-outline", true, Path.Combine(home, "Downloads")),
                .. _settings.Folders.Select(f => Item(LibraryCategory.Folder, FolderTitle(f), "folder-outline", true, f)),
            ], canAdd: true),
            new SidebarSection("筛选",
            [
                Item(LibraryCategory.LargeImages, "大图片", "image-size-select-large"),
                Item(LibraryCategory.Screenshots, "屏幕快照", "monitor-screenshot"),
                Item(LibraryCategory.Videos, "视频", "play-circle-outline"),
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
        Size = _settings.ThumbnailSize;
        var folders = Sections[0].Items;
        (folders.FirstOrDefault(i => i.Path == _settings.LastFolder) ?? folders[0]).IsSelected = true;
        _ = CountFoldersAsync();
        UpdateTagCounts();
        UpdateDeletedCount();
        AppServices.Deleted.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            UpdateDeletedCount();
            if (Selected?.Category == LibraryCategory.RecentlyDeleted) _ = LoadAsync(Selected, refresh: true);
        });
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

    private void UpdateDeletedCount()
    {
        foreach (var item in Sections.SelectMany(s => s.Items).Where(i => i.Category == LibraryCategory.RecentlyDeleted))
            item.Count = AppServices.Deleted.Present().Count.ToString();
    }

    /// <summary>What's in 最近删除: the trashed copies, newest deletion first.</summary>
    private static IReadOnlyList<MediaGroup> DeletedGroups()
    {
        var files = new List<MediaFile>();
        foreach (var item in AppServices.Deleted.Present())
        {
            var info = new FileInfo(item.TrashedPath);
            if (info.Exists && MediaTypes.KindOf(item.TrashedPath) is { } kind)
                files.Add(new MediaFile(item.TrashedPath, kind, info.Length, info.LastWriteTime));
        }
        return files.Count == 0 ? [] : [new MediaGroup(TimelineText.ItemCount(files.Count), TimelineText.FileSize(files.Sum(f => f.Length)), files)];
    }

    /// <summary>放回原处 for an item listed in 最近删除.</summary>
    public bool Restore(MediaFile file) => AppServices.Deleted.Restore(file.Path) is not null;

    /// <summary>移到废纸篓 from the grid's context menu.</summary>
    public bool Delete(MediaFile file) => AppServices.Delete(file.Path);

    public void ToggleTag(MediaFile file, PhotoTag tag) => AppServices.Tags.Toggle(file.Path, tag);

    public bool IsRecentlyDeleted => Selected?.Category == LibraryCategory.RecentlyDeleted;

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

    /// <summary>
    /// Shows or hides a folder's subfolders that contain photos, right under it, with their counts taken from
    /// the folder's last scan (no extra disk reads).
    /// </summary>
    public void ToggleExpanded(SidebarItem folder)
    {
        if (folder is not { Category: LibraryCategory.Folder, Path: { } path }) return;
        var list = Sections[0].Items;
        if (folder.IsExpanded)
        {
            Collapse(folder);
            return;
        }
        folder.IsExpanded = true;
        IReadOnlyList<MediaFile>? files;
        lock (_folderFiles) files = _folderFiles.FirstOrDefault(kv => path.StartsWith(kv.Key, StringComparison.Ordinal)).Value;
        var prefix = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        var counts = (files ?? [])
            .Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal) && f.Path.IndexOf(Path.DirectorySeparatorChar, prefix.Length) > 0)
            .GroupBy(f => f.Path[..f.Path.IndexOf(Path.DirectorySeparatorChar, prefix.Length)], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
        var at = list.IndexOf(folder) + 1;
        foreach (var group in counts)
        {
            var hasDeeper = group.Any(f => f.Path.IndexOf(Path.DirectorySeparatorChar, group.Key.Length + 1) > 0);
            var child = new SidebarItem(LibraryCategory.Folder, Path.GetFileName(group.Key), "folder-outline", OnSelected,
                hasDeeper, group.Key, folder.Depth + 1) { Count = group.Count().ToString() };
            folder.Children.Add(child);
            list.Insert(at++, child);
        }
    }

    private void Collapse(SidebarItem folder)
    {
        foreach (var child in folder.Children)
        {
            Collapse(child);
            Sections[0].Items.Remove(child);
        }
        folder.Children.Clear();
        folder.IsExpanded = false;
    }

    /// <summary>The settings window's model, sharing this window's settings object.</summary>
    public SettingsViewModel CreateSettings() => new(_settings, SaveSettings);

    public KanTuNext.Core.Settings.AppTheme Theme => _settings.Theme;

    private void SaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    public partial SidebarItem? Selected { get; private set; }

    /// <summary>Empty-state wording, as in the original.</summary>
    public string EmptyText => SearchText.Trim().Length > 0 && _unfiltered is { Count: > 0 } ? $"没有找到「{SearchText.Trim()}」" : Selected?.Category switch
    {
        null => "",
        LibraryCategory.Folder => $"{Selected.Title} 里没有图片",
        LibraryCategory.RecentlyDeleted => "没有已删除的图片或视频",
        LibraryCategory.TagGood or LibraryCategory.TagNeutral or LibraryCategory.TagBad => "没有标记过图片",
        _ => $"没有{Selected.Title}",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems), nameof(ShowEmpty), nameof(EmptyText))]
    public partial IReadOnlyList<MediaGroup>? Groups { get; private set; }

    // ---- 找图
    private IReadOnlyList<MediaGroup>? _unfiltered;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    public partial string SearchText { get; set; } = "";

    partial void OnSearchTextChanged(string value) => Groups = _unfiltered is null ? null : Search.Filter(_unfiltered, value);

    /// <summary>What the current view shows before 找图 filters it.</summary>
    private void SetGroups(IReadOnlyList<MediaGroup>? groups)
    {
        _unfiltered = groups;
        Groups = groups is null ? null : Search.Filter(groups, SearchText);
    }

    /// <summary>Sizes under thumbnails in 大图片 and 屏幕快照.</summary>
    [ObservableProperty]
    public partial bool ShowSizes { get; private set; }

    // ---- 重复照片: an explicit scan, as in the original
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool ShowDuplicateIntro { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial string DuplicateProgress { get; private set; } = "";

    /// <summary>Files of every common folder, by folder, from the latest scan of each (feeds the 筛选 views).</summary>
    private readonly Dictionary<string, IReadOnlyList<MediaFile>> _folderFiles = new(StringComparer.Ordinal);
    private Task? _allScanned;

    private IReadOnlyList<MediaFile> AllFiles
    {
        get
        {
            lock (_folderFiles)
                return _folderFiles.Values.SelectMany(f => f).DistinctBy(f => f.Path).ToArray();
        }
    }

    private void Remember(string folder, IReadOnlyList<MediaFile> files)
    {
        lock (_folderFiles) _folderFiles[folder] = files;
    }

    private void UpdateFilterCounts()
    {
        var all = AllFiles;
        foreach (var item in Sections.SelectMany(s => s.Items))
        {
            var count = item.Category switch
            {
                LibraryCategory.LargeImages => all.Count(LibraryFilters.IsLargeImage),
                LibraryCategory.Screenshots => all.Count(LibraryFilters.IsScreenshot),
                LibraryCategory.Videos => all.Count(f => f.Kind == MediaKind.Video),
                _ => -1,
            };
            if (count >= 0) item.Count = count.ToString();
        }
    }

    /// <summary>The filter views over all common folders.</summary>
    private IReadOnlyList<MediaGroup>? FilterGroups(LibraryCategory category) => category switch
    {
        LibraryCategory.LargeImages => LibraryFilters.BySize(AllFiles.Where(LibraryFilters.IsLargeImage)),
        LibraryCategory.Screenshots => LibraryFilters.BySize(AllFiles.Where(LibraryFilters.IsScreenshot)),
        LibraryCategory.Videos => TimelineBuilder.ByMonth(AllFiles.Where(f => f.Kind == MediaKind.Video)),
        _ => null,
    };

    private CancellationTokenSource? _duplicates;

    public async Task ScanDuplicatesAsync()
    {
        _duplicates?.Cancel();
        var cts = _duplicates = new CancellationTokenSource();
        ShowDuplicateIntro = false;
        DuplicateProgress = "正在扫描…";
        try
        {
            if (_allScanned is { } scanning) await scanning;
            var progress = new Progress<double>(p => DuplicateProgress = $"正在扫描… {p:P0}");
            var groups = await Task.Run(() => LibraryFilters.Duplicates(AllFiles.Where(f => f.Kind == MediaKind.Image), progress, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || Selected?.Category != LibraryCategory.Duplicates) return;
            SetGroups(groups);
            Sections.SelectMany(s => s.Items).First(i => i.Category == LibraryCategory.Duplicates).Count =
                groups.Sum(g => g.Items.Count - 1).ToString();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_duplicates == cts) DuplicateProgress = "";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoading { get; private set; }

    public bool HasItems => Groups is { Count: > 0 };
    public bool ShowEmpty => !IsLoading && !HasItems && !ShowDuplicateIntro && DuplicateProgress.Length == 0;

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
            SetGroups(null);
            Watch(item?.Category == LibraryCategory.Folder ? item.Path : null);
            _duplicates?.Cancel();
            ShowSizes = item?.Category is LibraryCategory.LargeImages or LibraryCategory.Screenshots or LibraryCategory.RecentlyDeleted;
            ShowDuplicateIntro = item?.Category == LibraryCategory.Duplicates;
            DuplicateProgress = "";
        }
        if (item is { Category: LibraryCategory.LargeImages or LibraryCategory.Screenshots or LibraryCategory.Videos })
        {
            IsLoading = !refresh;
            try
            {
                if (_allScanned is { } scanning) await scanning;
                var filtered = await Task.Run(() => FilterGroups(item.Category), cts.Token);
                if (!cts.IsCancellationRequested) SetGroups(filtered);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (_load == cts) IsLoading = false;
            }
            return;
        }
        if (item?.Category == LibraryCategory.RecentlyDeleted)
        {
            var deleted = await Task.Run(DeletedGroups, cts.Token);
            if (!cts.IsCancellationRequested) SetGroups(deleted);
            return;
        }
        if (item is not null && TagOf(item.Category) is var tag and not PhotoTag.None)
        {
            var tagged = await Task.Run(() => TimelineBuilder.ByMonth(TaggedFiles(tag)), cts.Token);
            if (!cts.IsCancellationRequested) SetGroups(tagged);
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
            Remember(folder, files);
            UpdateFilterCounts();
            if (cts.IsCancellationRequested) return;
            SetGroups(groups);
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
    private Task CountFoldersAsync() => _allScanned = CountFoldersCoreAsync();

    private async Task CountFoldersCoreAsync()
    {
        foreach (var item in Sections.SelectMany(s => s.Items).Where(i => i is { Category: LibraryCategory.Folder, Path: not null }).ToArray())
        {
            var files = await Task.Run(() => FolderScanner.Scan(item.Path!, cache: _metadata.Value));
            Remember(item.Path!, files);
            item.Count = files.Count > 0 ? files.Count.ToString() : "";
        }
        UpdateFilterCounts();
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

    partial void OnSizeChanged(ThumbnailSize value)
    {
        if (_settings.ThumbnailSize == value) return;
        _settings.ThumbnailSize = value;
        SaveSettings();
    }

    // ---- 简介: the selected photo's details
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(Info))]
    public partial MediaFile? SelectedFile { get; set; }

    public bool HasSelection => SelectedFile is not null;

    public IReadOnlyList<InfoRow> Info => SelectedFile is { } f ? PhotoInfo.Rows(f) : [];

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
