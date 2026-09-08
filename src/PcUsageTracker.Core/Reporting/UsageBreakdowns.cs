namespace PcUsageTracker.Core.Reporting;

/// <summary>
/// One foreground-session slice clipped to a selected local day. StartAt and EndAt are
/// absolute UTC instants; callers can project them into the requested time zone for display.
/// </summary>
public readonly record struct TimelineSegment(
    string ProcessName,
    string DisplayName,
    string? CategoryName,
    int ColorRgb,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string? ExePath = null)
{
    public bool IsIdle => string.Equals(
        ProcessName,
        Sampling.IdleSentinel.Name,
        StringComparison.OrdinalIgnoreCase);

    public int TotalSeconds => checked((int)(EndAt - StartAt).TotalSeconds);
    public TimeSpan Total => EndAt - StartAt;
}

/// <summary>Normalized UTC coordinates for drawing a segment within one local calendar day.</summary>
public readonly record struct TimelineCoordinates(double StartFraction, double EndFraction)
{
    public static TimelineCoordinates Project(
        TimelineSegment segment,
        DateOnly day,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var (dayStart, dayEnd) = Aggregator.DayRange(day, zone);
        return Project(segment, dayStart, dayEnd);
    }

    public static TimelineCoordinates Project(
        TimelineSegment segment,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd)
    {
        var durationTicks = (dayEnd - dayStart).Ticks;
        if (durationTicks <= 0) return default;

        var start = segment.StartAt < dayStart ? dayStart : segment.StartAt;
        var end = segment.EndAt > dayEnd ? dayEnd : segment.EndAt;
        var startFraction = Math.Clamp((start - dayStart).Ticks / (double)durationTicks, 0d, 1d);
        var endFraction = Math.Clamp((end - dayStart).Ticks / (double)durationTicks, 0d, 1d);
        return new TimelineCoordinates(startFraction, Math.Max(startFraction, endFraction));
    }

    public static IReadOnlyList<TimelineHourTick> CreateHourTicks(DateOnly day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var (dayStart, dayEnd) = Aggregator.DayRange(day, zone);
        return CreateHourTicks(day, zone, dayStart, dayEnd);
    }

    public static IReadOnlyList<TimelineHourTick> CreateHourTicks(
        DateOnly day,
        TimeZoneInfo zone,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (dayEnd <= dayStart) throw new ArgumentOutOfRangeException(nameof(dayEnd));
        var candidates = new List<(DateTimeOffset Instant, int Hour, TimeSpan Offset, bool Repeated)>();

        for (var hour = 0; hour <= 24; hour++)
        {
            var localDay = hour == 24 ? day.AddDays(1) : day;
            var localHour = hour == 24 ? 0 : hour;
            var local = DateTime.SpecifyKind(
                localDay.ToDateTime(new TimeOnly(localHour, 0)),
                DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local)) continue;

            if (zone.IsAmbiguousTime(local))
            {
                foreach (var offset in zone.GetAmbiguousTimeOffsets(local))
                {
                    var instant = new DateTimeOffset(local, offset).ToUniversalTime();
                    if (instant >= dayStart && instant <= dayEnd)
                        candidates.Add((instant, localHour, offset, Repeated: true));
                }
            }
            else
            {
                var offset = zone.GetUtcOffset(local);
                var instant = new DateTimeOffset(local, offset).ToUniversalTime();
                if (instant >= dayStart && instant <= dayEnd)
                    candidates.Add((instant, hour, offset, Repeated: false));
            }
        }

        candidates.Sort((left, right) => left.Instant.CompareTo(right.Instant));
        var result = new TimelineHourTick[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var offsetChanged = index > 0 && candidates[index - 1].Offset != candidate.Offset;
            var followsSkippedHour = index > 0 && !candidate.Repeated &&
                                     candidate.Hour > candidates[index - 1].Hour + 1;
            var transition = candidate.Repeated || offsetChanged || followsSkippedHour;
            var label = transition
                ? $"{candidate.Hour:00} {FormatOffset(candidate.Offset)}" +
                  (followsSkippedHour ? " skip" : string.Empty)
                : candidate.Hour == 24 ? "24" : $"{candidate.Hour:00}";
            var fraction = (candidate.Instant - dayStart).Ticks / (double)(dayEnd - dayStart).Ticks;
            result[index] = new TimelineHourTick(
                candidate.Instant,
                Math.Clamp(fraction, 0d, 1d),
                label,
                candidate.Hour,
                transition,
                followsSkippedHour);
        }
        return result;
    }

    static string FormatOffset(TimeSpan offset) =>
        $"{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
}

public readonly record struct TimelineHourTick(
    DateTimeOffset Instant,
    double Fraction,
    string Label,
    int LocalHour,
    bool IsTransition,
    bool FollowsSkippedHour);

/// <summary>Usage totals for one local calendar day. Empty days are represented by zeroes.</summary>
public readonly record struct DailyUsagePoint(
    DateOnly Date,
    int ActiveSeconds,
    int IdleSeconds)
{
    public int TotalSeconds => checked(ActiveSeconds + IdleSeconds);
    public TimeSpan Active => TimeSpan.FromSeconds(ActiveSeconds);
    public TimeSpan Idle => TimeSpan.FromSeconds(IdleSeconds);
    public TimeSpan Total => TimeSpan.FromSeconds(TotalSeconds);
}

/// <summary>
/// Usage for one current category. Idle is represented as its own fixed entry rather than
/// being folded into a user-editable category.
/// </summary>
public readonly record struct CategoryUsage(
    string? CategoryName,
    int TotalSeconds,
    int ColorRgb,
    bool IsIdle = false)
{
    public string DisplayName => IsIdle ? "(Idle)" : CategoryName ?? "Other";
    public TimeSpan Total => TimeSpan.FromSeconds(TotalSeconds);
}

/// <summary>Presentation-ready totals for the dashboard's selected local day.</summary>
public readonly record struct DashboardSummary(
    long TotalSeconds,
    long ActiveSeconds,
    long IdleSeconds,
    string? TopApplicationName,
    int TopApplicationSeconds)
{
    public static DashboardSummary FromApplications(IReadOnlyList<ReportEntry> applications)
    {
        ArgumentNullException.ThrowIfNull(applications);

        long total = 0;
        long idle = 0;
        ReportEntry? top = null;

        foreach (var application in applications)
        {
            total += application.TotalSeconds;
            if (string.Equals(
                    application.ProcessName,
                    Sampling.IdleSentinel.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                idle += application.TotalSeconds;
                continue;
            }

            if (top is null || application.TotalSeconds > top.Value.TotalSeconds)
                top = application;
        }

        return new DashboardSummary(
            TotalSeconds: total,
            ActiveSeconds: total - idle,
            IdleSeconds: idle,
            TopApplicationName: top?.DisplayName,
            TopApplicationSeconds: top?.TotalSeconds ?? 0);
    }
}

/// <summary>Pure date rollover policy shared by the Dashboard UI and its tests.</summary>
public static class DashboardDateNavigation
{
    public static DateOnly SelectionAfterTodayChanged(
        DateOnly previousToday,
        DateOnly selectedDay,
        DateOnly newToday)
    {
        if (selectedDay == previousToday) return newToday;
        return selectedDay > newToday ? newToday : selectedDay;
    }
}

public enum RefreshRequestDisposition
{
    Start,
    Coalesce,
    Supersede,
}

/// <summary>Policy for preventing periodic report refreshes from starving an in-flight query.</summary>
public static class ReportRefreshPolicy
{
    public static RefreshRequestDisposition Decide(
        bool refreshInFlight,
        bool sameView,
        bool manualInvalidation)
    {
        if (!refreshInFlight) return RefreshRequestDisposition.Start;
        return sameView && !manualInvalidation
            ? RefreshRequestDisposition.Coalesce
            : RefreshRequestDisposition.Supersede;
    }
}
