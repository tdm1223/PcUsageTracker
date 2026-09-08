using FluentAssertions;
using PcUsageTracker.Core.Reporting;

namespace PcUsageTracker.Core.Tests;

public sealed class DashboardSummaryTests
{
    [Fact]
    public void summary_separates_idle_and_selects_top_active_application()
    {
        var entries = new[]
        {
            new ReportEntry("code", 3_600, displayName: "Visual Studio Code"),
            new ReportEntry("game", 7_200, displayName: "A Game"),
            new ReportEntry("__IDLE__", 900, displayName: "(Idle)"),
        };

        var result = DashboardSummary.FromApplications(entries);

        result.TotalSeconds.Should().Be(11_700);
        result.ActiveSeconds.Should().Be(10_800);
        result.IdleSeconds.Should().Be(900);
        result.TopApplicationName.Should().Be("A Game");
        result.TopApplicationSeconds.Should().Be(7_200);
    }

    [Fact]
    public void summary_handles_an_empty_day()
    {
        var result = DashboardSummary.FromApplications(Array.Empty<ReportEntry>());

        result.TotalSeconds.Should().Be(0);
        result.ActiveSeconds.Should().Be(0);
        result.IdleSeconds.Should().Be(0);
        result.TopApplicationName.Should().BeNull();
        result.TopApplicationSeconds.Should().Be(0);
    }

    [Fact]
    public void midnight_rollover_follows_today_but_preserves_a_historical_selection()
    {
        var previousToday = new DateOnly(2026, 9, 8);
        var newToday = new DateOnly(2026, 9, 9);

        DashboardDateNavigation.SelectionAfterTodayChanged(previousToday, previousToday, newToday)
            .Should().Be(newToday);
        DashboardDateNavigation.SelectionAfterTodayChanged(
                previousToday, previousToday.AddDays(-2), newToday)
            .Should().Be(previousToday.AddDays(-2));
        DashboardDateNavigation.SelectionAfterTodayChanged(
                previousToday, newToday.AddDays(1), newToday)
            .Should().Be(newToday, "the picker selection must never exceed its new Today maximum");
    }

    [Fact]
    public void refresh_policy_coalesces_same_view_ticks_but_supersedes_navigation_and_manual_invalidations()
    {
        ReportRefreshPolicy.Decide(refreshInFlight: false, sameView: true, manualInvalidation: false)
            .Should().Be(RefreshRequestDisposition.Start);
        ReportRefreshPolicy.Decide(refreshInFlight: true, sameView: true, manualInvalidation: false)
            .Should().Be(RefreshRequestDisposition.Coalesce);
        ReportRefreshPolicy.Decide(refreshInFlight: true, sameView: false, manualInvalidation: false)
            .Should().Be(RefreshRequestDisposition.Supersede);
        ReportRefreshPolicy.Decide(refreshInFlight: true, sameView: true, manualInvalidation: true)
            .Should().Be(RefreshRequestDisposition.Supersede);
    }
}
