using FluentAssertions;
using PcUsageTracker.Core.Reporting;

namespace PcUsageTracker.Core.Tests;

public class TimelineRangeSummaryTests
{
    [Fact]
    public void Clips_each_session_to_the_selected_interval_and_preserves_overlaps()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        TimelineSegment[] segments = [new("a", "Alpha", "Work", 0, start, start.AddMinutes(10)),
            new("A", "Alpha", "Work", 0, start.AddMinutes(4), start.AddMinutes(6)),
            new("b", "Beta", "Game", 0, start.AddMinutes(9), start.AddMinutes(20))];
        var result = TimelineRangeSummary.Create(segments, start.AddMinutes(5), start.AddMinutes(12));
        result.Should().ContainSingle(row => row.ProcessName == "a" && row.TotalSeconds == 360);
        result.Should().ContainSingle(row => row.ProcessName == "b" && row.TotalSeconds == 180);
    }
}
