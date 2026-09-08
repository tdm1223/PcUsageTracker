using Microsoft.Data.Sqlite;

namespace PcUsageTracker.Core.Storage;

public static class Migrations
{
    const string V1 = """
        CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY);

        CREATE TABLE IF NOT EXISTS sessions (
          id            INTEGER PRIMARY KEY AUTOINCREMENT,
          process_name  TEXT    NOT NULL,
          start_at      INTEGER NOT NULL,
          end_at        INTEGER,
          duration_sec  INTEGER
        );
        CREATE INDEX IF NOT EXISTS idx_sessions_start   ON sessions(start_at);
        CREATE INDEX IF NOT EXISTS idx_sessions_process ON sessions(process_name);

        INSERT OR IGNORE INTO schema_version VALUES (1);
        """;

    const string V2 = """
        CREATE TABLE IF NOT EXISTS processes (
          name          TEXT PRIMARY KEY,
          exe_path      TEXT,
          last_seen_at  INTEGER NOT NULL
        );
        INSERT OR IGNORE INTO schema_version VALUES (2);
        """;

    const string V3 = """
        CREATE TABLE IF NOT EXISTS excluded_processes (
          name         TEXT PRIMARY KEY,
          reason       TEXT,
          excluded_at  INTEGER NOT NULL
        );
        INSERT OR IGNORE INTO schema_version VALUES (3);
        """;

    const string V4 = """
        CREATE TABLE IF NOT EXISTS settings (
          key    TEXT PRIMARY KEY,
          value  TEXT NOT NULL
        );
        INSERT OR IGNORE INTO schema_version VALUES (4);
        """;

    const string V5 = """
        CREATE TABLE IF NOT EXISTS categories (
          id          INTEGER PRIMARY KEY,
          name        TEXT    NOT NULL COLLATE NOCASE UNIQUE,
          color_rgb   INTEGER NOT NULL CHECK(color_rgb BETWEEN 0 AND 16777215),
          sort_order  INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS application_rules (
          process_name       TEXT    NOT NULL COLLATE NOCASE PRIMARY KEY,
          alias              TEXT,
          category_id        INTEGER REFERENCES categories(id) ON DELETE SET NULL,
          color_override_rgb INTEGER CHECK(color_override_rgb BETWEEN 0 AND 16777215),
          updated_at         INTEGER NOT NULL
        );

        -- Seed only while v5 is first being applied. A category intentionally deleted later
        -- must not silently reappear when the application starts again.
        INSERT OR IGNORE INTO categories (id, name, color_rgb, sort_order)
        SELECT 1, 'Coding',        5195493, 10 WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = 5);
        INSERT OR IGNORE INTO categories (id, name, color_rgb, sort_order)
        SELECT 2, 'Game',         14427686, 20 WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = 5);
        INSERT OR IGNORE INTO categories (id, name, color_rgb, sort_order)
        SELECT 3, 'Communication', 959977, 30 WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = 5);
        INSERT OR IGNORE INTO categories (id, name, color_rgb, sort_order)
        SELECT 4, 'Browsing',     16096779, 40 WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = 5);
        INSERT OR IGNORE INTO categories (id, name, color_rgb, sort_order)
        SELECT 5, 'System',       6583435, 50 WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = 5);
        INSERT OR IGNORE INTO categories (id, name, color_rgb, sort_order)
        SELECT 6, 'Other',        9741240, 60 WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = 5);

        INSERT OR IGNORE INTO schema_version VALUES (5);
        """;

    const string V6 = """
        -- Closed-session overlap scans start with end_at > range_start. Keeping start_at as
        -- the second column lets SQLite evaluate the other range bound from the index row.
        CREATE INDEX IF NOT EXISTS idx_sessions_end_start
          ON sessions(end_at, start_at)
          WHERE end_at IS NOT NULL;

        -- There is normally only one open row, but this keeps the UNION open-session branch
        -- indexable even while recovering a database with several orphaned rows.
        CREATE INDEX IF NOT EXISTS idx_sessions_open_start
          ON sessions(start_at)
          WHERE end_at IS NULL;

        -- Speeds logical-row detection during append imports without changing recorder writes
        -- into a uniqueness constraint.
        CREATE INDEX IF NOT EXISTS idx_sessions_import_logical
          ON sessions(process_name COLLATE NOCASE, start_at);

        CREATE INDEX IF NOT EXISTS idx_processes_name_nocase_seen
          ON processes(name COLLATE NOCASE, last_seen_at DESC);

        INSERT OR IGNORE INTO schema_version VALUES (6);
        """;

    const string V7 = """
        CREATE INDEX IF NOT EXISTS idx_sessions_open_heartbeat
          ON sessions(last_seen_at)
          WHERE end_at IS NULL;

        INSERT OR IGNORE INTO schema_version VALUES (7);
        """;

    /// <summary>v3 시드: Windows 시스템 UI 호스트 프로세스. 시작메뉴/검색/잠금 등 노이즈 차단.</summary>
    static readonly string[] DefaultSystemUiExclusions = new[]
    {
        "StartMenuExperienceHost",
        "ShellExperienceHost",
        "SearchHost",
        "SearchUI",
        "TextInputHost",
        "LockApp",
        "ApplicationFrameHost",
        "SystemSettings",
    };

    public static void Apply(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = V1;
            cmd.ExecuteNonQuery();
            cmd.CommandText = V2;
            cmd.ExecuteNonQuery();
            cmd.CommandText = V3;
            cmd.ExecuteNonQuery();
            cmd.CommandText = V4;
            cmd.ExecuteNonQuery();
            cmd.CommandText = V5;
            cmd.ExecuteNonQuery();
            cmd.CommandText = V6;
            cmd.ExecuteNonQuery();
        }

        EnsureSessionHeartbeatColumn(conn, tx);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = V7;
            cmd.ExecuteNonQuery();
        }

        NormalizeOtherCategory(conn, tx);

        using (var seedCmd = conn.CreateCommand())
        {
            seedCmd.Transaction = tx;
            seedCmd.CommandText = """
                INSERT OR IGNORE INTO excluded_processes (name, reason, excluded_at)
                VALUES ($n, 'system-ui', $t);
                """;
            var nameParam = seedCmd.Parameters.Add("$n", Microsoft.Data.Sqlite.SqliteType.Text);
            seedCmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            foreach (var name in DefaultSystemUiExclusions)
            {
                nameParam.Value = name;
                seedCmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    static void EnsureSessionHeartbeatColumn(SqliteConnection conn, SqliteTransaction tx)
    {
        var exists = false;
        using (var info = conn.CreateCommand())
        {
            info.Transaction = tx;
            info.CommandText = "PRAGMA table_info(sessions);";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (!string.Equals(reader.GetString(1), "last_seen_at", StringComparison.OrdinalIgnoreCase))
                    continue;
                exists = true;
                break;
            }
        }

        if (!exists)
            Execute("ALTER TABLE sessions ADD COLUMN last_seen_at INTEGER;", conn, tx);

        Execute("""
            UPDATE sessions
            SET last_seen_at = COALESCE(last_seen_at, end_at, start_at)
            WHERE last_seen_at IS NULL;
            """, conn, tx);
    }

    /// <summary>
    /// Repairs databases produced by early v5 builds where ID 6 could be renamed/reused or
    /// Other could live at another ID. Rule references and the user's Other color/order survive.
    /// </summary>
    static void NormalizeOtherCategory(SqliteConnection conn, SqliteTransaction tx)
    {
        Execute("DROP TRIGGER IF EXISTS protect_default_other_update;", conn, tx);
        Execute("DROP TRIGGER IF EXISTS protect_default_other_delete;", conn, tx);

        var idSix = ReadCategory(conn, tx, "WHERE id = 6");
        var namedOther = ReadCategory(conn, tx, "WHERE name = 'Other' COLLATE NOCASE");

        if (idSix is { } occupied &&
            !string.Equals(occupied.Name, "Other", StringComparison.OrdinalIgnoreCase))
        {
            using (var freeName = conn.CreateCommand())
            {
                freeName.Transaction = tx;
                freeName.CommandText = "UPDATE categories SET name = $temporaryName WHERE id = 6;";
                freeName.Parameters.AddWithValue("$temporaryName", $"__category_migration_{Guid.NewGuid():N}");
                freeName.ExecuteNonQuery();
            }
            long relocatedId;
            using (var relocate = conn.CreateCommand())
            {
                relocate.Transaction = tx;
                relocate.CommandText = """
                    INSERT INTO categories (name, color_rgb, sort_order) VALUES ($name, $color, $sort);
                    SELECT last_insert_rowid();
                    """;
                relocate.Parameters.AddWithValue("$name", occupied.Name);
                relocate.Parameters.AddWithValue("$color", occupied.ColorRgb);
                relocate.Parameters.AddWithValue("$sort", occupied.SortOrder);
                relocatedId = (long)relocate.ExecuteScalar()!;
            }
            UpdateRuleCategory(conn, tx, fromId: 6, toId: relocatedId);
            Execute("DELETE FROM categories WHERE id = 6;", conn, tx);
            idSix = null;
        }

        if (idSix is null && namedOther is { Id: not 6 } oldOther)
        {
            // The temporary unique name avoids the NOCASE unique-name constraint while both rows exist.
            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO categories (id, name, color_rgb, sort_order)
                    VALUES (6, $temporaryName, $color, $sort);
                    """;
                insert.Parameters.AddWithValue("$temporaryName", $"__other_migration_{Guid.NewGuid():N}");
                insert.Parameters.AddWithValue("$color", oldOther.ColorRgb);
                insert.Parameters.AddWithValue("$sort", oldOther.SortOrder);
                insert.ExecuteNonQuery();
            }
            UpdateRuleCategory(conn, tx, oldOther.Id, 6);
            using (var delete = conn.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM categories WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", oldOther.Id);
                delete.ExecuteNonQuery();
            }
            Execute("UPDATE categories SET name = 'Other' WHERE id = 6;", conn, tx);
        }
        else if (idSix is null)
        {
            Execute("""
                INSERT INTO categories (id, name, color_rgb, sort_order)
                VALUES (6, 'Other', 9741240, 60);
                """, conn, tx);
        }
        else if (!string.Equals(idSix.Value.Name, "Other", StringComparison.Ordinal))
        {
            Execute("UPDATE categories SET name = 'Other' WHERE id = 6;", conn, tx);
        }

        Execute("""
            CREATE TRIGGER protect_default_other_update
            BEFORE UPDATE OF id, name ON categories
            WHEN OLD.id = 6 AND (NEW.id <> 6 OR NEW.name <> 'Other' COLLATE BINARY)
            BEGIN
              SELECT RAISE(ABORT, 'The default Other category identity is immutable');
            END;

            CREATE TRIGGER protect_default_other_delete
            BEFORE DELETE ON categories
            WHEN OLD.id = 6
            BEGIN
              SELECT RAISE(ABORT, 'The default Other category cannot be deleted');
            END;
            """, conn, tx);
    }

    static CategoryRow? ReadCategory(
        SqliteConnection conn, SqliteTransaction tx, string whereClause)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT id, name, color_rgb, sort_order FROM categories {whereClause} LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new CategoryRow(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3))
            : null;
    }

    static void UpdateRuleCategory(
        SqliteConnection conn, SqliteTransaction tx, long fromId, long toId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE application_rules SET category_id = $to WHERE category_id = $from;";
        cmd.Parameters.AddWithValue("$from", fromId);
        cmd.Parameters.AddWithValue("$to", toId);
        cmd.ExecuteNonQuery();
    }

    static void Execute(string sql, SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    readonly record struct CategoryRow(long Id, string Name, int ColorRgb, int SortOrder);

    public static int ReadVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
