using KanTuNext.Core.Library;
using KanTuNext.Core.Timeline;

namespace KanTuNext.Tests;

public class SearchTests
{
    private static MediaFile F(string name, int y, int m) => new("/p/" + name, MediaKind.Image, 1, new DateTime(y, m, 10));

    [Theory]
    [InlineData("img", true)]
    [InlineData("IMG_0001", true)]
    [InlineData("2016", true)]
    [InlineData("2016年", true)]
    [InlineData("2016年6月", true)]
    [InlineData("6月", true)]
    [InlineData("2016-06", true)]
    [InlineData("2016/6", true)]
    [InlineData("2017", false)]
    [InlineData("7月", false)]
    [InlineData("img 2016年6月", true)]
    [InlineData("img 2015", false)]
    [InlineData("foo", false)]
    public void MatchesNamesAndDates(string query, bool expected) =>
        Assert.Equal(expected, Search.Matches(F("IMG_0001.JPG", 2016, 6), query));

    [Fact]
    public void FilterKeepsMonthsAndDropsEmptyOnes()
    {
        var groups = TimelineBuilder.ByMonth([F("a.jpg", 2016, 6), F("b.png", 2016, 6), F("c.jpg", 2017, 1)]);
        var filtered = Search.Filter(groups, "jpg");
        Assert.Equal(["2017年1月", "2016年6月"], filtered.Select(g => g.Title));
        Assert.Equal("1 项", filtered[1].CountText);
        Assert.Same(groups, Search.Filter(groups, "  "));
    }
}
