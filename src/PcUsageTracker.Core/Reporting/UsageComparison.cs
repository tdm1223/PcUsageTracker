using PcUsageTracker.Core.Sampling;

namespace PcUsageTracker.Core.Reporting;

public readonly record struct UsageComparisonRow(string Name, long CurrentSeconds, long PreviousSeconds)
{
    public long DifferenceSeconds => CurrentSeconds - PreviousSeconds;
}

public readonly record struct ComparisonRange(
    DateTimeOffset CurrentStart, DateTimeOffset CurrentEnd,
    DateTimeOffset PreviousStart, DateTimeOffset PreviousEnd);

public static class ComparisonPeriods
{
    public static (ComparisonRange Day, ComparisonRange Week) At(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var localDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var daysSinceMonday = ((int)localDay.DayOfWeek + 6) % 7;
        var monday = localDay.AddDays(-daysSinceMonday);
        var (todayStart, _) = Aggregator.DayRange(localDay, zone);
        var (yesterdayStart, yesterdayEnd) = Aggregator.DayRange(localDay.AddDays(-1), zone);
        var (weekStart, _) = Aggregator.DayRange(monday, zone);
        var (previousWeekStart, _) = Aggregator.DayRange(monday.AddDays(-7), zone);
        static DateTimeOffset End(DateTimeOffset start, TimeSpan elapsed, DateTimeOffset limit)
        {
            var candidate = start + elapsed;
            return candidate <= limit ? candidate : limit;
        }
        return (
            new ComparisonRange(todayStart, now, yesterdayStart, End(yesterdayStart, now - todayStart, yesterdayEnd)),
            new ComparisonRange(weekStart, now, previousWeekStart, End(previousWeekStart, now - weekStart, weekStart)));
    }
}

public static class UsageComparison
{
    public static IReadOnlyList<UsageComparisonRow> Create(
        IReadOnlyList<ReportEntry> current, IReadOnlyList<ReportEntry> previous)
    {
        static bool Idle(ReportEntry entry) => string.Equals(entry.ProcessName, IdleSentinel.Name, StringComparison.OrdinalIgnoreCase);
        static Dictionary<string, long> Categories(IReadOnlyList<ReportEntry> entries) => entries
            .Where(entry => !Idle(entry))
            .GroupBy(entry => entry.CategoryName ?? "Other", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(entry => (long)entry.TotalSeconds), StringComparer.OrdinalIgnoreCase);

        var currentCategories = Categories(current);
        var previousCategories = Categories(previous);
        var rows = new List<UsageComparisonRow>
        {
            new("Tracked", current.Sum(entry => (long)entry.TotalSeconds), previous.Sum(entry => (long)entry.TotalSeconds)),
            new("Active", current.Where(entry => !Idle(entry)).Sum(entry => (long)entry.TotalSeconds),
                previous.Where(entry => !Idle(entry)).Sum(entry => (long)entry.TotalSeconds)),
            new("Idle", current.Where(Idle).Sum(entry => (long)entry.TotalSeconds),
                previous.Where(Idle).Sum(entry => (long)entry.TotalSeconds)),
        };
        foreach (var name in currentCategories.Keys.Union(previousCategories.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
            rows.Add(new UsageComparisonRow($"Category: {name}", currentCategories.GetValueOrDefault(name), previousCategories.GetValueOrDefault(name)));
        return rows;
    }
}
