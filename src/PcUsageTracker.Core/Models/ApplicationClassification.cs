namespace PcUsageTracker.Core.Models;

/// <summary>A user-manageable application category.</summary>
public readonly record struct ApplicationCategory(
    long Id,
    string Name,
    int ColorRgb,
    int SortOrder);

/// <summary>
/// Presentation rule for one process. ProcessName remains the stable identity used by sessions.
/// </summary>
public readonly record struct ApplicationRule(
    string ProcessName,
    string? Alias,
    long? CategoryId,
    int? ColorOverrideRgb,
    DateTimeOffset UpdatedAt);

/// <summary>An application discovered from history, executable metadata, or an existing rule.</summary>
public readonly record struct KnownApplication(
    string ProcessName,
    string? ExePath,
    string? Alias,
    long? CategoryId,
    string CategoryName,
    int? ColorOverrideRgb,
    int ResolvedColorRgb)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? ProcessName : Alias;
}

/// <summary>Stable IDs and colors installed by schema v5.</summary>
public static class DefaultApplicationCategories
{
    public const long CodingId = 1;
    public const long GameId = 2;
    public const long CommunicationId = 3;
    public const long BrowsingId = 4;
    public const long SystemId = 5;
    public const long OtherId = 6;

    public const int CodingColor = 0x4F46E5;
    public const int GameColor = 0xDC2626;
    public const int CommunicationColor = 0x0EA5E9;
    public const int BrowsingColor = 0xF59E0B;
    public const int SystemColor = 0x64748B;
    public const int OtherColor = 0x94A3B8;
    public const int IdleColor = 0x808080;
}
