using KanTuNext.Core.Library;
using KanTuNext.Core.Timeline;

namespace KanTuNext.Tests;

public sealed class FolderScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kantunext-scan-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    private string Write(string relative, byte[] data)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void FindsMediaRecursivelyAndReadsExif()
    {
        Write("a.jpg", ExifReaderTests.JpegWithExif("2016:06:28 10:00:00", null, 1));
        Write("sub/b.JPG", ExifReaderTests.JpegWithExif("2016:06:01 10:00:00", null, 1));
        Write("sub/clip.mov", [0, 0, 0]);
        Write("notes.txt", [1]);
        Write("Lib.photoslibrary/originals/c.jpg", [0xFF, 0xD8]);

        var files = FolderScanner.Scan(_root, TestContext.Current.CancellationToken);

        Assert.Equal(["a.jpg", "b.JPG", "clip.mov"], files.Select(f => f.Name).Order());
        Assert.Equal(new DateTime(2016, 6, 28, 10, 0, 0), files.Single(f => f.Name == "a.jpg").Taken);
        Assert.Equal(MediaKind.Video, files.Single(f => f.Name == "clip.mov").Kind);
    }

    [Fact]
    public void MissingFolderIsEmpty() => Assert.Empty(FolderScanner.Scan(Path.Combine(_root, "nope"), TestContext.Current.CancellationToken));

    [Fact]
    public void TimelineGroupsByMonthNewestFirst()
    {
        MediaFile F(string name, DateTime date) => new(name, MediaKind.Image, 1, date);
        var groups = TimelineBuilder.ByMonth([
            F("a", new(2025, 7, 3)), F("b", new(2026, 10, 1)), F("c", new(2025, 7, 20)), F("d", new(2024, 12, 9)),
        ]);
        Assert.Equal(["2026年10月", "2025年7月", "2024年12月"], groups.Select(g => g.Title));
        Assert.Equal(["c", "a"], groups[1].Items.Select(i => i.Path));
        Assert.Equal("2 项", groups[1].CountText);
    }
}

public sealed class MetadataCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("kantunext-cache-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void RoundTripsAndInvalidatesOnChange()
    {
        var file = Path.Combine(_dir, "a.jpg");
        File.WriteAllBytes(file, ExifReaderTests.JpegWithExif("2016:06:28 10:00:00", null, 6));
        var cachePath = Path.Combine(_dir, "metadata.bin");

        var cache = new KanTuNext.Core.Metadata.MetadataCache();
        var first = KanTuNext.Core.Library.FolderScanner.Scan(_dir, TestContext.Current.CancellationToken, cache).Single();
        cache.Save(cachePath);

        var loaded = KanTuNext.Core.Metadata.MetadataCache.Load(cachePath);
        Assert.True(loaded.TryGet(file, first.Modified, first.Length, out var info));
        Assert.Equal(new DateTime(2016, 6, 28, 10, 0, 0), info.Taken);
        Assert.Equal(6, info.Orientation);
        Assert.False(loaded.TryGet(file, first.Modified.AddSeconds(1), first.Length, out _));
    }

    [Fact]
    public void DamagedFileStartsEmpty()
    {
        var cachePath = Path.Combine(_dir, "metadata.bin");
        File.WriteAllBytes(cachePath, [1, 2, 3]);
        Assert.False(KanTuNext.Core.Metadata.MetadataCache.Load(cachePath).TryGet("x", DateTime.Now, 1, out _));
    }
}

public sealed class LivePhotoTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kantunext-live-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void PairsStillAndClipWithTheSameName()
    {
        foreach (var name in new[] { "IMG_0001.HEIC", "IMG_0001.MOV", "IMG_0002.JPG", "clip.mov", "IMG_0003.heic" })
            File.WriteAllBytes(Path.Combine(_root, name), [0]);

        var files = KanTuNext.Core.Library.FolderScanner.Scan(_root, TestContext.Current.CancellationToken);

        // The paired .MOV disappears into its still; unpaired clips and stills stay as they are
        Assert.Equal(["IMG_0001.HEIC", "IMG_0002.JPG", "IMG_0003.heic", "clip.mov"], files.Select(f => f.Name).Order(StringComparer.Ordinal));
        var live = files.Single(f => f.Name == "IMG_0001.HEIC");
        Assert.True(live.IsLivePhoto);
        Assert.Equal(Path.Combine(_root, "IMG_0001.MOV"), live.LiveVideo);
        Assert.False(files.Single(f => f.Name == "IMG_0003.heic").IsLivePhoto);
    }
}
