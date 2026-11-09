using FluentAssertions;
using PcUsageTracker.Core.Reporting;

namespace PcUsageTracker.Core.Tests;

public class TimelineViewportTests
{
    [Fact]
    public void ZoomKeepsThePointUnderThePointerAndClampsToDay()
    {
        var zoomed = TimelineViewport.FullDay.Zoom(0.75, 0.5, 0.01);
        zoomed.Start.Should().BeApproximately(0.375, 1e-10);
        zoomed.End.Should().BeApproximately(0.875, 1e-10);
        zoomed.Zoom(0.75, 10, 0.01).Should().Be(TimelineViewport.FullDay);
    }

    [Fact]
    public void ZoomHonorsMinimumAndRevealKeepsWindowInDay()
    {
        var zoomed = TimelineViewport.FullDay.Zoom(0, 0.001, 0.1);
        zoomed.Start.Should().Be(0);
        zoomed.Width.Should().BeApproximately(0.1, 1e-10);
        var revealed = zoomed.Reveal(1);
        revealed.End.Should().Be(1);
        revealed.Width.Should().BeApproximately(0.1, 1e-10);
        revealed.Reveal(0.95).Should().Be(revealed);
    }

    [Fact]
    public void PanPreservesWidthAndStopsAtDayBoundaries()
    {
        var viewport = new TimelineViewport(0.25, 0.5);
        viewport.Pan(0.2).Start.Should().BeApproximately(0.45, 1e-10);
        viewport.Pan(-1).Should().Be(new TimelineViewport(0, 0.25));
        viewport.Pan(1).Should().Be(new TimelineViewport(0.75, 1));
        TimelineViewport.FullDay.Pan(0.5).Should().Be(TimelineViewport.FullDay);
    }
}
