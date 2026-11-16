namespace PcUsageTracker.Core.Reporting;

public readonly record struct TimelineRangeRow(string ProcessName, string DisplayName, string? CategoryName, long TotalSeconds, bool IsIdle);

public static class TimelineRangeSummary
{
    public static IReadOnlyList<TimelineRangeRow> Create(IReadOnlyList<TimelineSegment> segments, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (to <= from) throw new ArgumentOutOfRangeException(nameof(to));
        var totals = new Dictionary<string, (TimelineSegment First, long Ticks)>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in segments)
        {
            var start = segment.StartAt > from ? segment.StartAt : from;
            var end = segment.EndAt < to ? segment.EndAt : to;
            if (end <= start) continue;
            if (totals.TryGetValue(segment.ProcessName, out var previous))
                totals[segment.ProcessName] = (previous.First, checked(previous.Ticks + (end - start).Ticks));
            else
                totals.Add(segment.ProcessName, (segment, (end - start).Ticks));
        }
        return totals.Values.Select(value => new TimelineRangeRow(value.First.ProcessName, value.First.DisplayName,
                value.First.CategoryName, value.Ticks / TimeSpan.TicksPerSecond, value.First.IsIdle))
            .Where(row => row.TotalSeconds > 0)
            .OrderByDescending(row => row.TotalSeconds).ThenBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
