using FluentAssertions;
using Microsoft.Data.Sqlite;
using PcUsageTracker.Core.Models;
using PcUsageTracker.Core.Reporting;
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

    void CreateEarlyV5Database(string categoryRows, string ruleRows)
    {
        using var conn = new SqliteConnection($"Data Source={_tmp}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            CREATE TABLE schema_version (version INTEGER PRIMARY KEY);
            INSERT INTO schema_version VALUES (1), (2), (3), (4), (5);
            CREATE TABLE categories (
              id INTEGER PRIMARY KEY,
              name TEXT NOT NULL COLLATE NOCASE UNIQUE,
              color_rgb INTEGER NOT NULL CHECK(color_rgb BETWEEN 0 AND 16777215),
              sort_order INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE application_rules (
              process_name TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
              alias TEXT,
              category_id INTEGER REFERENCES categories(id) ON DELETE SET NULL,
              color_override_rgb INTEGER CHECK(color_override_rgb BETWEEN 0 AND 16777215),
              updated_at INTEGER NOT NULL
            );
            {{categoryRows}}
            {{ruleRows}}
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

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
    public void importing_the_same_session_twice_reuses_existing_row()
    {
        using var store = new SqliteStore(_tmp);

        var firstId = store.ImportSession("code", T(10), T(70), null);
        var secondId = store.ImportSession("CODE", T(10), T(70), null);

        secondId.Should().Be(firstId);
        store.EnumerateSessions().Should().ContainSingle();
    }

    [Fact]
    public void import_reconciles_open_to_closed_and_preserves_first_closed_conflict()
    {
        using var store = new SqliteStore(_tmp);

        var openId = store.ImportSession("Code", T(10), null, null);
        var closedId = store.ImportSession("code", T(10), T(70), null);
        var conflictingId = store.ImportSession("CODE", T(10), T(90), null);
        var closedThenOpenId = store.ImportSession("cOdE", T(10), null, null);

        closedId.Should().Be(openId);
        conflictingId.Should().Be(openId);
        closedThenOpenId.Should().Be(openId);
        var row = store.EnumerateSessions().Should().ContainSingle().Which;
        row.EndAtUnix.Should().Be(T(70).ToUnixTimeSeconds());
        row.DurationSec.Should().Be(60);
    }

    [Fact]
    public void atomic_replace_import_rolls_back_clear_and_partial_writes_when_source_fails()
    {
        using var store = new SqliteStore(_tmp);
        store.ImportSession("existing", T(0), T(10), @"C:\Existing.exe");

        IEnumerable<ImportSessionRow> FailingRows()
        {
            yield return new ImportSessionRow("new", T(20), T(30), @"C:\New.exe");
            throw new InvalidDataException("injected import failure");
        }

        var act = () => store.ImportSessions(FailingRows(), replace: true);

        act.Should().Throw<InvalidDataException>();
        var row = store.EnumerateSessions().Should().ContainSingle().Which;
        row.ProcessName.Should().Be("existing");
        row.ExePath.Should().Be(@"C:\Existing.exe");
        store.GetProcessPath("new").Should().BeNull();
    }

    [Fact]
    public void atomic_append_import_rolls_back_all_rows_when_a_row_is_invalid()
    {
        using var store = new SqliteStore(_tmp);
        var rows = new[]
        {
            new ImportSessionRow("valid", T(0), T(10), null),
            new ImportSessionRow("   ", T(20), T(30), null),
        };

        var act = () => store.ImportSessions(rows, replace: false);

        act.Should().Throw<ArgumentException>();
        store.EnumerateSessions().Should().BeEmpty();
    }

    [Fact]
    public void v6_installs_indexes_used_by_closed_and_open_overlap_branches()
    {
        using var store = new SqliteStore(_tmp);
        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT id FROM sessions
             WHERE end_at IS NOT NULL AND end_at > 100 AND start_at < 200
            UNION ALL
            SELECT id FROM sessions
             WHERE end_at IS NULL AND 300 > 100 AND start_at < 200;
            """;

        var details = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) details.Add(reader.GetString(3));

        details.Should().Contain(detail => detail.Contains("idx_sessions_end_start", StringComparison.Ordinal));
        details.Should().Contain(detail => detail.Contains("idx_sessions_open_start", StringComparison.Ordinal));
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

    [Fact]
    public void settings_get_returns_null_when_absent()
    {
        using var store = new SqliteStore(_tmp);
        store.GetSetting("idle_threshold_sec").Should().BeNull();
    }

    [Fact]
    public void settings_set_then_get_roundtrips()
    {
        using var store = new SqliteStore(_tmp);
        store.SetSetting("idle_threshold_sec", "180");
        store.GetSetting("idle_threshold_sec").Should().Be("180");
    }

    [Fact]
    public void settings_set_is_upsert_on_existing_key()
    {
        using var store = new SqliteStore(_tmp);
        store.SetSetting("idle_threshold_sec", "180");
        store.SetSetting("idle_threshold_sec", "300");
        store.GetSetting("idle_threshold_sec").Should().Be("300");
    }

    [Fact]
    public void migration_v3_db_upgrades_to_current_preserving_data()
    {
        // v3 시점 DB를 손수 제작: schema_version=3, settings 테이블 없음, 기존 데이터 시드.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_tmp}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE schema_version (version INTEGER PRIMARY KEY);
                INSERT INTO schema_version VALUES (1);
                INSERT INTO schema_version VALUES (2);
                INSERT INTO schema_version VALUES (3);

                CREATE TABLE sessions (
                  id            INTEGER PRIMARY KEY AUTOINCREMENT,
                  process_name  TEXT    NOT NULL,
                  start_at      INTEGER NOT NULL,
                  end_at        INTEGER,
                  duration_sec  INTEGER
                );
                INSERT INTO sessions (process_name, start_at, end_at, duration_sec)
                VALUES ('chrome', 1000, 1060, 60);

                CREATE TABLE processes (
                  name          TEXT PRIMARY KEY,
                  exe_path      TEXT,
                  last_seen_at  INTEGER NOT NULL
                );
                INSERT INTO processes (name, exe_path, last_seen_at)
                VALUES ('chrome', 'C:\Apps\chrome.exe', 1000);

                CREATE TABLE excluded_processes (
                  name         TEXT PRIMARY KEY,
                  reason       TEXT,
                  excluded_at  INTEGER NOT NULL
                );
                INSERT INTO excluded_processes (name, reason, excluded_at)
                VALUES ('LegacyApp', 'user-hidden', 999);
                """;
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var store = new SqliteStore(_tmp);

        // 스키마 최신 버전으로 올라감
        Migrations.ReadVersion(store.Connection).Should().Be(SqliteStore.CurrentSchemaVersion);
        SqliteStore.CurrentSchemaVersion.Should().Be(6);

        // 기존 데이터 보존
        store.EnumerateSessions().Should().HaveCount(1);
        store.GetProcessPath("chrome").Should().Be(@"C:\Apps\chrome.exe");
        store.IsExcluded("LegacyApp").Should().BeTrue();

        // V3 시드(시스템 UI 제외)가 v3→v4 업그레이드 경로에서도 INSERT OR IGNORE로 채워졌는지 검증
        store.IsExcluded("StartMenuExperienceHost").Should().BeTrue();

        // 새 settings 테이블 사용 가능
        store.GetSetting("idle_threshold_sec").Should().BeNull();
        store.SetSetting("idle_threshold_sec", "180");
        store.GetSetting("idle_threshold_sec").Should().Be("180");
    }

    [Fact]
    public void migration_v4_to_current_preserves_existing_data_and_settings()
    {
        using (var conn = new SqliteConnection($"Data Source={_tmp}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE schema_version (version INTEGER PRIMARY KEY);
                INSERT INTO schema_version VALUES (1), (2), (3), (4);
                CREATE TABLE sessions (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  process_name TEXT NOT NULL,
                  start_at INTEGER NOT NULL,
                  end_at INTEGER,
                  duration_sec INTEGER
                );
                INSERT INTO sessions (process_name, start_at, end_at, duration_sec)
                VALUES ('code', 1000, 1060, 60);
                CREATE TABLE processes (name TEXT PRIMARY KEY, exe_path TEXT, last_seen_at INTEGER NOT NULL);
                INSERT INTO processes VALUES ('code', 'C:\Apps\code.exe', 1060);
                CREATE TABLE excluded_processes (name TEXT PRIMARY KEY, reason TEXT, excluded_at INTEGER NOT NULL);
                INSERT INTO excluded_processes VALUES ('LegacyApp', 'user-hidden', 1000);
                CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO settings VALUES ('idle_threshold_sec', '600');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new SqliteStore(_tmp);

        Migrations.ReadVersion(store.Connection).Should().Be(SqliteStore.CurrentSchemaVersion);
        store.EnumerateSessions().Should().ContainSingle(row => row.ProcessName == "code" && row.DurationSec == 60);
        store.GetProcessPath("code").Should().Be(@"C:\Apps\code.exe");
        store.IsExcluded("LegacyApp").Should().BeTrue();
        store.GetSetting("idle_threshold_sec").Should().Be("600");
        store.ListCategories().Should().HaveCount(6);
    }

    [Fact]
    public void v5_seeds_stable_default_categories()
    {
        using var store = new SqliteStore(_tmp);
        var categories = store.ListCategories();

        categories.Select(c => (c.Id, c.Name, c.ColorRgb)).Should().ContainInOrder(
            (DefaultApplicationCategories.CodingId, "Coding", DefaultApplicationCategories.CodingColor),
            (DefaultApplicationCategories.GameId, "Game", DefaultApplicationCategories.GameColor),
            (DefaultApplicationCategories.CommunicationId, "Communication", DefaultApplicationCategories.CommunicationColor),
            (DefaultApplicationCategories.BrowsingId, "Browsing", DefaultApplicationCategories.BrowsingColor),
            (DefaultApplicationCategories.SystemId, "System", DefaultApplicationCategories.SystemColor),
            (DefaultApplicationCategories.OtherId, "Other", DefaultApplicationCategories.OtherColor));
    }

    [Fact]
    public void category_crud_rejects_duplicate_names_and_invalid_colors()
    {
        using var store = new SqliteStore(_tmp);
        var id = store.CreateCategory("Productivity", 0x123456, 5);
        store.GetCategory(id).Should().Be(new ApplicationCategory(id, "Productivity", 0x123456, 5));

        store.UpdateCategory(id, "Focused work", 0x654321, 7).Should().BeTrue();
        store.GetCategory(id).Should().Be(new ApplicationCategory(id, "Focused work", 0x654321, 7));

        var duplicate = () => store.CreateCategory("coding", 0, 0);
        duplicate.Should().Throw<SqliteException>();
        var invalidLow = () => store.CreateCategory("Invalid low", -1);
        invalidLow.Should().Throw<ArgumentOutOfRangeException>();
        var invalidHigh = () => store.CreateCategory("Invalid high", 0x1000000);
        invalidHigh.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void default_other_identity_is_immutable_but_color_and_order_are_editable()
    {
        using var store = new SqliteStore(_tmp);
        var id = store.Open("plain-app", T(0));
        store.Close(id, T(10));

        var update = () => store.UpdateCategory(
            DefaultApplicationCategories.OtherId, "Renamed", 0x010203, 999);
        update.Should().Throw<InvalidOperationException>();
        var delete = () => store.DeleteCategory(DefaultApplicationCategories.OtherId);
        delete.Should().Throw<InvalidOperationException>();

        store.UpdateCategory(DefaultApplicationCategories.OtherId, "Other", 0x223344, 123)
            .Should().BeTrue();

        // DB triggers preserve the invariant even if a caller bypasses SqliteStore.
        using (var cmd = store.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE categories SET name = 'Ghost' WHERE id = 6;";
            var directUpdate = () => cmd.ExecuteNonQuery();
            directUpdate.Should().Throw<SqliteException>();

            cmd.CommandText = "UPDATE categories SET id = 99 WHERE id = 6;";
            var directIdentityUpdate = () => cmd.ExecuteNonQuery();
            directIdentityUpdate.Should().Throw<SqliteException>();

            cmd.CommandText = "DELETE FROM categories WHERE id = 6;";
            var directDelete = () => cmd.ExecuteNonQuery();
            directDelete.Should().Throw<SqliteException>();
        }

        store.GetCategory(DefaultApplicationCategories.OtherId).Should().Be(new ApplicationCategory(
            DefaultApplicationCategories.OtherId, "Other", 0x223344, 123));
        var app = store.ListKnownApplications().Single(a => a.ProcessName == "plain-app");
        app.CategoryName.Should().Be("Other");
        app.ResolvedColorRgb.Should().Be(0x223344);
    }

    [Fact]
    public void early_v5_relocates_category_reusing_id_six_and_preserves_rule_reference()
    {
        CreateEarlyV5Database(
            "INSERT INTO categories VALUES (6, 'Personal', 1122867, 7);",
            "INSERT INTO application_rules VALUES ('personal-app', 'Personal alias', 6, NULL, 1000);");

        using var store = new SqliteStore(_tmp);
        var categories = store.ListCategories();
        categories.Should().ContainSingle(c => c.Id == 6 && c.Name == "Other");
        var relocated = categories.Single(c => c.Name == "Personal");
        relocated.Id.Should().NotBe(6);
        relocated.ColorRgb.Should().Be(1122867);
        store.GetApplicationRule("personal-app")!.Value.CategoryId.Should().Be(relocated.Id);

        var id = store.Open("personal-app", T(0));
        store.Close(id, T(10));
        var report = new Aggregator(store.Connection).AllTime(T(20)).Single();
        report.DisplayName.Should().Be("Personal alias");
        report.CategoryName.Should().Be("Personal");
        report.ColorRgb.Should().Be(1122867);
    }

    [Fact]
    public void early_v5_moves_other_to_id_six_preserving_rules_color_and_is_idempotent()
    {
        CreateEarlyV5Database(
            "INSERT INTO categories VALUES (9, 'other', 1267611, 77);",
            "INSERT INTO application_rules VALUES ('fallback-app', NULL, 9, NULL, 1000);");

        IReadOnlyList<ApplicationCategory> firstCategories;
        IReadOnlyList<ApplicationRule> firstRules;
        using (var store = new SqliteStore(_tmp))
        {
            firstCategories = store.ListCategories();
            firstRules = store.ListApplicationRules();
            firstCategories.Should().ContainSingle().Which.Should().Be(
                new ApplicationCategory(6, "Other", 1267611, 77));
            firstRules.Should().ContainSingle().Which.CategoryId.Should().Be(6);

            var id = store.Open("fallback-app", T(0));
            store.Close(id, T(10));
            var report = new Aggregator(store.Connection).AllTime(T(20)).Single();
            report.CategoryName.Should().Be("Other");
            report.ColorRgb.Should().Be(1267611);
        }
        SqliteConnection.ClearAllPools();

        using var reopened = new SqliteStore(_tmp);
        reopened.ListCategories().Should().Equal(firstCategories);
        reopened.ListApplicationRules().Should().Equal(firstRules);
        new Aggregator(reopened.Connection).AllTime(T(20)).Single().CategoryName.Should().Be("Other");
    }

    [Fact]
    public void early_v5_handles_reused_id_six_and_other_at_another_id_together()
    {
        CreateEarlyV5Database(
            """
            INSERT INTO categories VALUES (6, 'Personal', 1122867, 7);
            INSERT INTO categories VALUES (9, 'Other', 4478310, 88);
            """,
            """
            INSERT INTO application_rules VALUES ('personal-app', NULL, 6, NULL, 1000);
            INSERT INTO application_rules VALUES ('other-app', NULL, 9, NULL, 1000);
            """);

        using var store = new SqliteStore(_tmp);
        var categories = store.ListCategories();
        categories.Should().ContainSingle(c => c.Id == 6 && c.Name == "Other" && c.ColorRgb == 4478310);
        categories.Should().ContainSingle(c => c.Name == "Personal" && c.Id != 6);
        var personalId = categories.Single(c => c.Name == "Personal").Id;
        store.GetApplicationRule("personal-app")!.Value.CategoryId.Should().Be(personalId);
        store.GetApplicationRule("other-app")!.Value.CategoryId.Should().Be(6);
    }

    [Fact]
    public void application_rule_roundtrips_and_category_delete_sets_null()
    {
        using var store = new SqliteStore(_tmp);
        var categoryId = store.CreateCategory("Meetings", 0x112233);

        store.UpsertApplicationRule("zoom", "Zoom meetings", categoryId, 0x445566, T(10));
        store.GetApplicationRule("ZOOM").Should().Be(new ApplicationRule(
            "zoom", "Zoom meetings", categoryId, 0x445566, T(10)));

        store.DeleteCategory(categoryId).Should().BeTrue();
        var rule = store.GetApplicationRule("zoom");
        rule.Should().NotBeNull();
        rule!.Value.CategoryId.Should().BeNull();
        rule.Value.Alias.Should().Be("Zoom meetings");
    }

    [Fact]
    public void list_known_applications_unions_history_metadata_and_rules_but_omits_idle()
    {
        using var store = new SqliteStore(_tmp);
        store.Open("history-only", T(0));
        store.UpsertProcessPath("metadata-only", @"C:\Apps\metadata.exe", T(0));
        store.UpsertApplicationRule("rule-only", "Saved alias", DefaultApplicationCategories.CodingId, null, T(0));
        store.Open("__IDLE__", T(0));

        var apps = store.ListKnownApplications();

        apps.Select(a => a.ProcessName).Should().BeEquivalentTo("history-only", "metadata-only", "rule-only");
        apps.Single(a => a.ProcessName == "rule-only").DisplayName.Should().Be("Saved alias");
        apps.Single(a => a.ProcessName == "rule-only").CategoryName.Should().Be("Coding");

        store.UpsertApplicationRule("metadata-only", "AAA", null, null, T(1));
        store.ListKnownApplications()[0].ProcessName.Should().Be("metadata-only",
            "known applications should be re-sorted by their current alias");

        store.DeleteApplicationRule("rule-only");
        store.ListKnownApplications().Should().NotContain(a => a.ProcessName == "rule-only");
    }

    [Fact]
    public void list_known_applications_canonicalizes_casing_and_chooses_latest_non_null_path()
    {
        using var store = new SqliteStore(_tmp);
        store.Open("code", T(0));
        store.UpsertProcessPath("Code", @"C:\Old\Code.exe", T(10));
        store.UpsertProcessPath("code", @"D:\New\Code.exe", T(20));
        store.UpsertApplicationRule("CODE", "Editor", DefaultApplicationCategories.CodingId, null, T(30));

        var app = store.ListKnownApplications().Should().ContainSingle().Which;

        app.ProcessName.Should().Be("CODE", "binary-min casing is deterministic across all discovery sources");
        app.DisplayName.Should().Be("Editor");
        app.ExePath.Should().Be(@"D:\New\Code.exe");
        app.CategoryName.Should().Be("Coding");
    }

    [Fact]
    public void application_rule_rejects_idle_and_invalid_override_color()
    {
        using var store = new SqliteStore(_tmp);

        var idle = () => store.UpsertApplicationRule("__IDLE__", null, null, null, T(0));
        idle.Should().Throw<ArgumentException>();
        var invalid = () => store.UpsertApplicationRule("code", null, null, 0x1000000, T(0));
        invalid.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task independent_read_only_connection_sees_live_data_and_rejects_writes()
    {
        using var store = new SqliteStore(_tmp);
        store.Open("code", T(0));

        using var readOnly = store.OpenReadOnlyConnection();
        readOnly.Should().NotBeSameAs(store.Connection);
        using var count = readOnly.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM sessions WHERE process_name = 'code';";
        Convert.ToInt32(count.ExecuteScalar()).Should().Be(1);

        using var write = readOnly.CreateCommand();
        write.CommandText = "INSERT INTO sessions(process_name, start_at) VALUES ('blocked', 0);";
        var act = () => write.ExecuteNonQuery();
        act.Should().Throw<SqliteException>();

        await Task.Run(() => store.UpsertProcessPathIndependent("code", @"C:\Apps\Code.exe", T(1)));
        store.GetProcessPath("code").Should().Be(@"C:\Apps\Code.exe");
    }
}
