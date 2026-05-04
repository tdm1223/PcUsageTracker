using FluentAssertions;
using Microsoft.Data.Sqlite;
using PcUsageTracker.Core.Storage;

namespace PcUsageTracker.Core.Tests;

public class SqliteStoreTests : IDisposable
{
    readonly string _tmp;

    public SqliteStoreTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"pcut-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-shm", "-wal" })
            try { File.Delete(_tmp + suffix); } catch { }
    }

    static DateTimeOffset T(int seconds) => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + seconds);

    [Fact]
    public void migration_sets_version_to_current()
    {
        using var store = new SqliteStore(_tmp);
        Migrations.ReadVersion(store.Connection).Should().Be(SqliteStore.CurrentSchemaVersion);
    }

    [Fact]
    public void upsert_process_path_roundtrip()
    {
        using var store = new SqliteStore(_tmp);
        store.UpsertProcessPath("chrome", @"C:\Apps\chrome.exe", T(0));
        store.GetProcessPath("chrome").Should().Be(@"C:\Apps\chrome.exe");

        // 두 번째 upsert는 경로 갱신
        store.UpsertProcessPath("chrome", @"D:\NewPath\chrome.exe", T(10));
        store.GetProcessPath("chrome").Should().Be(@"D:\NewPath\chrome.exe");

        store.GetProcessPath("unknown").Should().BeNull();
    }

    [Fact]
    public void wal_mode_enabled()
    {
        using var store = new SqliteStore(_tmp);
        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = (string)cmd.ExecuteScalar()!;
        mode.Should().Be("wal");
    }

    [Fact]
    public void open_close_roundtrip_persists_duration()
    {
        using var store = new SqliteStore(_tmp);
        var id = store.Open("chrome", T(0));
        store.Close(id, T(60));

        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = "SELECT process_name, start_at, end_at, duration_sec FROM sessions WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        r.Read().Should().BeTrue();
        r.GetString(0).Should().Be("chrome");
        r.GetInt64(1).Should().Be(T(0).ToUnixTimeSeconds());
        r.GetInt64(2).Should().Be(T(60).ToUnixTimeSeconds());
        r.GetInt32(3).Should().Be(60);
    }

    [Fact]
    public void close_is_idempotent_on_already_closed()
    {
        using var store = new SqliteStore(_tmp);
        var id = store.Open("chrome", T(0));
        store.Close(id, T(30));
        store.Close(id, T(99)); // 이미 end_at 설정됨 — WHERE 절에 걸리지 않음

        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = "SELECT end_at, duration_sec FROM sessions WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        r.Read().Should().BeTrue();
        r.GetInt64(0).Should().Be(T(30).ToUnixTimeSeconds());
        r.GetInt32(1).Should().Be(30);
    }

    [Fact]
    public void recover_orphaned_closes_within_cap()
    {
        long id1, id2;
        using (var store = new SqliteStore(_tmp))
        {
            id1 = store.Open("chrome", T(0));
            id2 = store.Open("code", T(30));
            // Dispose 없이 종료 시뮬레이션: end_at NULL
        }

        using var reopened = new SqliteStore(_tmp);
        var recovered = reopened.RecoverOrphanedSessions(T(100));
        recovered.Should().Be(2);

        using var cmd = reopened.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, end_at, duration_sec FROM sessions ORDER BY id;";
        using var r = cmd.ExecuteReader();
        r.Read(); r.GetInt64(0).Should().Be(id1);
        r.GetInt64(1).Should().Be(T(100).ToUnixTimeSeconds());
        r.GetInt32(2).Should().Be(100);
        r.Read(); r.GetInt64(0).Should().Be(id2);
        r.GetInt64(1).Should().Be(T(100).ToUnixTimeSeconds());
        r.GetInt32(2).Should().Be(70);
    }

    [Fact]
    public void recover_orphaned_applies_24h_cap()
    {
        long id;
        using (var store = new SqliteStore(_tmp))
        {
            id = store.Open("chrome", T(0));
        }

        using var reopened = new SqliteStore(_tmp);
        // now = start+3일 → cap 86400초로 제한
        reopened.RecoverOrphanedSessions(T(3 * 86400));

        using var cmd = reopened.Connection.CreateCommand();
        cmd.CommandText = "SELECT end_at, duration_sec FROM sessions WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        r.Read().Should().BeTrue();
        r.GetInt64(0).Should().Be(T(86400).ToUnixTimeSeconds());
        r.GetInt32(1).Should().Be(86400);
    }

    [Fact]
    public void recover_orphaned_ignores_already_closed()
    {
        using var store = new SqliteStore(_tmp);
        var id = store.Open("chrome", T(0));
        store.Close(id, T(10));

        var recovered = store.RecoverOrphanedSessions(T(999));
        recovered.Should().Be(0);

        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = "SELECT duration_sec FROM sessions WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        Convert.ToInt32(cmd.ExecuteScalar()).Should().Be(10);
    }

    [Fact]
    public void migration_is_idempotent_on_reopen()
    {
        using (var s = new SqliteStore(_tmp)) { s.Open("chrome", T(0)); }
        using (var s = new SqliteStore(_tmp))
        {
            Migrations.ReadVersion(s.Connection).Should().Be(SqliteStore.CurrentSchemaVersion);
            using var cmd = s.Connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sessions;";
            Convert.ToInt32(cmd.ExecuteScalar()).Should().Be(1);
        }
    }

    [Fact]
    public void default_system_ui_processes_are_seeded_as_excluded()
    {
        using var store = new SqliteStore(_tmp);
        store.IsExcluded("StartMenuExperienceHost").Should().BeTrue();
        store.IsExcluded("ShellExperienceHost").Should().BeTrue();
        store.IsExcluded("SearchHost").Should().BeTrue();
        store.IsExcluded("LockApp").Should().BeTrue();
        store.IsExcluded("chrome").Should().BeFalse();
    }

    [Fact]
    public void add_remove_exclusion_roundtrip()
    {
        using var store = new SqliteStore(_tmp);
        store.IsExcluded("notepad").Should().BeFalse();

        store.AddExclusion("notepad", "user-hidden", T(100));
        store.IsExcluded("notepad").Should().BeTrue();

        // upsert: 두 번째 add는 reason/at 갱신, idempotent
        store.AddExclusion("notepad", "updated-reason", T(200));
        store.IsExcluded("notepad").Should().BeTrue();

        store.RemoveExclusion("notepad");
        store.IsExcluded("notepad").Should().BeFalse();

        // 없는 항목 remove는 no-op
        store.RemoveExclusion("nonexistent");
    }

    [Fact]
    public void list_exclusions_returns_seeded_plus_user_added()
    {
        using var store = new SqliteStore(_tmp);
        var initial = store.ListExclusions();
        initial.Should().Contain("StartMenuExperienceHost");
        initial.Should().NotContain("notepad");

        store.AddExclusion("notepad", null, T(0));
        store.ListExclusions().Should().Contain("notepad");
    }

    [Fact]
    public void delete_sessions_for_process_removes_sessions_and_metadata()
    {
        using var store = new SqliteStore(_tmp);

        var id1 = store.Open("chrome", T(0));
        store.Close(id1, T(10));
        var id2 = store.Open("chrome", T(20));
        store.Close(id2, T(30));
        store.UpsertProcessPath("chrome", @"C:\Apps\chrome.exe", T(0));

        var id3 = store.Open("code", T(40));
        store.Close(id3, T(50));

        var deleted = store.DeleteSessionsForProcess("chrome");
        deleted.Should().Be(2);

        // sessions에 chrome 사라짐, code는 그대로
        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = "SELECT process_name FROM sessions ORDER BY id;";
        var remaining = new List<string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) remaining.Add(r.GetString(0));
        remaining.Should().BeEquivalentTo(new[] { "code" });

        // processes 메타데이터도 삭제
        store.GetProcessPath("chrome").Should().BeNull();
    }

    [Fact]
    public void delete_sessions_for_process_returns_zero_when_no_match()
    {
        using var store = new SqliteStore(_tmp);
        store.DeleteSessionsForProcess("nonexistent").Should().Be(0);
    }

    [Fact]
    public void enumerate_sessions_yields_rows_with_metadata_join()
    {
        using var store = new SqliteStore(_tmp);
        var id1 = store.Open("chrome", T(0));
        store.Close(id1, T(60));
        store.UpsertProcessPath("chrome", @"C:\Apps\chrome.exe", T(0));

        store.Open("code", T(100)); // open session — end_at NULL

        var rows = store.EnumerateSessions().ToList();
        rows.Should().HaveCount(2);

        rows[0].ProcessName.Should().Be("chrome");
        rows[0].StartAtUnix.Should().Be(T(0).ToUnixTimeSeconds());
        rows[0].EndAtUnix.Should().Be(T(60).ToUnixTimeSeconds());
        rows[0].DurationSec.Should().Be(60);
        rows[0].ExePath.Should().Be(@"C:\Apps\chrome.exe");

        rows[1].ProcessName.Should().Be("code");
        rows[1].EndAtUnix.Should().BeNull();
        rows[1].DurationSec.Should().BeNull();
        rows[1].ExePath.Should().BeNull();
    }

    [Fact]
    public void clear_all_sessions_wipes_sessions_and_processes_but_preserves_exclusions()
    {
        using var store = new SqliteStore(_tmp);
        var id = store.Open("chrome", T(0));
        store.Close(id, T(60));
        store.UpsertProcessPath("chrome", @"C:\Apps\chrome.exe", T(0));
        store.AddExclusion("MyApp", "user-hidden", T(10));

        var deleted = store.ClearAllSessions();
        deleted.Should().Be(1);

        store.EnumerateSessions().Should().BeEmpty();
        store.GetProcessPath("chrome").Should().BeNull();

        // 사용자가 명시 추가한 exclusion + 시드된 system-ui 항목 모두 보존
        store.IsExcluded("MyApp").Should().BeTrue();
        store.IsExcluded("StartMenuExperienceHost").Should().BeTrue();
    }

    [Fact]
    public void clear_all_sessions_returns_zero_on_empty_db()
    {
        using var store = new SqliteStore(_tmp);
        store.ClearAllSessions().Should().Be(0);
    }

    [Fact]
    public void import_session_with_end_persists_duration_and_path()
    {
        using var store = new SqliteStore(_tmp);
        var id = store.ImportSession("notepad", T(100), T(160), @"C:\Windows\notepad.exe");
        id.Should().BeGreaterThan(0);

        var rows = store.EnumerateSessions().ToList();
        rows.Should().HaveCount(1);
        rows[0].ProcessName.Should().Be("notepad");
        rows[0].StartAtUnix.Should().Be(T(100).ToUnixTimeSeconds());
        rows[0].EndAtUnix.Should().Be(T(160).ToUnixTimeSeconds());
        rows[0].DurationSec.Should().Be(60);
        rows[0].ExePath.Should().Be(@"C:\Windows\notepad.exe");

        store.GetProcessPath("notepad").Should().Be(@"C:\Windows\notepad.exe");
    }

    [Fact]
    public void import_session_without_end_leaves_end_null()
    {
        using var store = new SqliteStore(_tmp);
        store.ImportSession("foo", T(0), null, exePath: null);

        var rows = store.EnumerateSessions().ToList();
        rows.Should().HaveCount(1);
        rows[0].EndAtUnix.Should().BeNull();
        rows[0].DurationSec.Should().BeNull();
        rows[0].ExePath.Should().BeNull();
    }

    [Fact]
    public void import_session_clamps_negative_duration_to_zero()
    {
        using var store = new SqliteStore(_tmp);
        // end < start (잘못된 import 데이터) → duration_sec 가 음수가 되지 않게 0 으로 클램프
        store.ImportSession("foo", T(100), T(50), null);

        var rows = store.EnumerateSessions().ToList();
        rows[0].DurationSec.Should().Be(0);
    }

    [Fact]
    public void enumerate_after_clear_then_import_returns_only_new()
    {
        using var store = new SqliteStore(_tmp);
        var id = store.Open("chrome", T(0));
        store.Close(id, T(10));
        store.ClearAllSessions();

        store.ImportSession("code", T(100), T(160), null);

        var rows = store.EnumerateSessions().ToList();
        rows.Should().HaveCount(1);
        rows[0].ProcessName.Should().Be("code");
    }
}
