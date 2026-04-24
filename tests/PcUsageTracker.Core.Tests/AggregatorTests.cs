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
}
