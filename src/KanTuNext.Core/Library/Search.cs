using System.Text.RegularExpressions;
using KanTuNext.Core.Timeline;

namespace KanTuNext.Core.Library;

/// <summary>找图: matches file names, and dates written the way the timeline shows them.</summary>
public static partial class Search
{
    /// <summary>
    /// Every whitespace-separated term must match: a date term ("2016", "2016年6月", "6月", "2016-06") against the
    /// capture date, anything else as a case-insensitive part of the file name.
    /// </summary>
    public static bool Matches(MediaFile file, string query)
    {
        foreach (var term in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (DateTerm(term) is var (year, month))
            {
                var date = file.SortDate;
                if ((year is { } y && date.Year != y) || (month is { } m && date.Month != m)) return false;
            }
            else if (!file.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>Groups with only the matching items; empty groups dropped. Group titles keep their meaning.</summary>
    public static IReadOnlyList<MediaGroup> Filter(IReadOnlyList<MediaGroup> groups, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return groups;
        var result = new List<MediaGroup>();
        foreach (var group in groups)
        {
            var items = group.Items.Where(f => Matches(f, query)).ToArray();
            if (items.Length == 0) continue;
            result.Add(group is MonthGroup month
                ? new MonthGroup(month.Year, month.Month, items)
                : group with { CountText = TimelineText.ItemCount(items.Length), Items = items });
        }
        return result;
    }

    private static (int? Year, int? Month)? DateTerm(string term)
    {
        var m = DatePattern().Match(term);
        if (!m.Success) return null;
        int? year = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value) : null;
        int? month = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value) : m.Groups["m2"].Success ? int.Parse(m.Groups["m2"].Value) : null;
        if (month is < 1 or > 12) return null;
        return (year, month);
    }

    // 2016 · 2016年 · 2016年6月 · 6月 · 2016-06 · 2016/6 · 2016.06
    [GeneratedRegex(@"^(?:(?<y>(?:19|20)\d{2})(?:年|[-/.](?<m2>\d{1,2}))?(?:(?<m>\d{1,2})月)?|(?<m>\d{1,2})月)$")]
    private static partial Regex DatePattern();
}
