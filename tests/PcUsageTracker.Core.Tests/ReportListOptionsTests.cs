using FluentAssertions;
using PcUsageTracker.Core.Reporting;

namespace PcUsageTracker.Core.Tests;

public class ReportListOptionsTests
{
    readonly ReportEntry[] _entries =
    [
        new("chrome", 12, displayName: "Browser", categoryName: "Web"),
        new("editor", 3600, displayName: "Code", categoryName: "Work"),
        new("game", 90, displayName: "Game", categoryName: "Play"),
    ];

    [Fact]
    public void Search_matches_alias_process_and_category_without_case_sensitivity()
    {
        ReportListOptions.Apply(_entries, "CHROME", ReportListSort.DurationDescending).Should().ContainSingle().Which.DisplayName.Should().Be("Browser");
        ReportListOptions.Apply(_entries, "work", ReportListSort.DurationDescending).Should().ContainSingle().Which.ProcessName.Should().Be("editor");
    }

    [Fact]
    public void Sort_uses_numeric_duration_not_display_text()
    {
        ReportListOptions.Apply(_entries, null, ReportListSort.DurationAscending).Select(entry => entry.TotalSeconds)
            .Should().Equal(12, 90, 3600);
        ReportListOptions.Apply(_entries, null, ReportListSort.NameDescending).Select(entry => entry.DisplayName)
            .Should().Equal("Game", "Code", "Browser");
    }
}
