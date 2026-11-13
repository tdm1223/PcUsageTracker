namespace PcUsageTracker.Core.Reporting;

public enum ReportListSort { DurationDescending, DurationAscending, NameAscending, NameDescending, Category }

public static class ReportListOptions
{
    public static IReadOnlyList<ReportEntry> Apply(IReadOnlyList<ReportEntry> entries, string? search, ReportListSort sort)
    {
        var query = entries.AsEnumerable();
        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
            query = query.Where(entry => entry.DisplayName.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                entry.ProcessName.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                (entry.CategoryName?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false));
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        return (sort switch
        {
            ReportListSort.DurationAscending => query.OrderBy(entry => entry.TotalSeconds).ThenBy(entry => entry.DisplayName, comparer),
            ReportListSort.NameAscending => query.OrderBy(entry => entry.DisplayName, comparer).ThenBy(entry => entry.ProcessName, comparer),
            ReportListSort.NameDescending => query.OrderByDescending(entry => entry.DisplayName, comparer).ThenBy(entry => entry.ProcessName, comparer),
            ReportListSort.Category => query.OrderBy(entry => entry.CategoryName ?? string.Empty, comparer).ThenBy(entry => entry.DisplayName, comparer),
            _ => query.OrderByDescending(entry => entry.TotalSeconds).ThenBy(entry => entry.ProcessName, comparer),
        }).ToArray();
    }
}
