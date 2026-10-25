using KanTuNext.Core.Library;

namespace KanTuNext.Core.Timeline;

/// <summary>One month in the timeline, newest items first.</summary>
public sealed record MonthGroup(int Year, int Month, IReadOnlyList<MediaFile> Items)
{
    public string Title => TimelineText.MonthTitle(Year, Month);
    public string CountText => TimelineText.ItemCount(Items.Count);
}

public static class TimelineBuilder
{
    /// <summary>Groups by the month of <see cref="MediaFile.SortDate"/>, newest month first.</summary>
    public static IReadOnlyList<MonthGroup> ByMonth(IEnumerable<MediaFile> files) =>
        files.OrderByDescending(f => f.SortDate)
            .GroupBy(f => (f.SortDate.Year, f.SortDate.Month))
            .Select(g => new MonthGroup(g.Key.Year, g.Key.Month, g.ToArray()))
            .ToArray();
}
