using FluentAssertions;
using PcUsageTracker.Core.Reporting;
using PcUsageTracker.Core.Storage;

namespace PcUsageTracker.Core.Tests;

public class ApplicationGroupsTests
{
    [Fact]
    public void Groups_survive_backup_and_do_not_rewrite_sessions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pcut-group-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.UtcNow;
            using var source = new SqliteStore(Path.Combine(root, "source.db"));
            var id = source.Open("game", now); source.Close(id, now.AddMinutes(1));
            ApplicationGroups.Save(source, [new("My game", ["game", "helper"])]);
            var backup = Path.Combine(root, "backup.pcutbackup");
            FullBackup.Export(source, Path.Combine(root, "icons"), backup);
            using var restored = new SqliteStore(Path.Combine(root, "restored.db"));
            FullBackup.Restore(restored, Path.Combine(root, "icons"), backup, now.AddHours(1));
            ApplicationGroups.Load(restored.Connection).Should().ContainSingle().Which.Processes.Should().Equal("game", "helper");
            restored.EnumerateSessions().Should().ContainSingle().Which.ProcessName.Should().Be("game");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void Group_summary_sums_member_processes_and_preserves_unassigned_entries()
    {
        var groups = new[] { new ApplicationGroup("Game", ["pbz", "helper"]) };
        var entries = new[] { new ReportEntry("PBZ", 300, categoryName: "Game"),
            new ReportEntry("helper", 60, categoryName: "Game"), new ReportEntry("browser", 120, categoryName: "Browsing") };
        var result = ApplicationGroups.Summarize(entries, groups);
        result.Should().ContainSingle(row => row.IsGroup && row.Name == "Game" && row.Seconds == 360);
        result.Should().ContainSingle(row => !row.IsGroup && row.Name == "browser" && row.Seconds == 120);
    }

    [Fact]
    public void Process_cannot_be_assigned_to_more_than_one_group()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pcut-group-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SqliteStore(root);
            var action = () => ApplicationGroups.Save(store, [new("A", ["Game"]), new("B", ["game"])]);
            action.Should().Throw<ArgumentException>();
            ApplicationGroups.Load(store.Connection).Should().BeEmpty();
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { File.Delete(root); } catch (IOException) { } }
    }
}
