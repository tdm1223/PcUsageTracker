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
