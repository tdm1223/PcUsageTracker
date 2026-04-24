namespace PcUsageTracker.Core.Sampling;

public readonly record struct ForegroundSnapshot(string ProcessName, string? ExePath);
