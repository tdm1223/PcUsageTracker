using FluentAssertions;
using PcUsageTracker.Core.Reporting;

namespace PcUsageTracker.Core.Tests;

public class UsageComparisonTests
{
    [Fact]
    public void Compares_tracked_active_idle_and_category_totals_including_missing_categories()
    {
        ReportEntry[] current = [new("a", 100, categoryName: "Work"), new("b", 50, categoryName: "WORK"), new("__IDLE__", 20)];
        ReportEntry[] previous = [new("c", 40, categoryName: "Work"), new("d", 80, categoryName: "Game")];
        var rows = UsageComparison.Create(current, previous);
        rows.Should().Contain(new UsageComparisonRow("Tracked", 170, 120));
        rows.Should().Contain(new UsageComparisonRow("Active", 150, 120));
        rows.Should().Contain(new UsageComparisonRow("Idle", 20, 0));
        rows.Should().Contain(new UsageComparisonRow("Category: Work", 150, 40));
        rows.Should().Contain(new UsageComparisonRow("Category: Game", 0, 80));
        rows.Single(row => row.Name == "Tracked").DifferenceSeconds.Should().Be(50);
    }

    [Fact]
    public void Comparison_uses_equal_elapsed_periods_and_monday_week_start()
    {
        var now = new DateTimeOffset(2026, 11, 11, 12, 30, 0, TimeSpan.Zero);
        var (day, week) = ComparisonPeriods.At(now, TimeZoneInfo.Utc);
        day.CurrentStart.Should().Be(new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero));
        day.PreviousStart.Should().Be(day.CurrentStart.AddDays(-1));
        day.PreviousEnd.Should().Be(day.PreviousStart.AddHours(12).AddMinutes(30));
        week.CurrentStart.Should().Be(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero));
        (week.PreviousEnd - week.PreviousStart).Should().Be(week.CurrentEnd - week.CurrentStart);
    }

    [Fact]
    public void Comparison_does_not_extend_a_short_previous_dst_day_into_the_next_day()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        var now = new DateTimeOffset(2026, 3, 9, 23, 30, 0, TimeSpan.FromHours(-4));
        var (day, _) = ComparisonPeriods.At(now, zone);
        day.PreviousEnd.Should().Be(day.CurrentStart);
        (day.PreviousEnd - day.PreviousStart).Should().Be(TimeSpan.FromHours(23));
    }
}
