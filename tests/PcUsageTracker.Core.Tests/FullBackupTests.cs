using System.IO.Compression;
using FluentAssertions;
using PcUsageTracker.Core.Storage;

namespace PcUsageTracker.Core.Tests;

public class FullBackupTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"pcut-full-test-{Guid.NewGuid():N}");
    public FullBackupTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Backup_restores_history_rules_settings_exclusions_icons_and_closes_open_sessions_at_heartbeat()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var iconDir = Path.Combine(_dir, "icons");
        Directory.CreateDirectory(iconDir);
        var iconName = new string('A', 64) + ".png";
        File.WriteAllBytes(Path.Combine(iconDir, iconName), [1, 2, 3]);
        var path = Path.Combine(_dir, "snapshot.pcutbackup");
        using var source = new SqliteStore(Path.Combine(_dir, "source.db"));
        var session = source.Open("alpha", now);
        source.Touch(session, now.AddSeconds(10));
        source.SetSetting("idle_threshold_sec", "240");
        source.AddExclusion("excluded-app", "user-hidden", now);
        var category = source.CreateCategory("Custom", 0x112233);
        source.UpsertApplicationRule("alpha", "Alias", category, 0x123456, now);
        FullBackup.Export(source, iconDir, path);

        using var target = new SqliteStore(Path.Combine(_dir, "target.db"));
        target.Open("old", now);
        var restoredIcons = Path.Combine(_dir, "restored-icons");
        FullBackup.Restore(target, restoredIcons, path, now.AddHours(2)).Should().Be(0);
        var restored = target.EnumerateSessions().Should().ContainSingle().Subject;
        restored.ProcessName.Should().Be("alpha");
        restored.EndAtUnix.Should().Be(now.AddSeconds(10).ToUnixTimeSeconds());
        target.GetSetting("idle_threshold_sec").Should().Be("240");
        target.IsExcluded("excluded-app").Should().BeTrue();
        target.GetApplicationRule("alpha")!.Value.Alias.Should().Be("Alias");
        target.ListCategories().Should().Contain(item => item.Name == "Custom");
        File.ReadAllBytes(Path.Combine(restoredIcons, iconName)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Invalid_backup_does_not_replace_existing_database()
    {
        using var target = new SqliteStore(Path.Combine(_dir, "target.db"));
        target.Open("existing", DateTimeOffset.UtcNow);
        var path = Path.Combine(_dir, "bad.pcutbackup");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("history.db").Open());
            writer.Write("not a database");
        }
        var restore = () => FullBackup.Restore(target, Path.Combine(_dir, "icons"), path, DateTimeOffset.UtcNow);
        restore.Should().Throw<Exception>();
        target.EnumerateSessions().Should().ContainSingle().Which.ProcessName.Should().Be("existing");
    }

    [Fact]
    public void Backup_cannot_overwrite_the_live_database()
    {
        var path = Path.Combine(_dir, "target.db");
        using var store = new SqliteStore(path);
        var export = () => FullBackup.Export(store, Path.Combine(_dir, "icons"), path);
        export.Should().Throw<ArgumentException>();
        store.SetSetting("still", "usable");
        store.GetSetting("still").Should().Be("usable");
    }

    [Fact]
    public void Restore_rejects_icon_paths_outside_the_cache_without_changing_data()
    {
        var now = DateTimeOffset.UtcNow;
        var backup = Path.Combine(_dir, "unsafe.pcutbackup");
        using var source = new SqliteStore(Path.Combine(_dir, "source.db"));
        source.Open("source", now);
        FullBackup.Export(source, Path.Combine(_dir, "icons"), backup);
        using (var archive = ZipFile.Open(backup, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(archive.CreateEntry("icons/../../escaped.png").Open()))
            writer.Write("invalid");
        using var target = new SqliteStore(Path.Combine(_dir, "target.db"));
        target.Open("existing", now);
        var restore = () => FullBackup.Restore(target, Path.Combine(_dir, "icons"), backup, now);
        restore.Should().Throw<InvalidDataException>();
        target.EnumerateSessions().Should().ContainSingle().Which.ProcessName.Should().Be("existing");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
