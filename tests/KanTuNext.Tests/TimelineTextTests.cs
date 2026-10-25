using KanTuNext.Core.Library;
using KanTuNext.Core.Timeline;

namespace KanTuNext.Tests;

public class TimelineTextTests
{
    [Fact]
    public void MonthTitle_UsesChineseYearMonth() => Assert.Equal("2026年10月", TimelineText.MonthTitle(2026, 10));

    [Fact]
    public void ItemCount_HasSpaceBeforeUnit() => Assert.Equal("2 项", TimelineText.ItemCount(2));

    [Theory]
    [InlineData(55_364_812L, "52.8 MB")]
    [InlineData(65_536L, "64 KB")]
    [InlineData(2_050_846_965L, "1.91 GB")]
    [InlineData(512L, "512 B")]
    public void FileSize_Formats(long bytes, string expected) => Assert.Equal(expected, TimelineText.FileSize(bytes));

    [Fact]
    public void SmallThumbnail_MatchesOriginal()
    {
        Assert.Equal(70, ThumbnailSizes.Edge(ThumbnailSize.Small));
        Assert.Equal(20, ThumbnailSizes.Gap(ThumbnailSize.Small));
        Assert.Equal("最大", ThumbnailSizes.Label(ThumbnailSize.Largest));
    }
}
