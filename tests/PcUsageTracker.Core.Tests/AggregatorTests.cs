using FluentAssertions;
using Microsoft.Data.Sqlite;
using PcUsageTracker.Core.Reporting;
using PcUsageTracker.Core.Storage;

namespace PcUsageTracker.Core.Tests;

public class AggregatorTests : IDisposable
{
    readonly string _tmp;
    readonly SqliteStore _store;
    readonly Aggregator _agg;

    public AggregatorTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"pcut-agg-{Guid.NewGuid():N}.db");
        _store = new SqliteStore(_tmp);
        _agg = new Aggregator(_store.Connection);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-shm", "-wal" })
            try { File.Delete(_tmp + suffix); } catch { }
    }

    static DateTimeOffset T(int seconds) => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + seconds);

    void Seed(string proc, int startOffset, int endOffset)
    {
        var id = _store.Open(proc, T(startOffset));
        _store.Close(id, T(endOffset));
    }

    [Fact]
    public void topn_orders_by_total_desc()
    {
        Seed("chrome", 0, 100);
        Seed("code", 0, 50);
        Seed("chrome", 200, 210);

        var result = _agg.TopN(T(0), T(1000), T(1000), 5);

        result.Should().HaveCount(2);
        result[0].ProcessName.Should().Be("chrome");
        result[0].TotalSeconds.Should().Be(110);
        result[1].ProcessName.Should().Be("code");
        result[1].TotalSeconds.Should().Be(50);
    }

    [Fact]
    public void topn_limit_truncates()
    {
        for (int i = 0; i < 10; i++)
            Seed($"proc{i}", i * 100, i * 100 + 10 + i);

        var result = _agg.TopN(T(0), T(10_000), T(10_000), 3);

        result.Should().HaveCount(3);
        // proc9=19s, proc8=18s, proc7=17s
        result.Select(r => r.ProcessName).Should().ContainInOrder("proc9", "proc8", "proc7");
    }

    [Fact]
    public void topn_clips_sessions_at_range_boundaries()
    {
        // 세션: start=50, end=150. 범위: [100, 200). 겹치는 시간 = 50.
        Seed("chrome", 50, 150);
        var result = _agg.TopN(T(100), T(200), T(200), 5);

        result.Should().HaveCount(1);
        result[0].TotalSeconds.Should().Be(50);
    }

    [Fact]
    public void topn_excludes_sessions_outside_range()
    {
        Seed("chrome", 0, 50);
        Seed("code", 100, 150);

        var result = _agg.TopN(T(200), T(300), T(300), 5);
        result.Should().BeEmpty();
    }

    [Fact]
    public void topn_handles_open_session_with_now_as_end()
    {
        _store.Open("chrome", T(100)); // open, end_at=NULL
        var result = _agg.TopN(T(0), T(300), nowUtc: T(250), limit: 5);

        result.Should().HaveCount(1);
        result[0].ProcessName.Should().Be("chrome");
        result[0].TotalSeconds.Should().Be(150); // 100..250
    }

    [Fact]
    public void alltime_includes_open_sessions()
    {
        Seed("chrome", 0, 100);
        _store.Open("code", T(200)); // open

        var result = _agg.AllTime(T(300), 10);

        result.Should().HaveCount(2);
        result[0].ProcessName.Should().Be("chrome");
        result[0].TotalSeconds.Should().Be(100);
        result[1].ProcessName.Should().Be("code");
        result[1].TotalSeconds.Should().Be(100); // 200..300
    }

    [Fact]
    public void empty_db_returns_empty()
    {
        var today = _agg.TopN(T(0), T(86400), T(86400), 5);
        today.Should().BeEmpty();
        _agg.AllTime(T(86400), 5).Should().BeEmpty();
    }

    [Fact]
    public void topn_includes_exe_path_when_available()
    {
        Seed("chrome", 0, 100);
        _store.UpsertProcessPath("chrome", @"C:\Apps\chrome.exe", T(0));

        var result = _agg.TopN(T(0), T(1000), T(1000), 5);
        result.Should().HaveCount(1);
        result[0].ExePath.Should().Be(@"C:\Apps\chrome.exe");
    }

    [Fact]
    public void topn_returns_null_path_when_metadata_missing()
    {
        Seed("unknownproc", 0, 100);

        var result = _agg.TopN(T(0), T(1000), T(1000), 5);
        result.Should().HaveCount(1);
        result[0].ExePath.Should().BeNull();
    }

    [Fact]
    public void alltime_includes_exe_path()
    {
        Seed("chrome", 0, 100);
        _store.UpsertProcessPath("chrome", @"C:\Apps\chrome.exe", T(0));

        var result = _agg.AllTime(T(1000), 5);
        result.Should().HaveCount(1);
        result[0].ExePath.Should().Be(@"C:\Apps\chrome.exe");
    }

    [Fact]
    public void today_range_covers_local_midnight_to_midnight()
    {
        var now = DateTimeOffset.UtcNow;
        var (from, to) = Aggregator.TodayRange(now);
        (to - from).TotalHours.Should().Be(24);
        from.ToLocalTime().TimeOfDay.Should().Be(TimeSpan.Zero);
        to.ToLocalTime().TimeOfDay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void week_range_starts_monday_and_spans_7_days()
    {
        var now = DateTimeOffset.UtcNow;
        var (from, to) = Aggregator.ThisWeekRange(now);
        (to - from).TotalDays.Should().Be(7);
        from.ToLocalTime().DayOfWeek.Should().Be(DayOfWeek.Monday);
        from.ToLocalTime().TimeOfDay.Should().Be(TimeSpan.Zero);
    }

    // 결정적 회귀: 한 주 7일 모두에 대해 from은 그 주 월요일이어야 함.
    // 2026-05-04 = Monday 인 주(2026-05-04 ~ 2026-05-10) 를 기준으로 7개 local 날짜를 입력으로 검증.
    [Theory]
    [InlineData(2026, 5, 4, 12, 2026, 5, 4)]   // Monday → 같은 날 월요일
    [InlineData(2026, 5, 5, 9, 2026, 5, 4)]    // Tuesday
    [InlineData(2026, 5, 6, 23, 2026, 5, 4)]   // Wednesday
    [InlineData(2026, 5, 7, 0, 2026, 5, 4)]    // Thursday
    [InlineData(2026, 5, 8, 18, 2026, 5, 4)]   // Friday
    [InlineData(2026, 5, 9, 6, 2026, 5, 4)]    // Saturday
    [InlineData(2026, 5, 10, 22, 2026, 5, 4)]  // Sunday → 직전 월요일
    public void week_range_is_monday_for_each_weekday(
        int y, int m, int d, int hour,
        int expY, int expM, int expD)
    {
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(y, m, d));
        var local = new DateTimeOffset(y, m, d, hour, 0, 0, localOffset);
        var nowUtc = local.ToUniversalTime();

        var (from, _) = Aggregator.ThisWeekRange(nowUtc);

        var fromLocal = from.ToLocalTime();
        fromLocal.DayOfWeek.Should().Be(DayOfWeek.Monday);
        fromLocal.Year.Should().Be(expY);
        fromLocal.Month.Should().Be(expM);
        fromLocal.Day.Should().Be(expD);
        fromLocal.TimeOfDay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void month_range_starts_first_of_local_month()
    {
        var now = DateTimeOffset.UtcNow;
        var (from, to) = Aggregator.ThisMonthRange(now);

        from.ToLocalTime().Day.Should().Be(1);
        from.ToLocalTime().TimeOfDay.Should().Be(TimeSpan.Zero);
        to.ToLocalTime().Day.Should().Be(1);
        to.ToLocalTime().TimeOfDay.Should().Be(TimeSpan.Zero);
        // 이번 달 마지막 날 23:59:59 가 [from, to) 안에 있어야 한다 — month boundary sanity.
        var fromLocal = from.ToLocalTime();
        var lastInstantLocal = fromLocal.AddMonths(1).AddSeconds(-1);
        lastInstantLocal.Month.Should().Be(fromLocal.Month);
    }

    [Fact]
    public void month_range_rolls_over_at_year_end()
    {
        // 2026-12-15 12:00 local → from = 2026-12-01 00:00 local, to = 2027-01-01 00:00 local
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 12, 15));
        var local = new DateTimeOffset(2026, 12, 15, 12, 0, 0, localOffset);
        var nowUtc = local.ToUniversalTime();

        var (from, to) = Aggregator.ThisMonthRange(nowUtc);

        var fromLocal = from.ToLocalTime();
        fromLocal.Year.Should().Be(2026);
        fromLocal.Month.Should().Be(12);
        fromLocal.Day.Should().Be(1);
        fromLocal.TimeOfDay.Should().Be(TimeSpan.Zero);

        var toLocal = to.ToLocalTime();
        toLocal.Year.Should().Be(2027);
        toLocal.Month.Should().Be(1);
        toLocal.Day.Should().Be(1);
        toLocal.TimeOfDay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void month_range_handles_february_28_or_29()
    {
        // 2026-02-15 (2026 is not a leap year) → from = 2026-02-01, to = 2026-03-01
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 2, 15));
        var local = new DateTimeOffset(2026, 2, 15, 8, 30, 0, localOffset);
        var nowUtc = local.ToUniversalTime();

        var (from, to) = Aggregator.ThisMonthRange(nowUtc);

        from.ToLocalTime().Month.Should().Be(2);
        from.ToLocalTime().Day.Should().Be(1);
        to.ToLocalTime().Month.Should().Be(3);
        to.ToLocalTime().Day.Should().Be(1);
    }
}
