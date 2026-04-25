using Microsoft.Data.Sqlite;
using PcUsageTracker.Core.Sampling;

namespace PcUsageTracker.Core.Storage;

/// <summary>
/// 단일 연결 기반 세션 저장소. 1Hz 쓰기 부하 전제 — 멀티스레드 write를 가정하지 않는다.
/// 호출자(UI 스레드 또는 타이머)가 단일 스레드로 호출해야 한다.
/// </summary>
public sealed class SqliteStore : ISessionSink, IDisposable
{
    public const int OrphanedCapSeconds = 86400; // 비정상 종료 후 복구 시 24시간 cap
    public const int CurrentSchemaVersion = 3;

    readonly SqliteConnection _conn;

    public SqliteStore(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
        };
        _conn = new SqliteConnection(builder.ConnectionString);
        _conn.Open();
        ApplyPragmas();
        Migrations.Apply(_conn);
    }

    void ApplyPragmas()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 3000;
            PRAGMA foreign_keys = ON;
            """;
        cmd.ExecuteNonQuery();
    }

    public long Open(string processName, DateTimeOffset startAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (process_name, start_at) VALUES ($p, $s);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$p", processName);
        cmd.Parameters.AddWithValue("$s", startAt.ToUnixTimeSeconds());
        var id = (long)cmd.ExecuteScalar()!;
        return id;
    }

    public void Close(long sessionId, DateTimeOffset endAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET end_at = $e,
                duration_sec = CASE WHEN $e - start_at >= 0 THEN $e - start_at ELSE 0 END
            WHERE id = $id AND end_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$e", endAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 앱 시작 시 호출. end_at IS NULL 레코드를 전부 강제 close.
    /// end_at = min(now, start_at + OrphanedCapSeconds). duration = end_at - start_at.
    /// 복구된 레코드 수 반환.
    /// </summary>
    public int RecoverOrphanedSessions(DateTimeOffset now)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET end_at = MIN($now, start_at + $cap),
                duration_sec = MIN($now, start_at + $cap) - start_at
            WHERE end_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$cap", OrphanedCapSeconds);
        return cmd.ExecuteNonQuery();
    }

    public SqliteConnection Connection => _conn;

    /// <summary>processes 테이블에 exe 경로를 upsert. 아이콘 추출 등 UI 메타데이터 용도.</summary>
    public void UpsertProcessPath(string processName, string exePath, DateTimeOffset at)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO processes (name, exe_path, last_seen_at) VALUES ($n, $p, $t)
            ON CONFLICT(name) DO UPDATE SET
              exe_path = excluded.exe_path,
              last_seen_at = excluded.last_seen_at;
            """;
        cmd.Parameters.AddWithValue("$n", processName);
        cmd.Parameters.AddWithValue("$p", exePath);
        cmd.Parameters.AddWithValue("$t", at.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public string? GetProcessPath(string processName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT exe_path FROM processes WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", processName);
        var v = cmd.ExecuteScalar();
        return v is string s ? s : null;
    }

    /// <summary>특정 프로세스가 추적 제외 목록에 있는지 확인.</summary>
    public bool IsExcluded(string processName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM excluded_processes WHERE name = $n LIMIT 1;";
        cmd.Parameters.AddWithValue("$n", processName);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>현재 등록된 모든 제외 프로세스명 반환.</summary>
    public IReadOnlyList<string> ListExclusions()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM excluded_processes ORDER BY name;";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>제외 목록에 추가 (UPSERT). 이미 있으면 reason/excluded_at 갱신.</summary>
    public void AddExclusion(string processName, string? reason, DateTimeOffset at)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO excluded_processes (name, reason, excluded_at) VALUES ($n, $r, $t)
            ON CONFLICT(name) DO UPDATE SET
              reason = excluded.reason,
              excluded_at = excluded.excluded_at;
            """;
        cmd.Parameters.AddWithValue("$n", processName);
        cmd.Parameters.AddWithValue("$r", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", at.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    /// <summary>제외 목록에서 제거. 없으면 no-op.</summary>
    public void RemoveExclusion(string processName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM excluded_processes WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", processName);
        cmd.ExecuteNonQuery();
    }

    /// <summary>특정 프로세스의 모든 sessions + processes 메타데이터 삭제. 삭제된 sessions 행 수 반환.</summary>
    public int DeleteSessionsForProcess(string processName)
    {
        using var tx = _conn.BeginTransaction();
        int deleted;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM sessions WHERE process_name = $n;";
            cmd.Parameters.AddWithValue("$n", processName);
            deleted = cmd.ExecuteNonQuery();

            cmd.Parameters.Clear();
            cmd.CommandText = "DELETE FROM processes WHERE name = $n;";
            cmd.Parameters.AddWithValue("$n", processName);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return deleted;
    }

    public void Dispose()
    {
        _conn.Dispose();
    }
}
