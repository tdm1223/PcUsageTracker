namespace PcUsageTracker.Core.Models;

public sealed record ForegroundSession(
    long Id,
    string ProcessName,
    DateTimeOffset StartAt,
    DateTimeOffset? EndAt,
    int? DurationSec)
{
    public bool IsOpen => EndAt is null;
}
