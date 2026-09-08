using FluentAssertions;
using Microsoft.Data.Sqlite;
using PcUsageTracker.Core.Models;
using PcUsageTracker.Core.Reporting;
using PcUsageTracker.Core.Sampling;
using PcUsageTracker.Core.Storage;

namespace PcUsageTracker.Core.Tests;

public sealed class AdvancedReportingTests : IDisposable
{
    static readonly TimeZoneInfo Korea = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Korea", TimeSpan.FromHours(9), "Test Korea", "Test Korea");

    readonly string _tmp;
    readonly SqliteStore _store;
    readonly Aggregator _aggregator;

    public AdvancedReportingTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"pcut-advanced-report-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_tmp);
        _aggregator = new Aggregator(_store.Connection);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-shm", "-wal" })
            try { File.Delete(_tmp + suffix); } catch { }
    }

    static DateTimeOffset Utc(
        TimeZoneInfo zone,
        int year,
        int month,
        int day,
        int hour = 0,
        int minute = 0,
        int second = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }

    void Seed(string processName, DateTimeOffset start, DateTimeOffset end)
    {
        var id = _store.Open(processName, start);
        _store.Close(id, end);
    }

    [Fact]
    public void timeline_clips_crossing_sessions_to_selected_local_day()
    {
        Seed("before", Utc(Korea, 2026, 1, 1, 23, 50), Utc(Korea, 2026, 1, 2, 0, 10));
        Seed("after", Utc(Korea, 2026, 1, 2, 23, 50), Utc(Korea, 2026, 1, 3, 0, 10));

        var result = _aggregator.Timeline(
            new DateOnly(2026, 1, 2),
            Utc(Korea, 2026, 1, 3, 12),
            Korea);

        result.Should().HaveCount(2);
        result[0].ProcessName.Should().Be("before");
        result[0].StartAt.Should().Be(Utc(Korea, 2026, 1, 2));
        result[0].EndAt.Should().Be(Utc(Korea, 2026, 1, 2, 0, 10));
        result[0].TotalSeconds.Should().Be(600);
        result[1].ProcessName.Should().Be("after");
        result[1].StartAt.Should().Be(Utc(Korea, 2026, 1, 2, 23, 50));
        result[1].EndAt.Should().Be(Utc(Korea, 2026, 1, 3));
        result[1].TotalSeconds.Should().Be(600);
    }

    [Fact]
    public void open_session_is_clipped_to_now_in_timeline_and_daily_totals()
    {
        _store.Open("code", Utc(Korea, 2026, 2, 3, 10));
        var now = Utc(Korea, 2026, 2, 3, 10, 30);

        var timeline = _aggregator.Timeline(new DateOnly(2026, 2, 3), now, Korea);
        var daily = _aggregator.DailyTotals(new DateOnly(2026, 2, 3), 1, now, Korea);

        timeline.Should().ContainSingle();
        timeline[0].EndAt.Should().Be(now);
        timeline[0].TotalSeconds.Should().Be(1_800);
        daily.Should().ContainSingle();
        daily[0].ActiveSeconds.Should().Be(1_800);
        daily[0].IdleSeconds.Should().Be(0);
    }

    [Fact]
    public void daily_totals_split_at_local_midnight_and_keep_idle_separate()
    {
        Seed("code", Utc(Korea, 2026, 3, 1, 23, 50), Utc(Korea, 2026, 3, 2, 0, 10));
        Seed("__IDLE__", Utc(Korea, 2026, 3, 2, 0, 10), Utc(Korea, 2026, 3, 2, 0, 15));

        var result = _aggregator.DailyTotals(
            new DateOnly(2026, 3, 2),
            2,
            Utc(Korea, 2026, 3, 3),
            Korea);

        result.Select(x => x.Date).Should().ContainInOrder(
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 2));
        result[0].ActiveSeconds.Should().Be(600);
        result[0].IdleSeconds.Should().Be(0);
        result[1].ActiveSeconds.Should().Be(600);
        result[1].IdleSeconds.Should().Be(300);
        result[1].TotalSeconds.Should().Be(900);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(30)]
    public void daily_totals_return_requested_series_with_explicit_empty_days(int dayCount)
    {
        var through = new DateOnly(2026, 4, 30);
        Seed("code", Utc(Korea, 2026, 4, 30, 9), Utc(Korea, 2026, 4, 30, 9, 1));

        var result = _aggregator.DailyTotals(through, dayCount, Utc(Korea, 2026, 5, 1), Korea);

        result.Should().HaveCount(dayCount);
        result[0].Date.Should().Be(through.AddDays(1 - dayCount));
        result[^1].Date.Should().Be(through);
        result[^1].ActiveSeconds.Should().Be(60);
        result.Take(dayCount - 1).Should().OnlyContain(point => point.TotalSeconds == 0);
    }

    [Theory]
    [InlineData(2026, 3, 8, 23)]
    [InlineData(2026, 11, 1, 25)]
    public void day_boundaries_and_totals_honor_dst_day_length(int year, int month, int day, int expectedHours)
    {
        var eastern = FindEasternTimeZone();
        var date = new DateOnly(year, month, day);
        var (from, to) = Aggregator.DayRange(date, eastern);
        Seed("code", from, to);

        var totals = _aggregator.DailyTotals(date, 1, to.AddHours(1), eastern);

        (to - from).TotalHours.Should().Be(expectedHours);
        totals.Should().ContainSingle();
        totals[0].ActiveSeconds.Should().Be(expectedHours * 3_600);
    }

    [Fact]
    public void timeline_coordinates_use_exact_utc_day_length_and_keep_repeated_fall_hour_visible()
    {
        var eastern = FindEasternTimeZone();
        var day = new DateOnly(2026, 11, 1);
        var (dayStart, dayEnd) = Aggregator.DayRange(day, eastern);
        var firstRepeatedHour = new TimelineSegment(
            "first", "First", "Other", 0, dayStart.AddMinutes(75), dayStart.AddMinutes(105));
        var secondRepeatedHour = new TimelineSegment(
            "second", "Second", "Other", 0, dayStart.AddMinutes(135), dayStart.AddMinutes(165));

        var first = TimelineCoordinates.Project(firstRepeatedHour, day, eastern);
        var second = TimelineCoordinates.Project(secondRepeatedHour, day, eastern);
        var fullDay = TimelineCoordinates.Project(
            new TimelineSegment("day", "Day", "Other", 0, dayStart, dayEnd), day, eastern);

        (dayEnd - dayStart).TotalHours.Should().Be(25);
        first.EndFraction.Should().BeGreaterThan(first.StartFraction);
        second.EndFraction.Should().BeGreaterThan(second.StartFraction);
        second.StartFraction.Should().BeGreaterThan(first.EndFraction,
            "the two occurrences of the repeated local hour are distinct UTC intervals");
        fullDay.StartFraction.Should().Be(0);
        fullDay.EndFraction.Should().Be(1);
    }

    [Fact]
    public void timeline_hour_ticks_distinguish_repeated_hour_and_skip_missing_hour()
    {
        var eastern = FindEasternTimeZone();
        var fallTicks = TimelineCoordinates.CreateHourTicks(new DateOnly(2026, 11, 1), eastern);
        var repeated = fallTicks.Where(tick => tick.LocalHour == 1).ToArray();

        repeated.Should().HaveCount(2);
        repeated[0].Fraction.Should().BeLessThan(repeated[1].Fraction);
        repeated.Should().OnlyContain(tick => tick.IsTransition && tick.Label.Contains(':'));
        repeated.Select(tick => tick.Label).Should().OnlyHaveUniqueItems();

        var springTicks = TimelineCoordinates.CreateHourTicks(new DateOnly(2026, 3, 8), eastern);
        springTicks.Should().NotContain(tick => tick.LocalHour == 2);
        springTicks.Should().ContainSingle(tick =>
            tick.LocalHour == 3 && tick.IsTransition && tick.FollowsSkippedHour &&
            tick.Label.Contains("skip"));
    }

    [Fact]
    public void selected_day_totals_resolve_current_rules_for_historical_sessions()
    {
        var day = new DateOnly(2026, 5, 4);
        Seed("code", Utc(Korea, 2026, 5, 4, 9), Utc(Korea, 2026, 5, 4, 10));
        Seed("__Idle__", Utc(Korea, 2026, 5, 4, 10), Utc(Korea, 2026, 5, 4, 10, 20));
        var now = Utc(Korea, 2026, 5, 5);

        var before = _aggregator.CategoryTotals(day, now, Korea);
        before.Should().ContainSingle(x => x.CategoryName == "Other" && x.TotalSeconds == 3_600 && !x.IsIdle);
        before.Should().ContainSingle(x => x.IsIdle && x.TotalSeconds == 1_200);

        _store.UpsertApplicationRule(
            "CODE", "Visual Studio Code", DefaultApplicationCategories.CodingId, 0x123456, now);

        var applications = _aggregator.ApplicationTotals(day, now, Korea);
        applications.Should().HaveCount(2);
        var code = applications.Single(x => x.ProcessName == "code");
        code.DisplayName.Should().Be("Visual Studio Code");
        code.CategoryName.Should().Be("Coding");
        code.ColorRgb.Should().Be(0x123456);

        var coding = _aggregator.CategoryTotals(day, now, Korea);
        coding.Should().ContainSingle(x =>
            x.CategoryName == "Coding" &&
            x.ColorRgb == DefaultApplicationCategories.CodingColor &&
            x.TotalSeconds == 3_600 &&
            !x.IsIdle);
        coding.Should().NotContain(x => x.CategoryName == "Other");

        _store.UpsertApplicationRule("code", null, DefaultApplicationCategories.GameId, null, now.AddSeconds(1));
        var reclassified = _aggregator.CategoryTotals(day, now.AddSeconds(1), Korea);
        reclassified.Should().ContainSingle(x => x.CategoryName == "Game" && x.TotalSeconds == 3_600);
        reclassified.Should().NotContain(x => x.CategoryName == "Coding");
    }

    [Fact]
    public void timeline_canonicalizes_idle_case_and_uses_fixed_presentation()
    {
        Seed("__IDLE__", Utc(Korea, 2026, 6, 1, 8), Utc(Korea, 2026, 6, 1, 8, 5));

        var idle = _aggregator.Timeline(
            new DateOnly(2026, 6, 1), Utc(Korea, 2026, 6, 2), Korea).Single();

        idle.ProcessName.Should().Be(IdleSentinel.Name);
        idle.DisplayName.Should().Be("(Idle)");
        idle.CategoryName.Should().BeNull();
        idle.ColorRgb.Should().Be(DefaultApplicationCategories.IdleColor);
        idle.IsIdle.Should().BeTrue();
    }

    [Fact]
    public void timeline_chooses_one_deterministic_path_when_process_metadata_has_casing_duplicates()
    {
        Seed("Code", Utc(Korea, 2026, 6, 2, 8), Utc(Korea, 2026, 6, 2, 8, 5));
        _store.UpsertProcessPath("Code", @"C:\Old\Code.exe", Utc(Korea, 2026, 6, 2, 8));
        _store.UpsertProcessPath("code", @"D:\New\Code.exe", Utc(Korea, 2026, 6, 2, 9));

        var result = _aggregator.Timeline(
            new DateOnly(2026, 6, 2), Utc(Korea, 2026, 6, 3), Korea);

        result.Should().ContainSingle("metadata casing variants must not duplicate a timeline session");
        result[0].ExePath.Should().Be(@"D:\New\Code.exe");
    }

    [Fact]
    public void future_day_returns_empty_breakdowns_without_negative_durations()
    {
        var future = new DateOnly(2026, 8, 2);
        var now = Utc(Korea, 2026, 8, 1, 12);

        _aggregator.Timeline(future, now, Korea).Should().BeEmpty();
        _aggregator.ApplicationTotals(future, now, Korea).Should().BeEmpty();
        _aggregator.CategoryTotals(future, now, Korea).Should().BeEmpty();
        _aggregator.DailyTotals(future, 1, now, Korea).Single().TotalSeconds.Should().Be(0);
    }

    [Fact]
    public void daily_totals_reject_non_positive_day_count()
    {
        var act = () => _aggregator.DailyTotals(
            new DateOnly(2026, 1, 1), 0, Utc(Korea, 2026, 1, 2), Korea);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void daily_totals_reject_unbounded_or_unrepresentable_ranges()
    {
        var tooMany = () => _aggregator.DailyTotals(
            new DateOnly(2026, 1, 1),
            Aggregator.MaximumDailyTotalsDays + 1,
            Utc(Korea, 2026, 1, 2),
            Korea);
        var beforeMinimum = () => _aggregator.DailyTotals(
            DateOnly.MinValue,
            2,
            DateTimeOffset.MinValue,
            Korea);
        var afterMaximum = () => _aggregator.DailyTotals(
            DateOnly.MaxValue,
            1,
            DateTimeOffset.MaxValue,
            Korea);

        tooMany.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("dayCount");
        beforeMinimum.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("dayCount");
        afterMaximum.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("throughDay");
    }

    [Fact]
    public void minimum_date_with_positive_offset_has_coherent_range_errors()
    {
        var dayRange = () => Aggregator.DayRange(DateOnly.MinValue, Korea);
        var daily = () => _aggregator.DailyTotals(
            DateOnly.MinValue,
            1,
            DateTimeOffset.MinValue,
            Korea);

        dayRange.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("day");
        daily.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("throughDay");
    }

    static TimeZoneInfo FindEasternTimeZone()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        throw new InvalidOperationException("An Eastern US time zone is required for DST tests.");
    }
}
