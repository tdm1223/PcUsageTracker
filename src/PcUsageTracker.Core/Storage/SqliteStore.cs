using Microsoft.Data.Sqlite;
using PcUsageTracker.Core.Models;
using PcUsageTracker.Core.Sampling;

namespace PcUsageTracker.Core.Storage;

/// <summary>
/// 단일 연결 기반 세션 저장소. 1Hz 쓰기 부하 전제 — 멀티스레드 write를 가정하지 않는다.
/// 호출자(UI 스레드 또는 타이머)가 단일 스레드로 호출해야 한다.
/// </summary>
public sealed class SqliteStore : ISessionSink, IDisposable
{
    public const int CurrentSchemaVersion = 7;

    readonly SqliteConnection _conn;
    readonly string _dbPath;

    public SqliteStore(string dbPath)
    {
        _dbPath = Path.GetFullPath(dbPath);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
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
            INSERT INTO sessions (process_name, start_at, last_seen_at) VALUES ($p, $s, $s);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$p", processName);
        cmd.Parameters.AddWithValue("$s", startAt.ToUnixTimeSeconds());
        var id = (long)cmd.ExecuteScalar()!;
        return id;
    }

    public void Touch(long sessionId, DateTimeOffset observedAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET last_seen_at = MAX(start_at, $observed)
            WHERE id = $id
              AND end_at IS NULL
              AND (last_seen_at IS NULL OR $observed > last_seen_at);
            """;
        cmd.Parameters.AddWithValue("$observed", observedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public void Close(long sessionId, DateTimeOffset endAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET end_at = $e,
                duration_sec = CASE WHEN $e - start_at >= 0 THEN $e - start_at ELSE 0 END,
                last_seen_at = MAX(start_at, $e)
            WHERE id = $id AND end_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$e", endAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 앱 시작 시 호출. end_at IS NULL 레코드를 마지막으로 실제 관찰된 시각에 close한다.
    /// 현재 시각까지 늘리지 않으므로 종료·절전 중 시간이 마지막 앱에 붙지 않는다.
    /// 복구된 레코드 수 반환.
    /// </summary>
    public int RecoverOrphanedSessions(DateTimeOffset now)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET end_at = MAX(start_at, MIN($now, COALESCE(last_seen_at, start_at))),
                duration_sec = MAX(start_at, MIN($now, COALESCE(last_seen_at, start_at))) - start_at,
                last_seen_at = MAX(start_at, MIN($now, COALESCE(last_seen_at, start_at)))
            WHERE end_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        return cmd.ExecuteNonQuery();
    }

    public SqliteConnection Connection => _conn;

    /// <summary>
    /// Opens an independent query-only connection suitable for background reporting. The live
    /// recorder connection is intentionally never shared across threads.
    /// </summary>
    public SqliteConnection OpenReadOnlyConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 3000;";
        cmd.ExecuteNonQuery();
        return connection;
    }

    /// <summary>processes 테이블에 exe 경로를 upsert. 아이콘 추출 등 UI 메타데이터 용도.</summary>
    public void UpsertProcessPath(string processName, string exePath, DateTimeOffset at)
        => UpsertProcessPathCore(processName, exePath, at, transaction: null);

    /// <summary>
    /// Persists metadata from a background resolver without touching the recorder's connection.
    /// </summary>
    public void UpsertProcessPathIndependent(string processName, string exePath, DateTimeOffset at)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA busy_timeout = 3000;
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

    void UpsertProcessPathCore(
        string processName,
        string exePath,
        DateTimeOffset at,
        SqliteTransaction? transaction)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = transaction;
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
        cmd.CommandText = "SELECT 1 FROM excluded_processes WHERE name = $n COLLATE NOCASE LIMIT 1;";
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
        cmd.CommandText = "DELETE FROM excluded_processes WHERE name = $n COLLATE NOCASE;";
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
            SELECT s.process_name, s.start_at, s.end_at, s.duration_sec,
              (SELECT p.exe_path
                 FROM processes p
                WHERE p.name = s.process_name COLLATE NOCASE
                ORDER BY (p.exe_path IS NOT NULL) DESC, p.last_seen_at DESC, p.name COLLATE BINARY
                LIMIT 1) AS exe_path
            FROM sessions s
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
        var deleted = ClearAllSessionsCore(tx);
        tx.Commit();
        return deleted;
    }

    int ClearAllSessionsCore(SqliteTransaction transaction)
    {
        int deleted;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM sessions;";
            deleted = cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM processes;";
            cmd.ExecuteNonQuery();
        }
        return deleted;
    }

    /// <summary>
    /// Excel import 등 외부 입력으로부터 단건 session row를 삽입. duration_sec는 endAt 주어진 경우 자동 계산.
    /// exePath가 non-null이면 processes 테이블에 UpsertProcessPath와 동일하게 반영.
    /// 새 session id 반환. Import identity는 process_name(NOCASE) + start_at이다.
    /// 기존 open row에 closed row가 들어오면 기존 row를 close하고, closed→open 및 서로 다른
    /// closed end 충돌은 먼저 저장된 row를 보존한다. Append 재실행으로 사용 시간이 중복되지 않는다.
    /// </summary>
    public long ImportSession(string processName, DateTimeOffset startAt, DateTimeOffset? endAt, string? exePath)
    {
        processName = ValidateProcessName(processName);
        using var transaction = _conn.BeginTransaction();
        var id = ImportSessionCore(processName, startAt, endAt, exePath, transaction);
        transaction.Commit();
        return id;
    }

    /// <summary>
    /// Imports a sequence atomically. Replace clears session/process data inside the same
    /// transaction, so enumeration, validation, or SQLite failures restore all previous data.
    /// Returns the number of input rows processed (idempotent matches are included).
    /// </summary>
    public int ImportSessions(IEnumerable<ImportSessionRow> sessions, bool replace)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        using var transaction = _conn.BeginTransaction();
        if (replace) ClearAllSessionsCore(transaction);

        var count = 0;
        foreach (var row in sessions)
        {
            var processName = ValidateProcessName(row.ProcessName);
            ImportSessionCore(processName, row.StartAt, row.EndAt, row.ExePath, transaction);
            count++;
        }

        transaction.Commit();
        return count;
    }

    long ImportSessionCore(
        string processName,
        DateTimeOffset startAt,
        DateTimeOffset? endAt,
        string? exePath,
        SqliteTransaction transaction)
    {
        var startUnix = startAt.ToUnixTimeSeconds();
        var endUnix = endAt?.ToUnixTimeSeconds();
        long id;

        long? existingId = null;
        long? existingEndUnix = null;
        string? existingProcessName = null;
        using (var existing = _conn.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT id, process_name, end_at
                FROM sessions
                WHERE process_name = $p COLLATE NOCASE
                  AND start_at = $s
                ORDER BY id
                LIMIT 1;
                """;
            existing.Parameters.AddWithValue("$p", processName);
            existing.Parameters.AddWithValue("$s", startUnix);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                existingId = reader.GetInt64(0);
                existingProcessName = reader.GetString(1);
                existingEndUnix = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            }
        }

        if (existingId is { } matchedId)
        {
            if (existingEndUnix is null && endUnix is { } closingEndUnix)
            {
                var duration = Math.Max(closingEndUnix - startUnix, 0);
                using var close = _conn.CreateCommand();
                close.Transaction = transaction;
                close.CommandText = """
                    UPDATE sessions
                    SET end_at = $end, duration_sec = $duration, last_seen_at = MAX(start_at, $end)
                    WHERE id = $id AND end_at IS NULL;
                    """;
                close.Parameters.AddWithValue("$end", closingEndUnix);
                close.Parameters.AddWithValue("$duration", duration);
                close.Parameters.AddWithValue("$id", matchedId);
                close.ExecuteNonQuery();
            }

            if (!string.IsNullOrEmpty(exePath))
                UpsertProcessPathCore(existingProcessName!, exePath, endAt ?? startAt, transaction);
            return matchedId;
        }

        using var cmd = _conn.CreateCommand();
        cmd.Transaction = transaction;
        if (endUnix is { } importedEndUnix)
        {
            var duration = importedEndUnix - startUnix;
            if (duration < 0) duration = 0;
            cmd.CommandText = """
                INSERT INTO sessions (process_name, start_at, end_at, duration_sec, last_seen_at)
                VALUES ($p, $s, $e, $d, MAX($s, $e));
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$p", processName);
            cmd.Parameters.AddWithValue("$s", startUnix);
            cmd.Parameters.AddWithValue("$e", importedEndUnix);
            cmd.Parameters.AddWithValue("$d", duration);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO sessions (process_name, start_at, last_seen_at) VALUES ($p, $s, $s);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$p", processName);
            cmd.Parameters.AddWithValue("$s", startAt.ToUnixTimeSeconds());
        }
        id = (long)cmd.ExecuteScalar()!;

        if (!string.IsNullOrEmpty(exePath))
            UpsertProcessPathCore(processName, exePath, endAt ?? startAt, transaction);

        return id;
    }

    /// <summary>settings 테이블에서 key의 value를 반환. 없으면 null.</summary>
    public string? GetSetting(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        var v = cmd.ExecuteScalar();
        return v is string s ? s : null;
    }

    /// <summary>settings 테이블에 (key, value) UPSERT. 이미 있으면 value 갱신.</summary>
    public void SetSetting(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns all categories in user-defined order.</summary>
    public IReadOnlyList<ApplicationCategory> ListCategories()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, color_rgb, sort_order FROM categories ORDER BY sort_order, name COLLATE NOCASE;";
        var result = new List<ApplicationCategory>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new ApplicationCategory(
                r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
        }
        return result;
    }

    public ApplicationCategory? GetCategory(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, color_rgb, sort_order FROM categories WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new ApplicationCategory(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3))
            : null;
    }

    public long CreateCategory(string name, int colorRgb, int sortOrder = 0)
    {
        name = ValidateCategoryName(name);
        ValidateColor(colorRgb, nameof(colorRgb));

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO categories (name, color_rgb, sort_order) VALUES ($n, $c, $s);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$c", colorRgb);
        cmd.Parameters.AddWithValue("$s", sortOrder);
        return (long)cmd.ExecuteScalar()!;
    }

    public bool UpdateCategory(long id, string name, int colorRgb, int sortOrder)
    {
        name = ValidateCategoryName(name);
        ValidateColor(colorRgb, nameof(colorRgb));

        using var cmd = _conn.CreateCommand();
        if (id == DefaultApplicationCategories.OtherId)
        {
            if (!string.Equals(name, "Other", StringComparison.Ordinal))
                throw new InvalidOperationException("The default Other category cannot be renamed.");
            cmd.CommandText = """
                UPDATE categories SET color_rgb = $c, sort_order = $s WHERE id = $id;
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE categories SET name = $n, color_rgb = $c, sort_order = $s WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$n", name);
        }
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$c", colorRgb);
        cmd.Parameters.AddWithValue("$s", sortOrder);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Deletes a category. SQLite sets matching application rule category IDs to NULL.</summary>
    public bool DeleteCategory(long id)
    {
        if (id == DefaultApplicationCategories.OtherId)
            throw new InvalidOperationException("The default Other category cannot be deleted.");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM categories WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public ApplicationRule? GetApplicationRule(string processName)
    {
        processName = ValidateProcessName(processName);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT process_name, alias, category_id, color_override_rgb, updated_at
            FROM application_rules WHERE process_name = $n;
            """;
        cmd.Parameters.AddWithValue("$n", processName);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadApplicationRule(r) : null;
    }

    public IReadOnlyList<ApplicationRule> ListApplicationRules()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT process_name, alias, category_id, color_override_rgb, updated_at
            FROM application_rules ORDER BY process_name COLLATE NOCASE;
            """;
        var result = new List<ApplicationRule>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(ReadApplicationRule(r));
        return result;
    }

    public void UpsertApplicationRule(
        string processName,
        string? alias,
        long? categoryId,
        int? colorOverrideRgb,
        DateTimeOffset at)
    {
        processName = ValidateProcessName(processName);
        if (string.Equals(processName, IdleSentinel.Name, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The idle sentinel cannot have an application rule.", nameof(processName));
        alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        if (colorOverrideRgb is { } color) ValidateColor(color, nameof(colorOverrideRgb));

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO application_rules
              (process_name, alias, category_id, color_override_rgb, updated_at)
            VALUES ($n, $a, $category, $color, $at)
            ON CONFLICT(process_name) DO UPDATE SET
              alias = excluded.alias,
              category_id = excluded.category_id,
              color_override_rgb = excluded.color_override_rgb,
              updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$n", processName);
        cmd.Parameters.AddWithValue("$a", (object?)alias ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$category", (object?)categoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$color", (object?)colorOverrideRgb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Applies one category to several process rules atomically while preserving aliases and
    /// per-application color overrides. A null category means Other / unassigned.
    /// </summary>
    public int SetApplicationCategories(
        IEnumerable<string> processNames,
        long? categoryId,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(processNames);
        var names = processNames
            .Select(ValidateProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Any(name => string.Equals(name, IdleSentinel.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The idle sentinel cannot have an application rule.", nameof(processNames));

        using var transaction = _conn.BeginTransaction();
        if (categoryId is { } selectedCategory)
        {
            using var category = _conn.CreateCommand();
            category.Transaction = transaction;
            category.CommandText = "SELECT 1 FROM categories WHERE id = $id;";
            category.Parameters.AddWithValue("$id", selectedCategory);
            if (category.ExecuteScalar() is null)
                throw new ArgumentException("The selected category does not exist.", nameof(categoryId));
        }

        foreach (var name in names)
        {
            using var apply = _conn.CreateCommand();
            apply.Transaction = transaction;
            apply.CommandText = """
                INSERT INTO application_rules
                  (process_name, alias, category_id, color_override_rgb, updated_at)
                VALUES ($name, NULL, $category, NULL, $at)
                ON CONFLICT(process_name) DO UPDATE SET
                  category_id = excluded.category_id,
                  updated_at = excluded.updated_at;
                """;
            apply.Parameters.AddWithValue("$name", name);
            apply.Parameters.AddWithValue("$category", (object?)categoryId ?? DBNull.Value);
            apply.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
            apply.ExecuteNonQuery();

            if (categoryId is null)
            {
                using var cleanup = _conn.CreateCommand();
                cleanup.Transaction = transaction;
                cleanup.CommandText = """
                    DELETE FROM application_rules
                    WHERE process_name = $name
                      AND alias IS NULL
                      AND category_id IS NULL
                      AND color_override_rgb IS NULL;
                    """;
                cleanup.Parameters.AddWithValue("$name", name);
                cleanup.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return names.Length;
    }

    public bool DeleteApplicationRule(string processName)
    {
        processName = ValidateProcessName(processName);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM application_rules WHERE process_name = $n;";
        cmd.Parameters.AddWithValue("$n", processName);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Lists applications present in sessions, executable metadata, or saved rules.
    /// The idle sentinel is intentionally omitted because it is fixed and non-editable.
    /// </summary>
    public IReadOnlyList<KnownApplication> ListKnownApplications()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            WITH discovered(process_name) AS (
              SELECT process_name FROM sessions
              UNION ALL
              SELECT name FROM processes
              UNION ALL
              SELECT process_name FROM application_rules
            ),
            known(process_name) AS (
              SELECT MIN(process_name COLLATE BINARY)
              FROM discovered
              WHERE process_name <> $idle COLLATE NOCASE
              GROUP BY process_name COLLATE NOCASE
            )
            SELECT
              k.process_name,
              (SELECT p.exe_path
                 FROM processes p
                WHERE p.name = k.process_name COLLATE NOCASE
                ORDER BY (p.exe_path IS NOT NULL) DESC, p.last_seen_at DESC, p.name COLLATE BINARY
                LIMIT 1) AS exe_path,
              ar.alias,
              ar.category_id,
              COALESCE(c.name, other.name) AS category_name,
              ar.color_override_rgb,
              COALESCE(ar.color_override_rgb, c.color_rgb, other.color_rgb) AS resolved_color
            FROM known k
            JOIN categories other ON other.id = $otherId
            LEFT JOIN application_rules ar ON ar.process_name = k.process_name COLLATE NOCASE
            LEFT JOIN categories c ON c.id = ar.category_id
            ORDER BY COALESCE(NULLIF(TRIM(ar.alias), ''), k.process_name) COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$idle", IdleSentinel.Name);
        cmd.Parameters.AddWithValue("$otherId", DefaultApplicationCategories.OtherId);

        var result = new List<KnownApplication>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new KnownApplication(
                ProcessName: r.GetString(0),
                ExePath: r.IsDBNull(1) ? null : r.GetString(1),
                Alias: r.IsDBNull(2) ? null : r.GetString(2),
                CategoryId: r.IsDBNull(3) ? null : r.GetInt64(3),
                CategoryName: r.GetString(4),
                ColorOverrideRgb: r.IsDBNull(5) ? null : r.GetInt32(5),
                ResolvedColorRgb: r.GetInt32(6)));
        }
        return result;
    }

    static ApplicationRule ReadApplicationRule(SqliteDataReader r) => new(
        ProcessName: r.GetString(0),
        Alias: r.IsDBNull(1) ? null : r.GetString(1),
        CategoryId: r.IsDBNull(2) ? null : r.GetInt64(2),
        ColorOverrideRgb: r.IsDBNull(3) ? null : r.GetInt32(3),
        UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)));

    static string ValidateCategoryName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Category name cannot be empty.", nameof(name));
        return name;
    }

    static string ValidateProcessName(string processName)
    {
        ArgumentNullException.ThrowIfNull(processName);
        processName = processName.Trim();
        if (processName.Length == 0) throw new ArgumentException("Process name cannot be empty.", nameof(processName));
        return processName;
    }

    static void ValidateColor(int colorRgb, string parameterName)
    {
        if (colorRgb is < 0 or > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(parameterName, "RGB color must be between 0x000000 and 0xFFFFFF.");
    }

    /// <summary>특정 프로세스의 모든 sessions + processes 메타데이터 삭제. 삭제된 sessions 행 수 반환.</summary>
    public int DeleteSessionsForProcess(string processName)
    {
        using var tx = _conn.BeginTransaction();
        int deleted;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM sessions WHERE process_name = $n COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$n", processName);
            deleted = cmd.ExecuteNonQuery();

            cmd.Parameters.Clear();
            cmd.CommandText = "DELETE FROM processes WHERE name = $n COLLATE NOCASE;";
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

/// <summary>A validated-at-write session value used by atomic batch imports.</summary>
public readonly record struct ImportSessionRow(
    string ProcessName,
    DateTimeOffset StartAt,
    DateTimeOffset? EndAt,
    string? ExePath);
