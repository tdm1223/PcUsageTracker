namespace PcUsageTracker.Core.Reporting;

/// <summary>
/// Aggregated usage for one stable process identity. Display metadata is resolved dynamically
/// from application rules, so editing a rule immediately affects historical reports.
/// </summary>
public readonly record struct ReportEntry
{
    public ReportEntry(
        string processName,
        int totalSeconds,
        string? exePath = null,
        string? displayName = null,
        string? categoryName = null,
        int? colorRgb = null)
    {
        ProcessName = processName;
        TotalSeconds = totalSeconds;
        ExePath = exePath;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? processName : displayName;
        CategoryName = categoryName;
        ColorRgb = colorRgb;
    }

    public string ProcessName { get; init; }
    public int TotalSeconds { get; init; }
    public string? ExePath { get; init; }
    public string DisplayName { get; init; }
    public string? CategoryName { get; init; }
    public int? ColorRgb { get; init; }
    public TimeSpan Total => TimeSpan.FromSeconds(TotalSeconds);
}
