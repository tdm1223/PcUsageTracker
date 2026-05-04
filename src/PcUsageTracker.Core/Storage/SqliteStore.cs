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

    /// <summary>
    /// 모든 sessions를 (id 오름차순) 스트리밍으로 열거. exe_path는 processes 메타데이터 LEFT JOIN 결과.
    /// 호출자가 모든 row를 읽을 때까지 underlying reader가 살아있으므로 enumeration 도중 다른 connection write를 시도하지 말 것.
    /// </summary>
    public IEnumerable<SessionRow> EnumerateSessions()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.process_name, s.start_at, s.end_at, s.duration_sec, p.exe_path
            FROM sessions s
            LEFT JOIN processes p ON p.name = s.process_name
            ORDER BY s.id;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return new SessionRow(
                ProcessName: r.GetString(0),
                StartAtUnix: r.GetInt64(1),
                EndAtUnix: r.IsDBNull(2) ? null : r.GetInt64(2),
                DurationSec: r.IsDBNull(3) ? null : r.GetInt32(3),
                ExePath: r.IsDBNull(4) ? null : r.GetString(4));
        }
    }

    /// <summary>
    /// sessions와 processes의 모든 row를 삭제. excluded_processes는 보존(사용자가 명시 추가한 항목 보호).
    /// 삭제된 sessions 행 수 반환. Replace-import 시 사용.
    /// </summary>
    public int ClearAllSessions()
    {
        using var tx = _conn.BeginTransaction();
        int deleted;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM sessions;";
            deleted = cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM processes;";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return deleted;
    }

    /// <summary>
    /// Excel import 등 외부 입력으로부터 단건 session row를 삽입. duration_sec는 endAt 주어진 경우 자동 계산.
    /// exePath가 non-null이면 processes 테이블에 UpsertProcessPath와 동일하게 반영.
    /// 새 session id 반환.
    /// </summary>
    public long ImportSession(string processName, DateTimeOffset startAt, DateTimeOffset? endAt, string? exePath)
    {
        long id;
        using var cmd = _conn.CreateCommand();
        if (endAt is { } e)
        {
            var startUnix = startAt.ToUnixTimeSeconds();
            var endUnix = e.ToUnixTimeSeconds();
            var duration = endUnix - startUnix;
            if (duration < 0) duration = 0;
            cmd.CommandText = """
                INSERT INTO sessions (process_name, start_at, end_at, duration_sec)
                VALUES ($p, $s, $e, $d);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$p", processName);
            cmd.Parameters.AddWithValue("$s", startUnix);
            cmd.Parameters.AddWithValue("$e", endUnix);
            cmd.Parameters.AddWithValue("$d", duration);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO sessions (process_name, start_at) VALUES ($p, $s);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$p", processName);
            cmd.Parameters.AddWithValue("$s", startAt.ToUnixTimeSeconds());
        }
        id = (long)cmd.ExecuteScalar()!;

        if (!string.IsNullOrEmpty(exePath))
            UpsertProcessPath(processName, exePath, endAt ?? startAt);

        return id;
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

/// <summary>EnumerateSessions / Excel I/O 가 사용하는 sessions 테이블 row 표현.</summary>
public readonly record struct SessionRow(
    string ProcessName,
    long StartAtUnix,
    long? EndAtUnix,
    int? DurationSec,
    string? ExePath);
