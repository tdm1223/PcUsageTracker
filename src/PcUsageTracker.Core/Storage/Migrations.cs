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
        }

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

    public static int ReadVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
