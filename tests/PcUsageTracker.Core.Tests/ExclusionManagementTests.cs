using FluentAssertions;
using PcUsageTracker.Core.Storage;

namespace PcUsageTracker.Core.Tests;

public class ExclusionManagementTests
{
    [Fact]
    public void Restoring_a_default_exclusion_remains_restored_after_restart_and_backup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pcut-exclusions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "data.db");
            var backup = Path.Combine(root, "snapshot.pcutbackup");
            using (var store = new SqliteStore(database))
            {
                store.IsExcluded("SearchHost").Should().BeTrue();
                store.RemoveExclusion("searchhost");
                store.IsExcluded("SearchHost").Should().BeFalse();
                FullBackup.Export(store, Path.Combine(root, "icons"), backup);
            }
            using (var store = new SqliteStore(database))
            {
                store.IsExcluded("SearchHost").Should().BeFalse();
                store.AddExclusion("SEARCHHOST", "user-hidden", DateTimeOffset.UtcNow);
                store.ListExclusionEntries().Should().ContainSingle(item => item.ProcessName == "SEARCHHOST");
                FullBackup.Restore(store, Path.Combine(root, "icons"), backup, DateTimeOffset.UtcNow);
                store.IsExcluded("SearchHost").Should().BeFalse();
            }
            using var final = new SqliteStore(database);
            final.IsExcluded("SearchHost").Should().BeFalse();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Bulk_restore_validates_every_name_before_changing_any_exclusion()
    {
        var database = Path.Combine(Path.GetTempPath(), $"pcut-exclusions-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SqliteStore(database);
            var action = () => store.RemoveExclusions(["SearchHost", "  "]);
            action.Should().Throw<ArgumentException>();
            store.IsExcluded("SearchHost").Should().BeTrue();
            store.RemoveExclusions(["SearchHost", "SearchUI"]);
            store.IsExcluded("SearchHost").Should().BeFalse();
            store.IsExcluded("SearchUI").Should().BeFalse();
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { File.Delete(database); } catch (IOException) { } }
    }
}
