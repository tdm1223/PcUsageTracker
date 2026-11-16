using FluentAssertions;
using PcUsageTracker.Core.Storage;

namespace PcUsageTracker.Core.Tests;

public class AutomaticBackupTests
{
    [Fact]
    public void Schedules_only_due_backups_and_respects_calendar_months()
    {
        var last = new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero);
        AutomaticBackup.IsDue(BackupFrequency.Off, null, last).Should().BeFalse();
        AutomaticBackup.IsDue(BackupFrequency.Daily, null, last).Should().BeTrue();
        AutomaticBackup.IsDue(BackupFrequency.Weekly, last, last.AddDays(6)).Should().BeFalse();
        AutomaticBackup.IsDue(BackupFrequency.Weekly, last, last.AddDays(7)).Should().BeTrue();
        AutomaticBackup.IsDue(BackupFrequency.Monthly, last, last.AddMonths(1)).Should().BeTrue();
        AutomaticBackup.ParseFrequency("unknown").Should().Be(BackupFrequency.Off);
        AutomaticBackup.ParseLastSuccess(last.ToString("O")).Should().Be(last);
        AutomaticBackup.ParseLastSuccess("invalid").Should().BeNull();
    }

    [Fact]
    public void Retention_removes_only_old_automatic_backups_after_success()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pcut-autobackup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var directory = Path.Combine(root, "backups");
            Directory.CreateDirectory(directory);
            var manual = Path.Combine(directory, "before-restore-keep.pcutbackup");
            File.WriteAllText(manual, "keep");
            using var store = new SqliteStore(Path.Combine(root, "db.sqlite"));
            using var readOnly = store.OpenReadOnlyConnection();
            var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            string newest = "";
            for (var day = 0; day < 7; day++)
                newest = AutomaticBackup.Create(readOnly, Path.Combine(root, "icons"), directory, start.AddDays(day));
            Directory.GetFiles(directory, "automatic-*.pcutbackup").Should().HaveCount(5);
            File.Exists(newest).Should().BeTrue();
            File.Exists(manual).Should().BeTrue();
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void Failed_snapshot_does_not_remove_existing_backups()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pcut-auto-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var directory = Path.Combine(root, "backups");
            var icons = Path.Combine(root, "icons");
            Directory.CreateDirectory(icons);
            using var store = new SqliteStore(Path.Combine(root, "db.sqlite"));
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index < 5; index++) AutomaticBackup.Create(store.Connection, icons, directory, now.AddDays(-index));
            File.WriteAllBytes(Path.Combine(icons, new string('A', 64) + ".png"), new byte[4 * 1024 * 1024 + 1]);
            var action = () => AutomaticBackup.Create(store.Connection, icons, directory, now.AddDays(1));
            action.Should().Throw<InvalidDataException>();
            Directory.GetFiles(directory, "automatic-*.pcutbackup").Should().HaveCount(5);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
