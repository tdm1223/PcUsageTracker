namespace PcUsageTracker.Core.Reporting;

public readonly record struct ReportEntry(string ProcessName, int TotalSeconds, string? ExePath = null)
{
    public TimeSpan Total => TimeSpan.FromSeconds(TotalSeconds);
}
