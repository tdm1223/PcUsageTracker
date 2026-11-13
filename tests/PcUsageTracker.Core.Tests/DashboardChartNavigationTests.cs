using FluentAssertions;
using PcUsageTracker.Core.Reporting;

namespace PcUsageTracker.Core.Tests;

public class DashboardChartNavigationTests
{
    [Fact]
    public void Selected_day_is_centered_in_seven_day_view()
    {
        var selected = new DateOnly(2026, 11, 6);
        DashboardDateNavigation.ChartThroughDay(selected, 7).Should().Be(new DateOnly(2026, 11, 9));
    }

    [Fact]
    public void Thirty_day_view_places_selected_day_next_to_the_middle()
    {
        var selected = new DateOnly(2026, 11, 6);
        DashboardDateNavigation.ChartThroughDay(selected, 30).Should().Be(new DateOnly(2026, 11, 21));
    }
}
