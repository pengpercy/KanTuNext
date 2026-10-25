using KanTuNext.Core.Library;

namespace KanTuNext.Tests;

public sealed class LibraryFiltersTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("kantunext-filters-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private MediaFile Write(string name, byte[] data, DateTime? modified = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return new MediaFile(path, MediaTypes.KindOf(name) ?? MediaKind.Image, data.Length, modified ?? DateTime.Now);
    }

    [Theory]
    [InlineData("屏幕快照 2019-01-01 上午10.00.00.png", true)]
    [InlineData("截屏2023-05-06 12.00.00.png", true)]
    [InlineData("Screen Shot 2020-01-01 at 10.00.00.png", true)]
    [InlineData("Screenshot_20240101-101010.png", true)]
    [InlineData("IMG_0001.JPG", false)]
    public void RecognizesScreenshotNames(string name, bool expected) =>
        Assert.Equal(expected, LibraryFilters.IsScreenshot(new MediaFile("/x/" + name, MediaKind.Image, 1, DateTime.Now)));

    [Fact]
    public void LargeImagesAreFiveMegabytesAndUp()
    {
        Assert.True(LibraryFilters.IsLargeImage(new MediaFile("/a.jpg", MediaKind.Image, LibraryFilters.LargeImageBytes, DateTime.Now)));
        Assert.False(LibraryFilters.IsLargeImage(new MediaFile("/a.jpg", MediaKind.Image, LibraryFilters.LargeImageBytes - 1, DateTime.Now)));
        Assert.False(LibraryFilters.IsLargeImage(new MediaFile("/a.mov", MediaKind.Video, long.MaxValue, DateTime.Now)));
    }

    [Fact]
    public void BySizeIsOneGroupBiggestFirstWithTotal()
    {
        var groups = LibraryFilters.BySize([
            new MediaFile("/s", MediaKind.Image, 1024, DateTime.Now), new MediaFile("/b", MediaKind.Image, 2048, DateTime.Now),
        ]);
        Assert.Equal("2 项", groups.Single().Title);
        Assert.Equal("3 KB", groups.Single().CountText);
        Assert.Equal(["/b", "/s"], groups.Single().Items.Select(i => i.Path));
    }

    [Fact]
    public void FindsByteIdenticalFilesOnly()
    {
        var a = Write("a.jpg", [1, 2, 3, 4], new DateTime(2020, 1, 1));
        var b = Write("b.jpg", [1, 2, 3, 4], new DateTime(2019, 1, 1));
        var c = Write("c.jpg", [1, 2, 3, 5]); // same size, different bytes
        var d = Write("d.jpg", [9]);
        var groups = LibraryFilters.Duplicates([a, b, c, d], cancel: TestContext.Current.CancellationToken);
        var set = Assert.Single(groups);
        Assert.Equal([b.Path, a.Path], set.Items.Select(i => i.Path)); // oldest first
        Assert.Equal("2 张相同", set.Title);
    }
}
