using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PcUsageTracker.Core.Storage;

public enum BackupFrequency { Off, Daily, Weekly, Monthly }

public static partial class AutomaticBackup
{
    public const string FrequencyKey = "automatic_backup_frequency";
    public const string LastSuccessKey = "automatic_backup_last_success_utc";
    public const int RetainedCount = 5;

    public static BackupFrequency ParseFrequency(string? value) =>
        Enum.TryParse<BackupFrequency>(value, true, out var frequency) && Enum.IsDefined(frequency)
            ? frequency : BackupFrequency.Off;

    public static bool IsDue(BackupFrequency frequency, DateTimeOffset? lastSuccess, DateTimeOffset now)
    {
        if (frequency == BackupFrequency.Off) return false;
        if (lastSuccess is null) return true;
        var previous = lastSuccess.Value.ToUniversalTime();
        var next = frequency switch
        {
            BackupFrequency.Daily => previous.AddDays(1),
            BackupFrequency.Weekly => previous.AddDays(7),
            BackupFrequency.Monthly => previous.AddMonths(1),
            _ => DateTimeOffset.MaxValue,
        };
        return now.ToUniversalTime() >= next;
    }

    public static DateTimeOffset? ParseLastSuccess(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime() : null;

    public static string Create(SqliteConnection connection, string iconDirectory, string backupDirectory, DateTimeOffset now)
    {
        Directory.CreateDirectory(backupDirectory);
        var file = Path.Combine(backupDirectory, $"automatic-{now.UtcDateTime:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.pcutbackup");
        FullBackup.Export(connection, iconDirectory, file);
        var previous = Directory.EnumerateFiles(backupDirectory, "automatic-*.pcutbackup", SearchOption.TopDirectoryOnly)
            .Where(path => BackupName().IsMatch(Path.GetFileName(path)))
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Skip(RetainedCount - 1)
            .ToArray();
        foreach (var path in previous)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return file;
    }

    [GeneratedRegex(@"^automatic-\d{8}T\d{6}-[a-f0-9]{32}\.pcutbackup$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BackupName();
}
