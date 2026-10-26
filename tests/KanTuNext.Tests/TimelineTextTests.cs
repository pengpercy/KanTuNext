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

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("kantunext-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void RoundTripsSizeAndThemeAsNames()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new KanTuNext.Core.Settings.AppSettings { ThumbnailSize = ThumbnailSize.Large, Theme = KanTuNext.Core.Settings.AppTheme.Dark },
            KanTuNext.Core.Settings.SettingsJson.Default.AppSettings);
        Assert.Contains("\"Large\"", json);
        Assert.Contains("\"Dark\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, KanTuNext.Core.Settings.SettingsJson.Default.AppSettings)!;
        Assert.Equal(ThumbnailSize.Large, back.ThumbnailSize);
        Assert.Equal(KanTuNext.Core.Settings.AppTheme.Dark, back.Theme);
    }

    [Fact]
    public void RoundTripsWindowPlacement()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new KanTuNext.Core.Settings.AppSettings
            {
                MainWindow = new() { Width = 1440, Height = 900, Maximized = true },
            },
            KanTuNext.Core.Settings.SettingsJson.Default.AppSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, KanTuNext.Core.Settings.SettingsJson.Default.AppSettings)!.MainWindow!;
        Assert.Equal((1440d, 900d, true), (back.Width, back.Height, back.Maximized));
        // Settings written before placement existed load without one
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize("{\"theme\":\"Dark\"}", KanTuNext.Core.Settings.SettingsJson.Default.AppSettings)!.MainWindow);
    }
}
